using GameValueEditor.Models;
using GameValueEditor.ModuleSdk;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;

namespace GameValueEditor.Services.Adapters;

public sealed class GameAdapterRegistry : IDisposable
{
    private readonly string _modulesDirectory;
    private readonly string _runtimeRoot;
    private readonly string _runtimeSessionDirectory;
    private readonly List<IGameAdapter> _adapters = [];
    private readonly List<ModuleLoadContext> _loadContexts = [];
    private readonly List<string> _shadowDirectories = [];
    private readonly List<string> _loadErrors = [];

    public GameAdapterRegistry(string? modulesDirectory = null)
    {
        _modulesDirectory = modulesDirectory ?? new ProfileStore().ModulesDirectory;
        _runtimeRoot = Path.GetFullPath(Path.Combine(_modulesDirectory, "runtime"));
        CleanupStaleRuntimeDirectories();
        _runtimeSessionDirectory = Path.Combine(_runtimeRoot, $"{Environment.ProcessId}-{Guid.NewGuid():N}");
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

    public void Dispose()
    {
        UnloadAll();
        TryDeleteDirectory(_runtimeSessionDirectory);
    }

    private void UnloadAll()
    {
        var unloadedContexts = ReleaseLoadContexts();
        WaitForUnload(unloadedContexts);
        foreach (var directory in _shadowDirectories) TryDeleteDirectory(directory);
        _shadowDirectories.Clear();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference[] ReleaseLoadContexts()
    {
        _adapters.Clear();
        var collectibleContexts = _loadContexts.Where(context => context.IsCollectible).ToArray();
        var unloadedContexts = collectibleContexts.Select(context => new WeakReference(context)).ToArray();
        foreach (var context in collectibleContexts) context.Unload();
        _loadContexts.Clear();
        return unloadedContexts;
    }

    private static void WaitForUnload(IReadOnlyList<WeakReference> unloadedContexts)
    {
        for (var attempt = 0; attempt < 4 && unloadedContexts.Any(reference => reference.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
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
        var packageAssemblyPath = Path.GetFullPath(Path.Combine(packageRoot, manifest.AssemblyFile));
        if (!packageAssemblyPath.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(packageAssemblyPath)) return;

        var shadowDirectory = CreateShadowCopy(packageDirectory);
        var shadowRoot = Path.GetFullPath(shadowDirectory) + Path.DirectorySeparatorChar;
        var assemblyPath = Path.GetFullPath(Path.Combine(shadowRoot, manifest.AssemblyFile));
        if (!assemblyPath.StartsWith(shadowRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(assemblyPath))
        {
            TryDeleteDirectory(shadowDirectory);
            return;
        }

        ModuleLoadContext? context = null;
        try
        {
            context = new ModuleLoadContext(assemblyPath, isCollectible: manifest.HostApiVersion <= 5);
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
                throw new InvalidOperationException("Host API 2 及以上游戏包必须且只能导出一个 IGameAdapter。");
            foreach (var adapter in loadedAdapters)
            {
                if (_adapters.Any(existing => string.Equals(existing.Id, adapter.Id, StringComparison.Ordinal)))
                    throw new InvalidOperationException($"游戏模块 ID 重复：{adapter.Id}。");
                _adapters.Add(adapter);
            }
            if (loadedAdapters.Count > 0)
            {
                _loadContexts.Add(context);
                _shadowDirectories.Add(shadowDirectory);
                context = null;
            }
        }
        finally
        {
            if (context?.IsCollectible == true) context.Unload();
            if (context is not null) TryDeleteDirectory(shadowDirectory);
        }
    }

    private string CreateShadowCopy(string packageDirectory)
    {
        Directory.CreateDirectory(_runtimeSessionDirectory);
        var shadowDirectory = Path.Combine(_runtimeSessionDirectory, Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.GetFullPath(packageDirectory) + Path.DirectorySeparatorChar;
        var targetRoot = Path.GetFullPath(shadowDirectory) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(shadowDirectory);
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false
            };
            foreach (var source in Directory.EnumerateFiles(packageDirectory, "*", options))
            {
                var resolvedSource = Path.GetFullPath(source);
                if (!resolvedSource.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("模块影子复制的源路径无效。");
                var relative = Path.GetRelativePath(packageDirectory, resolvedSource);
                var destination = Path.GetFullPath(Path.Combine(shadowDirectory, relative));
                if (!destination.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("模块影子复制的目标路径无效。");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(resolvedSource, destination, true);
            }
            return shadowDirectory;
        }
        catch
        {
            TryDeleteDirectory(shadowDirectory);
            throw;
        }
    }

    private void CleanupStaleRuntimeDirectories()
    {
        if (!Directory.Exists(_runtimeRoot)) return;
        foreach (var directory in Directory.EnumerateDirectories(_runtimeRoot))
        {
            var name = Path.GetFileName(directory);
            var separator = name.IndexOf('-');
            if (separator <= 0 || !int.TryParse(name[..separator], out var processId) || IsProcessRunning(processId))
                continue;
            TryDeleteDirectory(directory);
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
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

            if (!string.Equals(adapter.DisplayName, manifest.DisplayName, StringComparison.Ordinal))
                throw new InvalidOperationException("module.json 的模块显示名称与程序集不一致。");

            foreach (var manifestEditor in manifest.Editors)
            {
                var descriptor = adapter.Editors.Single(editor =>
                    string.Equals(editor.Id, manifestEditor.Id, StringComparison.Ordinal));
                if (!string.Equals(descriptor.DisplayName, manifestEditor.DisplayName, StringComparison.Ordinal) ||
                    descriptor.Order != manifestEditor.Order ||
                    descriptor.SessionOnly != manifestEditor.SessionOnly)
                    throw new InvalidOperationException($"module.json 的编辑模块元数据与程序集不一致：{manifestEditor.Id}。");
                if (manifest.HostApiVersion <= 5)
                {
                    var manifestKind = manifestEditor.Kind switch
                    {
                        "collection" => GameEditorKind.Collection,
                        "master-detail" => GameEditorKind.MasterDetail,
                        "property-grid" => GameEditorKind.PropertyGrid,
                        _ => throw new InvalidOperationException($"module.json 的编辑模块类型无效：{manifestEditor.Kind}。")
                    };
                    if (descriptor.Kind != manifestKind)
                        throw new InvalidOperationException($"module.json 的编辑模块类型与程序集不一致：{manifestEditor.Id}。");
                }
                else if (descriptor.Kind != GameEditorKind.Custom)
                {
                    throw new InvalidOperationException($"Host API 6 编辑器必须使用 Custom 兼容标记：{manifestEditor.Id}。");
                }
            }
        }
        if (manifest.HostApiVersion is >= 4 and <= 5) GameEditorPageResolver.ValidateApi4Provider(adapter);
        if (manifest.HostApiVersion >= 6) GameEditorPageResolver.ValidateApi6Provider(adapter);
        if (manifest.HostApiVersion >= 5 && adapter is not IGameCompatibilityDiagnosticsProvider)
            throw new InvalidOperationException("Host API 5 游戏包必须实现只读兼容性诊断接口。");
    }

    private static bool IsSafePathSegment(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value is not "." and not ".." &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !value.Contains(Path.DirectorySeparatorChar) &&
        !value.Contains(Path.AltDirectorySeparatorChar);

    private sealed class ModuleLoadContext(string assemblyPath, bool isCollectible) : AssemblyLoadContext(isCollectible)
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
    public string GameDisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string AssemblyFile { get; set; } = string.Empty;
    public int HostApiVersion { get; set; } = 1;
    public List<string> ProcessNames { get; set; } = [];
    public List<GameModuleBuildMatch> CompatibleBuilds { get; set; } = [];
    public List<InstalledEditorManifest> Editors { get; set; } = [];
    public List<GameModuleContributor> Contributors { get; set; } = [];
}

public sealed class InstalledEditorManifest
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Kind { get; set; }
    public int Order { get; set; }
    public bool SessionOnly { get; set; }
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

    public static bool SupportsEntityEditor(
        this IEntityEditorsGameAdapter adapter,
        ProcessItem process,
        string editorId) =>
        adapter.SupportsEntityEditor(process.ToModuleContext(), editorId);

    public static IReadOnlyList<AdapterEditorEntity> ReadEditorEntities(
        this IEntityEditorsGameAdapter adapter,
        ProcessItem process,
        string editorId) =>
        adapter.ReadEditorEntities(process.ToModuleContext(), editorId);

    public static AdapterEditorEntity WriteEditorField(
        this IEntityEditorsGameAdapter adapter,
        ProcessItem process,
        string editorId,
        string entityId,
        string fieldKey,
        long targetValue) =>
        adapter.WriteEditorField(process.ToModuleContext(), editorId, entityId, fieldKey, targetValue);

    public static bool Supports(this IGameAdapter adapter, ProcessItem process, VersionFingerprint fingerprint) =>
        adapter.Supports(process.ToModuleContext(), fingerprint.ToModuleIdentity());
}
