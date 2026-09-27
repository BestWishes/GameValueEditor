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
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public GameModuleCatalogService(string modulesDirectory, HttpClient? httpClient = null)
    {
        _modulesDirectory = modulesDirectory;
        _httpClient = httpClient ?? new HttpClient();
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("GameValueEditor-Modules/1.0");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public InstalledModuleRecord? FindInstalled(string moduleId) =>
        LoadInstalled().Modules.FirstOrDefault(item => string.Equals(item.Id, moduleId, StringComparison.Ordinal));

    public IReadOnlyList<InstalledModuleRecord> GetInstalledModules() => LoadInstalled().Modules;

    public async Task<GameModuleCheckResult> CheckAsync(
        GameProfile game,
        GameVersionProfile version,
        CancellationToken cancellationToken = default)
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
            .Where(module => module.HostApiVersion is >= 1 and <= ModuleHostApi.CurrentVersion && Matches(module, game, version))
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

    public async Task InstallAsync(GameModuleCatalogEntry module, CancellationToken cancellationToken = default)
    {
        EnsureSafePathSegment(module.Id, "模块 ID");
        EnsureSafePathSegment(module.Version, "模块版本");
        if (string.IsNullOrWhiteSpace(module.Sha256) || module.Sha256.Length != 64)
            throw new InvalidOperationException("模块清单缺少有效的 SHA-256 校验值。");
        Directory.CreateDirectory(_modulesDirectory);
        var downloadsDirectory = Path.Combine(_modulesDirectory, "downloads");
        Directory.CreateDirectory(downloadsDirectory);
        var temporaryArchive = Path.Combine(downloadsDirectory, $"{Guid.NewGuid():N}.download");
        using (var response = await _httpClient.GetAsync(module.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = new FileStream(temporaryArchive, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target, cancellationToken);
        }
        try
        {
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
            if (File.Exists(temporaryArchive)) File.Delete(temporaryArchive);
        }
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
        var assemblyPath = Path.GetFullPath(Path.Combine(directory, manifest.AssemblyFile));
        if (!assemblyPath.StartsWith(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase) || !File.Exists(assemblyPath))
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

    private static bool Matches(GameModuleCatalogEntry module, GameProfile game, GameVersionProfile version)
    {
        if (module.ProcessNames.Count > 0 &&
            !module.ProcessNames.Any(name => string.Equals(name, game.ProcessName, StringComparison.OrdinalIgnoreCase)))
            return false;
        return module.CompatibleBuilds.Any(build =>
            MatchOptional(build.BuildFingerprint, version.BuildFingerprint) &&
            MatchOptional(build.ExecutableSha256, version.ExecutableSha256) &&
            MatchOptional(build.GameAssemblySha256, version.GameAssemblySha256) &&
            MatchOptional(build.MetadataSha256, version.MetadataSha256));
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

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }
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
    public string Description { get; set; } = string.Empty;
    public int HostApiVersion { get; set; } = 1;
    public List<string> LegacyIds { get; set; } = [];
    public List<GameModuleEditorEntry> Editors { get; set; } = [];
    public List<string> ProcessNames { get; set; } = [];
    public List<GameModuleBuildMatch> CompatibleBuilds { get; set; } = [];
    public string DownloadUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
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
