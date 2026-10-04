using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameValueEditor.Services;

public sealed class ApplicationUpdateService
{
    private const string ReleasesApi = "https://api.github.com/repos/BestWishes/GameValueEditor/releases?per_page=50";
    private readonly HttpClient _httpClient;
    private readonly string _updatesDirectory;
    private readonly string _currentVersion;
    private readonly DownloadTimeoutPolicy _downloadTimeoutPolicy;

    public ApplicationUpdateService(
        string updatesDirectory,
        HttpClient? httpClient = null,
        string? currentVersion = null,
        DownloadTimeoutPolicy? downloadTimeoutPolicy = null)
    {
        _updatesDirectory = updatesDirectory;
        _currentVersion = currentVersion ?? ApplicationVersion.Current;
        _downloadTimeoutPolicy = downloadTimeoutPolicy ?? DownloadTimeoutPolicy.Default;
        _httpClient = httpClient ?? new HttpClient();
        if (httpClient is null)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("GameValueEditor-Updater/1.0");
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }
        CleanupStaleUpdateArtifacts();
    }

    public string PendingManifestPath => Path.Combine(_updatesDirectory, "pending-update.json");
    public string LastErrorNoticePath => Path.Combine(_updatesDirectory, "last-update-error.json");

    public ApplicationUpdateFailure? TakeLastFailure()
    {
        if (!File.Exists(LastErrorNoticePath)) return null;
        try
        {
            return JsonSerializer.Deserialize<ApplicationUpdateFailure>(
                File.ReadAllText(LastErrorNoticePath), JsonOptions);
        }
        catch
        {
            return new ApplicationUpdateFailure(
                DateTimeOffset.Now,
                "上次应用更新没有完成，请查看更新日志。",
                Path.Combine(_updatesDirectory, "update-error.log"));
        }
        finally
        {
            try { File.Delete(LastErrorNoticePath); }
            catch
            {
            }
        }
    }

    public async Task<ApplicationUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(ReleasesApi, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(stream,
                           cancellationToken: cancellationToken)
                       ?? throw new InvalidOperationException("无法读取 GitHub 版本信息。");
        if (!SemanticVersion.TryParse(_currentVersion, out var current))
            throw new InvalidOperationException($"当前应用版本号无效：{_currentVersion}");

        GitHubRelease? latestRelease = null;
        GitHubAsset? asset = null;
        SemanticVersion latest = default;
        var latestText = string.Empty;
        foreach (var release in releases)
        {
            if (release.Draft || release.Prerelease) continue;
            var releaseText = release.TagName.TrimStart('v', 'V');
            if (!SemanticVersion.TryParse(releaseText, out var releaseVersion)) continue;
            var releaseAsset = release.Assets.FirstOrDefault(candidate => IsApplicationPackage(candidate, releaseText));
            if (releaseAsset is null || latestRelease is not null && releaseVersion.CompareTo(latest) <= 0) continue;
            latestRelease = release;
            asset = releaseAsset;
            latest = releaseVersion;
            latestText = releaseText;
        }

        if (latestRelease is null || asset is null)
            throw new InvalidOperationException("GitHub 暂无可用的 Windows x64 应用发布包。");
        var digest = asset.Digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true
            ? asset.Digest[7..]
            : string.Empty;
        return new ApplicationUpdateCheckResult(
            latest.CompareTo(current) > 0,
            latestText,
            asset.Name,
            asset.DownloadUrl,
            digest,
            asset.Size);
    }

    private static bool IsApplicationPackage(GitHubAsset asset, string version) =>
        string.Equals(asset.Name, $"GameValueEditor-v{version}-win-x64.zip", StringComparison.OrdinalIgnoreCase);

    public async Task<PendingApplicationUpdate> DownloadAsync(
        ApplicationUpdateCheckResult update,
        IProgress<DownloadProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!update.IsUpdateAvailable) throw new InvalidOperationException("当前已经是最新正式版。");
        if (string.IsNullOrWhiteSpace(update.Sha256))
            throw new InvalidOperationException("最新发布包没有 SHA-256 校验值，已取消下载。");

        var versionSegment = Sanitize(update.Version);
        if (versionSegment is "." or ".." || string.IsNullOrWhiteSpace(versionSegment))
            throw new InvalidOperationException("更新版本号无法用于本地路径。");
        var assetName = Path.GetFileName(update.AssetName);
        if (string.IsNullOrWhiteSpace(assetName) || !string.Equals(assetName, update.AssetName, StringComparison.Ordinal))
            throw new InvalidOperationException("更新包文件名包含不安全路径。");
        var versionDirectory = Path.Combine(_updatesDirectory, versionSegment);
        Directory.CreateDirectory(versionDirectory);
        var finalPath = Path.Combine(versionDirectory, assetName);
        var temporaryPath = finalPath + ".download";
        try
        {
            await HttpDownloadService.DownloadToFileAsync(_httpClient, update.DownloadUrl, temporaryPath, update.Size,
                progress, _downloadTimeoutPolicy, cancellationToken);

            var actualHash = await ComputeSha256Async(temporaryPath, cancellationToken);
            if (!actualHash.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("更新包 SHA-256 校验失败，已拒绝安装。");
            File.Move(temporaryPath, finalPath, true);

            var pending = new PendingApplicationUpdate(update.Version, finalPath, actualHash, DateTime.UtcNow);
            var pendingTemporary = PendingManifestPath + ".tmp";
            await File.WriteAllTextAsync(pendingTemporary, JsonSerializer.Serialize(pending, JsonOptions), cancellationToken);
            File.Move(pendingTemporary, PendingManifestPath, true);
            CleanupStaleUpdateArtifacts();
            return pending;
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public bool LaunchPendingUpdate(bool restartApplication)
    {
        if (!File.Exists(PendingManifestPath)) return false;
        var runner = PrepareUpdaterRunner();
        var start = new ProcessStartInfo(runner)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--pending");
        start.ArgumentList.Add(PendingManifestPath);
        start.ArgumentList.Add("--pid");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add("--app-dir");
        start.ArgumentList.Add(AppContext.BaseDirectory);
        if (restartApplication) start.ArgumentList.Add("--restart");
        try
        {
            _ = Process.Start(start) ?? throw new InvalidOperationException("无法启动更新程序。");
            return true;
        }
        catch
        {
            TryDeleteFile(runner);
            throw;
        }
    }

    internal string PrepareUpdaterRunner()
    {
        var pending = JsonSerializer.Deserialize<PendingApplicationUpdate>(
                          File.ReadAllText(PendingManifestPath), JsonOptions)
                      ?? throw new InvalidOperationException("待安装更新记录无效。");
        var updatesRoot = EnsureDirectoryRoot(_updatesDirectory);
        var archivePath = Path.GetFullPath(pending.ArchivePath);
        if (!archivePath.StartsWith(updatesRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(archivePath))
            throw new InvalidOperationException("待安装更新包不在受信任的更新目录中。");
        var actualHash = ComputeSha256(archivePath);
        if (!actualHash.Equals(pending.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("待安装更新包的 SHA-256 已变化，已取消安装。");

        Directory.CreateDirectory(_updatesDirectory);
        var runner = Path.Combine(_updatesDirectory, $"updater-{Guid.NewGuid():N}.exe");
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var updaterEntries = archive.Entries.Where(entry =>
                    string.Equals(entry.FullName.Replace('\\', '/'), "GameValueEditor.Updater.exe",
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(entry.Name))
                .ToList();
            if (updaterEntries.Count != 1)
                throw new InvalidOperationException("更新包必须且只能在根目录包含一个 GameValueEditor.Updater.exe。");
            using var source = updaterEntries[0].Open();
            using var destination = new FileStream(runner, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.CopyTo(destination);
            if (destination.Length == 0) throw new InvalidOperationException("更新包中的更新程序为空。");
            return runner;
        }
        catch
        {
            TryDeleteFile(runner);
            throw;
        }
    }

    public static bool TryLaunchPendingAtStartup()
    {
        var store = new ProfileStore();
        var service = new ApplicationUpdateService(store.UpdatesDirectory);
        return File.Exists(service.PendingManifestPath) && service.LaunchPendingUpdate(true);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private void CleanupStaleUpdateArtifacts()
    {
        if (!Directory.Exists(_updatesDirectory)) return;
        try
        {
            var updatesRoot = EnsureDirectoryRoot(_updatesDirectory);
            string? retainedArchive = null;
            if (File.Exists(PendingManifestPath))
            {
                try
                {
                    var pending = JsonSerializer.Deserialize<PendingApplicationUpdate>(
                        File.ReadAllText(PendingManifestPath), JsonOptions);
                    if (pending is not null)
                    {
                        var candidate = Path.GetFullPath(pending.ArchivePath);
                        if (candidate.StartsWith(updatesRoot, StringComparison.OrdinalIgnoreCase))
                            retainedArchive = candidate;
                    }
                }
                catch
                {
                    // A damaged pending manifest is handled by the normal startup path.
                }
            }

            foreach (var file in Directory.EnumerateFiles(_updatesDirectory, "updater-*.exe", SearchOption.TopDirectoryOnly))
                TryDeleteFile(file);
            foreach (var file in Directory.EnumerateFiles(_updatesDirectory, "failed-update-*.json", SearchOption.TopDirectoryOnly))
                TryDeleteFile(file);
            TryDeleteFile(PendingManifestPath + ".tmp");

            foreach (var directory in Directory.EnumerateDirectories(_updatesDirectory, "extract-*", SearchOption.TopDirectoryOnly))
                TryDeleteDirectory(directory);
            foreach (var directory in Directory.EnumerateDirectories(_updatesDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                var directoryRoot = EnsureDirectoryRoot(directory);
                if (retainedArchive is not null && retainedArchive.StartsWith(directoryRoot, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                        if (!string.Equals(Path.GetFullPath(file), retainedArchive, StringComparison.OrdinalIgnoreCase))
                            TryDeleteFile(file);
                    continue;
                }
                TryDeleteDirectory(directory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Cleanup is best-effort and must never prevent the installed application from starting.
        }
    }

    private static string EnsureDirectoryRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Path.EndsInDirectorySeparator(fullPath) ? fullPath : fullPath + Path.DirectorySeparatorChar;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = string.Empty;
        [JsonPropertyName("digest")] public string? Digest { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }
}

public sealed record ApplicationUpdateCheckResult(
    bool IsUpdateAvailable,
    string Version,
    string AssetName,
    string DownloadUrl,
    string Sha256,
    long Size);

public sealed record PendingApplicationUpdate(string Version, string ArchivePath, string Sha256, DateTime DownloadedUtc);
public sealed record ApplicationUpdateFailure(DateTimeOffset FailedAt, string Message, string LogPath);
