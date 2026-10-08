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
    private readonly SemaphoreSlim _supportGate = new(1, 1);
    private readonly List<ModuleLoadContext> _loadContexts = [];
    private readonly List<string> _shadowDirectories = [];
    private readonly List<string> _loadErrors = [];
    private readonly HashSet<string> _restartRequiredIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _nonCollectibleModuleIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _loadedVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _gameNames = new(StringComparer.Ordinal);

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
        _loadErrors.Clear();
        try
        {
            using var mutation = GameValueEditor.Updates.ModuleMutationLock.Acquire(_modulesDirectory);
            var store = new ModuleStateStore(_modulesDirectory);
            new ModuleInstallTransaction(store).RecoverAll();
            var records = store.ReadInstalled().Modules;
            store.ReadDeletions();
            foreach (var adapter in _adapters.ToArray())
            {
                var record = records.FirstOrDefault(item => string.Equals(item.Id, adapter.Id, StringComparison.Ordinal));
                if (record is not null && _loadedVersions.GetValueOrDefault(adapter.Id) == record.Version) continue;
                if (_nonCollectibleModuleIds.Contains(adapter.Id)) DeactivateUntilRestart(adapter.Id);
                else Deactivate(adapter.Id);
            }
            foreach (var module in records)
            {
                try { LoadModule(module); }
                catch (Exception exception) { _loadErrors.Add($"{module.Id} v{module.Version}: {exception.Message}"); }
            }
        }
        catch (Exception exception)
        {
            // A damaged optional module must never prevent the main application from starting.
            foreach (var adapter in _adapters.ToArray()) DeactivateUntilRestart(adapter.Id);
            _loadErrors.Add($"模块资料: {exception.Message}");
        }
    }

    public IGameAdapter? Resolve(ProcessItem process, VersionFingerprint fingerprint) =>
        _adapters.FirstOrDefault(adapter => Supports(adapter, process.ToModuleContext(), fingerprint.ToModuleIdentity()));

    private static bool Supports(IGameAdapter adapter, GameProcessContext process, GameBuildIdentity build)
    {
        try { return adapter.Supports(process, build); }
        catch (Exception error) { Debug.WriteLine($"Optional module {adapter.Id}: {error}"); return false; }
    }

    // Snapshot on the caller/UI thread. Optional Supports implementations may hash
    // large packages; never enumerate a mutable registry on the worker thread.
    public async Task<IGameAdapter?> ResolveAsync(ProcessItem process, VersionFingerprint fingerprint)
    {
        var adapters = _adapters.ToArray();
        var context = process.ToModuleContext();
        var identity = fingerprint.ToModuleIdentity();
        var match = await Task.Run(async () =>
        {
            await _supportGate.WaitAsync().ConfigureAwait(false);
            try { return adapters.FirstOrDefault(adapter => Supports(adapter, context, identity)); }
            finally { _supportGate.Release(); }
        });
        return match is not null && _adapters.Contains(match) ? match : null;
    }

    public IGameAdapter? FindById(string id) =>
        _adapters.FirstOrDefault(adapter =>
            string.Equals(adapter.Id, id, StringComparison.Ordinal) ||
            adapter.LegacyIds.Any(alias => string.Equals(alias, id, StringComparison.Ordinal)));

    public IReadOnlyList<string> GetGameNames(string id) =>
        _gameNames.GetValueOrDefault(FindById(id)?.Id ?? id) ?? [];

    public bool Deactivate(string id) => _adapters.RemoveAll(adapter =>
        string.Equals(adapter.Id, id, StringComparison.Ordinal) ||
        adapter.LegacyIds.Any(alias => string.Equals(alias, id, StringComparison.Ordinal))) > 0;

    public bool RequiresRestart(string id) =>
        _restartRequiredIds.Contains(id) || _nonCollectibleModuleIds.Contains(id);

    public bool IsRestartRequired(string id) => _restartRequiredIds.Contains(id);

    public void DeactivateUntilRestart(string id)
    {
        var canonicalId = FindById(id)?.Id ?? id;
        _restartRequiredIds.Add(canonicalId);
        Deactivate(canonicalId);
    }

    public void LoadInstalledModule(string id)
    {
        if (_restartRequiredIds.Contains(id)) return;
        if (FindById(id) is not null) return;
        using var mutation = GameValueEditor.Updates.ModuleMutationLock.Acquire(_modulesDirectory);
        var store = new ModuleStateStore(_modulesDirectory);
        new ModuleInstallTransaction(store).EnsureNoTransactions();
        store.ReadDeletions();
        var document = store.ReadInstalled();
        var record = document.Modules.SingleOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal))
                     ?? throw new InvalidOperationException("模块安装记录不存在。");
        try { LoadModule(record); }
        catch (Exception exception)
        {
            _loadErrors.Add($"{record.Id} v{record.Version}: {exception.Message}");
            throw;
        }
    }

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
        _gameNames.Clear();
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
        if (_restartRequiredIds.Contains(record.Id)) return;
        if (FindById(record.Id) is not null) return;
        if (_nonCollectibleModuleIds.Contains(record.Id))
        {
            DeactivateUntilRestart(record.Id);
            return;
        }
        if (!IsSafePathSegment(record.Id) || !IsSafePathSegment(record.Version))
            throw new InvalidDataException("模块登记的身份或版本含不安全路径。");
        var packagesRoot = Path.GetFullPath(Path.Combine(_modulesDirectory, "packages")) + Path.DirectorySeparatorChar;
        var packageDirectory = Path.GetFullPath(Path.Combine(packagesRoot, record.Id, record.Version));
        if (!packageDirectory.StartsWith(packagesRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("模块包路径越界。");
        var stateStore = new ModuleStateStore(_modulesDirectory);
        var manifestPath = Path.Combine(packageDirectory, "module.json");
        stateStore.EnsureNoLinks(manifestPath);
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("模块包缺少 module.json，不能加载。");
        var manifest = JsonSerializer.Deserialize<InstalledModuleManifest>(File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (manifest is null || !string.Equals(manifest.Id, record.Id, StringComparison.Ordinal) ||
            !string.Equals(manifest.Version, record.Version, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("module.json 的模块身份/版本与登记不一致。");
        // Name aliases are metadata, independent of whether the optional DLL can load or run.
        _gameNames[record.Id] = new[] { manifest.GameDisplayName }.Concat(manifest.ProcessNames ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
        if (manifest.HostApiVersion is < 1 or > ModuleHostApi.CurrentVersion)
            throw new InvalidOperationException(
                $"模块需要 Host API {manifest.HostApiVersion}，当前最高支持 {ModuleHostApi.CurrentVersion}。");
        var hostVersion = ParseVersion(ApplicationVersion.Current, "当前主程序版本");
        if (!string.IsNullOrWhiteSpace(manifest.MinimumHostVersion) &&
            ParseVersion(manifest.MinimumHostVersion, "模块最低主程序版本").CompareTo(hostVersion) > 0)
            throw new InvalidOperationException($"模块最低需要主程序 v{manifest.MinimumHostVersion}。");
        if (!string.IsNullOrWhiteSpace(manifest.MaximumHostVersion) &&
            ParseVersion(manifest.MaximumHostVersion, "模块最高主程序版本").CompareTo(hostVersion) < 0)
            throw new InvalidOperationException($"模块最高支持主程序 v{manifest.MaximumHostVersion}。");
        var packageRoot = Path.GetFullPath(packageDirectory) + Path.DirectorySeparatorChar;
        if (string.IsNullOrWhiteSpace(manifest.AssemblyFile)) throw new InvalidDataException("module.json 没有声明程序集。");
        var packageAssemblyPath = Path.GetFullPath(Path.Combine(packageRoot, manifest.AssemblyFile));
        if (!packageAssemblyPath.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("模块程序集路径越出模块包。");
        stateStore.EnsureNoLinks(packageAssemblyPath);
        if (!File.Exists(packageAssemblyPath)) throw new FileNotFoundException("模块包缺少声明的 DLL，不能加载。");

        var shadowDirectory = CreateShadowCopy(packageDirectory);
        var shadowRoot = Path.GetFullPath(shadowDirectory) + Path.DirectorySeparatorChar;
        var assemblyPath = Path.GetFullPath(Path.Combine(shadowRoot, manifest.AssemblyFile));
        if (!assemblyPath.StartsWith(shadowRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(assemblyPath))
        {
            TryDeleteDirectory(shadowDirectory);
            throw new InvalidDataException("模块影子副本缺少声明的程序集。");
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
                _loadedVersions[record.Id] = record.Version;
                foreach (var alias in loadedAdapters.SelectMany(adapter => adapter.LegacyIds))
                    _gameNames[alias] = _gameNames[record.Id];
                if (!context.IsCollectible) _nonCollectibleModuleIds.Add(record.Id);
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
        if (manifest.HostApiVersion >= 8 && adapter is not ICoordinatedGameEditorPageProvider)
            throw new InvalidOperationException("Host API 8 模块页面必须实现协调写入契约。");
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

    private static SemanticVersion ParseVersion(string value, string label) =>
        SemanticVersion.TryParse(value, out var version)
            ? version
            : throw new InvalidOperationException($"{label}无效：{value}");
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
    public bool SupportsUnlistedBuildValidation { get; set; }
    public string MinimumHostVersion { get; set; } = string.Empty;
    public string? MaximumHostVersion { get; set; }
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
