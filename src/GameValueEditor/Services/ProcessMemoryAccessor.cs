using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GameValueEditor.Services;

public sealed class ProcessMemoryAccessor : IDisposable, IMemoryWriteAccess
{
    private readonly SafeProcessHandle _handle;

    public ProcessMemoryAccessor(int processId)
    {
        ProcessId = processId;
        _handle = NativeMethods.OpenProcess(
            NativeMethods.ProcessAccess.QueryInformation |
            NativeMethods.ProcessAccess.VirtualMemoryRead |
            NativeMethods.ProcessAccess.VirtualMemoryWrite |
            NativeMethods.ProcessAccess.VirtualMemoryOperation,
            false,
            processId);

        if (_handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法连接目标进程。可以尝试以管理员身份运行本应用。");
        }
        if (!NativeMethods.GetProcessTimes(_handle, out var creation, out _, out _, out _))
        {
            var error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(error, "无法核对目标进程的启动身份。");
        }
        StartTimeUtc = DateTime.FromFileTimeUtc(creation);
    }

    public int ProcessId { get; }
    public DateTime StartTimeUtc { get; }

    public void EnsureInstance(int processId, DateTime startTimeUtc)
    {
        if (ProcessId != processId || StartTimeUtc != startTimeUtc)
            throw new InvalidOperationException("目标进程实例已变化，请重新连接并扫描。");
    }

    public IReadOnlyList<MemoryRegion> EnumerateReadableRegions(bool writableOnly)
    {
        var regions = new List<MemoryRegion>();
        ulong address = 0;
        const ulong maximumUserAddress = 0x00007FFFFFFFFFFF;
        var structureSize = (nuint)Marshal.SizeOf<NativeMethods.MemoryBasicInformation64>();

        while (address < maximumUserAddress)
        {
            var result = NativeMethods.VirtualQueryEx(
                _handle,
                unchecked((nint)(long)address),
                out var info,
                structureSize);
            if (result == 0) break;

            var readable = info.State == NativeMethods.MemoryCommit && IsReadable(info.Protect);
            var writable = IsWritable(info.Protect);
            if (readable && (!writableOnly || writable) && info.RegionSize > 0)
            {
                regions.Add(new MemoryRegion(info.BaseAddress, info.RegionSize, writable));
            }

            var next = info.BaseAddress + info.RegionSize;
            if (next <= address) break;
            address = next;
        }

        return regions;
    }

    public bool TryRead(ulong address, int length, out byte[] data)
    {
        data = new byte[length];
        if (length <= 0) return false;
        var success = NativeMethods.ReadProcessMemory(
            _handle,
            unchecked((nint)(long)address),
            data,
            (nuint)length,
            out var bytesRead);
        if (!success || bytesRead < (nuint)length)
        {
            data = [];
            return false;
        }
        return true;
    }

    public bool TryWrite(ulong address, byte[] data, out string error)
    {
        var success = NativeMethods.WriteProcessMemory(
            _handle,
            unchecked((nint)(long)address),
            data,
            (nuint)data.Length,
            out var bytesWritten);
        if (success && bytesWritten == (nuint)data.Length)
        {
            error = string.Empty;
            return true;
        }

        error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        return false;
    }

    public ModuleLocation? FindContainingModule(ulong address)
    {
        try
        {
            using var process = Process.GetProcessById(ProcessId);
            foreach (ProcessModule module in process.Modules)
            {
                var start = unchecked((ulong)module.BaseAddress.ToInt64());
                var end = start + (ulong)module.ModuleMemorySize;
                if (address >= start && address < end)
                {
                    return new ModuleLocation(module.ModuleName, start, (long)(address - start));
                }
            }
        }
        catch
        {
            // Module enumeration can fail for protected processes.
        }
        return null;
    }

    public bool TryGetModuleBase(string moduleName, out ulong baseAddress)
    {
        baseAddress = 0;
        try
        {
            using var process = Process.GetProcessById(ProcessId);
            foreach (ProcessModule module in process.Modules)
            {
                if (!string.Equals(module.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase)) continue;
                baseAddress = unchecked((ulong)module.BaseAddress.ToInt64());
                return true;
            }
        }
        catch
        {
            // Caller receives false and marks the field unavailable.
        }
        return false;
    }

    public void Dispose() => _handle.Dispose();

    private static bool IsReadable(uint protect)
    {
        if ((protect & NativeMethods.PageGuard) != 0 || protect == NativeMethods.PageNoAccess) return false;
        var baseProtect = protect & 0xFF;
        return baseProtect is NativeMethods.PageReadOnly
            or NativeMethods.PageReadWrite
            or NativeMethods.PageWriteCopy
            or NativeMethods.PageExecuteRead
            or NativeMethods.PageExecuteReadWrite
            or NativeMethods.PageExecuteWriteCopy;
    }

    private static bool IsWritable(uint protect)
    {
        if ((protect & NativeMethods.PageGuard) != 0) return false;
        var baseProtect = protect & 0xFF;
        return baseProtect is NativeMethods.PageReadWrite
            or NativeMethods.PageWriteCopy
            or NativeMethods.PageExecuteReadWrite
            or NativeMethods.PageExecuteWriteCopy;
    }
}

public sealed record MemoryRegion(ulong BaseAddress, ulong RegionSize, bool IsWritable);
public sealed record ModuleLocation(string ModuleName, ulong ModuleBase, long Offset);

internal static class NativeMethods
{
    [Flags]
    internal enum ProcessAccess : uint
    {
        VirtualMemoryOperation = 0x0008,
        VirtualMemoryRead = 0x0010,
        VirtualMemoryWrite = 0x0020,
        QueryInformation = 0x0400
    }

    internal const uint MemoryCommit = 0x1000;
    internal const uint PageNoAccess = 0x01;
    internal const uint PageReadOnly = 0x02;
    internal const uint PageReadWrite = 0x04;
    internal const uint PageWriteCopy = 0x08;
    internal const uint PageExecuteRead = 0x20;
    internal const uint PageExecuteReadWrite = 0x40;
    internal const uint PageExecuteWriteCopy = 0x80;
    internal const uint PageGuard = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryBasicInformation64
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint Alignment1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint Alignment2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(ProcessAccess access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit,
        out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nuint VirtualQueryEx(
        SafeProcessHandle process,
        nint address,
        out MemoryBasicInformation64 buffer,
        nuint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadProcessMemory(
        SafeProcessHandle process,
        nint baseAddress,
        [Out] byte[] buffer,
        nuint size,
        out nuint bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteProcessMemory(
        SafeProcessHandle process,
        nint baseAddress,
        byte[] buffer,
        nuint size,
        out nuint bytesWritten);
}
