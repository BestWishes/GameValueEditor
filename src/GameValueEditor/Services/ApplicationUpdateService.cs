using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameValueEditor.Updates;

namespace GameValueEditor.Services;

public sealed class ApplicationUpdateService
{
    public const string ReleaseIndexUrl =
        "https://raw.githubusercontent.com/BestWishes/GameValueEditor/main/release-index.json";
    private const string ReleasesApi = "https://api.github.com/repos/BestWishes/GameValueEditor/releases?per_page=50";
    private readonly HttpClient _httpClient;
    private readonly string _updatesDirectory;
    private readonly string _currentVersion;
    private readonly DownloadTimeoutPolicy _downloadTimeoutPolicy;
    private readonly string _applicationDirectory;

    public ApplicationUpdateService(
        string updatesDirectory,
        HttpClient? httpClient = null,
        string? currentVersion = null,
        DownloadTimeoutPolicy? downloadTimeoutPolicy = null,
        string? applicationDirectory = null)
    {
        _updatesDirectory = updatesDirectory;
        _applicationDirectory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
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
    public string RecoveryRequiredPath => Path.Combine(_updatesDirectory, "recovery-required.json");

    internal void EnsureNoRecoveryRequired()
    {
        if (File.Exists(RecoveryRequiredPath))
            throw new ApplicationRecoveryRequiredException($"安装尚未完成，请先按恢复记录手动恢复：{RecoveryRequiredPath}");
        if (UpdateRecovery.FindIncomplete(_applicationDirectory) is { } journal)
            throw new ApplicationRecoveryRequiredException($"发现未完成的安装事务，请先使用保留的更新器 --recover 此记录 --app-dir 应用目录：{journal}");
    }

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
        using var indexResponse = await _httpClient.GetAsync(ReleaseIndexUrl, cancellationToken);
        indexResponse.EnsureSuccessStatusCode();
        await using var indexStream = await indexResponse.Content.ReadAsStreamAsync(cancellationToken);
        var index = await JsonSerializer.DeserializeAsync<ApplicationReleaseIndex>(indexStream, JsonOptions, cancellationToken)
                    ?? throw new InvalidOperationException("无法读取主程序发布索引。");
        if (index.SchemaVersion != 1 || index.Releases.Count is < 1 or > 3)
            throw new InvalidOperationException("主程序发布索引的版本或保留数量无效。");
        if (!SemanticVersion.TryParse(_currentVersion, out var current))
            throw new InvalidOperationException($"当前应用版本号无效：{_currentVersion}");

        var targets = index.Releases.Select(ValidateReleaseTarget)
            .OrderByDescending(target => ParseVersion(target.Version))
            .ToList();
        if (targets.Select(target => target.Version).Distinct(StringComparer.Ordinal).Count() != targets.Count)
            throw new InvalidOperationException("主程序发布索引包含重复版本。");

        using var releasesResponse = await _httpClient.GetAsync(ReleasesApi, cancellationToken);
        releasesResponse.EnsureSuccessStatusCode();
        await using var releasesStream = await releasesResponse.Content.ReadAsStreamAsync(cancellationToken);
        var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(releasesStream, JsonOptions, cancellationToken)
                       ?? throw new InvalidOperationException("无法读取 GitHub 版本信息。");
        foreach (var target in targets)
        {
            var release = releases.SingleOrDefault(candidate => !candidate.Draft && !candidate.Prerelease &&
                string.Equals(candidate.TagName, $"v{target.Version}", StringComparison.OrdinalIgnoreCase));
            var asset = release?.Assets.SingleOrDefault(candidate =>
                string.Equals(candidate.Name, target.AssetName, StringComparison.OrdinalIgnoreCase));
            if (asset is null || asset.Size != target.SizeBytes ||
                !string.Equals(asset.DownloadUrl, target.DownloadUrl, StringComparison.Ordinal))
                throw new InvalidOperationException($"主程序 v{target.Version} 的发布资产与索引不一致。");
            var digest = asset.Digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true
                ? asset.Digest[7..]
                : string.Empty;
            if (digest.Length > 0 && !digest.Equals(target.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"主程序 v{target.Version} 的 GitHub 摘要与索引不一致。");
        }

        var updateTarget = targets.FirstOrDefault(target => ParseVersion(target.Version).CompareTo(current) > 0);
        var rollbackTarget = targets.FirstOrDefault(target => ParseVersion(target.Version).CompareTo(current) < 0);
        return new ApplicationUpdateCheckResult(_currentVersion, updateTarget, rollbackTarget);
    }

    public IReadOnlyList<ApplicationRollbackBlock> FindRollbackBlocks(
        ApplicationReleaseTarget target,
        IEnumerable<Adapters.InstalledModuleManifest> installedModules)
    {
        var targetVersion = ParseVersion(target.Version);
        var blocks = new List<ApplicationRollbackBlock>();
        foreach (var module in installedModules)
        {
            var reasons = new List<string>();
            if (module.HostApiVersion < target.MinimumModuleHostApi || module.HostApiVersion > target.MaximumModuleHostApi)
                reasons.Add($"需要 Host API {module.HostApiVersion}，目标主程序只支持 {target.MinimumModuleHostApi}–{target.MaximumModuleHostApi}");
            if (!string.IsNullOrWhiteSpace(module.MinimumHostVersion) &&
                ParseVersion(module.MinimumHostVersion).CompareTo(targetVersion) > 0)
                reasons.Add($"最低主程序版本为 {module.MinimumHostVersion}");
            if (!string.IsNullOrWhiteSpace(module.MaximumHostVersion) &&
                ParseVersion(module.MaximumHostVersion).CompareTo(targetVersion) < 0)
                reasons.Add($"最高主程序版本为 {module.MaximumHostVersion}");
            if (reasons.Count > 0)
                blocks.Add(new ApplicationRollbackBlock(module.Id, module.DisplayName, module.Version,
                    string.Join("；", reasons)));
        }
        return blocks;
    }

    public async Task<PendingApplicationUpdate> DownloadAsync(
        ApplicationReleaseTarget target,
        ApplicationUpdateOperation operation,
        IProgress<DownloadProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNoRecoveryRequired();
        var current = ParseVersion(_currentVersion);
        var requested = ParseVersion(target.Version);
        if (operation == ApplicationUpdateOperation.Update && requested.CompareTo(current) <= 0)
            throw new InvalidOperationException("更新目标必须严格高于当前主程序版本。");
        if (operation == ApplicationUpdateOperation.Rollback && requested.CompareTo(current) >= 0)
            throw new InvalidOperationException("回退目标必须严格低于当前主程序版本。");
        ValidateReleaseTarget(target);

        var versionSegment = Sanitize(target.Version);
        if (versionSegment is "." or ".." || string.IsNullOrWhiteSpace(versionSegment))
            throw new InvalidOperationException("更新版本号无法用于本地路径。");
        var assetName = Path.GetFileName(target.AssetName);
        if (string.IsNullOrWhiteSpace(assetName) || !string.Equals(assetName, target.AssetName, StringComparison.Ordinal))
            throw new InvalidOperationException("更新包文件名包含不安全路径。");
        var versionDirectory = Path.Combine(_updatesDirectory, versionSegment);
        Directory.CreateDirectory(versionDirectory);
        var finalPath = Path.Combine(versionDirectory, assetName);
        var temporaryPath = finalPath + ".download";
        try
        {
            await HttpDownloadService.DownloadToFileAsync(_httpClient, target.DownloadUrl, temporaryPath, target.SizeBytes,
                progress, _downloadTimeoutPolicy, cancellationToken);

            var actualHash = await ComputeSha256Async(temporaryPath, cancellationToken);
            if (!actualHash.Equals(target.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("更新包 SHA-256 校验失败，已拒绝安装。");
            progress?.Report(new DownloadProgressSnapshot(target.SizeBytes, target.SizeBytes) { Phase = DownloadPhase.Installing });
            File.Move(temporaryPath, finalPath, true);

            var pending = new PendingApplicationUpdate(target.Version, finalPath, actualHash, DateTime.UtcNow, operation,
                new ApplicationUpdateCompatibility(target.MinimumModuleHostApi, target.MaximumModuleHostApi, target.MaximumCatalogSchemaVersion));
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
        EnsureNoRecoveryRequired();
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
        start.ArgumentList.Add(_applicationDirectory);
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
        EnsureNoRecoveryRequired();
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

        using var moduleMutation = ModuleMutationLock.Acquire(Path.Combine(_applicationDirectory, "data", "modules"));
        var compatibility = ApplicationUpdateCompatibilityValidator.ResolveVerifiedArchive(archivePath, pending.Version, pending.Compatibility);
        ApplicationUpdateCompatibilityValidator.Validate(_applicationDirectory, pending.Version, compatibility);
        var installedUpdater = Path.Combine(_applicationDirectory, "GameValueEditor.Updater.exe");
        if (!File.Exists(installedUpdater) || (File.GetAttributes(installedUpdater) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("当前安装的可信更新器缺失或为链接，已拒绝使用目标旧版本的更新器；请重新解压当前主程序包。");

        Directory.CreateDirectory(_updatesDirectory);
        var runner = Path.Combine(_updatesDirectory, $"recovery-runner-{Guid.NewGuid():N}.exe");
        try
        {
            using var source = File.OpenRead(installedUpdater);
            using var destination = new FileStream(runner, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.CopyTo(destination);
            if (destination.Length == 0) throw new InvalidOperationException("当前安装的更新程序为空。");
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
        service.EnsureNoRecoveryRequired();
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
        if (UpdateRecovery.FindIncomplete(_applicationDirectory) is null && Directory.Exists(_applicationDirectory) &&
            Directory.EnumerateDirectories(_applicationDirectory, ".update-transaction-*", SearchOption.TopDirectoryOnly).Any())
        {
            using var mutation = ModuleMutationLock.Acquire(Path.Combine(_applicationDirectory, "data", "modules"));
            UpdateRecovery.CleanupCommitted(_applicationDirectory);
        }
        // Recovery may need the original runner, archive and failure record. Preserve all evidence.
        if (File.Exists(RecoveryRequiredPath) || UpdateRecovery.FindIncomplete(_applicationDirectory) is not null) return;
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
            foreach (var file in Directory.EnumerateFiles(_updatesDirectory, "recovery-runner-*.exe", SearchOption.TopDirectoryOnly))
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

    private static ApplicationReleaseTarget ValidateReleaseTarget(ApplicationReleaseTarget target)
    {
        _ = ParseVersion(target.Version);
        if (!string.Equals(target.AssetName, $"GameValueEditor-v{target.Version}-win-x64.zip", StringComparison.Ordinal))
            throw new InvalidOperationException($"主程序 v{target.Version} 的资产名无效。");
        if (!Uri.TryCreate(target.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"主程序 v{target.Version} 的下载地址必须使用 HTTPS。");
        if (target.SizeBytes <= 0 || string.IsNullOrWhiteSpace(target.Sha256) ||
            target.Sha256.Length != 64 || !target.Sha256.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(target.SourceCommit) || target.SourceCommit.Length != 40 ||
            !target.SourceCommit.All(Uri.IsHexDigit))
            throw new InvalidOperationException($"主程序 v{target.Version} 的资产校验元数据无效。");
        if (target.MinimumModuleHostApi < 1 || target.MaximumModuleHostApi < target.MinimumModuleHostApi ||
            target.MaximumCatalogSchemaVersion < 1)
            throw new InvalidOperationException($"主程序 v{target.Version} 的兼容范围无效。");
        return target;
    }

    private static SemanticVersion ParseVersion(string value) =>
        SemanticVersion.TryParse(value, out var version)
            ? version
            : throw new InvalidOperationException($"无效的主程序版本：{value}");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

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

public sealed class ApplicationReleaseIndex
{
    public int SchemaVersion { get; set; } = 1;
    public List<ApplicationReleaseTarget> Releases { get; set; } = [];
}

public sealed record ApplicationReleaseTarget(
    string Version,
    string AssetName,
    string DownloadUrl,
    long SizeBytes,
    string Sha256,
    string SourceCommit,
    int MinimumModuleHostApi,
    int MaximumModuleHostApi,
    int MaximumCatalogSchemaVersion);

public sealed record ApplicationUpdateCheckResult(
    string CurrentVersion,
    ApplicationReleaseTarget? UpdateTarget,
    ApplicationReleaseTarget? RollbackTarget)
{
    public bool IsUpdateAvailable => UpdateTarget is not null;
}

public enum ApplicationUpdateOperation { Update, Rollback }

public sealed record PendingApplicationUpdate(
    string Version,
    string ArchivePath,
    string Sha256,
    DateTime DownloadedUtc,
    ApplicationUpdateOperation Operation = ApplicationUpdateOperation.Update,
    ApplicationUpdateCompatibility? Compatibility = null);
public sealed class ApplicationRecoveryRequiredException(string message) : InvalidOperationException(message);
public sealed record ApplicationRollbackBlock(string ModuleId, string DisplayName, string ModuleVersion, string Reason);
public sealed record ApplicationUpdateFailure(DateTimeOffset FailedAt, string Message, string LogPath);
