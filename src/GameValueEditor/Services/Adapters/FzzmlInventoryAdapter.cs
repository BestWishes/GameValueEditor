using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using GameValueEditor.Models;

namespace GameValueEditor.Services.Adapters;

public sealed class FzzmlInventoryAdapter : IInventoryGameAdapter
{
    private static readonly ConcurrentDictionary<string, BuildLayout> LayoutCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyList<BuildLayout> SupportedBuilds =
    [
        new(
            "8B476C50395ACF8B4BD32E3DB60E29AC136436A5FADAB0E8D0049CA87CE3AACD",
            "CEC90FD2CCEEBD51637BFAC90C86C6E45D68CC76E6FDB0FAB1430F2A198D7421",
            "EB4D7A29675EC35CDE1106D6AE3C0E22BCA0AEF5C50248C2D42FA139077E446F",
            0x5E7660, 0x168, 0x35AE50, 0x5C87D0, 0x5A3DE0, 0x5BE980, 0x5B0710, 0x5C4070,
            0x17CF960, Convert.FromHexString("4883EC28803D051BA800007513")),
        new(
            "8B476C50395ACF8B4BD32E3DB60E29AC136436A5FADAB0E8D0049CA87CE3AACD",
            "EAE4239F59FD5DB09033C9FE21195A800881267B57833DA2AABB659843F99B27",
            "BCAA6845585DA9920F61F75A04FB70ACB6298D95C94FC545D0C4583C44C88947",
            0x602F90, 0x1A8, 0x35C0B0, 0x5E3360, 0x5BD450, 0x5D91B0, 0x5CA6B0, 0x5DEBD0,
            0x17DA730, Convert.FromHexString("4883EC28803DAD3BA800007513"))
    ];
    private static readonly object WriteGate = new();

    public string Id => "game.fzzml.inventory.v1";
    public string DisplayName => "放置斩魔录背包实时适配器";
    public string Description => "按物品名重新定位当前 IL2CPP 背包行，并在 Unity 主线程更新缓存和存档。";

    public bool Supports(ProcessItem process, VersionFingerprint fingerprint)
    {
        return SupportedBuilds.Any(layout => layout.Matches(
            fingerprint.Sha256,
            fingerprint.GameAssemblySha256,
            fingerprint.MetadataSha256));
    }

    public AdapterFieldValue ReadField(ProcessItem process, string fieldKey)
    {
        using var session = new Session(process.ProcessId, ResolveLayout(process));
        var inventory = session.FindInventoryRows(fieldKey);
        return new AdapterFieldValue(
            fieldKey,
            inventory.TotalCount.ToString(CultureInfo.InvariantCulture),
            $"实时定位可用 · {inventory.Rows.Count} 条后台记录");
    }

    public IReadOnlyList<AdapterInventoryItem> ReadInventory(ProcessItem process)
    {
        using var session = new Session(process.ProcessId, ResolveLayout(process));
        return session.ReadInventoryItems();
    }

    public AdapterFieldValue WriteField(ProcessItem process, string fieldKey, string displayValue)
    {
        if (!int.TryParse(displayValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var newValue) || newValue < 0)
            throw new InvalidOperationException("背包数量必须是 0 到 2147483647 之间的整数。");

        lock (WriteGate)
        {
            using var session = new Session(process.ProcessId, ResolveLayout(process));
            var updated = session.WriteInventoryTotal(fieldKey, newValue);
            return new AdapterFieldValue(
                fieldKey,
                updated.TotalCount.ToString(CultureInfo.InvariantCulture),
                $"已实时写入并保存 · {updated.Rows.Count} 条后台记录");
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static BuildLayout ResolveLayout(ProcessItem process)
    {
        try
        {
            var root = Path.GetDirectoryName(process.ExecutablePath) ?? string.Empty;
            var executableName = Path.GetFileNameWithoutExtension(process.ExecutablePath);
            var assembly = Path.Combine(root, "GameAssembly.dll");
            var metadata = Path.Combine(root, $"{executableName}_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
            var executableInfo = new FileInfo(process.ExecutablePath);
            var assemblyInfo = new FileInfo(assembly);
            var metadataInfo = new FileInfo(metadata);
            var cacheKey = $"{process.ExecutablePath}|{executableInfo.Length}:{executableInfo.LastWriteTimeUtc.Ticks}|" +
                           $"{assemblyInfo.Length}:{assemblyInfo.LastWriteTimeUtc.Ticks}|{metadataInfo.Length}:{metadataInfo.LastWriteTimeUtc.Ticks}";
            return LayoutCache.GetOrAdd(cacheKey, _ =>
            {
                var executableHash = ComputeSha256(process.ExecutablePath);
                var assemblyHash = ComputeSha256(assembly);
                var metadataHash = ComputeSha256(metadata);
                return SupportedBuilds.FirstOrDefault(layout => layout.Matches(executableHash, assemblyHash, metadataHash))
                       ?? throw new InvalidOperationException("当前 fzzml 构建尚未配置专属修改布局，请更新应用后重试。");
            });
        }
        catch (FileNotFoundException exception)
        {
            throw new InvalidOperationException("当前 fzzml 安装缺少专属修改所需的 IL2CPP 文件。", exception);
        }
    }

    private sealed class Session : IDisposable
    {
        private const uint ProcessAccess = 0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400;
        private const uint MemCommitReserve = 0x1000 | 0x2000;
        private const uint MemRelease = 0x8000;
        private const uint PageExecuteReadWrite = 0x40;
        private const uint Infinite = 0xFFFFFFFF;
        private readonly Process _process;
        private readonly IntPtr _handle;
        private readonly ulong _moduleBase;
        private readonly ulong _saveManager;
        private readonly BuildLayout _layout;
        private readonly ulong _domainGet;
        private readonly ulong _threadAttach;
        private readonly ulong _threadDetach;
        private readonly ulong _stringNew;
        private readonly ulong _writeBarrierSetField;

        public Session(int processId, BuildLayout layout)
        {
            _layout = layout;
            _process = Process.GetProcessById(processId);
            var module = _process.Modules.Cast<ProcessModule>()
                .SingleOrDefault(candidate => string.Equals(candidate.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("目标进程没有加载 GameAssembly.dll。");
            _moduleBase = unchecked((ulong)module.BaseAddress.ToInt64());
            _handle = OpenProcess(ProcessAccess, false, processId);
            if (_handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法连接 fzzml 进程。可以尝试以管理员身份运行本应用。");

            var exports = PortableExportResolver.Read(module.FileName);
            _domainGet = _moduleBase + exports.GetRequired("il2cpp_domain_get");
            _threadAttach = _moduleBase + exports.GetRequired("il2cpp_thread_attach");
            _threadDetach = _moduleBase + exports.GetRequired("il2cpp_thread_detach");
            _stringNew = _moduleBase + exports.GetRequired("il2cpp_string_new");
            _writeBarrierSetField = _moduleBase + exports.GetRequired("il2cpp_gc_wbarrier_set_field");

            _saveManager = CallPointerFunction(_moduleBase + _layout.SaveManagerGetInstance);
            if (_saveManager == 0) throw new InvalidOperationException("SaveManager.Instance 尚未就绪，请进入存档后重试。");
        }

        public InventoryMatch FindInventoryRows(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName)) throw new InvalidOperationException("游戏内物品名不能为空。");
            var snapshot = ReadInventoryRows();
            var matches = snapshot.Rows
                .Where(row => string.Equals(row.ItemName, itemName.Trim(), StringComparison.Ordinal))
                .ToList();

            if (matches.Count == 0)
                throw new InvalidOperationException($"当前背包中没有找到“{itemName}”。请填写游戏内的精确物品名。");
            return new InventoryMatch(snapshot.RowsAddress, matches);
        }

        public IReadOnlyList<AdapterInventoryItem> ReadInventoryItems()
        {
            var snapshot = ReadInventoryRows();
            return snapshot.Rows
                .GroupBy(row => row.ItemName, StringComparer.Ordinal)
                .Select(group => new AdapterInventoryItem(
                    group.Key,
                    group.Key,
                    group.Sum(row => (long)row.Count)))
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCulture)
                .ToList();
        }

        private InventorySnapshot ReadInventoryRows()
        {
            var allData = ReadUInt64(_saveManager + _layout.SaveManagerSnapshotOffset);
            if (allData == 0) throw new InvalidOperationException("当前存档快照尚未加载。");
            var rows = ReadUInt64(allData + 0x20);
            if (rows == 0) throw new InvalidOperationException("当前背包列表尚未加载。");
            var outerArray = ReadUInt64(rows + 0x10);
            var rowCount = ReadInt32(rows + 0x18);
            if (outerArray == 0 || rowCount is < 0 or > 100_000)
                throw new InvalidDataException($"背包列表结构无效（行数 {rowCount}）。");

            var activeRows = new List<ActiveRow>();
            for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                var row = ReadUInt64(outerArray + 0x20UL + (ulong)rowIndex * 8);
                if (row == 0) continue;
                var rowArray = ReadUInt64(row + 0x10);
                var columnCount = ReadInt32(row + 0x18);
                if (rowArray == 0 || columnCount < 3) continue;

                var name = ReadManagedString(ReadUInt64(rowArray + 0x28));
                if (string.IsNullOrWhiteSpace(name)) continue;

                var slotId = ReadManagedString(ReadUInt64(rowArray + 0x20));
                var countPointerSlot = rowArray + 0x30;
                var countText = ReadManagedString(ReadUInt64(countPointerSlot));
                if (!int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                    throw new InvalidDataException($"物品“{name}”的数量“{countText}”不是整数。");
                activeRows.Add(new ActiveRow(rows, rowIndex, rowArray, countPointerSlot, slotId, name, count));
            }
            return new InventorySnapshot(rows, activeRows);
        }

        public InventoryMatch WriteInventoryTotal(string itemName, int newValue)
        {
            var inventory = FindInventoryRows(itemName);
            var rowUpdates = new List<RowUpdate>(inventory.Rows.Count);
            var orderedRows = inventory.Rows.OrderBy(candidate => candidate.RowIndex).ToList();
            for (var index = 0; index < orderedRows.Count; index++)
            {
                rowUpdates.Add(new RowUpdate(orderedRows[index], index == 0 ? newValue : 0));
            }
            var hookAddress = _moduleBase + _layout.ExecuteTasks;
            var original = Read(hookAddress, _layout.ExpectedHookPrologue.Length);
            if (!original.AsSpan().SequenceEqual(_layout.ExpectedHookPrologue))
                throw new InvalidDataException("游戏主线程入口与受支持版本不一致，已拒绝写入。请为该游戏版本更新适配器。");

            const int allocationSize = 8192;
            var remote = Allocate(allocationSize);
            var remoteBase = unchecked((ulong)remote.ToInt64());
            var stringWrites = rowUpdates.Select((update, index) => new ManagedStringWrite(
                update.Row.RowArrayAddress,
                update.Row.CountPointerSlotAddress,
                remoteBase + 0x800UL + (ulong)index * 0x20UL,
                remoteBase + 0x1000UL + (ulong)index * 8UL,
                update.Count)).ToArray();
            var slotTextAddress = remoteBase + 0x1800;
            var slotStringResultAddress = remoteBase + 0x1880;
            var saveResultAddress = remoteBase + 0x1890;
            var statusAddress = remoteBase + 0x1898;
            var canFreeRemote = true;

            try
            {
                foreach (var write in stringWrites)
                    Write(write.TextAddress, Encoding.UTF8.GetBytes(write.Count.ToString(CultureInfo.InvariantCulture) + "\0"));
                Write(slotTextAddress, Encoding.UTF8.GetBytes("player.inventory.2d\0"));
                var trampoline = BuildMainThreadTrampoline(new HookParameters(
                    HookAddress: hookAddress,
                    InitializationFlagAddress: _moduleBase + _layout.InitializationFlag,
                    SaveManager: _saveManager,
                    Rows: inventory.RowsAddress,
                    CountWrites: stringWrites,
                    SlotTextAddress: slotTextAddress,
                    SlotStringResultAddress: slotStringResultAddress,
                    SaveResultAddress: saveResultAddress,
                    StatusAddress: statusAddress,
                    StringNew: _stringNew,
                    WriteBarrierSetField: _writeBarrierSetField,
                    CleanupInventoryRows: _moduleBase + _layout.CleanupInventoryRows,
                    SetInventoryCacheRows: _moduleBase + _layout.SetInventoryCacheRows,
                    ClearPendingInventory2DDeltaState: _moduleBase + _layout.ClearPendingInventory2DDeltaState,
                    RequestSaveInventory2D: _moduleBase + _layout.RequestSaveInventory2D,
                    FlushPendingInventory2DSave: _moduleBase + _layout.FlushPendingInventory2DSave,
                    SaveInventory2DBinary: _moduleBase + _layout.SaveInventory2DBinary));
                Write(remoteBase, trampoline);

                if (!VirtualProtectEx(_handle, (IntPtr)(long)hookAddress, (nuint)original.Length, PageExecuteReadWrite, out var oldProtection))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法临时写入游戏主线程入口。");

                var patched = false;
                try
                {
                    var patch = BuildAbsoluteJump(remoteBase, original.Length);
                    Write(hookAddress, patch);
                    patched = true;
                    FlushInstructionCache(_handle, (IntPtr)(long)hookAddress, (nuint)patch.Length);

                    var stopwatch = Stopwatch.StartNew();
                    var status = 0;
                    while (status != 1 && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
                    {
                        Thread.Sleep(10);
                        status = ReadInt32(statusAddress);
                    }

                    if (status != 1)
                    {
                        canFreeRemote = status == 0;
                        throw new TimeoutException(status == 2
                            ? "游戏主线程已进入修改流程，但未在五秒内完成；为保护进程，本次远程代码内存将保留。"
                            : "游戏主线程五秒内没有执行修改流程。");
                    }

                    if (ReadInt32(saveResultAddress) == 0)
                        throw new InvalidOperationException("游戏自身的 SaveInventory2D_Binary 返回失败。");
                }
                finally
                {
                    if (patched)
                    {
                        Write(hookAddress, original);
                        FlushInstructionCache(_handle, (IntPtr)(long)hookAddress, (nuint)original.Length);
                    }
                    VirtualProtectEx(_handle, (IntPtr)(long)hookAddress, (nuint)original.Length, oldProtection, out _);
                }

                Thread.Sleep(250);
                InventoryMatch updated;
                try
                {
                    updated = FindInventoryRows(itemName);
                }
                catch (InvalidOperationException) when (newValue == 0)
                {
                    updated = new InventoryMatch(inventory.RowsAddress, []);
                }
                if (updated.TotalCount != newValue)
                    throw new InvalidOperationException($"实时缓存复核失败：预期后台总数 {newValue}，实际 {updated.TotalCount}。");
                return updated;
            }
            finally
            {
                if (canFreeRemote) VirtualFreeEx(_handle, remote, 0, MemRelease);
            }
        }

        private ulong CallPointerFunction(ulong target)
        {
            var result = Allocate(8);
            var codeAddress = Allocate(256);
            try
            {
                var code = new Emitter();
                code.Emit(0x53);
                code.Emit(0x48, 0x83, 0xEC, 0x20);
                code.MovRax(_domainGet); code.CallRax();
                code.Emit(0x48, 0x89, 0xC1);
                code.MovRax(_threadAttach); code.CallRax();
                code.Emit(0x48, 0x89, 0xC3);
                code.Emit(0x33, 0xC9, 0x33, 0xD2);
                code.MovRax(target); code.CallRax();
                code.MovRdx(unchecked((ulong)result.ToInt64())); code.Emit(0x48, 0x89, 0x02);
                code.Emit(0x48, 0x89, 0xD9);
                code.MovRax(_threadDetach); code.CallRax();
                code.Emit(0x33, 0xC0, 0x48, 0x83, 0xC4, 0x20, 0x5B, 0xC3);
                Write(unchecked((ulong)codeAddress.ToInt64()), code.ToArray());
                var thread = CreateRemoteThread(_handle, IntPtr.Zero, 0, codeAddress, IntPtr.Zero, 0, out _);
                if (thread == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 IL2CPP 查询线程。");
                try
                {
                    if (WaitForSingleObject(thread, Infinite) != 0) throw new InvalidOperationException("等待 IL2CPP 查询线程失败。");
                }
                finally
                {
                    CloseHandle(thread);
                }
                return ReadUInt64(unchecked((ulong)result.ToInt64()));
            }
            finally
            {
                VirtualFreeEx(_handle, result, 0, MemRelease);
                VirtualFreeEx(_handle, codeAddress, 0, MemRelease);
            }
        }

        private string ReadManagedString(ulong address)
        {
            if (address == 0) return string.Empty;
            var length = ReadInt32(address + 0x10);
            if (length is < 0 or > 1_000_000) throw new InvalidDataException($"IL2CPP 字符串长度无效：{length}。");
            return Encoding.Unicode.GetString(Read(address + 0x14, checked(length * 2)));
        }

        private IntPtr Allocate(int size)
        {
            var address = VirtualAllocEx(_handle, IntPtr.Zero, (nuint)size, MemCommitReserve, PageExecuteReadWrite);
            if (address == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法在目标进程分配临时内存。");
            return address;
        }

        private int ReadInt32(ulong address) => BitConverter.ToInt32(Read(address, 4));
        private ulong ReadUInt64(ulong address) => BitConverter.ToUInt64(Read(address, 8));

        private byte[] Read(ulong address, int count)
        {
            var bytes = new byte[count];
            if (!ReadProcessMemory(_handle, (IntPtr)(long)address, bytes, (nuint)count, out var read) || read != (nuint)count)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"读取目标内存失败：0x{address:X}。");
            return bytes;
        }

        private void Write(ulong address, byte[] bytes)
        {
            if (!WriteProcessMemory(_handle, (IntPtr)(long)address, bytes, (nuint)bytes.Length, out var written) || written != (nuint)bytes.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"写入目标内存失败：0x{address:X}。");
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero) CloseHandle(_handle);
            _process.Dispose();
        }

        private static byte[] BuildMainThreadTrampoline(HookParameters p)
        {
            var code = new Emitter();
            code.MovRax(p.StatusAddress);
            code.Emit(0x83, 0x38, 0x00);
            var alreadyExecutedJump = code.EmitNearConditionalJump(0x85);

            code.Emit(0x9C, 0x50, 0x51, 0x52);
            code.Emit(0x41, 0x50, 0x41, 0x51, 0x41, 0x52, 0x41, 0x53);
            code.Emit(0x48, 0x83, 0xEC, 0x48);
            code.MovRdx(p.StatusAddress);
            code.Emit(0xC7, 0x02, 0x02, 0x00, 0x00, 0x00);

            foreach (var write in p.CountWrites)
            {
                code.MovRcx(write.TextAddress);
                code.MovRax(p.StringNew); code.CallRax();
                code.MovRdx(write.ResultAddress); code.Emit(0x48, 0x89, 0x02);
                code.MovRcx(write.RowArray);
                code.MovRdx(write.PointerSlot);
                code.Emit(0x49, 0x89, 0xC0);
                code.MovRax(p.WriteBarrierSetField); code.CallRax();
            }

            code.MovRcx(p.Rows);
            code.Emit(0x33, 0xD2);
            code.MovRax(p.CleanupInventoryRows); code.CallRax();

            code.MovRcx(p.SlotTextAddress);
            code.MovRax(p.StringNew); code.CallRax();
            code.MovRdx(p.SlotStringResultAddress); code.Emit(0x48, 0x89, 0x02);

            code.Emit(0x33, 0xC0);
            code.Emit(0x48, 0x89, 0x44, 0x24, 0x20);
            code.Emit(0x48, 0x89, 0x44, 0x24, 0x28);
            code.Emit(0x48, 0x89, 0x44, 0x24, 0x30);
            code.Emit(0x48, 0x89, 0x44, 0x24, 0x38);
            code.Emit(0xC6, 0x44, 0x24, 0x28, 0x01);
            code.MovRcx(p.SaveManager);
            code.MovRdx(p.Rows);
            code.Emit(0x41, 0xB8, 0x01, 0x00, 0x00, 0x00);
            code.Emit(0x41, 0xB9, 0x01, 0x00, 0x00, 0x00);
            code.MovRax(p.SetInventoryCacheRows); code.CallRax();

            code.MovRcx(p.SaveManager);
            code.MovRax(p.SlotStringResultAddress); code.Emit(0x48, 0x8B, 0x10);
            code.MovRax(p.ClearPendingInventory2DDeltaState); code.CallRax();

            code.MovRcx(p.SaveManager);
            code.MovRax(p.SlotStringResultAddress); code.Emit(0x48, 0x8B, 0x10);
            code.MovR8(p.Rows);
            code.Emit(0x45, 0x33, 0xC9);
            code.MovRax(p.RequestSaveInventory2D); code.CallRax();
            code.MovRdx(p.SaveResultAddress); code.Emit(0x89, 0x02);

            code.MovRcx(p.SaveManager);
            code.MovRax(p.SlotStringResultAddress); code.Emit(0x48, 0x8B, 0x10);
            code.Emit(0x45, 0x33, 0xC0);
            code.MovRax(p.FlushPendingInventory2DSave); code.CallRax();

            code.MovRcx(p.SaveManager);
            code.MovRdx(p.Rows);
            code.Emit(0x45, 0x33, 0xC0);
            code.Emit(0x45, 0x33, 0xC9);
            code.MovRax(p.SaveInventory2DBinary); code.CallRax();
            code.Emit(0x0F, 0xB6, 0xC0);
            code.MovRdx(p.SaveResultAddress); code.Emit(0x89, 0x02);
            code.MovRdx(p.StatusAddress); code.Emit(0xC7, 0x02, 0x01, 0x00, 0x00, 0x00);

            code.Emit(0x48, 0x83, 0xC4, 0x48);
            code.Emit(0x41, 0x5B, 0x41, 0x5A, 0x41, 0x59, 0x41, 0x58);
            code.Emit(0x5A, 0x59, 0x58, 0x9D);

            var fastPath = code.Position;
            code.PatchNearJump(alreadyExecutedJump, fastPath);
            code.Emit(0x48, 0x83, 0xEC, 0x28);
            code.MovRax(p.InitializationFlagAddress);
            code.Emit(0x80, 0x38, 0x00);
            var initializedJump = code.EmitNearConditionalJump(0x85);
            code.MovRax(p.HookAddress + 13); code.JumpRax();
            var initializedPath = code.Position;
            code.PatchNearJump(initializedJump, initializedPath);
            code.MovRax(p.HookAddress + 0x20); code.JumpRax();
            return code.ToArray();
        }

        private static byte[] BuildAbsoluteJump(ulong target, int length)
        {
            if (length < 12) throw new ArgumentOutOfRangeException(nameof(length));
            var patch = Enumerable.Repeat((byte)0x90, length).ToArray();
            patch[0] = 0x48;
            patch[1] = 0xB8;
            BitConverter.GetBytes(target).CopyTo(patch, 2);
            patch[10] = 0xFF;
            patch[11] = 0xE0;
            return patch;
        }
    }

    private sealed class Emitter
    {
        private readonly List<byte> _bytes = [];
        public int Position => _bytes.Count;
        public void Emit(params byte[] bytes) => _bytes.AddRange(bytes);
        public void MovRax(ulong value) { Emit(0x48, 0xB8); _bytes.AddRange(BitConverter.GetBytes(value)); }
        public void MovRcx(ulong value) { Emit(0x48, 0xB9); _bytes.AddRange(BitConverter.GetBytes(value)); }
        public void MovRdx(ulong value) { Emit(0x48, 0xBA); _bytes.AddRange(BitConverter.GetBytes(value)); }
        public void MovR8(ulong value) { Emit(0x49, 0xB8); _bytes.AddRange(BitConverter.GetBytes(value)); }
        public void CallRax() => Emit(0xFF, 0xD0);
        public void JumpRax() => Emit(0xFF, 0xE0);
        public int EmitNearConditionalJump(byte condition)
        {
            Emit(0x0F, condition, 0, 0, 0, 0);
            return Position - 4;
        }
        public void PatchNearJump(int displacementOffset, int targetOffset)
        {
            var displacement = targetOffset - (displacementOffset + 4);
            var bytes = BitConverter.GetBytes(displacement);
            for (var index = 0; index < bytes.Length; index++) _bytes[displacementOffset + index] = bytes[index];
        }
        public byte[] ToArray() => _bytes.ToArray();
    }

    private sealed record ActiveRow(
        ulong RowsAddress,
        int RowIndex,
        ulong RowArrayAddress,
        ulong CountPointerSlotAddress,
        string SlotId,
        string ItemName,
        int Count);

    private sealed record InventorySnapshot(ulong RowsAddress, IReadOnlyList<ActiveRow> Rows);

    private sealed record InventoryMatch(ulong RowsAddress, IReadOnlyList<ActiveRow> Rows)
    {
        public long TotalCount => Rows.Sum(row => (long)row.Count);
    }

    private sealed record RowUpdate(ActiveRow Row, int Count);
    private sealed record ManagedStringWrite(
        ulong RowArray,
        ulong PointerSlot,
        ulong TextAddress,
        ulong ResultAddress,
        int Count);

    private sealed record HookParameters(
        ulong HookAddress,
        ulong InitializationFlagAddress,
        ulong SaveManager,
        ulong Rows,
        IReadOnlyList<ManagedStringWrite> CountWrites,
        ulong SlotTextAddress,
        ulong SlotStringResultAddress,
        ulong SaveResultAddress,
        ulong StatusAddress,
        ulong StringNew,
        ulong WriteBarrierSetField,
        ulong CleanupInventoryRows,
        ulong SetInventoryCacheRows,
        ulong ClearPendingInventory2DDeltaState,
        ulong RequestSaveInventory2D,
        ulong FlushPendingInventory2DSave,
        ulong SaveInventory2DBinary);

    private sealed record BuildLayout(
        string ExecutableSha256,
        string GameAssemblySha256,
        string MetadataSha256,
        ulong SaveManagerGetInstance,
        ulong SaveManagerSnapshotOffset,
        ulong CleanupInventoryRows,
        ulong SetInventoryCacheRows,
        ulong ClearPendingInventory2DDeltaState,
        ulong RequestSaveInventory2D,
        ulong FlushPendingInventory2DSave,
        ulong SaveInventory2DBinary,
        ulong ExecuteTasks,
        byte[] ExpectedHookPrologue)
    {
        public bool Matches(string executable, string assembly, string metadata) =>
            string.Equals(ExecutableSha256, executable, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(GameAssemblySha256, assembly, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(MetadataSha256, metadata, StringComparison.OrdinalIgnoreCase);

        public ulong InitializationFlag
        {
            get
            {
                if (ExpectedHookPrologue.Length < 11 || ExpectedHookPrologue[4] != 0x80 || ExpectedHookPrologue[5] != 0x3D)
                    throw new InvalidDataException("fzzml 主线程入口校验数据无效。");
                var displacement = BitConverter.ToInt32(ExpectedHookPrologue, 6);
                return checked((ulong)((long)ExecuteTasks + 11 + displacement));
            }
        }
    }

    private static class PortableExportResolver
    {
        private static readonly ConcurrentDictionary<string, ExportMap> Cache = new(StringComparer.OrdinalIgnoreCase);

        public static ExportMap Read(string path)
        {
            var info = new FileInfo(path);
            var key = $"{path}|{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            return Cache.GetOrAdd(key, _ => Parse(path));
        }

        private static ExportMap Parse(string path)
        {
            var bytes = File.ReadAllBytes(path);
            int ReadInt32(int offset) => BitConverter.ToInt32(bytes, offset);
            uint ReadUInt32(int offset) => BitConverter.ToUInt32(bytes, offset);
            ushort ReadUInt16(int offset) => BitConverter.ToUInt16(bytes, offset);

            var peOffset = ReadInt32(0x3C);
            if (ReadUInt32(peOffset) != 0x00004550) throw new InvalidDataException("GameAssembly.dll 不是有效的 PE 文件。");
            var sectionCount = ReadUInt16(peOffset + 6);
            var optionalHeaderSize = ReadUInt16(peOffset + 20);
            var optionalHeader = peOffset + 24;
            var magic = ReadUInt16(optionalHeader);
            var dataDirectoryOffset = magic switch
            {
                0x20B => optionalHeader + 112,
                0x10B => optionalHeader + 96,
                _ => throw new InvalidDataException("GameAssembly.dll 的 PE 可选头格式不受支持。")
            };
            var sectionsOffset = optionalHeader + optionalHeaderSize;
            var sections = new List<PeSection>(sectionCount);
            for (var index = 0; index < sectionCount; index++)
            {
                var offset = sectionsOffset + index * 40;
                sections.Add(new PeSection(
                    ReadUInt32(offset + 12),
                    ReadUInt32(offset + 8),
                    ReadUInt32(offset + 20),
                    ReadUInt32(offset + 16)));
            }

            int RvaToOffset(uint rva)
            {
                foreach (var section in sections)
                {
                    var size = Math.Max(section.VirtualSize, section.RawSize);
                    if (rva >= section.VirtualAddress && rva < section.VirtualAddress + size)
                        return checked((int)(section.RawAddress + rva - section.VirtualAddress));
                }
                throw new InvalidDataException($"无法在 GameAssembly.dll 中映射 RVA 0x{rva:X}。");
            }

            var exportDirectoryRva = ReadUInt32(dataDirectoryOffset);
            if (exportDirectoryRva == 0) throw new InvalidDataException("GameAssembly.dll 没有导出表。");
            var exportDirectory = RvaToOffset(exportDirectoryRva);
            var numberOfNames = ReadUInt32(exportDirectory + 24);
            var functionsOffset = RvaToOffset(ReadUInt32(exportDirectory + 28));
            var namesOffset = RvaToOffset(ReadUInt32(exportDirectory + 32));
            var ordinalsOffset = RvaToOffset(ReadUInt32(exportDirectory + 36));
            var exports = new Dictionary<string, ulong>(StringComparer.Ordinal);
            for (uint index = 0; index < numberOfNames; index++)
            {
                var nameOffset = RvaToOffset(ReadUInt32(checked(namesOffset + (int)index * 4)));
                var end = nameOffset;
                while (end < bytes.Length && bytes[end] != 0) end++;
                if (end == bytes.Length) throw new InvalidDataException("GameAssembly.dll 导出名称未正确终止。");
                var name = Encoding.ASCII.GetString(bytes, nameOffset, end - nameOffset);
                var ordinal = ReadUInt16(checked(ordinalsOffset + (int)index * 2));
                exports[name] = ReadUInt32(checked(functionsOffset + ordinal * 4));
            }
            return new ExportMap(exports);
        }

        private sealed record PeSection(uint VirtualAddress, uint VirtualSize, uint RawAddress, uint RawSize);
    }

    private sealed record ExportMap(IReadOnlyDictionary<string, ulong> Entries)
    {
        public ulong GetRequired(string name) => Entries.TryGetValue(name, out var rva)
            ? rva
            : throw new InvalidDataException($"GameAssembly.dll 缺少导出函数 {name}。");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nuint size, uint allocationType, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, nuint size, uint freeType);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtectEx(IntPtr process, IntPtr address, nuint size, uint newProtection, out uint oldProtection);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, nuint size, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr threadAttributes, nuint stackSize, IntPtr startAddress, IntPtr parameter, uint creationFlags, out uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushInstructionCache(IntPtr process, IntPtr baseAddress, nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
