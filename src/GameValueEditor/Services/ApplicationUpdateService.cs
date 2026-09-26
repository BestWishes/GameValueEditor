using System.Diagnostics;
using System.IO;
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

    public ApplicationUpdateService(
        string updatesDirectory,
        HttpClient? httpClient = null,
        string? currentVersion = null)
    {
        _updatesDirectory = updatesDirectory;
        _currentVersion = currentVersion ?? ApplicationVersion.Current;
        _httpClient = httpClient ?? new HttpClient();
        if (httpClient is null)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("GameValueEditor-Updater/1.0");
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }
    }

    public string PendingManifestPath => Path.Combine(_updatesDirectory, "pending-update.json");

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

        var acceptPrerelease = !string.IsNullOrEmpty(current.PreRelease);
        GitHubRelease? latestRelease = null;
        GitHubAsset? asset = null;
        SemanticVersion latest = default;
        var latestText = string.Empty;
        foreach (var release in releases)
        {
            if (release.Draft) continue;
            var releaseText = release.TagName.TrimStart('v', 'V');
            if (!SemanticVersion.TryParse(releaseText, out var releaseVersion)) continue;
            if (!acceptPrerelease && (release.Prerelease || !string.IsNullOrEmpty(releaseVersion.PreRelease)))
                continue;
            var releaseAsset = release.Assets.FirstOrDefault(IsApplicationPackage);
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

    private static bool IsApplicationPackage(GitHubAsset asset) =>
        asset.Name.StartsWith("GameValueEditor-v", StringComparison.OrdinalIgnoreCase) &&
        asset.Name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase);

    public async Task<PendingApplicationUpdate> DownloadAsync(
        ApplicationUpdateCheckResult update,
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
        using (var response = await _httpClient.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target, cancellationToken);
        }

        var file = new FileInfo(temporaryPath);
        if (update.Size > 0 && file.Length != update.Size)
        {
            File.Delete(temporaryPath);
            throw new InvalidOperationException("更新包大小与 GitHub 记录不一致。");
        }
        var actualHash = await ComputeSha256Async(temporaryPath, cancellationToken);
        if (!actualHash.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temporaryPath);
            throw new InvalidOperationException("更新包 SHA-256 校验失败，已拒绝安装。");
        }
        File.Move(temporaryPath, finalPath, true);

        var pending = new PendingApplicationUpdate(update.Version, finalPath, actualHash, DateTime.UtcNow);
        var pendingTemporary = PendingManifestPath + ".tmp";
        await File.WriteAllTextAsync(pendingTemporary, JsonSerializer.Serialize(pending, JsonOptions), cancellationToken);
        File.Move(pendingTemporary, PendingManifestPath, true);
        return pending;
    }

    public bool LaunchPendingUpdate(bool restartApplication)
    {
        if (!File.Exists(PendingManifestPath)) return false;
        var updater = Path.Combine(AppContext.BaseDirectory, "GameValueEditor.Updater.exe");
        if (!File.Exists(updater))
            throw new InvalidOperationException("应用目录缺少 GameValueEditor.Updater.exe，无法安装已下载更新。");
        Directory.CreateDirectory(_updatesDirectory);
        var runner = Path.Combine(_updatesDirectory, $"updater-{Guid.NewGuid():N}.exe");
        File.Copy(updater, runner, true);
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
        _ = Process.Start(start) ?? throw new InvalidOperationException("无法启动更新程序。");
        return true;
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
