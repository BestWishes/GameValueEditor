using GameValueEditor.Models;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace GameValueEditor.Services.Adapters;

public interface IGameAdapter
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    bool Supports(ProcessItem process, VersionFingerprint fingerprint);
    AdapterFieldValue ReadField(ProcessItem process, string fieldKey);
    AdapterFieldValue WriteField(ProcessItem process, string fieldKey, string displayValue);
}

public interface IInventoryGameAdapter : IGameAdapter
{
    IReadOnlyList<AdapterInventoryItem> ReadInventory(ProcessItem process);
}

public sealed record AdapterFieldValue(string FieldKey, string DisplayValue, string Status);
public sealed record AdapterInventoryItem(string FieldKey, string DisplayName, long Count)
{
    public string CountDisplay => Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

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
        _adapters.FirstOrDefault(adapter => adapter.Supports(process, fingerprint));

    public IGameAdapter? FindById(string id) =>
        _adapters.FirstOrDefault(adapter => string.Equals(adapter.Id, id, StringComparison.Ordinal));

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
        var packageRoot = Path.GetFullPath(packageDirectory) + Path.DirectorySeparatorChar;
        var assemblyPath = Path.GetFullPath(Path.Combine(packageRoot, manifest.AssemblyFile));
        if (!assemblyPath.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(assemblyPath)) return;
        var context = new ModuleLoadContext(assemblyPath);
        var assembly = context.LoadFromAssemblyPath(assemblyPath);
        var types = assembly.GetTypes().Where(type =>
            !type.IsAbstract && typeof(IGameAdapter).IsAssignableFrom(type));
        var loaded = false;
        foreach (var type in types)
        {
            if (Activator.CreateInstance(type) is not IGameAdapter adapter) continue;
            _adapters.Add(adapter);
            loaded = true;
        }
        if (loaded) _loadContexts.Add(context);
        else context.Unload();
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
}
