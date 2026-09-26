using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

var options = ParseArguments(args);
if (!options.TryGetValue("pending", out var pendingPath) ||
    !options.TryGetValue("pid", out var pidText) ||
    !options.TryGetValue("app-dir", out var appDirectory) ||
    !int.TryParse(pidText, out var processId)) return 2;

var restart = options.ContainsKey("restart");
var updatesDirectory = Path.GetDirectoryName(pendingPath) ?? Path.GetTempPath();
var errorPath = Path.Combine(updatesDirectory, "update-error.log");
try
{
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

        foreach (var source in Directory.EnumerateFiles(extractionDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(extractionDirectory, source);
            if (relative.Equals("data", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith($"data{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
            var destination = Path.Combine(appDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, true);
        }
    }
    finally
    {
        if (Directory.Exists(extractionDirectory)) Directory.Delete(extractionDirectory, true);
    }

    File.Delete(pendingPath);
    File.Delete(pending.ArchivePath);
    if (File.Exists(errorPath)) File.Delete(errorPath);
    if (restart)
    {
        var executable = Path.Combine(appDirectory, "GameValueEditor.exe");
        _ = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
    }
    return 0;
}
catch (Exception exception)
{
    Directory.CreateDirectory(updatesDirectory);
    await File.WriteAllTextAsync(errorPath, $"[{DateTimeOffset.Now:O}]{Environment.NewLine}{exception}");
    try
    {
        var failedPath = Path.Combine(updatesDirectory, $"failed-update-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        if (File.Exists(pendingPath)) File.Move(pendingPath, failedPath, true);
    }
    catch
    {
    }
    if (restart)
    {
        var executable = Path.Combine(appDirectory, "GameValueEditor.exe");
        if (File.Exists(executable)) _ = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
    }
    return 1;
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
