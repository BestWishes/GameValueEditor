using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameValueEditor.Updates;

// Shared by the WPF host and the independent updater; no module SDK dependency.
public sealed record ApplicationUpdateCompatibility(int MinimumModuleHostApi, int MaximumModuleHostApi,
    int MaximumCatalogSchemaVersion);

internal static class ApplicationUpdateCompatibilityValidator
{
    internal const string ArchiveManifestName = "release-compatibility.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Call only after validating the archive hash against the verified release index.
    internal static ApplicationUpdateCompatibility ResolveVerifiedArchive(string archivePath, string targetVersion,
        ApplicationUpdateCompatibility? pendingCompatibility)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries.Where(entry => entry.FullName == ArchiveManifestName).ToArray();
        if (entries.Length > 1 || entries.Any(entry => entry.Length > 16 * 1024))
            throw new InvalidDataException("发布包兼容清单重复或过大。");
        if (entries.Length == 1)
        {
            using var stream = entries[0].Open();
            var manifest = JsonSerializer.Deserialize<ApplicationReleaseManifest>(stream, JsonOptions)
                           ?? throw new InvalidDataException("发布包兼容清单无效。");
            if (manifest.SchemaVersion != 1 || manifest.Version != targetVersion || manifest.Compatibility is null ||
                pendingCompatibility is not null && manifest.Compatibility != pendingCompatibility)
                throw new InvalidDataException("发布包兼容清单与待安装目标不一致。");
            return manifest.Compatibility;
        }
        return pendingCompatibility ?? throw new InvalidOperationException("旧待安装记录和发布包都缺少兼容信息，请重新下载更新或回退包。");
    }

    internal static void Validate(string applicationDirectory, string targetVersion, ApplicationUpdateCompatibility? compatibility)
    {
        if (compatibility is null || compatibility.MinimumModuleHostApi < 1 ||
            compatibility.MaximumModuleHostApi < compatibility.MinimumModuleHostApi || compatibility.MaximumCatalogSchemaVersion < 1)
            throw new InvalidOperationException("待安装记录缺少有效的目标兼容信息，请重新下载更新或回退包。");
        var target = ParseVersion(targetVersion);
        var root = Path.Combine(Path.GetFullPath(applicationDirectory), "data", "modules");
        var installedPath = Path.Combine(root, "installed.json");
        if (!File.Exists(installedPath)) return;
        EnsureNoLinks(installedPath, root);
        var document = JsonSerializer.Deserialize<ModuleDocument>(File.ReadAllText(installedPath), JsonOptions)
                       ?? throw new InvalidDataException("模块安装记录无效，已停止应用版本替换。");
        if (document.Modules is null || document.SchemaVersion != 1)
            throw new InvalidDataException("模块安装记录格式不受支持。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in document.Modules)
        {
            if (record is null || !IsSegment(record.Id) || !seen.Add(record.Id))
                throw new InvalidDataException("模块安装记录包含无效或重复的身份。");
            ParseVersion(record.Version);
            var manifestPath = Path.Combine(root, "packages", record.Id, record.Version, "module.json");
            EnsureNoLinks(manifestPath, root);
            var manifest = JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(manifestPath), JsonOptions)
                           ?? throw new InvalidDataException($"模块 {record.Id} 清单无效。");
            if (manifest.Id != record.Id || manifest.Version != record.Version || manifest.HostApiVersion < 1)
                throw new InvalidDataException($"模块 {record.Id} 的清单与安装记录不一致。");
            var reasons = new List<string>();
            if (manifest.HostApiVersion < compatibility.MinimumModuleHostApi || manifest.HostApiVersion > compatibility.MaximumModuleHostApi)
                reasons.Add($"需要 Host API {manifest.HostApiVersion}");
            if (string.IsNullOrWhiteSpace(manifest.MinimumHostVersion))
                throw new InvalidDataException($"模块 {record.Id} 缺少最低主程序版本。");
            if (ParseVersion(manifest.MinimumHostVersion).CompareTo(target) > 0)
                reasons.Add($"最低主程序版本 {manifest.MinimumHostVersion}");
            if (!string.IsNullOrWhiteSpace(manifest.MaximumHostVersion) && ParseVersion(manifest.MaximumHostVersion).CompareTo(target) < 0)
                reasons.Add($"最高主程序版本 {manifest.MaximumHostVersion}");
            if (reasons.Count > 0)
                throw new InvalidOperationException($"模块 {manifest.DisplayName} ({record.Id}) v{record.Version} 与目标主程序 v{targetVersion} 不兼容：{string.Join("；", reasons)}。请先手动处理该模块。");
        }
    }

    private static Version ParseVersion(string text) =>
        Regex.IsMatch(text ?? string.Empty, @"^(0|[1-9][0-9]*)\.[0-9]\.[0-9]$") && Version.TryParse(text, out var version)
            ? version : throw new InvalidDataException($"兼容记录中的版本号无效：{text}");

    private static bool IsSegment(string value) => !string.IsNullOrWhiteSpace(value) && value is not "." and not ".." &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static void EnsureNoLinks(string path, string root)
    {
        for (var current = path; current is not null && current.Length >= root.Length; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("模块清单路径包含符号链接或目录联接。");
    }

    private sealed class ModuleDocument { public int SchemaVersion { get; set; } public List<ModuleRecord>? Modules { get; set; } }
    private sealed class ModuleRecord { public string Id { get; set; } = ""; public string Version { get; set; } = ""; }
    private sealed class ModuleManifest
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public int HostApiVersion { get; set; }
        public string MinimumHostVersion { get; set; } = "";
        public string? MaximumHostVersion { get; set; }
    }
}

internal sealed record ApplicationReleaseManifest(int SchemaVersion, string Version, ApplicationUpdateCompatibility Compatibility);

internal static class ModuleMutationLock
{
    internal static async Task<FileStream> AcquireAsync(string modulesDirectory, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(modulesDirectory);
        var clock = Stopwatch.StartNew();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(modulesDirectory, "mutation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (clock.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(50, cancellation).ConfigureAwait(false); }
        }
    }

    internal static FileStream Acquire(string modulesDirectory) => AcquireAsync(modulesDirectory).GetAwaiter().GetResult();
}
