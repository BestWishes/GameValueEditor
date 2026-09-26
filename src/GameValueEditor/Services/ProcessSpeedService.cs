using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GameValueEditor.Services;

/// <summary>
/// Installs stable x64 clock wrappers once, then changes their shared clock state from
/// inside the target process. Speed changes never suspend the process or repatch its IAT.
/// Normalizing leaves the 1x wrappers installed so the target never observes a clock reset.
/// </summary>
public sealed class ProcessSpeedService : IDisposable
{
    private const int StateSize = 96;
    private const int StateMultiplierOffset = 8;
    private const int RemoteUpdateTimeoutMilliseconds = 1500;
    private static readonly byte[] LegacyWrapperMagic = "GVESPD01"u8.ToArray();
    private static readonly byte[] StableWrapperMagic = "GVESPD02"u8.ToArray();
    private static readonly byte[] StateMagic = "GVESTATE"u8.ToArray();

    private readonly List<PatchedSlot> _patchedSlots = [];
    private SafeProcessHandle? _processHandle;
    private ulong _stateAddress;
    private ulong _updaterAddress;
    private int _wrapperCount;

    public bool HasHooks =>
        _processHandle is { IsInvalid: false, IsClosed: false } &&
        _patchedSlots.Count > 0 && _stateAddress != 0 && _updaterAddress != 0;

    public int ActiveProcessId { get; private set; }
    public int Multiplier { get; private set; } = 1;

    public SpeedHookResult Accelerate(int processId, int multiplier)
    {
        if (multiplier is < 1 or > 100)
            throw new InvalidOperationException("加速倍数必须是 1 到 100 的整数。");
        if (multiplier == 1)
            throw new InvalidOperationException("1 倍就是正常速度，不需要加速。");

        if (HasHooks)
        {
            if (ActiveProcessId != processId)
                throw new InvalidOperationException("另一个进程仍在使用游戏加速，请先回正或重新连接。");
            SetMultiplierCore(multiplier);
            return new SpeedHookResult(_patchedSlots.Count, _wrapperCount);
        }

        DetachSafely();
        var handle = OpenTargetProcess(processId);
        var newlyPatched = new List<PatchedSlot>();
        try
        {
            using var process = Process.GetProcessById(processId);
            var imports = FindClockImports(handle, process);
            if (imports.Count == 0)
                throw new InvalidOperationException("目标进程没有找到可挂接的计时 API，暂时无法使用通用游戏加速。");
            if (imports.Any(import => import.PreviousWrapper?.Version == 1))
                throw new InvalidOperationException(
                    "目标游戏仍保留旧版加速挂钩。请先退出并重新启动游戏，再使用新版加速功能。");

            var stableWrappers = imports
                .Where(import => import.PreviousWrapper?.Version == 2)
                .Select(import => import.PreviousWrapper!)
                .ToList();

            if (stableWrappers.Count > 0)
                AttachExistingStableHooks(handle, processId, imports, stableWrappers, newlyPatched);
            else
                InstallStableHooks(handle, processId, imports, newlyPatched);
        }
        catch
        {
            if (!ReferenceEquals(_processHandle, handle))
            {
                RestorePatchedSlots(handle, newlyPatched);
                handle.Dispose();
            }
            throw;
        }

        SetMultiplierCore(multiplier);
        return new SpeedHookResult(_patchedSlots.Count, _wrapperCount);
    }

    public SpeedHookResult Normalize()
    {
        if (!HasHooks)
        {
            Multiplier = 1;
            return new SpeedHookResult(0, 0);
        }
        var result = new SpeedHookResult(_patchedSlots.Count, _wrapperCount);
        if (Multiplier != 1) SetMultiplierCore(1);
        return result;
    }

    public void DetachSafely()
    {
        var handle = _processHandle;
        if (HasHooks && Multiplier != 1)
        {
            try
            {
                SetMultiplierCore(1);
            }
            catch (Exception exception) when (!IsActiveProcessRunning())
            {
                Debug.WriteLine(exception);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "无法在关闭编辑器前把游戏安全回正。编辑器已停止等待，不会无限卡住；请稍后重试。",
                    exception);
            }
        }

        _patchedSlots.Clear();
        handle?.Dispose();
        _processHandle = null;
        _stateAddress = 0;
        _updaterAddress = 0;
        _wrapperCount = 0;
        ActiveProcessId = 0;
        Multiplier = 1;
    }

    public void Restore() => DetachSafely();
    public void Dispose() => DetachSafely();

    private static SafeProcessHandle OpenTargetProcess(int processId)
    {
        var handle = SpeedNativeMethods.OpenProcess(
            SpeedNativeMethods.ProcessAccess.CreateThread |
            SpeedNativeMethods.ProcessAccess.QueryInformation |
            SpeedNativeMethods.ProcessAccess.VirtualMemoryOperation |
            SpeedNativeMethods.ProcessAccess.VirtualMemoryRead |
            SpeedNativeMethods.ProcessAccess.VirtualMemoryWrite,
            false,
            processId);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        throw new Win32Exception(Marshal.GetLastWin32Error(),
            "无法为目标进程启用游戏加速。可以尝试以管理员身份运行本应用。");
    }

    private void InstallStableHooks(
        SafeProcessHandle handle,
        int processId,
        IReadOnlyList<ClockImport> imports,
        ICollection<PatchedSlot> newlyPatched)
    {
        SpeedNativeMethods.QueryPerformanceCounter(out var nowQpc);
        var nowTick64 = SpeedNativeMethods.GetTickCount64();
        var nowTick32 = SpeedNativeMethods.GetTickCount();
        var originalQpc = imports.FirstOrDefault(import => import.Kind == ClockKind.QueryPerformanceCounter)?.OriginalPointer ?? 0;
        var originalTick64 = imports.FirstOrDefault(import => import.Kind == ClockKind.GetTickCount64)?.OriginalPointer ?? 0;
        var originalTick32 = imports.FirstOrDefault(import => import.Kind == ClockKind.GetTickCount)?.OriginalPointer
                             ?? imports.FirstOrDefault(import => import.Kind == ClockKind.TimeGetTime)?.OriginalPointer
                             ?? 0;

        var state = BuildInitialState(nowQpc, nowQpc, nowTick64, nowTick64, nowTick32, nowTick32,
            originalQpc, originalTick64, originalTick32);
        var stateAddress = AllocateRemote(handle, state, SpeedNativeMethods.PageReadWrite);
        var updaterAddress = AllocateExecutable(handle,
            BuildUpdater(stateAddress, originalQpc, originalTick64, originalTick32));

        var wrappers = new Dictionary<(ClockKind Kind, ulong Original), ulong>();
        foreach (var import in imports)
        {
            var key = (import.Kind, import.OriginalPointer);
            if (!wrappers.TryGetValue(key, out var wrapperAddress))
            {
                wrapperAddress = AllocateExecutable(handle,
                    BuildStableWrapper(import.Kind, import.OriginalPointer, stateAddress, updaterAddress));
                wrappers[key] = wrapperAddress;
            }
            WritePointer(handle, import.SlotAddress, wrapperAddress);
            newlyPatched.Add(new PatchedSlot(import.Kind, import.SlotAddress, import.OriginalPointer, import.CurrentPointer));
        }

        AdoptHooks(handle, processId, imports, stateAddress, updaterAddress, wrappers.Count, 1);
    }

    private void AttachExistingStableHooks(
        SafeProcessHandle handle,
        int processId,
        IReadOnlyList<ClockImport> imports,
        IReadOnlyList<WrapperState> stableWrappers,
        ICollection<PatchedSlot> newlyPatched)
    {
        var stateGroups = stableWrappers
            .GroupBy(wrapper => (wrapper.StateAddress, wrapper.UpdaterAddress))
            .OrderByDescending(group => group.Count())
            .ToList();
        if (stateGroups.Count != 1)
            throw new InvalidOperationException("目标游戏中存在多套不一致的加速状态，请退出并重新启动游戏后再试。");

        var existing = stateGroups[0].First();
        var state = ReadStableState(handle, existing.StateAddress);
        var currentMultiplier = BitConverter.ToInt32(state, StateMultiplierOffset);
        if (currentMultiplier is < 1 or > 100)
            throw new InvalidOperationException("目标游戏中的加速状态已损坏，请重启游戏。");

        AdoptHooks(handle, processId, imports, existing.StateAddress, existing.UpdaterAddress,
            stableWrappers.Select(wrapper => (wrapper.Kind, wrapper.OriginalPointer)).Distinct().Count(), currentMultiplier);

        if (Multiplier != 1) SetMultiplierCore(1);

        var wrapperAddresses = imports
            .Where(import => import.PreviousWrapper is { Version: 2 } wrapper &&
                             wrapper.StateAddress == existing.StateAddress &&
                             wrapper.UpdaterAddress == existing.UpdaterAddress)
            .GroupBy(import => (import.Kind, import.OriginalPointer))
            .ToDictionary(group => group.Key, group => group.First().CurrentPointer);

        foreach (var import in imports)
        {
            if (import.PreviousWrapper is { Version: 2 } wrapper &&
                wrapper.StateAddress == existing.StateAddress && wrapper.UpdaterAddress == existing.UpdaterAddress)
                continue;

            var key = (import.Kind, import.OriginalPointer);
            if (!wrapperAddresses.TryGetValue(key, out var wrapperAddress))
            {
                wrapperAddress = AllocateExecutable(handle,
                    BuildStableWrapper(import.Kind, import.OriginalPointer, existing.StateAddress, existing.UpdaterAddress));
                wrapperAddresses[key] = wrapperAddress;
            }
            WritePointer(handle, import.SlotAddress, wrapperAddress);
            newlyPatched.Add(new PatchedSlot(import.Kind, import.SlotAddress, import.OriginalPointer, import.CurrentPointer));
        }
        _wrapperCount = wrapperAddresses.Count;
    }

    private void AdoptHooks(
        SafeProcessHandle handle,
        int processId,
        IEnumerable<ClockImport> imports,
        ulong stateAddress,
        ulong updaterAddress,
        int wrapperCount,
        int multiplier)
    {
        _patchedSlots.Clear();
        _patchedSlots.AddRange(imports.Select(import =>
            new PatchedSlot(import.Kind, import.SlotAddress, import.OriginalPointer, import.CurrentPointer)));
        _processHandle = handle;
        _stateAddress = stateAddress;
        _updaterAddress = updaterAddress;
        _wrapperCount = wrapperCount;
        ActiveProcessId = processId;
        Multiplier = multiplier;
    }

    private void SetMultiplierCore(int multiplier)
    {
        var handle = _processHandle ?? throw new InvalidOperationException("游戏加速状态已经失效。");
        if (_updaterAddress == 0) throw new InvalidOperationException("目标进程内的倍速更新函数已失效。");
        if (Multiplier == multiplier) return;

        var thread = SpeedNativeMethods.CreateRemoteThread(handle, IntPtr.Zero, 0,
            unchecked((IntPtr)(long)_updaterAddress), new IntPtr(multiplier), 0, out _);
        if (thread == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法在目标进程内切换游戏倍速。");

        try
        {
            var wait = SpeedNativeMethods.WaitForSingleObject(thread, RemoteUpdateTimeoutMilliseconds);
            if (wait == SpeedNativeMethods.WaitTimeout)
            {
                var state = ReadStableState(handle, _stateAddress);
                throw new TimeoutException(
                    $"目标进程在 {RemoteUpdateTimeoutMilliseconds} 毫秒内没有完成倍速切换，已停止等待以避免编辑器卡死。" +
                    $" 锁状态 QPC={BitConverter.ToInt32(state, 0)}, Tick={BitConverter.ToInt32(state, 4)}。");
            }
            if (wait == SpeedNativeMethods.WaitFailed)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "等待目标进程切换倍速时失败。");
            if (wait != SpeedNativeMethods.WaitObject0)
                throw new InvalidOperationException($"目标进程返回了未知的倍速切换状态：0x{wait:X8}。");
            if (!SpeedNativeMethods.GetExitCodeThread(thread, out var exitCode))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取目标进程的倍速切换结果。");
            if (exitCode != 0)
                throw new InvalidOperationException($"目标进程内的倍速更新函数失败，代码 {exitCode}。");
        }
        finally
        {
            _ = SpeedNativeMethods.CloseHandle(thread);
        }
        Multiplier = multiplier;
    }

    private bool IsActiveProcessRunning()
    {
        if (ActiveProcessId == 0) return false;
        try
        {
            using var process = Process.GetProcessById(ActiveProcessId);
            return !process.HasExited;
        }
        catch { return false; }
    }

    private static void RestorePatchedSlots(SafeProcessHandle handle, IEnumerable<PatchedSlot> patches)
    {
        foreach (var patch in patches.Reverse())
        {
            try { WritePointer(handle, patch.SlotAddress, patch.PreviousPointer); }
            catch { }
        }
    }

    private static List<ClockImport> FindClockImports(SafeProcessHandle handle, Process process)
    {
        var result = new List<ClockImport>();
        foreach (ProcessModule module in process.Modules)
        {
            if (IsWindowsSystemModule(module)) continue;
            try { FindClockImportsInModule(handle, unchecked((ulong)module.BaseAddress.ToInt64()), result); }
            catch { }
        }
        return result.GroupBy(item => item.SlotAddress).Select(group => group.First()).ToList();
    }

    private static bool IsWindowsSystemModule(ProcessModule module)
    {
        try
        {
            var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(module.FileName).StartsWith(windowsDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // If the module path is inaccessible, skip it instead of risking a hook inside a system component.
            return true;
        }
    }

    private static void FindClockImportsInModule(SafeProcessHandle handle, ulong moduleBase, ICollection<ClockImport> result)
    {
        var dos = ReadBytes(handle, moduleBase, 0x40);
        if (BitConverter.ToUInt16(dos, 0) != 0x5A4D) return;
        var peOffset = BitConverter.ToInt32(dos, 0x3C);
        if (peOffset is < 0x40 or > 0x100000) return;
        var header = ReadBytes(handle, moduleBase + (ulong)peOffset, 0x200);
        if (BitConverter.ToUInt32(header, 0) != 0x00004550) return;
        const int optionalHeader = 24;
        if (BitConverter.ToUInt16(header, optionalHeader) != 0x20B) return;
        var importRva = BitConverter.ToUInt32(header, optionalHeader + 120);
        var importSize = BitConverter.ToUInt32(header, optionalHeader + 124);
        if (importRva == 0 || importSize == 0) return;

        var descriptorAddress = moduleBase + importRva;
        for (var descriptorIndex = 0; descriptorIndex < 4096; descriptorIndex++)
        {
            var descriptor = ReadBytes(handle, descriptorAddress + (ulong)(descriptorIndex * 20), 20);
            var originalFirstThunk = BitConverter.ToUInt32(descriptor, 0);
            var nameRva = BitConverter.ToUInt32(descriptor, 12);
            var firstThunk = BitConverter.ToUInt32(descriptor, 16);
            if (originalFirstThunk == 0 && nameRva == 0 && firstThunk == 0) break;
            if (originalFirstThunk == 0 || firstThunk == 0) continue;

            for (var thunkIndex = 0; thunkIndex < 65536; thunkIndex++)
            {
                var lookup = ReadUInt64(handle, moduleBase + originalFirstThunk + (ulong)(thunkIndex * 8));
                if (lookup == 0) break;
                if ((lookup & 0x8000000000000000UL) != 0) continue;
                var functionName = ReadAscii(handle, moduleBase + (lookup & 0x7FFFFFFFUL) + 2, 128);
                if (!TryGetClockKind(functionName, out var kind)) continue;
                var slot = moduleBase + firstThunk + (ulong)(thunkIndex * 8);
                var current = ReadUInt64(handle, slot);
                if (current == 0) continue;
                if (TryReadWrapperMetadata(handle, current, kind, out var wrapper))
                    result.Add(new ClockImport(kind, slot, wrapper.OriginalPointer, current, wrapper));
                else
                    result.Add(new ClockImport(kind, slot, current, current, null));
            }
        }
    }

    private static bool TryGetClockKind(string name, out ClockKind kind)
    {
        kind = name switch
        {
            "QueryPerformanceCounter" => ClockKind.QueryPerformanceCounter,
            "GetTickCount64" => ClockKind.GetTickCount64,
            "GetTickCount" => ClockKind.GetTickCount,
            "timeGetTime" => ClockKind.TimeGetTime,
            _ => ClockKind.None
        };
        return kind != ClockKind.None;
    }

    private static bool TryReadWrapperMetadata(SafeProcessHandle handle, ulong wrapperAddress, ClockKind expectedKind, out WrapperState state)
    {
        state = default!;
        try
        {
            var bytes = ReadBytes(handle, wrapperAddress, 256);
            var stableOffset = FindSequence(bytes, StableWrapperMagic);
            if (stableOffset >= 0 && stableOffset + 33 <= bytes.Length)
            {
                var kind = (ClockKind)bytes[stableOffset + 8];
                if (kind != expectedKind) return false;
                var original = BitConverter.ToUInt64(bytes, stableOffset + 9);
                var stateAddress = BitConverter.ToUInt64(bytes, stableOffset + 17);
                var updaterAddress = BitConverter.ToUInt64(bytes, stableOffset + 25);
                var stableState = ReadStableState(handle, stateAddress);
                var multiplier = BitConverter.ToInt32(stableState, StateMultiplierOffset);
                if (original == 0 || stateAddress == 0 || updaterAddress == 0 || multiplier is < 1 or > 100)
                    return false;
                state = new WrapperState(2, kind, original,
                    BitConverter.ToUInt64(stableState, 16), BitConverter.ToUInt64(stableState, 24),
                    multiplier, stateAddress, updaterAddress);
                return true;
            }

            var legacyOffset = FindSequence(bytes, LegacyWrapperMagic);
            if (legacyOffset < 0 || legacyOffset + 37 > bytes.Length) return false;
            var legacyKind = (ClockKind)bytes[legacyOffset + 8];
            if (legacyKind != expectedKind) return false;
            var legacyOriginal = BitConverter.ToUInt64(bytes, legacyOffset + 9);
            var baseReal = BitConverter.ToUInt64(bytes, legacyOffset + 17);
            var baseVirtual = BitConverter.ToUInt64(bytes, legacyOffset + 25);
            var legacyMultiplier = BitConverter.ToInt32(bytes, legacyOffset + 33);
            if (legacyOriginal == 0 || legacyMultiplier is < 1 or > 100) return false;
            state = new WrapperState(1, legacyKind, legacyOriginal, baseReal, baseVirtual,
                legacyMultiplier, 0, 0);
            return true;
        }
        catch { return false; }
    }

    private static byte[] ReadStableState(SafeProcessHandle handle, ulong stateAddress)
    {
        var state = ReadBytes(handle, stateAddress, StateSize);
        if (!state.AsSpan(80, StateMagic.Length).SequenceEqual(StateMagic))
            throw new InvalidOperationException("目标进程内的倍速状态标记无效。");
        return state;
    }

    private static int FindSequence(ReadOnlySpan<byte> source, ReadOnlySpan<byte> value)
    {
        for (var index = 0; index <= source.Length - value.Length; index++)
            if (source.Slice(index, value.Length).SequenceEqual(value)) return index;
        return -1;
    }

    private static byte[] BuildInitialState(long baseRealQpc, long baseVirtualQpc,
        ulong baseRealTick64, ulong baseVirtualTick64, uint baseRealTick32, uint baseVirtualTick32,
        ulong originalQpc, ulong originalTick64, ulong originalTick32)
    {
        var state = new byte[StateSize];
        BitConverter.GetBytes(1).CopyTo(state, StateMultiplierOffset);
        BitConverter.GetBytes(baseRealQpc).CopyTo(state, 16);
        BitConverter.GetBytes(baseVirtualQpc).CopyTo(state, 24);
        BitConverter.GetBytes(baseRealTick64).CopyTo(state, 32);
        BitConverter.GetBytes(baseVirtualTick64).CopyTo(state, 40);
        BitConverter.GetBytes(baseRealTick32).CopyTo(state, 48);
        BitConverter.GetBytes(baseVirtualTick32).CopyTo(state, 52);
        BitConverter.GetBytes(originalQpc).CopyTo(state, 56);
        BitConverter.GetBytes(originalTick64).CopyTo(state, 64);
        BitConverter.GetBytes(originalTick32).CopyTo(state, 72);
        StateMagic.CopyTo(state, 80);
        return state;
    }

    private static byte[] BuildStableWrapper(ClockKind kind, ulong original, ulong stateAddress, ulong updaterAddress) => kind switch
    {
        ClockKind.QueryPerformanceCounter => BuildStableQpcWrapper(original, stateAddress, updaterAddress),
        ClockKind.GetTickCount64 => BuildStableTick64Wrapper(original, stateAddress, updaterAddress),
        ClockKind.GetTickCount or ClockKind.TimeGetTime => BuildStableTick32Wrapper(kind, original, stateAddress, updaterAddress),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static byte[] BuildStableQpcWrapper(ulong original, ulong stateAddress, ulong updaterAddress)
    {
        var code = new X64Emitter();
        code.Emit(0x48, 0x83, 0xEC, 0x38);
        code.Emit(0x48, 0x89, 0x4C, 0x24, 0x30);
        EmitAcquireLock(code, stateAddress, 0, 88);
        code.Emit(0x48, 0x8B, 0x4C, 0x24, 0x30);
        EmitMovRax(code, original);
        code.Emit(0xFF, 0xD0, 0x85, 0xC0);
        var failed = code.EmitJz();
        code.Emit(0x48, 0x8B, 0x54, 0x24, 0x30, 0x48, 0x8B, 0x02);
        EmitMovR11(code, stateAddress);
        code.Emit(0x49, 0x2B, 0x43, 0x10);
        code.Emit(0x49, 0x63, 0x4B, 0x08);
        code.Emit(0x48, 0x0F, 0xAF, 0xC1);
        code.Emit(0x49, 0x03, 0x43, 0x18);
        code.Emit(0x48, 0x89, 0x02);
        EmitReleaseLock(code, 0, 88);
        code.Emit(0xB8, 0x01, 0, 0, 0);
        code.Emit(0x48, 0x83, 0xC4, 0x38, 0xC3);
        var failedTarget = code.Position;
        EmitMovR11(code, stateAddress);
        EmitReleaseLock(code, 0, 88);
        code.Emit(0x31, 0xC0, 0x48, 0x83, 0xC4, 0x38, 0xC3);
        code.PatchRelative(failed, failedTarget);
        AppendStableMetadata(code, ClockKind.QueryPerformanceCounter, original, stateAddress, updaterAddress);
        return code.ToArray();
    }

    private static byte[] BuildStableTick64Wrapper(ulong original, ulong stateAddress, ulong updaterAddress)
    {
        var code = new X64Emitter();
        code.Emit(0x48, 0x83, 0xEC, 0x28);
        EmitAcquireLock(code, stateAddress, 4, 92);
        EmitMovRax(code, original);
        code.Emit(0xFF, 0xD0);
        EmitMovR11(code, stateAddress);
        code.Emit(0x49, 0x2B, 0x43, 0x20);
        code.Emit(0x49, 0x63, 0x4B, 0x08);
        code.Emit(0x48, 0x0F, 0xAF, 0xC1);
        code.Emit(0x49, 0x03, 0x43, 0x28);
        EmitReleaseLock(code, 4, 92);
        code.Emit(0x48, 0x83, 0xC4, 0x28, 0xC3);
        AppendStableMetadata(code, ClockKind.GetTickCount64, original, stateAddress, updaterAddress);
        return code.ToArray();
    }

    private static byte[] BuildStableTick32Wrapper(ClockKind kind, ulong original, ulong stateAddress, ulong updaterAddress)
    {
        var code = new X64Emitter();
        code.Emit(0x48, 0x83, 0xEC, 0x28);
        EmitAcquireLock(code, stateAddress, 4, 92);
        EmitMovRax(code, original);
        code.Emit(0xFF, 0xD0);
        EmitMovR11(code, stateAddress);
        code.Emit(0x41, 0x2B, 0x43, 0x30);
        code.Emit(0x41, 0x0F, 0xAF, 0x43, 0x08);
        code.Emit(0x41, 0x03, 0x43, 0x34);
        EmitReleaseLock(code, 4, 92);
        code.Emit(0x48, 0x83, 0xC4, 0x28, 0xC3);
        AppendStableMetadata(code, kind, original, stateAddress, updaterAddress);
        return code.ToArray();
    }

    private static byte[] BuildUpdater(ulong stateAddress, ulong originalQpc, ulong originalTick64, ulong originalTick32)
    {
        var code = new X64Emitter();
        code.Emit(0x48, 0x83, 0xEC, 0x58);
        code.Emit(0x89, 0x4C, 0x24, 0x50);
        EmitAcquireLock(code, stateAddress, 0, 88);
        EmitAcquireLock(code, stateAddress, 4, 92);

        if (originalQpc != 0)
        {
            EmitMovRax(code, originalQpc);
            code.Emit(0x48, 0x8D, 0x4C, 0x24, 0x48);
            code.Emit(0xFF, 0xD0, 0x85, 0xC0);
            var skipQpc = code.EmitJz();
            code.Emit(0x48, 0x8B, 0x44, 0x24, 0x48);
            EmitMovR11(code, stateAddress);
            code.Emit(0x48, 0x89, 0xC2);
            code.Emit(0x49, 0x2B, 0x53, 0x10);
            code.Emit(0x49, 0x63, 0x4B, 0x08);
            code.Emit(0x48, 0x0F, 0xAF, 0xD1);
            code.Emit(0x49, 0x03, 0x53, 0x18);
            code.Emit(0x49, 0x89, 0x43, 0x10);
            code.Emit(0x49, 0x89, 0x53, 0x18);
            code.PatchRelative(skipQpc, code.Position);
        }

        if (originalTick64 != 0)
        {
            EmitMovRax(code, originalTick64);
            code.Emit(0xFF, 0xD0);
            EmitMovR11(code, stateAddress);
            code.Emit(0x48, 0x89, 0xC2);
            code.Emit(0x49, 0x2B, 0x53, 0x20);
            code.Emit(0x49, 0x63, 0x4B, 0x08);
            code.Emit(0x48, 0x0F, 0xAF, 0xD1);
            code.Emit(0x49, 0x03, 0x53, 0x28);
            code.Emit(0x49, 0x89, 0x43, 0x20);
            code.Emit(0x49, 0x89, 0x53, 0x28);
        }

        if (originalTick32 != 0)
        {
            EmitMovRax(code, originalTick32);
            code.Emit(0xFF, 0xD0);
            EmitMovR11(code, stateAddress);
            code.Emit(0x89, 0xC2);
            code.Emit(0x41, 0x2B, 0x53, 0x30);
            code.Emit(0x41, 0x0F, 0xAF, 0x53, 0x08);
            code.Emit(0x41, 0x03, 0x53, 0x34);
            code.Emit(0x41, 0x89, 0x43, 0x30);
            code.Emit(0x41, 0x89, 0x53, 0x34);
        }

        EmitMovR11(code, stateAddress);
        code.Emit(0x8B, 0x44, 0x24, 0x50);
        code.Emit(0x41, 0x89, 0x43, 0x08);
        EmitReleaseLock(code, 4, 92);
        EmitReleaseLock(code, 0, 88);
        code.Emit(0x31, 0xC0, 0x48, 0x83, 0xC4, 0x58, 0xC3);
        return code.ToArray();
    }

    private static void EmitAcquireLock(X64Emitter code, ulong stateAddress, byte countOffset, byte ownerOffset)
    {
        EmitMovR11(code, stateAddress);
        code.Emit(0x65, 0x8B, 0x14, 0x25, 0x48, 0, 0, 0);        // mov edx,gs:[48h] (thread id)
        code.Emit(0x41, 0x39, 0x53, ownerOffset);                  // cmp [r11+owner],edx
        var notRecursive = code.EmitJnz();
        code.Emit(0xF0, 0x41, 0xFF, 0x43, countOffset);            // lock inc [r11+count]
        var recursiveDone = code.EmitJmp();
        code.PatchRelative(notRecursive, code.Position);
        code.Emit(0xB9, 0x01, 0, 0, 0);
        var retry = code.Position;
        code.Emit(0x31, 0xC0);
        code.Emit(0xF0, 0x41, 0x0F, 0xB1, 0x4B, countOffset);
        var acquired = code.EmitJz();
        code.Emit(0xF3, 0x90);
        var retryJump = code.EmitJmp();
        code.PatchRelative(retryJump, retry);
        var acquiredTarget = code.Position;
        code.Emit(0x41, 0x89, 0x53, ownerOffset);                  // mov [r11+owner],edx
        code.PatchRelative(acquired, acquiredTarget);
        code.PatchRelative(recursiveDone, code.Position);
    }

    private static void EmitReleaseLock(X64Emitter code, byte countOffset, byte ownerOffset)
    {
        code.Emit(0xF0, 0x41, 0xFF, 0x4B, countOffset);            // lock dec [r11+count]
        var stillHeld = code.EmitJnz();
        code.Emit(0x41, 0xC7, 0x43, ownerOffset, 0, 0, 0, 0);     // mov [r11+owner],0
        code.PatchRelative(stillHeld, code.Position);
    }

    private static void EmitMovRax(X64Emitter code, ulong value)
    {
        code.Emit(0x48, 0xB8);
        code.Emit(BitConverter.GetBytes(value));
    }

    private static void EmitMovR11(X64Emitter code, ulong value)
    {
        code.Emit(0x49, 0xBB);
        code.Emit(BitConverter.GetBytes(value));
    }

    private static void AppendStableMetadata(X64Emitter code, ClockKind kind, ulong original, ulong stateAddress, ulong updaterAddress)
    {
        code.Emit(StableWrapperMagic);
        code.Emit((byte)kind);
        code.Emit(BitConverter.GetBytes(original));
        code.Emit(BitConverter.GetBytes(stateAddress));
        code.Emit(BitConverter.GetBytes(updaterAddress));
    }

    private static ulong AllocateExecutable(SafeProcessHandle handle, byte[] code) =>
        AllocateRemote(handle, code, SpeedNativeMethods.PageExecuteRead);

    private static ulong AllocateRemote(SafeProcessHandle handle, byte[] bytes, uint finalProtection)
    {
        var address = SpeedNativeMethods.VirtualAllocEx(handle, IntPtr.Zero, (nuint)bytes.Length,
            SpeedNativeMethods.MemCommit | SpeedNativeMethods.MemReserve, SpeedNativeMethods.PageReadWrite);
        if (address == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法在目标进程中建立加速数据。");
        if (!SpeedNativeMethods.WriteProcessMemory(handle, address, bytes, (nuint)bytes.Length, out var written) || written != (nuint)bytes.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法写入目标进程的加速数据。");
        if (finalProtection != SpeedNativeMethods.PageReadWrite &&
            !SpeedNativeMethods.VirtualProtectEx(handle, address, (nuint)bytes.Length, finalProtection, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启用目标进程的加速代码。");
        _ = SpeedNativeMethods.FlushInstructionCache(handle, address, (nuint)bytes.Length);
        return unchecked((ulong)address.ToInt64());
    }

    private static void WritePointer(SafeProcessHandle handle, ulong address, ulong value)
    {
        var pointer = BitConverter.GetBytes(value);
        var nativeAddress = unchecked((IntPtr)(long)address);
        if (!SpeedNativeMethods.VirtualProtectEx(handle, nativeAddress, 8, SpeedNativeMethods.PageReadWrite, out var oldProtection))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法解锁目标进程的计时入口。");
        try
        {
            if (!SpeedNativeMethods.WriteProcessMemory(handle, nativeAddress, pointer, 8, out var written) || written != 8)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法更新目标进程的计时入口。");
        }
        finally
        {
            _ = SpeedNativeMethods.VirtualProtectEx(handle, nativeAddress, 8, oldProtection, out _);
        }
        _ = SpeedNativeMethods.FlushInstructionCache(handle, nativeAddress, 8);
    }

    private static byte[] ReadBytes(SafeProcessHandle handle, ulong address, int length)
    {
        var bytes = new byte[length];
        if (!SpeedNativeMethods.ReadProcessMemory(handle, unchecked((IntPtr)(long)address), bytes, (nuint)length, out var read) || read != (nuint)length)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return bytes;
    }

    private static ulong ReadUInt64(SafeProcessHandle handle, ulong address) => BitConverter.ToUInt64(ReadBytes(handle, address, 8));

    private static string ReadAscii(SafeProcessHandle handle, ulong address, int maximumLength)
    {
        var bytes = ReadBytes(handle, address, maximumLength);
        var length = Array.IndexOf(bytes, (byte)0);
        if (length < 0) length = bytes.Length;
        return System.Text.Encoding.ASCII.GetString(bytes, 0, length);
    }

    private enum ClockKind { None, QueryPerformanceCounter, GetTickCount64, GetTickCount, TimeGetTime }
    private sealed record ClockImport(ClockKind Kind, ulong SlotAddress, ulong OriginalPointer, ulong CurrentPointer, WrapperState? PreviousWrapper);
    private sealed record WrapperState(int Version, ClockKind Kind, ulong OriginalPointer, ulong BaseReal, ulong BaseVirtual,
        int Multiplier, ulong StateAddress, ulong UpdaterAddress);
    private sealed record PatchedSlot(ClockKind Kind, ulong SlotAddress, ulong OriginalPointer, ulong PreviousPointer);

    private sealed class X64Emitter
    {
        private readonly List<byte> _bytes = [];
        public int Position => _bytes.Count;
        public void Emit(params byte[] bytes) => _bytes.AddRange(bytes);

        public int EmitJz()
        {
            Emit(0x0F, 0x84, 0, 0, 0, 0);
            return Position - 4;
        }

        public int EmitJnz()
        {
            Emit(0x0F, 0x85, 0, 0, 0, 0);
            return Position - 4;
        }

        public int EmitJmp()
        {
            Emit(0xE9, 0, 0, 0, 0);
            return Position - 4;
        }

        public void PatchRelative(int displacementOffset, int target)
        {
            var displacement = target - (displacementOffset + 4);
            var bytes = BitConverter.GetBytes(displacement);
            for (var index = 0; index < bytes.Length; index++) _bytes[displacementOffset + index] = bytes[index];
        }

        public byte[] ToArray() => _bytes.ToArray();
    }
}

public sealed record SpeedHookResult(int PatchedImportCount, int WrapperCount);

internal static class SpeedNativeMethods
{
    [Flags]
    internal enum ProcessAccess : uint
    {
        CreateThread = 0x0002,
        VirtualMemoryOperation = 0x0008,
        VirtualMemoryRead = 0x0010,
        VirtualMemoryWrite = 0x0020,
        QueryInformation = 0x0400
    }

    internal const uint MemCommit = 0x1000;
    internal const uint MemReserve = 0x2000;
    internal const uint PageReadWrite = 0x04;
    internal const uint PageExecuteRead = 0x20;
    internal const uint WaitObject0 = 0x00000000;
    internal const uint WaitTimeout = 0x00000102;
    internal const uint WaitFailed = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(ProcessAccess access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr VirtualAllocEx(SafeProcessHandle process, IntPtr address, nuint size, uint allocationType, uint protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool VirtualProtectEx(SafeProcessHandle process, IntPtr address, nuint size, uint newProtection, out uint oldProtection);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadProcessMemory(SafeProcessHandle process, IntPtr address, [Out] byte[] buffer, nuint size, out nuint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteProcessMemory(SafeProcessHandle process, IntPtr address, byte[] buffer, nuint size, out nuint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FlushInstructionCache(SafeProcessHandle process, IntPtr address, nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateRemoteThread(SafeProcessHandle process, IntPtr threadAttributes, nuint stackSize,
        IntPtr startAddress, IntPtr parameter, uint creationFlags, out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryPerformanceCounter(out long value);

    [DllImport("kernel32.dll")]
    internal static extern ulong GetTickCount64();

    [DllImport("kernel32.dll")]
    internal static extern uint GetTickCount();
}
