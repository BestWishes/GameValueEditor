using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

var options = ParseArguments(args);
if (options.TryGetValue("recover", out var recoveryPath))
{
    if (!options.TryGetValue("app-dir", out var recoveryApplication)) return 2;
    try
    {
        await UpdateRecovery.RestoreAsync(recoveryPath, recoveryApplication);
        return 0;
    }
    catch (Exception exception)
    {
        try { await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(recoveryPath)!, "recovery-error.log"), exception.ToString()); }
        catch { }
        return 1;
    }
}
if (!options.TryGetValue("pending", out var pendingPath) ||
    !options.TryGetValue("pid", out var pidText) ||
    !options.TryGetValue("app-dir", out var appDirectory) ||
    !int.TryParse(pidText, out var processId)) return 2;

var restart = options.ContainsKey("restart");
var updatesDirectory = Path.GetDirectoryName(pendingPath) ?? Path.GetTempPath();
var errorPath = Path.Combine(updatesDirectory, "update-error.log");
var errorNoticePath = Path.Combine(updatesDirectory, "last-update-error.json");
try
{
    if (File.Exists(Path.Combine(updatesDirectory, UpdateRecovery.MarkerName)))
        throw new InvalidOperationException("上次更新尚需恢复旧文件，请先使用保留的恢复记录完成恢复。");
    var pending = JsonSerializer.Deserialize<PendingUpdate>(await File.ReadAllTextAsync(pendingPath))
                  ?? throw new InvalidOperationException("待安装更新记录无效。");
    if (!File.Exists(pending.ArchivePath)) throw new FileNotFoundException("已下载的更新包不存在。", pending.ArchivePath);
    await using (var archiveStream = new FileStream(pending.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(archiveStream));
        if (!actualHash.Equals(pending.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("待安装更新包的 SHA-256 已变化，已取消安装。");
    }

    try
    {
        using var application = Process.GetProcessById(processId);
        if (!application.WaitForExit(30_000))
            throw new TimeoutException("等待肝肾大圣退出超时，未更换任何文件。");
    }
    catch (ArgumentException)
    {
        // The application already exited.
    }

    var extractionDirectory = Path.Combine(updatesDirectory, $"extract-{Guid.NewGuid():N}");
    Directory.CreateDirectory(extractionDirectory);
    try
    {
        ZipFile.ExtractToDirectory(pending.ArchivePath, extractionDirectory, true);
        var newExecutable = Path.Combine(extractionDirectory, "GameValueEditor.exe");
        if (!File.Exists(newExecutable)) throw new InvalidOperationException("更新包中缺少 GameValueEditor.exe。");
        await WaitForExclusiveAccessAsync(Path.Combine(appDirectory, "GameValueEditor.exe"));
        await InstallUpdateAsync(extractionDirectory, appDirectory);
    }
    finally
    {
        TryDeleteDirectory(extractionDirectory);
    }

    File.Delete(pendingPath);
    File.Delete(pending.ArchivePath);
    if (File.Exists(errorPath)) File.Delete(errorPath);
    if (File.Exists(errorNoticePath)) File.Delete(errorNoticePath);
    if (restart)
    {
        var executable = Path.Combine(appDirectory, "GameValueEditor.exe");
        _ = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
    }
    return 0;
}
catch (Exception exception)
{
    try
    {
        Directory.CreateDirectory(updatesDirectory);
        await File.WriteAllTextAsync(errorPath, $"[{DateTimeOffset.Now:O}]{Environment.NewLine}{exception}");
        var errorNotice = new UpdateFailureNotice(
            DateTimeOffset.Now,
            exception.Message,
            errorPath);
        var errorNoticeTemporary = errorNoticePath + ".tmp";
        await File.WriteAllTextAsync(errorNoticeTemporary, JsonSerializer.Serialize(errorNotice));
        File.Move(errorNoticeTemporary, errorNoticePath, true);
    }
    catch
    {
        // Reporting a failed update must never prevent the previous application version from restarting.
    }
    try
    {
        var failedPath = Path.Combine(updatesDirectory, $"failed-update-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        if (File.Exists(pendingPath)) File.Move(pendingPath, failedPath, true);
    }
    catch
    {
    }
    if (restart && exception is not UpdateRecoveryRequiredException &&
        !File.Exists(Path.Combine(updatesDirectory, UpdateRecovery.MarkerName)))
    {
        var executable = Path.Combine(appDirectory, "GameValueEditor.exe");
        if (File.Exists(executable)) _ = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
    }
    return 1;
}

static async Task InstallUpdateAsync(string extractionDirectory, string appDirectory)
{
    var applicationRoot = Path.GetFullPath(appDirectory);
    if (!Path.EndsInDirectorySeparator(applicationRoot))
        applicationRoot += Path.DirectorySeparatorChar;
    var transactionDirectory = Path.Combine(appDirectory, $".update-transaction-{Guid.NewGuid():N}");
    var stagedRoot = Path.Combine(transactionDirectory, "staged");
    var backupRoot = Path.Combine(transactionDirectory, "backup");
    var files = new List<UpdateFile>();
    var retainTransaction = false;
    var journalPath = Path.Combine(transactionDirectory, "recovery.json");
    var journal = new RecoveryJournal(Path.GetFullPath(appDirectory), []);
    var hasSavedJournal = false;
    Directory.CreateDirectory(stagedRoot);
    try
    {
        foreach (var source in Directory.EnumerateFiles(extractionDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(extractionDirectory, source);
            if (relative.Equals("data", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith($"data{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
            var destination = Path.GetFullPath(Path.Combine(appDirectory, relative));
            if (!destination.StartsWith(applicationRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"更新包路径超出应用目录：{relative}");
            var staged = Path.Combine(stagedRoot, relative);
            var backup = Path.Combine(backupRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            File.Copy(source, staged, true);
            files.Add(new UpdateFile(relative, destination, staged, backup, File.Exists(destination)));
        }

        try
        {
            foreach (var file in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.Destination)!);
                if (file.HadOriginal)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file.Backup)!);
                    await RetryFileOperationAsync(
                        () => File.Copy(file.Destination, file.Backup, true), file.Destination, "备份");
                }
                journal.Files.Add(new RecoveryFile(file.RelativePath, file.HadOriginal));
                await UpdateRecovery.SaveAsync(journalPath, journal);
                hasSavedJournal = true;
                await RetryFileOperationAsync(
                    () => File.Move(file.Staged, file.Destination, true), file.Destination, "替换");
            }
        }
        catch (Exception installException)
        {
            try
            {
                if (hasSavedJournal) await UpdateRecovery.RestoreAsync(journalPath, appDirectory);
            }
            catch (Exception recoveryException)
            {
                retainTransaction = true;
                try { await UpdateRecovery.RecordRequiredAsync(appDirectory, journalPath); }
                catch (Exception noticeException) { recoveryException = new AggregateException(recoveryException, noticeException); }
                throw new UpdateRecoveryRequiredException(journalPath, installException, recoveryException);
            }
            throw;
        }
    }
    finally
    {
        if (!retainTransaction) TryDeleteDirectory(transactionDirectory);
    }
}

static async Task WaitForExclusiveAccessAsync(string path)
{
    if (!File.Exists(path)) return;
    await RetryFileOperationAsync(() =>
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }, path, "等待释放", 15);
}

static async Task RetryFileOperationAsync(Action operation, string path, string action, int attempts = 10)
{
    Exception? lastException = null;
    for (var attempt = 0; attempt < attempts; attempt++)
    {
        try
        {
            operation();
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            lastException = exception;
            if (attempt + 1 < attempts)
                await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)));
        }
    }
    throw new IOException($"{action}更新文件失败，文件可能仍被占用：{path}", lastException);
}

static void TryDeleteDirectory(string path)
{
    try
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
}

static Dictionary<string, string> ParseArguments(IReadOnlyList<string> arguments)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < arguments.Count; index++)
    {
        var argument = arguments[index];
        if (!argument.StartsWith("--", StringComparison.Ordinal)) continue;
        var name = argument[2..];
        if (index + 1 < arguments.Count && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            result[name] = arguments[++index];
        else
            result[name] = string.Empty;
    }
    return result;
}

internal sealed record PendingUpdate(string Version, string ArchivePath, string Sha256, DateTime DownloadedUtc);
internal sealed record UpdateFile(string RelativePath, string Destination, string Staged, string Backup, bool HadOriginal);
internal sealed record UpdateFailureNotice(DateTimeOffset FailedAt, string Message, string LogPath);
