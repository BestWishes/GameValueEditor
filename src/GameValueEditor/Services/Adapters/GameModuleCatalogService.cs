using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameValueEditor.Models;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Updates;

namespace GameValueEditor.Services.Adapters;

public sealed class GameModuleCatalogService
{
    public const string DefaultCatalogUrl =
        "https://raw.githubusercontent.com/BestWishes/GameValueEditor-Modules/main/catalog.json";

    private readonly string _modulesDirectory;
    private readonly HttpClient _httpClient;
    private readonly DownloadTimeoutPolicy _downloadTimeoutPolicy;
    private readonly string _currentHostVersion;
    private readonly ModuleStateStore _stateStore;
    private readonly ModuleInstallTransaction _transactions;
    private readonly string? _initializationError;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public GameModuleCatalogService(
        string modulesDirectory,
        HttpClient? httpClient = null,
        DownloadTimeoutPolicy? downloadTimeoutPolicy = null,
        string? currentHostVersion = null)
    {
        _modulesDirectory = Path.GetFullPath(modulesDirectory);
        _stateStore = new ModuleStateStore(_modulesDirectory);
        _transactions = new ModuleInstallTransaction(_stateStore);
        _httpClient = httpClient ?? new HttpClient();
        _downloadTimeoutPolicy = downloadTimeoutPolicy ?? DownloadTimeoutPolicy.Default;
        _currentHostVersion = currentHostVersion ?? ApplicationVersion.Current;
        if (httpClient is null)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("GameValueEditor-Modules/1.0");
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }
        try
        {
            using (ModuleMutationLock.Acquire(_modulesDirectory)) _transactions.RecoverAll();
            CleanupPendingDeletions();
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            _initializationError = $"模块资料/安装恢复：{exception.Message}";
        }
    }

    public IReadOnlyList<string> StorageErrors
    {
        get
        {
            var errors = new List<string>();
            if (_initializationError is not null) errors.Add(_initializationError);
            try { _stateStore.ReadInstalled(); }
            catch (Exception exception) when (IsStorageFailure(exception)) { errors.Add($"installed.json: {exception.Message}"); }
            try { _stateStore.ReadDeletions(); }
            catch (Exception exception) when (IsStorageFailure(exception)) { errors.Add($"pending-deletions.json: {exception.Message}"); }
            try { _transactions.EnsureNoTransactions(); }
            catch (Exception exception) when (IsStorageFailure(exception)) { errors.Add($"模块安装事务：{exception.Message}"); }
            return errors.Distinct(StringComparer.Ordinal).ToArray();
        }
    }

    private void EnsureStorageSafe()
    {
        if (_initializationError is not null) throw new InvalidOperationException(_initializationError);
        _stateStore.ReadInstalled();
        _stateStore.ReadDeletions();
        _transactions.EnsureNoTransactions();
    }

    private static bool IsStorageFailure(Exception exception) =>
        exception is IOException or InvalidDataException or JsonException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException;

    public InstalledModuleRecord? FindInstalled(string moduleId) =>
        LoadInstalled().Modules.FirstOrDefault(item => string.Equals(item.Id, moduleId, StringComparison.Ordinal));

    public IReadOnlyList<InstalledModuleRecord> GetInstalledModules() => LoadInstalled().Modules;

    public InstalledModuleManifest? GetInstalledManifest(string moduleId)
    {
        var record = FindInstalled(moduleId);
        if (record is null || !IsSafePathSegment(record.Id) || !IsSafePathSegment(record.Version)) return null;
        var path = Path.Combine(_modulesDirectory, "packages", record.Id, record.Version, "module.json");
        if (!File.Exists(path)) return null;
        try
        {
            _stateStore.EnsureNoLinks(path);
            var manifest = JsonSerializer.Deserialize<InstalledModuleManifest>(File.ReadAllText(path), _jsonOptions);
            return manifest?.Id == record.Id && manifest.Version == record.Version ? manifest : null;
        }
        catch { return null; }
    }

    public IReadOnlyList<InstalledModuleManifest> GetInstalledManifests() =>
        GetInstalledModules().Select(record => GetInstalledManifest(record.Id)).OfType<InstalledModuleManifest>().ToList();

    public async Task<GameModuleCheckResult> CheckAsync(
        GameProfile game,
        GameVersionProfile version,
        CancellationToken cancellationToken = default)
        => await CheckCoreAsync(game.ProcessName, version.BuildFingerprint, version.ExecutableSha256,
            version.GameAssemblySha256, version.MetadataSha256, version.PackageSha256, cancellationToken);

    public async Task<GameModuleCheckResult> CheckAsync(
        string processName,
        VersionFingerprint fingerprint,
        CancellationToken cancellationToken = default)
        => await CheckCoreAsync(processName, fingerprint.BuildSha256, fingerprint.Sha256,
            fingerprint.GameAssemblySha256, fingerprint.MetadataSha256, fingerprint.PackageSha256, cancellationToken);

    private async Task<GameModuleCheckResult> CheckCoreAsync(
        string processName,
        string buildFingerprint,
        string executableSha256,
        string gameAssemblySha256,
        string metadataSha256,
        string packageSha256,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(DefaultCatalogUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var catalog = await JsonSerializer.DeserializeAsync<GameModuleCatalog>(stream, _jsonOptions, cancellationToken)
                      ?? throw new InvalidOperationException("无法读取游戏专属模块清单。");
        if (catalog.SchemaVersion is < 1 or > 5)
            throw new InvalidOperationException(
                $"服务器模块清单 Schema {catalog.SchemaVersion} 超出当前应用支持范围 1–5。");
        if (catalog.HostApiVersion > ModuleHostApi.CurrentVersion)
            throw new InvalidOperationException(
                $"服务器模块清单需要接口版本 {catalog.HostApiVersion}，当前应用最高支持 {ModuleHostApi.CurrentVersion}。");

        var matchingModules = catalog.Modules.Where(module => MatchesProcess(module, processName)).ToList();
        var hostCompatibleCandidates = matchingModules
            .SelectMany(GetVersionCandidates)
            .Where(module => module.HostApiVersion is >= 1 and <= ModuleHostApi.CurrentVersion &&
                             IsHostCompatible(module))
            .OrderByDescending(module => ParseVersion(module.Version))
            .ToList();
        if (hostCompatibleCandidates.Count == 0)
            return new GameModuleCheckResult(GameModuleAvailability.NotAvailable, null, null,
                matchingModules.Count == 0
                    ? "服务器暂无适用于当前游戏的专属模块。"
                    : $"服务器有当前游戏的专属模块，但没有兼容主程序 v{_currentHostVersion} 与 Host API {ModuleHostApi.CurrentVersion} 的版本。");

        var exactCandidates = hostCompatibleCandidates.Where(module => MatchesBuild(module, buildFingerprint,
                executableSha256, gameAssemblySha256, metadataSha256, packageSha256))
            .ToList();
        var exactBuildMatch = exactCandidates.Count > 0;
        var candidates = exactBuildMatch
            ? exactCandidates
            : hostCompatibleCandidates
                .Where(module => module.SupportsUnlistedBuildValidation && module.HostApiVersion >= 5)
                .ToList();
        if (candidates.Count == 0)
            return new GameModuleCheckResult(GameModuleAvailability.NotAvailable, null, null,
                "服务器有当前游戏的专属模块，但没有精确兼容当前构建的版本，模块也未声明可对未知构建执行本地安全验证。",
                CatalogReferenceModule: hostCompatibleCandidates[0]);

        var compatible = candidates[0];
        candidates = candidates.Where(module => string.Equals(module.Id, compatible.Id, StringComparison.Ordinal))
            .ToList();

        var installed = FindInstalled(compatible.Id);
        if (installed is null)
            return new GameModuleCheckResult(GameModuleAvailability.Available, compatible, null,
                exactBuildMatch
                    ? $"发现可下载模块：{compatible.DisplayName} v{compatible.Version}"
                    : $"发现可下载模块：{compatible.DisplayName} v{compatible.Version}；当前构建尚未精确收录，下载后必须通过本地只读兼容验证才会启用。",
                exactBuildMatch, CatalogReferenceModule: compatible);
        var installedVersion = ParseVersion(installed.Version);
        var updateTarget = candidates.FirstOrDefault(module => ParseVersion(module.Version).CompareTo(installedVersion) > 0);
        var rollbackTarget = exactBuildMatch
            ? candidates.FirstOrDefault(module => ParseVersion(module.Version).CompareTo(installedVersion) < 0)
            : null;
        if (updateTarget is not null)
        {
            var updateExact = MatchesBuild(updateTarget, buildFingerprint, executableSha256,
                gameAssemblySha256, metadataSha256, packageSha256);
            return new GameModuleCheckResult(GameModuleAvailability.UpdateAvailable, updateTarget, installed,
                updateExact
                    ? $"发现模块更新：v{installed.Version} → v{updateTarget.Version}"
                    : $"发现模块更新 v{updateTarget.Version}；更新后会在本地安全验证当前构建。",
                updateExact, rollbackTarget, updateTarget);
        }

        var currentEntry = candidates.FirstOrDefault(module =>
                               ParseVersion(module.Version).CompareTo(installedVersion) == 0)
                           ?? compatible;
        var rollbackSuffix = rollbackTarget is null ? string.Empty : $"；可手动回退到 v{rollbackTarget.Version}";
        return new GameModuleCheckResult(GameModuleAvailability.Current, currentEntry, installed,
            exactBuildMatch
                ? $"本地专属模块已是最新：v{installed.Version}{rollbackSuffix}"
                : $"本地模块已是最新 v{installed.Version}；当前构建尚未明确收录，将由本地安全校验决定是否启用{rollbackSuffix}。",
            exactBuildMatch, rollbackTarget, currentEntry);
    }

    public async Task InstallAsync(
        GameModuleCatalogEntry module,
        IProgress<DownloadProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureStorageSafe();
        EnsureSafePathSegment(module.Id, "模块 ID");
        EnsureSafePathSegment(module.Version, "模块版本");
        if (!IsHostCompatible(module))
            throw new InvalidOperationException($"模块 v{module.Version} 与当前主程序 v{_currentHostVersion} 不兼容。");
        if (module.SizeBytes <= 0 || string.IsNullOrWhiteSpace(module.Sha256) ||
            module.Sha256.Length != 64 || !module.Sha256.All(Uri.IsHexDigit))
            throw new InvalidOperationException("模块下载大小或 SHA-256 元数据无效。");
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
            await HttpDownloadService.DownloadToFileAsync(_httpClient, module.DownloadUrl, temporaryArchive, module.SizeBytes,
                progress, _downloadTimeoutPolicy, cancellationToken);
            var hash = await ComputeSha256Async(temporaryArchive, cancellationToken);
            if (!hash.Equals(module.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("专属模块 SHA-256 校验失败，已拒绝安装。");

            progress?.Report(new DownloadProgressSnapshot(module.SizeBytes, module.SizeBytes) { Phase = DownloadPhase.Installing });
            using var mutation = await ModuleMutationLock.AcquireAsync(_modulesDirectory, cancellationToken);
            EnsureStorageSafe();
            var document = _stateStore.ReadInstalled();
            var targetDocument = new InstalledModuleDocument { Modules = document.Modules
                .Where(item => item.Id != module.Id && !module.LegacyIds.Contains(item.Id, StringComparer.Ordinal)).ToList() };
            targetDocument.Modules.Add(new InstalledModuleRecord(module.Id, module.Version, DateTime.UtcNow));
            ModuleStateStore.ValidateInstalled(targetDocument);
            var transactionDirectory = _transactions.CreateDirectory();
            var temporaryDirectory = Path.Combine(transactionDirectory, "staged");
            Directory.CreateDirectory(temporaryDirectory);
            try
            {
                ExtractSafely(temporaryArchive, temporaryDirectory);
                ValidatePackage(temporaryDirectory, module);
                _transactions.Commit(transactionDirectory, module.Id, module.Version, document, targetDocument, legacyIds: module.LegacyIds);
            }
            finally
            {
                if (Directory.Exists(transactionDirectory)) _transactions.DiscardUnprepared(transactionDirectory);
            }
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
        using var mutation = ModuleMutationLock.Acquire(_modulesDirectory);
        EnsureStorageSafe();
        var document = _stateStore.ReadInstalled();
        var record = document.Modules.FirstOrDefault(item => string.Equals(item.Id, moduleId, StringComparison.Ordinal));
        if (record is null) return null;
        document.Modules.Remove(record);
        SaveInstalled(document);
        return record;
    }

    public void RestoreRegistration(InstalledModuleRecord record)
    {
        using var mutation = ModuleMutationLock.Acquire(_modulesDirectory);
        EnsureStorageSafe();
        var document = _stateStore.ReadInstalled();
        document.Modules.RemoveAll(item => string.Equals(item.Id, record.Id, StringComparison.Ordinal));
        document.Modules.Add(record);
        SaveInstalled(document);
    }

    public async Task<bool> DeletePackageAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        using var mutation = await ModuleMutationLock.AcquireAsync(_modulesDirectory, cancellationToken);
        EnsureStorageSafe();
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

    private void CleanupPendingDeletions()
    {
        using var mutation = ModuleMutationLock.Acquire(_modulesDirectory);
        EnsureStorageSafe();
        var document = LoadPendingDeletions();
        if (document.ModuleIds.Count == 0) return;
        if (document.ModuleIds.Any(id => _stateStore.ReadInstalled().Modules.Any(item => item.Id == id)))
            throw new InvalidDataException("待删除任务与已安装模块冲突，已停止清理并保留资料。");
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
        var packageRoot = _stateStore.Resolve(Path.Combine("packages", moduleId));
        if (Directory.Exists(packageRoot)) { _stateStore.HashPackage(packageRoot); Directory.Delete(packageRoot, true); }
    }

    private PendingModuleDeletionDocument LoadPendingDeletions()
        => _stateStore.ReadDeletions();

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
        => _stateStore.SaveDeletions(document);

    private InstalledModuleDocument LoadInstalled()
    {
        try { return _stateStore.ReadInstalled(); }
        catch (Exception exception) when (IsStorageFailure(exception)) { return new InstalledModuleDocument(); }
    }

    private void SaveInstalled(InstalledModuleDocument document)
        => _stateStore.SaveInstalled(document);

    private void ValidatePackage(string directory, GameModuleCatalogEntry catalogEntry)
    {
        var manifestPath = Path.Combine(directory, "module.json");
        if (!File.Exists(manifestPath)) throw new InvalidOperationException("专属模块包缺少 module.json。");
        var manifest = JsonSerializer.Deserialize<InstalledModuleManifest>(File.ReadAllText(manifestPath), _jsonOptions)
                       ?? throw new InvalidOperationException("专属模块包的 module.json 无效。");
        var manifestMinimumHostVersion = string.IsNullOrWhiteSpace(manifest.MinimumHostVersion)
            ? GetPublishedLegacyMinimumHostVersion(manifest.HostApiVersion)
            : manifest.MinimumHostVersion;
        if (!string.Equals(manifest.Id, catalogEntry.Id, StringComparison.Ordinal) ||
            !string.Equals(manifest.Version, catalogEntry.Version, StringComparison.OrdinalIgnoreCase) ||
            manifest.HostApiVersion != catalogEntry.HostApiVersion ||
            manifest.SupportsUnlistedBuildValidation != catalogEntry.SupportsUnlistedBuildValidation ||
            !string.Equals(manifestMinimumHostVersion, catalogEntry.MinimumHostVersion, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.MaximumHostVersion ?? string.Empty, catalogEntry.MaximumHostVersion ?? string.Empty,
                StringComparison.OrdinalIgnoreCase) ||
            manifest.SupportsUnlistedBuildValidation && manifest.HostApiVersion < 5 ||
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

    private static bool MatchesProcess(GameModuleCatalogEntry module, string processName) =>
        module.ProcessNames.Count == 0 ||
        module.ProcessNames.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesBuild(
        GameModuleCatalogEntry module,
        string buildFingerprint,
        string executableSha256,
        string gameAssemblySha256,
        string metadataSha256,
        string packageSha256)
    {
        return module.CompatibleBuilds.Any(build =>
            MatchOptional(build.BuildFingerprint, buildFingerprint) &&
            MatchOptional(build.ExecutableSha256, executableSha256) &&
            MatchOptional(build.GameAssemblySha256, gameAssemblySha256) &&
            MatchOptional(build.MetadataSha256, metadataSha256) &&
            MatchOptional(build.PackageSha256, packageSha256));
    }

    private static bool MatchOptional(string expected, string actual) =>
        string.IsNullOrWhiteSpace(expected) || string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    private static SemanticVersion ParseVersion(string value) =>
        SemanticVersion.TryParse(value, out var version)
            ? version
            : throw new InvalidOperationException($"无效的模块版本：{value}");

    private static string GetPublishedLegacyMinimumHostVersion(int hostApiVersion) => hostApiVersion switch
    {
        4 => "0.4.1",
        6 => "0.4.3",
        _ => string.Empty
    };

    private bool IsHostCompatible(GameModuleCatalogEntry module)
    {
        var current = ParseVersion(_currentHostVersion);
        var minimum = string.IsNullOrWhiteSpace(module.MinimumHostVersion)
            ? GetPublishedLegacyMinimumHostVersion(module.HostApiVersion)
            : module.MinimumHostVersion;
        if (string.IsNullOrWhiteSpace(minimum) || ParseVersion(minimum).CompareTo(current) > 0) return false;
        return string.IsNullOrWhiteSpace(module.MaximumHostVersion) ||
               ParseVersion(module.MaximumHostVersion).CompareTo(current) >= 0;
    }

    private static IEnumerable<GameModuleCatalogEntry> GetVersionCandidates(GameModuleCatalogEntry module)
    {
        if (module.Releases.Count == 0)
        {
            yield return module;
            yield break;
        }

        foreach (var release in module.Releases)
        {
            yield return new GameModuleCatalogEntry
            {
                Id = module.Id,
                Version = release.Version,
                DisplayName = module.DisplayName,
                GameDisplayName = module.GameDisplayName,
                Description = module.Description,
                HostApiVersion = release.HostApiVersion,
                SupportsUnlistedBuildValidation = release.SupportsUnlistedBuildValidation,
                MinimumHostVersion = release.MinimumHostVersion,
                MaximumHostVersion = release.MaximumHostVersion,
                LegacyIds = [.. module.LegacyIds],
                Editors = [.. release.Editors],
                ProcessNames = [.. module.ProcessNames],
                CompatibleBuilds = [.. release.CompatibleBuilds],
                Contributors = [.. module.Contributors],
                DownloadUrl = release.DownloadUrl,
                SizeBytes = release.SizeBytes,
                Sha256 = release.Sha256
            };
        }
    }

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
    string StatusText,
    bool IsExactBuildMatch = false,
    GameModuleCatalogEntry? RollbackModule = null,
    GameModuleCatalogEntry? CatalogReferenceModule = null);

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
    public bool SupportsUnlistedBuildValidation { get; set; }
    public string MinimumHostVersion { get; set; } = string.Empty;
    public string? MaximumHostVersion { get; set; }
    public List<string> LegacyIds { get; set; } = [];
    public List<GameModuleEditorEntry> Editors { get; set; } = [];
    public List<string> ProcessNames { get; set; } = [];
    public List<GameModuleBuildMatch> CompatibleBuilds { get; set; } = [];
    public List<GameModuleContributor> Contributors { get; set; } = [];
    public string DownloadUrl { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public List<GameModuleReleaseEntry> Releases { get; set; } = [];
}

public sealed class GameModuleReleaseEntry
{
    public string Version { get; set; } = string.Empty;
    public int HostApiVersion { get; set; } = 1;
    public bool SupportsUnlistedBuildValidation { get; set; }
    public string MinimumHostVersion { get; set; } = string.Empty;
    public string? MaximumHostVersion { get; set; }
    public List<GameModuleEditorEntry> Editors { get; set; } = [];
    public List<GameModuleBuildMatch> CompatibleBuilds { get; set; } = [];
    public string DownloadUrl { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
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
    public string PackageSha256 { get; set; } = string.Empty;
}
