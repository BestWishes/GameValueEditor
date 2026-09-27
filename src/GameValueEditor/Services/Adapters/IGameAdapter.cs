using GameValueEditor.Models;
using GameValueEditor.ModuleSdk;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace GameValueEditor.Services.Adapters;

public sealed class GameAdapterRegistry : IDisposable
{
    private readonly string _modulesDirectory;
    private readonly List<IGameAdapter> _adapters = [];
    private readonly List<ModuleLoadContext> _loadContexts = [];
    private readonly List<string> _loadErrors = [];

    public GameAdapterRegistry(string? modulesDirectory = null)
    {
        _modulesDirectory = modulesDirectory ?? new ProfileStore().ModulesDirectory;
        Reload();
    }

    public void Reload()
    {
        UnloadAll();
        _loadErrors.Clear();
        var installedPath = Path.Combine(_modulesDirectory, "installed.json");
        if (!File.Exists(installedPath)) return;
        try
        {
            var document = JsonSerializer.Deserialize<InstalledModuleDocument>(File.ReadAllText(installedPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            foreach (var module in document?.Modules ?? [])
            {
                try { LoadModule(module); }
                catch (Exception exception) { _loadErrors.Add($"{module.Id} v{module.Version}: {exception.Message}"); }
            }
        }
        catch (Exception exception)
        {
            // A damaged optional module must never prevent the main application from starting.
            _loadErrors.Add($"installed.json: {exception.Message}");
        }
    }

    public IGameAdapter? Resolve(ProcessItem process, VersionFingerprint fingerprint) =>
        _adapters.FirstOrDefault(adapter => adapter.Supports(process.ToModuleContext(), fingerprint.ToModuleIdentity()));

    public IGameAdapter? FindById(string id) =>
        _adapters.FirstOrDefault(adapter =>
            string.Equals(adapter.Id, id, StringComparison.Ordinal) ||
            adapter.LegacyIds.Any(alias => string.Equals(alias, id, StringComparison.Ordinal)));

    public IReadOnlyList<string> LoadErrors => _loadErrors;

    public void Dispose() => UnloadAll();

    private void UnloadAll()
    {
        _adapters.Clear();
        foreach (var context in _loadContexts) context.Unload();
        _loadContexts.Clear();
    }

    private void LoadModule(InstalledModuleRecord record)
    {
        if (!IsSafePathSegment(record.Id) || !IsSafePathSegment(record.Version)) return;
        var packagesRoot = Path.GetFullPath(Path.Combine(_modulesDirectory, "packages")) + Path.DirectorySeparatorChar;
        var packageDirectory = Path.GetFullPath(Path.Combine(packagesRoot, record.Id, record.Version));
        if (!packageDirectory.StartsWith(packagesRoot, StringComparison.OrdinalIgnoreCase)) return;
        var manifestPath = Path.Combine(packageDirectory, "module.json");
        if (!File.Exists(manifestPath)) return;
        var manifest = JsonSerializer.Deserialize<InstalledModuleManifest>(File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (manifest is null || !string.Equals(manifest.Id, record.Id, StringComparison.Ordinal) ||
            !string.Equals(manifest.Version, record.Version, StringComparison.OrdinalIgnoreCase)) return;
        if (manifest.HostApiVersion is < 1 or > ModuleHostApi.CurrentVersion)
            throw new InvalidOperationException(
                $"模块需要 Host API {manifest.HostApiVersion}，当前最高支持 {ModuleHostApi.CurrentVersion}。");
        var packageRoot = Path.GetFullPath(packageDirectory) + Path.DirectorySeparatorChar;
        var assemblyPath = Path.GetFullPath(Path.Combine(packageRoot, manifest.AssemblyFile));
        if (!assemblyPath.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(assemblyPath)) return;
        var context = new ModuleLoadContext(assemblyPath);
        var assembly = context.LoadFromAssemblyPath(assemblyPath);
        var types = assembly.GetTypes().Where(type =>
            !type.IsAbstract && typeof(IGameAdapter).IsAssignableFrom(type));
        var loadedAdapters = new List<IGameAdapter>();
        foreach (var type in types)
        {
            if (Activator.CreateInstance(type) is not IGameAdapter adapter) continue;
            ValidateAdapter(adapter, manifest);
            loadedAdapters.Add(adapter);
        }
        if (manifest.HostApiVersion >= 2 && loadedAdapters.Count != 1)
            throw new InvalidOperationException("Host API v2 游戏包必须且只能导出一个 IGameAdapter。");
        foreach (var adapter in loadedAdapters)
        {
            if (_adapters.Any(existing => string.Equals(existing.Id, adapter.Id, StringComparison.Ordinal)))
                throw new InvalidOperationException($"游戏模块 ID 重复：{adapter.Id}。");
            _adapters.Add(adapter);
        }
        if (loadedAdapters.Count > 0) _loadContexts.Add(context);
        else context.Unload();
    }

    private static void ValidateAdapter(IGameAdapter adapter, InstalledModuleManifest manifest)
    {
        if (!string.Equals(adapter.Id, manifest.Id, StringComparison.Ordinal))
            throw new InvalidOperationException($"程序集游戏 ID“{adapter.Id}”与 module.json 不一致。");
        if (adapter.Editors.Count == 0)
            throw new InvalidOperationException("游戏模块没有注册任何游戏内编辑模块。");
        var duplicate = adapter.Editors.GroupBy(editor => editor.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"游戏内编辑模块 ID 重复：{duplicate.Key}。");
        if (manifest.HostApiVersion >= 2)
        {
            var manifestIds = manifest.Editors.Select(editor => editor.Id).ToHashSet(StringComparer.Ordinal);
            var adapterIds = adapter.Editors.Select(editor => editor.Id).ToHashSet(StringComparer.Ordinal);
            if (manifestIds.Count != manifest.Editors.Count || !manifestIds.SetEquals(adapterIds))
                throw new InvalidOperationException("module.json 的编辑模块列表与程序集不一致。");
        }
    }

    private static bool IsSafePathSegment(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value is not "." and not ".." &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !value.Contains(Path.DirectorySeparatorChar) &&
        !value.Contains(Path.AltDirectorySeparatorChar);

    private sealed class ModuleLoadContext(string assemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, typeof(IGameAdapter).Assembly.GetName().Name,
                    StringComparison.OrdinalIgnoreCase)) return null;
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}

public sealed class InstalledModuleDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<InstalledModuleRecord> Modules { get; set; } = [];
}

public sealed record InstalledModuleRecord(string Id, string Version, DateTime InstalledUtc);

public sealed class InstalledModuleManifest
{
    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string AssemblyFile { get; set; } = string.Empty;
    public int HostApiVersion { get; set; } = 1;
    public List<InstalledEditorManifest> Editors { get; set; } = [];
}

public sealed class InstalledEditorManifest
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int Order { get; set; }
}

public static class AdapterHostExtensions
{
    public static GameProcessContext ToModuleContext(this ProcessItem process) => new(
        process.ProcessId,
        process.ProcessName,
        process.ExecutablePath,
        process.StartTimeUtc);

    public static GameBuildIdentity ToModuleIdentity(this VersionFingerprint fingerprint) => new(
        fingerprint.Sha256,
        fingerprint.BuildSha256,
        fingerprint.GameAssemblySha256,
        fingerprint.MetadataSha256);

    public static AdapterFieldValue ReadField(this IGameAdapter adapter, ProcessItem process, string fieldKey) =>
        adapter.ReadField(process.ToModuleContext(), fieldKey);

    public static AdapterFieldValue WriteField(this IGameAdapter adapter, ProcessItem process, string fieldKey, string displayValue) =>
        adapter.WriteField(process.ToModuleContext(), fieldKey, displayValue);

    public static IReadOnlyList<AdapterInventoryItem> ReadInventory(this IInventoryGameAdapter adapter, ProcessItem process) =>
        adapter.ReadInventory(process.ToModuleContext());

    public static IReadOnlyList<AdapterCharacterItem> ReadCharacters(this ICharacterAttributesGameAdapter adapter, ProcessItem process) =>
        adapter.ReadCharacters(process.ToModuleContext());

    public static bool SupportsCharacterAttributes(this ICharacterAttributesGameAdapter adapter, ProcessItem process) =>
        adapter.SupportsCharacterAttributes(process.ToModuleContext());

    public static AdapterCharacterItem WriteCharacterAttribute(
        this ICharacterAttributesGameAdapter adapter,
        ProcessItem process,
        string characterId,
        string attributeKey,
        int targetValue) =>
        adapter.WriteCharacterAttribute(process.ToModuleContext(), characterId, attributeKey, targetValue);

    public static bool Supports(this IGameAdapter adapter, ProcessItem process, VersionFingerprint fingerprint) =>
        adapter.Supports(process.ToModuleContext(), fingerprint.ToModuleIdentity());
}
