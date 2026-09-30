using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameValueEditor.Models;
using GameValueEditor.ModuleSdk;

namespace GameValueEditor.Services.Adapters;

public sealed class GameModuleCatalogService
{
    public const string DefaultCatalogUrl =
        "https://raw.githubusercontent.com/BestWishes/GameValueEditor-Modules/main/catalog.json";

    private readonly string _modulesDirectory;
    private readonly HttpClient _httpClient;
    private readonly DownloadTimeoutPolicy _downloadTimeoutPolicy;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public GameModuleCatalogService(
        string modulesDirectory,
        HttpClient? httpClient = null,
        DownloadTimeoutPolicy? downloadTimeoutPolicy = null)
    {
        _modulesDirectory = modulesDirectory;
        _httpClient = httpClient ?? new HttpClient();
        _downloadTimeoutPolicy = downloadTimeoutPolicy ?? DownloadTimeoutPolicy.Default;
        if (httpClient is null)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("GameValueEditor-Modules/1.0");
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }
        CleanupPendingDeletions();
    }

    public InstalledModuleRecord? FindInstalled(string moduleId) =>
        LoadInstalled().Modules.FirstOrDefault(item => string.Equals(item.Id, moduleId, StringComparison.Ordinal));

    public IReadOnlyList<InstalledModuleRecord> GetInstalledModules() => LoadInstalled().Modules;

    public InstalledModuleManifest? GetInstalledManifest(string moduleId)
    {
        var record = FindInstalled(moduleId);
        if (record is null || !IsSafePathSegment(record.Id) || !IsSafePathSegment(record.Version)) return null;
        var path = Path.Combine(_modulesDirectory, "packages", record.Id, record.Version, "module.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<InstalledModuleManifest>(File.ReadAllText(path), _jsonOptions); }
        catch { return null; }
    }

    public IReadOnlyList<InstalledModuleManifest> GetInstalledManifests() =>
        GetInstalledModules().Select(record => GetInstalledManifest(record.Id)).OfType<InstalledModuleManifest>().ToList();

    public async Task<GameModuleCheckResult> CheckAsync(
        GameProfile game,
        GameVersionProfile version,
        CancellationToken cancellationToken = default)
        => await CheckCoreAsync(game.ProcessName, version.BuildFingerprint, version.ExecutableSha256,
            version.GameAssemblySha256, version.MetadataSha256, cancellationToken);

    public async Task<GameModuleCheckResult> CheckAsync(
        string processName,
        VersionFingerprint fingerprint,
        CancellationToken cancellationToken = default)
        => await CheckCoreAsync(processName, fingerprint.BuildSha256, fingerprint.Sha256,
            fingerprint.GameAssemblySha256, fingerprint.MetadataSha256, cancellationToken);

    private async Task<GameModuleCheckResult> CheckCoreAsync(
        string processName,
        string buildFingerprint,
        string executableSha256,
        string gameAssemblySha256,
        string metadataSha256,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(DefaultCatalogUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var catalog = await JsonSerializer.DeserializeAsync<GameModuleCatalog>(stream, _jsonOptions, cancellationToken)
                      ?? throw new InvalidOperationException("无法读取游戏专属模块清单。");
        if (catalog.HostApiVersion > ModuleHostApi.CurrentVersion)
            throw new InvalidOperationException(
                $"服务器模块清单需要接口版本 {catalog.HostApiVersion}，当前应用最高支持 {ModuleHostApi.CurrentVersion}。");

        var compatible = catalog.Modules
            .Where(module => module.HostApiVersion is >= 1 and <= ModuleHostApi.CurrentVersion &&
                             Matches(module, processName, buildFingerprint, executableSha256,
                                 gameAssemblySha256, metadataSha256))
            .OrderByDescending(module => ParseVersion(module.Version))
            .FirstOrDefault();
        if (compatible is null)
            return new GameModuleCheckResult(GameModuleAvailability.NotAvailable, null, null,
                "服务器暂无适用于当前游戏和版本的专属模块。");

        var installed = FindInstalled(compatible.Id);
        if (installed is null)
            return new GameModuleCheckResult(GameModuleAvailability.Available, compatible, null,
                $"发现可下载模块：{compatible.DisplayName} v{compatible.Version}");
        var installedVersion = ParseVersion(installed.Version);
        var remoteVersion = ParseVersion(compatible.Version);
        return remoteVersion.CompareTo(installedVersion) > 0
            ? new GameModuleCheckResult(GameModuleAvailability.UpdateAvailable, compatible, installed,
                $"发现模块更新：v{installed.Version} → v{compatible.Version}")
            : new GameModuleCheckResult(GameModuleAvailability.Current, compatible, installed,
                $"本地专属模块已是最新：v{installed.Version}");
    }

    public async Task InstallAsync(
        GameModuleCatalogEntry module,
        IProgress<DownloadProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSafePathSegment(module.Id, "模块 ID");
        EnsureSafePathSegment(module.Version, "模块版本");
        if (!Uri.TryCreate(module.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("模块下载地址必须使用 HTTPS。");
        if (string.IsNullOrWhiteSpace(module.Sha256) || module.Sha256.Length != 64)
            throw new InvalidOperationException("模块清单缺少有效的 SHA-256 校验值。");
        if (HasPendingDeletion(module.Id) && !await DeletePackageAsync(module.Id, cancellationToken))
            throw new InvalidOperationException("上次卸载的模块文件仍被占用，请重启肝肾大圣后再安装。");
        Directory.CreateDirectory(_modulesDirectory);
        var downloadsDirectory = Path.Combine(_modulesDirectory, "downloads");
        Directory.CreateDirectory(downloadsDirectory);
        var temporaryArchive = Path.Combine(downloadsDirectory, $"{Guid.NewGuid():N}.download");
        try
        {
            await HttpDownloadService.DownloadToFileAsync(_httpClient, module.DownloadUrl, temporaryArchive, 0,
                progress, _downloadTimeoutPolicy, cancellationToken);
            var hash = await ComputeSha256Async(temporaryArchive, cancellationToken);
            if (!hash.Equals(module.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("专属模块 SHA-256 校验失败，已拒绝安装。");

            var packagesRoot = Path.GetFullPath(Path.Combine(_modulesDirectory, "packages")) + Path.DirectorySeparatorChar;
            var packageRoot = Path.GetFullPath(Path.Combine(packagesRoot, module.Id));
            var finalDirectory = Path.GetFullPath(Path.Combine(packageRoot, module.Version));
            if (!packageRoot.StartsWith(packagesRoot, StringComparison.OrdinalIgnoreCase) ||
                !finalDirectory.StartsWith(packageRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("模块安装路径无效。");
            var temporaryDirectory = finalDirectory + $".tmp-{Guid.NewGuid():N}";
            Directory.CreateDirectory(temporaryDirectory);
            try
            {
                ExtractSafely(temporaryArchive, temporaryDirectory);
                ValidatePackage(temporaryDirectory, module);
                Directory.CreateDirectory(packageRoot);
                if (Directory.Exists(finalDirectory)) Directory.Delete(finalDirectory, true);
                Directory.Move(temporaryDirectory, finalDirectory);
            }
            finally
            {
                if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true);
            }

            var document = LoadInstalled();
            document.Modules.RemoveAll(item => string.Equals(item.Id, module.Id, StringComparison.Ordinal));
            foreach (var legacyId in module.LegacyIds)
                document.Modules.RemoveAll(item => string.Equals(item.Id, legacyId, StringComparison.Ordinal));
            document.Modules.Add(new InstalledModuleRecord(module.Id, module.Version, DateTime.UtcNow));
            SaveInstalled(document);
        }
        finally
        {
            try { if (File.Exists(temporaryArchive)) File.Delete(temporaryArchive); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public InstalledModuleRecord? Unregister(string moduleId)
    {
        var document = LoadInstalled();
        var record = document.Modules.FirstOrDefault(item => string.Equals(item.Id, moduleId, StringComparison.Ordinal));
        if (record is null) return null;
        document.Modules.Remove(record);
        SaveInstalled(document);
        return record;
    }

    public void RestoreRegistration(InstalledModuleRecord record)
    {
        var document = LoadInstalled();
        document.Modules.RemoveAll(item => string.Equals(item.Id, record.Id, StringComparison.Ordinal));
        document.Modules.Add(record);
        SaveInstalled(document);
    }

    public async Task<bool> DeletePackageAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        EnsureSafePathSegment(moduleId, "模块 ID");
        AddPendingDeletion(moduleId);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                DeletePackageOnce(moduleId);
                RemovePendingDeletion(moduleId);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == 5) return false;
                await Task.Delay(TimeSpan.FromMilliseconds(150 * (attempt + 1)), cancellationToken);
            }
        }
        return false;
    }

    public bool HasPendingDeletion(string moduleId) =>
        LoadPendingDeletions().ModuleIds.Contains(moduleId, StringComparer.Ordinal);

    private string PendingDeletionsPath => Path.Combine(_modulesDirectory, "pending-deletions.json");

    private void CleanupPendingDeletions()
    {
        var document = LoadPendingDeletions();
        var remaining = new List<string>();
        foreach (var moduleId in document.ModuleIds.Distinct(StringComparer.Ordinal))
        {
            if (!IsSafePathSegment(moduleId)) continue;
            try { DeletePackageOnce(moduleId); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                remaining.Add(moduleId);
            }
        }
        SavePendingDeletions(new PendingModuleDeletionDocument { ModuleIds = remaining });
    }

    private void DeletePackageOnce(string moduleId)
    {
        EnsureSafePathSegment(moduleId, "模块 ID");
        var packagesRoot = Path.GetFullPath(Path.Combine(_modulesDirectory, "packages")) + Path.DirectorySeparatorChar;
        var packageRoot = Path.GetFullPath(Path.Combine(packagesRoot, moduleId));
        if (!packageRoot.StartsWith(packagesRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("模块卸载路径无效。");
        if (Directory.Exists(packageRoot)) Directory.Delete(packageRoot, true);
    }

    private PendingModuleDeletionDocument LoadPendingDeletions()
    {
        if (!File.Exists(PendingDeletionsPath)) return new PendingModuleDeletionDocument();
        try
        {
            return JsonSerializer.Deserialize<PendingModuleDeletionDocument>(
                       File.ReadAllText(PendingDeletionsPath), _jsonOptions)
                   ?? new PendingModuleDeletionDocument();
        }
        catch
        {
            return new PendingModuleDeletionDocument();
        }
    }

    private void AddPendingDeletion(string moduleId)
    {
        var document = LoadPendingDeletions();
        if (!document.ModuleIds.Contains(moduleId, StringComparer.Ordinal)) document.ModuleIds.Add(moduleId);
        SavePendingDeletions(document);
    }

    private void RemovePendingDeletion(string moduleId)
    {
        var document = LoadPendingDeletions();
        document.ModuleIds.RemoveAll(item => string.Equals(item, moduleId, StringComparison.Ordinal));
        SavePendingDeletions(document);
    }

    private void SavePendingDeletions(PendingModuleDeletionDocument document)
    {
        if (document.ModuleIds.Count == 0)
        {
            if (File.Exists(PendingDeletionsPath)) File.Delete(PendingDeletionsPath);
            return;
        }
        Directory.CreateDirectory(_modulesDirectory);
        var temporary = PendingDeletionsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, _jsonOptions));
        File.Move(temporary, PendingDeletionsPath, true);
    }

    private InstalledModuleDocument LoadInstalled()
    {
        var path = Path.Combine(_modulesDirectory, "installed.json");
        if (!File.Exists(path)) return new InstalledModuleDocument();
        try
        {
            return JsonSerializer.Deserialize<InstalledModuleDocument>(File.ReadAllText(path), _jsonOptions)
                   ?? new InstalledModuleDocument();
        }
        catch
        {
            return new InstalledModuleDocument();
        }
    }

    private void SaveInstalled(InstalledModuleDocument document)
    {
        Directory.CreateDirectory(_modulesDirectory);
        var path = Path.Combine(_modulesDirectory, "installed.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, _jsonOptions));
        File.Move(temporary, path, true);
    }

    private void ValidatePackage(string directory, GameModuleCatalogEntry catalogEntry)
    {
        var manifestPath = Path.Combine(directory, "module.json");
        if (!File.Exists(manifestPath)) throw new InvalidOperationException("专属模块包缺少 module.json。");
        var manifest = JsonSerializer.Deserialize<InstalledModuleManifest>(File.ReadAllText(manifestPath), _jsonOptions)
                       ?? throw new InvalidOperationException("专属模块包的 module.json 无效。");
        if (!string.Equals(manifest.Id, catalogEntry.Id, StringComparison.Ordinal) ||
            !string.Equals(manifest.Version, catalogEntry.Version, StringComparison.OrdinalIgnoreCase) ||
            manifest.HostApiVersion != catalogEntry.HostApiVersion ||
            manifest.HostApiVersion is < 1 or > ModuleHostApi.CurrentVersion)
            throw new InvalidOperationException("专属模块包与服务器清单不一致。");
        if (!string.Equals(manifest.GameDisplayName, catalogEntry.GameDisplayName, StringComparison.Ordinal) ||
            !manifest.ProcessNames.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(catalogEntry.ProcessNames) ||
            !manifest.Editors.Select(item => item.Id).ToHashSet(StringComparer.Ordinal)
                .SetEquals(catalogEntry.Editors.Select(item => item.Id)))
            throw new InvalidOperationException("专属模块包的游戏身份或编辑器清单与服务器不一致。");
        var packageRoot = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var assemblyPath = Path.GetFullPath(Path.Combine(packageRoot, manifest.AssemblyFile));
        if (!assemblyPath.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(assemblyPath))
            throw new InvalidOperationException("专属模块包缺少声明的程序集。");
    }

    private static void ExtractSafely(string archivePath, string destination)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("专属模块包包含不安全路径。");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    private static bool Matches(
        GameModuleCatalogEntry module,
        string processName,
        string buildFingerprint,
        string executableSha256,
        string gameAssemblySha256,
        string metadataSha256)
    {
        if (module.ProcessNames.Count > 0 &&
            !module.ProcessNames.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)))
            return false;
        return module.CompatibleBuilds.Any(build =>
            MatchOptional(build.BuildFingerprint, buildFingerprint) &&
            MatchOptional(build.ExecutableSha256, executableSha256) &&
            MatchOptional(build.GameAssemblySha256, gameAssemblySha256) &&
            MatchOptional(build.MetadataSha256, metadataSha256));
    }

    private static bool MatchOptional(string expected, string actual) =>
        string.IsNullOrWhiteSpace(expected) || string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    private static SemanticVersion ParseVersion(string value) =>
        SemanticVersion.TryParse(value, out var version)
            ? version
            : throw new InvalidOperationException($"无效的模块版本：{value}");

    private static void EnsureSafePathSegment(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar))
            throw new InvalidOperationException($"{label}包含不安全字符。");
    }

    private static bool IsSafePathSegment(string value) =>
        !string.IsNullOrWhiteSpace(value) && value is not "." and not ".." &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !value.Contains(Path.DirectorySeparatorChar) && !value.Contains(Path.AltDirectorySeparatorChar);

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}

public sealed class PendingModuleDeletionDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<string> ModuleIds { get; set; } = [];
}

public enum GameModuleAvailability { NotChecked, NotAvailable, Available, UpdateAvailable, Current }

public sealed record GameModuleCheckResult(
    GameModuleAvailability Availability,
    GameModuleCatalogEntry? RemoteModule,
    InstalledModuleRecord? InstalledModule,
    string StatusText);

public sealed class GameModuleCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public int HostApiVersion { get; set; } = ModuleHostApi.CurrentVersion;
    public List<GameModuleCatalogEntry> Modules { get; set; } = [];
}

public sealed class GameModuleCatalogEntry
{
    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string GameDisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int HostApiVersion { get; set; } = 1;
    public List<string> LegacyIds { get; set; } = [];
    public List<GameModuleEditorEntry> Editors { get; set; } = [];
    public List<string> ProcessNames { get; set; } = [];
    public List<GameModuleBuildMatch> CompatibleBuilds { get; set; } = [];
    public List<GameModuleContributor> Contributors { get; set; } = [];
    public string DownloadUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class GameModuleContributor
{
    public long GithubId { get; set; }
    public string GithubLogin { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ProfileUrl { get; set; } = string.Empty;
    public DateOnly FirstContributionDate { get; set; }
    public DateOnly LatestContributionDate { get; set; }
}

public sealed class GameModuleEditorEntry
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int Order { get; set; }
    public bool SessionOnly { get; set; }
}

public sealed class GameModuleBuildMatch
{
    public string BuildFingerprint { get; set; } = string.Empty;
    public string ExecutableSha256 { get; set; } = string.Empty;
    public string GameAssemblySha256 { get; set; } = string.Empty;
    public string MetadataSha256 { get; set; } = string.Empty;
}
