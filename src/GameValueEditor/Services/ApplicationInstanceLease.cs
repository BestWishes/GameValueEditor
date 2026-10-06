using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace GameValueEditor.Services;

internal sealed class ApplicationInstanceLease : IDisposable
{
    private readonly FileStream _stream;
    private ApplicationInstanceLease(FileStream stream) => _stream = stream;

    internal static bool TryAcquire(string dataDirectory, out ApplicationInstanceLease? lease, out Owner? owner)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(Path.GetFullPath(dataDirectory), ".application-instance.lock");
        lease = null;
        owner = null;
        FileStream stream;
        try { stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read); }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
        {
            try
            {
                using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (reader.Length <= 4096) owner = JsonSerializer.Deserialize<Owner>(reader);
            }
            catch (Exception readError) when (readError is IOException or JsonException or UnauthorizedAccessException) { }
            return false;
        }
        try
        {
            using var self = Process.GetCurrentProcess();
            owner = new Owner(Environment.ProcessId, self.StartTime.ToUniversalTime(), Environment.ProcessPath ?? "");
            stream.SetLength(0);
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(owner));
            stream.Flush(true);
            lease = new ApplicationInstanceLease(stream);
            return true;
        }
        catch { stream.Dispose(); throw; }
    }

    internal static bool TryActivate(Owner? owner)
    {
        if (owner is null || owner.ProcessId <= 0 || owner.ProcessId == Environment.ProcessId) return false;
        try
        {
            using var process = Process.GetProcessById(owner.ProcessId);
            if (process.HasExited || process.StartTime.ToUniversalTime() != owner.StartTimeUtc ||
                !string.Equals(process.MainModule?.FileName, owner.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(owner.ExecutablePath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return false;
            var handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero) return false;
            if (IsIconic(handle)) ShowWindowAsync(handle, 9);
            return SetForegroundWindow(handle);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    public void Dispose() => _stream.Dispose(); // OS also releases the lease on process termination.
    internal sealed record Owner(int ProcessId, DateTime StartTimeUtc, string ExecutablePath);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}
