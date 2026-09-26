using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GameValueEditor.Services;

/// <summary>
/// Speeds up an attached x64 process by replacing its imported clock functions with
/// small per-process wrappers. Only import-table pointers are changed; game files and
/// save data are never touched. Clock transitions are applied while the target process
/// is briefly suspended so its threads can never observe a mixture of old and new clocks.
/// </summary>
public sealed class ProcessSpeedService : IDisposable
{
    private static readonly byte[] WrapperMetadataMagic = "GVESPD01"u8.ToArray();
    private readonly List<PatchedSlot> _patchedSlots = [];
    private SafeProcessHandle? _processHandle;
    private long _baseRealQpc;
    private long _baseVirtualQpc;
    private ulong _baseRealTick64;
    private ulong _baseVirtualTick64;
    private uint _baseRealTick32;
    private uint _baseVirtualTick32;

    public bool HasHooks => _processHandle is { IsInvalid: false, IsClosed: false } && _patchedSlots.Count > 0;
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
            if (Multiplier == multiplier)
                return new SpeedHookResult(_patchedSlots.Count,
                    _patchedSlots.Select(patch => (patch.Kind, patch.OriginalPointer)).Distinct().Count());
            DetachSafely();
        }

        DetachSafely();
        var handle = SpeedNativeMethods.OpenProcess(
            SpeedNativeMethods.ProcessAccess.QueryInformation |
            SpeedNativeMethods.ProcessAccess.SuspendResume |
            SpeedNativeMethods.ProcessAccess.VirtualMemoryOperation |
            SpeedNativeMethods.ProcessAccess.VirtualMemoryRead |
            SpeedNativeMethods.ProcessAccess.VirtualMemoryWrite,
            false,
            processId);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法为目标进程启用游戏加速。可以尝试以管理员身份运行本应用。");
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var imports = FindClockImports(handle, process);
            if (imports.Count == 0)
                throw new InvalidOperationException("目标进程没有找到可挂接的计时 API，暂时无法使用通用游戏加速。");

            var wrappers = new Dictionary<(ClockKind Kind, ulong Original), ulong>();
            using (TargetProcessSuspension.Acquire(handle))
            {
                SpeedNativeMethods.QueryPerformanceCounter(out _baseRealQpc);
                _baseRealTick64 = SpeedNativeMethods.GetTickCount64();
                _baseRealTick32 = SpeedNativeMethods.GetTickCount();
                _baseVirtualQpc = ContinueQpc(imports, _baseRealQpc);
                _baseVirtualTick64 = ContinueTick64(imports, _baseRealTick64);
                _baseVirtualTick32 = ContinueTick32(imports, _baseRealTick32);

                try
                {
                    foreach (var import in imports)
                    {
                        var key = (import.Kind, import.OriginalPointer);
                        if (!wrappers.TryGetValue(key, out var wrapperAddress))
                        {
                            var code = import.Kind switch
                            {
                                ClockKind.QueryPerformanceCounter => BuildQpcWrapper(import.OriginalPointer, _baseRealQpc, _baseVirtualQpc, multiplier),
                                ClockKind.GetTickCount64 => BuildTick64Wrapper(import.OriginalPointer, _baseRealTick64, _baseVirtualTick64, multiplier),
                                ClockKind.GetTickCount or ClockKind.TimeGetTime =>
                                    BuildTick32Wrapper(import.Kind, import.OriginalPointer, _baseRealTick32, _baseVirtualTick32, multiplier),
                                _ => throw new ArgumentOutOfRangeException()
                            };
                            wrapperAddress = AllocateExecutable(handle, code);
                            wrappers[key] = wrapperAddress;
                        }

                        WritePointer(handle, import.SlotAddress, wrapperAddress);
                        _patchedSlots.Add(new PatchedSlot(import.Kind, import.SlotAddress, import.OriginalPointer));
                    }
                }
                catch
                {
                    RestorePatchedSlots(handle);
                    throw;
                }
            }

            _processHandle = handle;
            ActiveProcessId = processId;
            Multiplier = multiplier;
            return new SpeedHookResult(_patchedSlots.Count, wrappers.Count);
        }
        catch
        {
            RestorePatchedSlots(handle);
            handle.Dispose();
            throw;
        }
    }

    public SpeedHookResult Normalize()
    {
        if (!HasHooks)
        {
            Multiplier = 1;
            return new SpeedHookResult(0, 0);
        }
        var result = new SpeedHookResult(_patchedSlots.Count,
            _patchedSlots.Select(patch => (patch.Kind, patch.OriginalPointer)).Distinct().Count());
        DetachSafely();
        return result;
    }

    public void DetachSafely()
    {
        var handle = _processHandle;
        if (handle is not null && !handle.IsInvalid && !handle.IsClosed && _patchedSlots.Count > 0)
        {
            try
            {
                if (Multiplier != 1) RepointWrappers(1);
            }
            catch (Exception exception) when (!IsActiveProcessRunning())
            {
                // The target already exited, so there is no clock state left to protect.
                Debug.WriteLine(exception);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "无法在关闭编辑器前把游戏安全回正。为避免游戏卡死，编辑器将保持打开；请先点击“回正”后再关闭。",
                    exception);
            }
        }

        _patchedSlots.Clear();
        handle?.Dispose();
        _processHandle = null;
        ActiveProcessId = 0;
        Multiplier = 1;
    }

    public void Restore() => DetachSafely();

    public void Dispose() => DetachSafely();

    private bool IsActiveProcessRunning()
    {
        if (ActiveProcessId == 0) return false;
        try
        {
            using var process = Process.GetProcessById(ActiveProcessId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private void RestorePatchedSlots(SafeProcessHandle handle)
    {
        foreach (var patch in _patchedSlots.AsEnumerable().Reverse())
        {
            try { WritePointer(handle, patch.SlotAddress, patch.OriginalPointer); }
            catch { /* The target may already have exited. */ }
        }
        _patchedSlots.Clear();
    }

    private SpeedHookResult RepointWrappers(int newMultiplier)
    {
        var handle = _processHandle ?? throw new InvalidOperationException("游戏加速状态已经失效。");
        var oldBaseRealQpc = _baseRealQpc;
        var oldBaseVirtualQpc = _baseVirtualQpc;
        var oldBaseRealTick64 = _baseRealTick64;
        var oldBaseVirtualTick64 = _baseVirtualTick64;
        var oldBaseRealTick32 = _baseRealTick32;
        var oldBaseVirtualTick32 = _baseVirtualTick32;
        var wrappers = new Dictionary<(ClockKind Kind, ulong Original), ulong>();
        using (TargetProcessSuspension.Acquire(handle))
        {
            var previousPointers = _patchedSlots.Select(patch => ReadUInt64(handle, patch.SlotAddress)).ToArray();
            SpeedNativeMethods.QueryPerformanceCounter(out var nowQpc);
            var nowTick64 = SpeedNativeMethods.GetTickCount64();
            var nowTick32 = SpeedNativeMethods.GetTickCount();
            _baseVirtualQpc = unchecked(_baseVirtualQpc + (nowQpc - _baseRealQpc) * Multiplier);
            _baseVirtualTick64 = unchecked(_baseVirtualTick64 + (nowTick64 - _baseRealTick64) * (ulong)Multiplier);
            _baseVirtualTick32 = unchecked(_baseVirtualTick32 + (nowTick32 - _baseRealTick32) * (uint)Multiplier);
            _baseRealQpc = nowQpc;
            _baseRealTick64 = nowTick64;
            _baseRealTick32 = nowTick32;

            var patchedCount = 0;
            try
            {
                foreach (var patch in _patchedSlots)
                {
                    var key = (patch.Kind, patch.OriginalPointer);
                    if (!wrappers.TryGetValue(key, out var wrapperAddress))
                    {
                        var code = patch.Kind switch
                        {
                            ClockKind.QueryPerformanceCounter => BuildQpcWrapper(patch.OriginalPointer, _baseRealQpc, _baseVirtualQpc, newMultiplier),
                            ClockKind.GetTickCount64 => BuildTick64Wrapper(patch.OriginalPointer, _baseRealTick64, _baseVirtualTick64, newMultiplier),
                            ClockKind.GetTickCount or ClockKind.TimeGetTime =>
                                BuildTick32Wrapper(patch.Kind, patch.OriginalPointer, _baseRealTick32, _baseVirtualTick32, newMultiplier),
                            _ => throw new ArgumentOutOfRangeException()
                        };
                        wrapperAddress = AllocateExecutable(handle, code);
                        wrappers[key] = wrapperAddress;
                    }
                    WritePointer(handle, patch.SlotAddress, wrapperAddress);
                    patchedCount++;
                }
            }
            catch
            {
                for (var index = patchedCount - 1; index >= 0; index--)
                {
                    try { WritePointer(handle, _patchedSlots[index].SlotAddress, previousPointers[index]); }
                    catch { }
                }
                _baseRealQpc = oldBaseRealQpc;
                _baseVirtualQpc = oldBaseVirtualQpc;
                _baseRealTick64 = oldBaseRealTick64;
                _baseVirtualTick64 = oldBaseVirtualTick64;
                _baseRealTick32 = oldBaseRealTick32;
                _baseVirtualTick32 = oldBaseVirtualTick32;
                throw;
            }
            Multiplier = newMultiplier;
        }
        return new SpeedHookResult(_patchedSlots.Count, wrappers.Count);
    }

    private sealed class TargetProcessSuspension : IDisposable
    {
        private readonly SafeProcessHandle _handle;
        private bool _mustResume;

        private TargetProcessSuspension(SafeProcessHandle handle)
        {
            _handle = handle;
            _mustResume = true;
        }

        public static TargetProcessSuspension Acquire(SafeProcessHandle handle)
        {
            var status = SpeedNativeMethods.NtSuspendProcess(handle);
            if (status != 0)
                throw new InvalidOperationException($"无法暂停目标进程以安全切换计时入口（NTSTATUS 0x{unchecked((uint)status):X8}）。");
            return new TargetProcessSuspension(handle);
        }

        public void Dispose()
        {
            if (!_mustResume) return;
            _mustResume = false;
            var status = SpeedNativeMethods.NtResumeProcess(_handle);
            if (status != 0)
                throw new InvalidOperationException($"目标进程的计时入口已切换，但恢复运行失败（NTSTATUS 0x{unchecked((uint)status):X8}）。");
        }
    }

    private static List<ClockImport> FindClockImports(SafeProcessHandle handle, Process process)
    {
        var result = new List<ClockImport>();
        foreach (ProcessModule module in process.Modules)
        {
            try { FindClockImportsInModule(handle, unchecked((ulong)module.BaseAddress.ToInt64()), result); }
            catch { /* Some protected or unloading modules cannot be inspected. */ }
        }
        return result
            .GroupBy(item => item.SlotAddress)
            .Select(group => group.First())
            .ToList();
    }

    private static void FindClockImportsInModule(SafeProcessHandle handle, ulong moduleBase, ICollection<ClockImport> result)
    {
        var dos = ReadBytes(handle, moduleBase, 0x40);
        if (BitConverter.ToUInt16(dos, 0) != 0x5A4D) return;
        var peOffset = BitConverter.ToInt32(dos, 0x3C);
        if (peOffset is < 0x40 or > 0x100000) return;
        var header = ReadBytes(handle, moduleBase + (ulong)peOffset, 0x200);
        if (BitConverter.ToUInt32(header, 0) != 0x00004550) return;
        var optionalHeader = 24;
        if (BitConverter.ToUInt16(header, optionalHeader) != 0x20B) return; // x64 only
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
                    result.Add(new ClockImport(kind, slot, wrapper.OriginalPointer, wrapper));
                else
                    result.Add(new ClockImport(kind, slot, current, null));
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

    private static long ContinueQpc(IEnumerable<ClockImport> imports, long now)
    {
        var wrapper = imports.FirstOrDefault(item => item.Kind == ClockKind.QueryPerformanceCounter && item.PreviousWrapper is not null)
            ?.PreviousWrapper;
        return wrapper is null
            ? now
            : unchecked((long)wrapper.BaseVirtual + (now - (long)wrapper.BaseReal) * wrapper.Multiplier);
    }

    private static ulong ContinueTick64(IEnumerable<ClockImport> imports, ulong now)
    {
        var wrapper = imports.FirstOrDefault(item => item.Kind == ClockKind.GetTickCount64 && item.PreviousWrapper is not null)
            ?.PreviousWrapper;
        return wrapper is null
            ? now
            : unchecked(wrapper.BaseVirtual + (now - wrapper.BaseReal) * (ulong)wrapper.Multiplier);
    }

    private static uint ContinueTick32(IEnumerable<ClockImport> imports, uint now)
    {
        var wrapper = imports.FirstOrDefault(item =>
                (item.Kind is ClockKind.GetTickCount or ClockKind.TimeGetTime) && item.PreviousWrapper is not null)
            ?.PreviousWrapper;
        return wrapper is null
            ? now
            : unchecked((uint)wrapper.BaseVirtual + (now - (uint)wrapper.BaseReal) * (uint)wrapper.Multiplier);
    }

    private static bool TryReadWrapperMetadata(
        SafeProcessHandle handle,
        ulong wrapperAddress,
        ClockKind expectedKind,
        out WrapperState state)
    {
        state = default!;
        try
        {
            var bytes = ReadBytes(handle, wrapperAddress, 160);
            var metadataOffset = FindSequence(bytes, WrapperMetadataMagic);
            if (metadataOffset < 0 || metadataOffset + 37 > bytes.Length) return false;
            var kind = (ClockKind)bytes[metadataOffset + 8];
            if (kind != expectedKind) return false;
            var original = BitConverter.ToUInt64(bytes, metadataOffset + 9);
            var baseReal = BitConverter.ToUInt64(bytes, metadataOffset + 17);
            var baseVirtual = BitConverter.ToUInt64(bytes, metadataOffset + 25);
            var multiplier = BitConverter.ToInt32(bytes, metadataOffset + 33);
            if (original == 0 || multiplier is < 1 or > 100) return false;
            state = new WrapperState(original, baseReal, baseVirtual, multiplier);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int FindSequence(ReadOnlySpan<byte> source, ReadOnlySpan<byte> value)
    {
        for (var index = 0; index <= source.Length - value.Length; index++)
            if (source.Slice(index, value.Length).SequenceEqual(value)) return index;
        return -1;
    }

    private static ulong AllocateExecutable(SafeProcessHandle handle, byte[] code)
    {
        var address = SpeedNativeMethods.VirtualAllocEx(handle, IntPtr.Zero, (nuint)code.Length,
            SpeedNativeMethods.MemCommit | SpeedNativeMethods.MemReserve, SpeedNativeMethods.PageReadWrite);
        if (address == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法在目标进程中建立加速代码。");
        if (!SpeedNativeMethods.WriteProcessMemory(handle, address, code, (nuint)code.Length, out var written) || written != (nuint)code.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法写入目标进程的加速代码。");
        if (!SpeedNativeMethods.VirtualProtectEx(handle, address, (nuint)code.Length, SpeedNativeMethods.PageExecuteRead, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启用目标进程的加速代码。");
        _ = SpeedNativeMethods.FlushInstructionCache(handle, address, (nuint)code.Length);
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

    private static ulong ReadUInt64(SafeProcessHandle handle, ulong address) =>
        BitConverter.ToUInt64(ReadBytes(handle, address, 8));

    private static string ReadAscii(SafeProcessHandle handle, ulong address, int maximumLength)
    {
        var bytes = ReadBytes(handle, address, maximumLength);
        var length = Array.IndexOf(bytes, (byte)0);
        if (length < 0) length = bytes.Length;
        return System.Text.Encoding.ASCII.GetString(bytes, 0, length);
    }

    private static byte[] BuildQpcWrapper(ulong original, long baseReal, long baseVirtual, int multiplier)
    {
        var code = new List<byte>();
        Add(code, 0x48, 0x83, 0xEC, 0x28);                         // sub rsp, 28h
        Add(code, 0x48, 0x89, 0x4C, 0x24, 0x20);                   // save output pointer
        AddMovRax(code, original);
        Add(code, 0xFF, 0xD0, 0x85, 0xC0, 0x74, 0x38);             // call; test; jz cleanup
        Add(code, 0x48, 0x8B, 0x54, 0x24, 0x20, 0x48, 0x8B, 0x02);
        AddMovRcx(code, unchecked((ulong)baseReal));
        Add(code, 0x48, 0x29, 0xC8);
        AddMovRcx(code, (ulong)multiplier);
        Add(code, 0x48, 0x0F, 0xAF, 0xC1);
        AddMovRcx(code, unchecked((ulong)baseVirtual));
        Add(code, 0x48, 0x01, 0xC8, 0x48, 0x89, 0x02, 0xB8, 0x01, 0, 0, 0);
        Add(code, 0x48, 0x83, 0xC4, 0x28, 0xC3);
        AppendWrapperMetadata(code, ClockKind.QueryPerformanceCounter, original,
            unchecked((ulong)baseReal), unchecked((ulong)baseVirtual), multiplier);
        return code.ToArray();
    }

    private static byte[] BuildTick64Wrapper(ulong original, ulong baseReal, ulong baseVirtual, int multiplier)
    {
        var code = new List<byte>();
        Add(code, 0x48, 0x83, 0xEC, 0x28);
        AddMovRax(code, original);
        Add(code, 0xFF, 0xD0);
        AddMovRcx(code, baseReal);
        Add(code, 0x48, 0x29, 0xC8);
        AddMovRcx(code, (ulong)multiplier);
        Add(code, 0x48, 0x0F, 0xAF, 0xC1);
        AddMovRcx(code, baseVirtual);
        Add(code, 0x48, 0x01, 0xC8, 0x48, 0x83, 0xC4, 0x28, 0xC3);
        AppendWrapperMetadata(code, ClockKind.GetTickCount64, original, baseReal, baseVirtual, multiplier);
        return code.ToArray();
    }

    private static byte[] BuildTick32Wrapper(ClockKind kind, ulong original, uint baseReal, uint baseVirtual, int multiplier)
    {
        var code = new List<byte>();
        Add(code, 0x48, 0x83, 0xEC, 0x28);
        AddMovRax(code, original);
        Add(code, 0xFF, 0xD0, 0xB9);
        code.AddRange(BitConverter.GetBytes(baseReal));
        Add(code, 0x29, 0xC8, 0x69, 0xC0);
        code.AddRange(BitConverter.GetBytes(multiplier));
        Add(code, 0x05);
        code.AddRange(BitConverter.GetBytes(baseVirtual));
        Add(code, 0x48, 0x83, 0xC4, 0x28, 0xC3);
        AppendWrapperMetadata(code, kind, original, baseReal, baseVirtual, multiplier);
        return code.ToArray();
    }

    private static void AppendWrapperMetadata(
        List<byte> code,
        ClockKind kind,
        ulong original,
        ulong baseReal,
        ulong baseVirtual,
        int multiplier)
    {
        code.AddRange(WrapperMetadataMagic);
        code.Add((byte)kind);
        code.AddRange(BitConverter.GetBytes(original));
        code.AddRange(BitConverter.GetBytes(baseReal));
        code.AddRange(BitConverter.GetBytes(baseVirtual));
        code.AddRange(BitConverter.GetBytes(multiplier));
    }

    private static void AddMovRax(List<byte> code, ulong value)
    {
        Add(code, 0x48, 0xB8);
        code.AddRange(BitConverter.GetBytes(value));
    }

    private static void AddMovRcx(List<byte> code, ulong value)
    {
        Add(code, 0x48, 0xB9);
        code.AddRange(BitConverter.GetBytes(value));
    }

    private static void Add(List<byte> code, params byte[] bytes) => code.AddRange(bytes);

    private enum ClockKind { None, QueryPerformanceCounter, GetTickCount64, GetTickCount, TimeGetTime }
    private sealed record ClockImport(ClockKind Kind, ulong SlotAddress, ulong OriginalPointer, WrapperState? PreviousWrapper);
    private sealed record WrapperState(ulong OriginalPointer, ulong BaseReal, ulong BaseVirtual, int Multiplier);
    private sealed record PatchedSlot(ClockKind Kind, ulong SlotAddress, ulong OriginalPointer);
}

public sealed record SpeedHookResult(int PatchedImportCount, int WrapperCount);

internal static class SpeedNativeMethods
{
    [Flags]
    internal enum ProcessAccess : uint
    {
        VirtualMemoryOperation = 0x0008,
        VirtualMemoryRead = 0x0010,
        VirtualMemoryWrite = 0x0020,
        SuspendResume = 0x0800,
        QueryInformation = 0x0400
    }

    internal const uint MemCommit = 0x1000;
    internal const uint MemReserve = 0x2000;
    internal const uint PageReadWrite = 0x04;
    internal const uint PageExecuteRead = 0x20;

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

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryPerformanceCounter(out long value);

    [DllImport("kernel32.dll")]
    internal static extern ulong GetTickCount64();

    [DllImport("kernel32.dll")]
    internal static extern uint GetTickCount();

    [DllImport("ntdll.dll")]
    internal static extern int NtSuspendProcess(SafeProcessHandle process);

    [DllImport("ntdll.dll")]
    internal static extern int NtResumeProcess(SafeProcessHandle process);
}
