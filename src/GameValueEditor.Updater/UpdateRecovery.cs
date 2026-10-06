using System.Text.Json;
using System.IO;

internal static class UpdateRecovery
{
    internal const string MarkerName = "recovery-required.json";

    internal static async Task SaveAsync(string journalPath, RecoveryJournal journal)
    {
        var temporary = journalPath + ".tmp";
        await WriteDurableAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(journal));
        File.Move(temporary, journalPath, true);
    }

    private static async Task WriteDurableAsync(string path, byte[] bytes)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
        stream.Flush(true);
    }

    internal static string? FindIncomplete(string appDirectory)
    {
        if (!Directory.Exists(appDirectory)) return null;
        foreach (var directory in Directory.EnumerateDirectories(appDirectory, ".update-transaction-*", SearchOption.TopDirectoryOnly))
        {
            var path = Path.Combine(directory, "recovery.json");
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                    (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)) return path;
                var journal = JsonSerializer.Deserialize<RecoveryJournal>(File.ReadAllText(path));
                if (journal is null || journal.State != "Committed" || !SameApplication(journal.ApplicationDirectory, appDirectory)) return path;
            }
            catch { return path; }
        }
        return null;
    }

    private static bool SameApplication(string left, string right) => string.Equals(
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    internal static void CleanupCommitted(string appDirectory)
    {
        if (!Directory.Exists(appDirectory)) return;
        foreach (var directory in Directory.EnumerateDirectories(appDirectory, ".update-transaction-*", SearchOption.TopDirectoryOnly))
        {
            var path = Path.Combine(directory, "recovery.json");
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                    !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                var journal = JsonSerializer.Deserialize<RecoveryJournal>(File.ReadAllText(path));
                if (journal is null || journal.State != "Committed" || !SameApplication(journal.ApplicationDirectory, appDirectory)) continue;
                ClearMarker(appDirectory, path);
                Directory.Delete(directory, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        }
    }

    internal static void ClearMarker(string appDirectory, string journalPath)
    {
        var markerPath = Path.Combine(appDirectory, "data", "updates", MarkerName);
        if (!File.Exists(markerPath)) return;
        var marker = JsonSerializer.Deserialize<RecoveryRequired>(File.ReadAllText(markerPath));
        if (marker is not null && string.Equals(Path.GetFullPath(marker.JournalPath), Path.GetFullPath(journalPath), StringComparison.OrdinalIgnoreCase))
            File.Delete(markerPath);
    }

    internal static async Task RestoreAsync(string journalPath, string appDirectory)
    {
        var appRoot = Path.GetFullPath(appDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var transaction = Path.GetDirectoryName(Path.GetFullPath(journalPath))!;
        if (!string.Equals(Path.GetDirectoryName(transaction), appRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(transaction).StartsWith(".update-transaction-", StringComparison.Ordinal) ||
            Path.GetFileName(journalPath) != "recovery.json")
            throw new InvalidOperationException("恢复记录不在当前应用的事务目录中。");
        if ((File.GetAttributes(transaction) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(journalPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("恢复事务或记录包含符号链接或目录联接。");
        var journal = JsonSerializer.Deserialize<RecoveryJournal>(await File.ReadAllTextAsync(journalPath))
                      ?? throw new InvalidOperationException("恢复记录无效。");
        if (journal.State != "Applying" || journal.Files is null)
            throw new InvalidOperationException("该事务已经提交或状态无效，不能恢复成旧文件。");
        if (!string.Equals(Path.GetFullPath(journal.ApplicationDirectory).TrimEnd(Path.DirectorySeparatorChar),
                appRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("恢复记录属于其他应用目录。");
        // Validate the complete record before touching any application file.
        var files = journal.Files.Select(file => (File: file,
            Destination: ResolveSafe(appRoot, file.RelativePath),
            Backup: ResolveSafe(Path.Combine(transaction, "backup"), file.RelativePath))).ToList();
        foreach (var file in files)
            if (file.File.HadOriginal && !File.Exists(file.Backup))
                throw new FileNotFoundException("旧文件备份缺失，已停止恢复。", file.Backup);
        foreach (var file in files.AsEnumerable().Reverse())
        {
            if (file.File.HadOriginal)
                await RetryAsync(() => File.Copy(file.Backup, file.Destination, true));
            else if (File.Exists(file.Destination))
                await RetryAsync(() => File.Delete(file.Destination));
        }
        ClearMarker(appRoot, journalPath);
        Directory.Delete(transaction, true);
    }

    internal static async Task RecordRequiredAsync(string appDirectory, string journalPath)
    {
        var updates = Path.Combine(appDirectory, "data", "updates");
        Directory.CreateDirectory(updates);
        var path = Path.Combine(updates, MarkerName);
        await WriteDurableAsync(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(
            new RecoveryRequired(Path.GetFullPath(journalPath), Environment.ProcessPath ?? string.Empty)));
        File.Move(path + ".tmp", path, true);
    }

    internal static string ResolveSafe(string root, string relative)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var normalized = relative.Replace('\\', '/');
        if (Path.IsPathRooted(relative) || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("data", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("data/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Split('/').Any(part => part is ".." or "." || part.Contains(':')))
            throw new InvalidOperationException("恢复记录包含不安全的文件路径。");
        for (var parent = Path.GetDirectoryName(path); parent is not null &&
             parent.Length >= prefix.Length - 1; parent = Path.GetDirectoryName(parent))
            if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("恢复路径包含符号链接或目录联接。");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("恢复文件是符号链接。");
        return path;
    }

    private static async Task RetryAsync(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (Exception exception) when (attempt < 9 && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)));
            }
        }
    }
}

internal sealed record RecoveryFile(string RelativePath, bool HadOriginal);
internal sealed record RecoveryJournal(string ApplicationDirectory, List<RecoveryFile> Files, string State = "Applying");
internal sealed record RecoveryRequired(string JournalPath, string RunnerPath);
internal sealed class UpdateRecoveryRequiredException(string journalPath, Exception install, Exception recovery)
    : AggregateException($"更新失败且旧文件尚未恢复完整。备份与恢复记录已保留：{journalPath}；关闭程序并释放占用后，可运行更新器 --recover 此记录 --app-dir 应用目录。", install, recovery)
{
    internal string JournalPath { get; } = journalPath;
}
