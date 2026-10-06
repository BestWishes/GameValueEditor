using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GameValueEditor.Services;

internal sealed class CrashLogService(string directory, string? fallbackDirectory = null, int maxLogBytes = 1024 * 1024)
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly object _gate = new();

    // Logging is best effort: callers must still report the original error when this returns null.
    internal string? TryWrite(Exception exception)
    {
        try
        {
            lock (_gate)
            {
                var entry = CreateEntry(exception);
                if (TryWriteTo(directory, entry) is { } primary) return primary;
                var fallback = fallbackDirectory ?? Path.Combine(Path.GetTempPath(), "GameValueEditor", "crash-logs",
                    Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant())))[..16]);
                return TryWriteTo(fallback, entry);
            }
        }
        catch (Exception) { return null; }
    }

    private byte[] CreateEntry(Exception exception)
    {
        var header = $"[{DateTimeOffset.Now:O}]{Environment.NewLine}";
        var ending = Environment.NewLine + new string('-', 40) + Environment.NewLine;
        string details;
        try { details = exception.ToString(); }
        catch (Exception) { details = exception.GetType().FullName + "：无法格式化异常详情。"; }
        var limit = Math.Min(maxLogBytes, 64 * 1024);
        var marker = Environment.NewLine + "[异常详情已截断]";
        var budget = (limit - Utf8.GetByteCount(header + ending + marker)) / 4;
        if (budget < 1) throw new ArgumentOutOfRangeException(nameof(maxLogBytes));
        if (details.Length > budget)
        {
            // Avoid splitting a surrogate pair when truncating Unicode details.
            if (char.IsHighSurrogate(details[budget - 1])) budget--;
            details = details[..budget] + marker;
        }
        return Utf8.GetBytes(header + details + ending);
    }

    private string? TryWriteTo(string root, byte[] entry)
    {
        try
        {
            root = Path.GetFullPath(root);
            EnsurePlainPath(root);
            Directory.CreateDirectory(root);
            var files = new[] { Path.Combine(root, "crash.log"), Path.Combine(root, "crash.1.log"), Path.Combine(root, "crash.2.log") };
            foreach (var file in files)
            {
                EnsurePlainPath(file);
                TrimOversized(file);
            }
            if (File.Exists(files[0]) && new FileInfo(files[0]).Length + entry.Length > maxLogBytes)
            {
                if (File.Exists(files[1])) File.Move(files[1], files[2], true);
                File.Move(files[0], files[1], true);
            }
            using var stream = new FileStream(files[0], FileMode.Append, FileAccess.Write, FileShare.Read,
                4096, FileOptions.WriteThrough);
            stream.Write(entry);
            stream.Flush(true);
            return files[0];
        }
        catch (Exception) { return null; }
    }

    private void TrimOversized(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length <= maxLogBytes) return;
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var marker = Utf8.GetBytes("[较早日志已截断]" + Environment.NewLine);
            byte[] tail;
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var count = maxLogBytes - marker.Length;
                source.Position = source.Length - count;
                tail = new byte[count];
                source.ReadExactly(tail);
            }
            var start = 0;
            while (start < tail.Length && (tail[start] & 0xC0) == 0x80) start++;
            using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                target.Write(marker);
                target.Write(tail.AsSpan(start));
                target.Flush(true);
            }
            File.Replace(temporary, path, null);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception) { }
        }
    }

    private static void EnsurePlainPath(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("日志位置包含链接，已拒绝写入。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
