using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using GameValueEditor.Models;
using GameValueEditor.Services;
using GameValueEditor.Services.Adapters;
using GameValueEditor.ViewModels;

internal static class ModuleLifecycleRegressionTests
{
    internal static Task RunAsync(string[] args)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
            {
                var root = Path.Combine(Path.GetTempPath(), "gve-lifecycle-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    await CheckStaleResultsAsync(root);
                    await CheckRecoveryMarkerAsync(root);
                    await StabilityRegressionTests.RunAsync(root, args);
                    await LayoutRegressionTests.CheckUpdateStatesAsync(root);
                    var paths = args.Where(arg => arg.StartsWith("--verify-module-package=", StringComparison.Ordinal))
                        .Select(arg => arg["--verify-module-package=".Length..]).ToArray();
                    if (paths.Length >= 2) await CheckInstalledLifecycleAsync(root, paths);
                    completion.SetResult();
                }
                catch (Exception exception) { completion.SetException(exception); }
                finally
                {
                    try { Directory.Delete(root, true); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    internal static MainViewModel CreateViewModel(string root, GameModuleCatalogService catalog, GameAdapterRegistry registry,
        ApplicationUpdateService? updater = null)
    {
        var store = new ProfileStore(root);
        var ctor = typeof(MainViewModelServices).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var services = (MainViewModelServices)ctor.Invoke(new object[]
        {
            store, new ProcessService(), new VersionFingerprintService(), new MemoryScanService(Path.Combine(root, "scan")),
            registry, new ThemeService(), new ProcessSpeedService(), new GameIconService(store.IconsDirectory), catalog,
            updater ?? new ApplicationUpdateService(store.UpdatesDirectory)
        });
        return new MainViewModel(services);
    }

    private static async Task CheckStaleResultsAsync(string root)
    {
        foreach (var scenario in new[] { "other-game", "return-game", "other-version", "stale-error", "disconnect" })
        {
            var scenarioRoot = Path.Combine(root, scenario);
            var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var client = new HttpClient(new Handler(_ => gate.Task));
            var catalog = new GameModuleCatalogService(Path.Combine(scenarioRoot, "modules"), client);
            using var registry = new GameAdapterRegistry(Path.Combine(scenarioRoot, "modules"));
            var vm = CreateViewModel(scenarioRoot, catalog, registry);
            var a = new GameProfile { Name = "A", ProcessName = "game-a", Versions = [new() { ExecutableSha256 = "A" }, new() { ExecutableSha256 = "B" }] };
            var b = new GameProfile { Name = "B", ProcessName = "game-b", Versions = [new() { ExecutableSha256 = "B" }] };
            vm.Games.Add(a); vm.Games.Add(b);
            vm.SelectedGame = a; vm.SelectedVersion = a.Versions[0];
            var attachedProperty = typeof(MainViewModel).GetProperty(nameof(MainViewModel.AttachedProcess))!;
            if (scenario == "disconnect") attachedProperty.SetValue(vm,
                new ProcessItem { ProcessId = int.MaxValue, ProcessName = "game-a" });
            var check = vm.CheckGameModuleUpdatesAsync();
            if (scenario == "disconnect") attachedProperty.SetValue(vm, null);
            else if (scenario == "other-version") vm.SelectedVersion = a.Versions[1];
            else
            {
                vm.SelectedGame = b; vm.SelectedVersion = b.Versions[0];
                if (scenario == "return-game") { vm.SelectedGame = a; vm.SelectedVersion = a.Versions[0]; }
            }
            var expectedText = vm.ModuleStatusText;
            gate.SetResult(new HttpResponseMessage(scenario == "stale-error" ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {"schemaVersion":5,"hostApiVersion":7,"modules":[{"id":"game.a","version":"0.1.0",
                "displayName":"A module","hostApiVersion":7,"minimumHostVersion":"0.4.4",
                "processNames":["game-a"],"compatibleBuilds":[{"executableSha256":"A"}]}]}
                """)
            });
            await check;
            if (vm.ModuleStatusText != expectedText || vm.CanInstallGameModule || vm.CanRollbackGameModule)
                throw new Exception($"Stale module result changed current page: {scenario}");
            try { await vm.InstallAvailableGameModuleAsync(); throw new Exception("Stale install command was accepted."); }
            catch (InvalidOperationException exception) when (exception.Message.Contains("重新", StringComparison.Ordinal)) { }
            vm.Shutdown();
        }
    }

    private static async Task CheckInstalledLifecycleAsync(string root, string[] paths)
    {
        var entries = paths.Select(ReadEntry).ToArray();
        var downloadGate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gated = false;
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsoluteUri == GameModuleCatalogService.DefaultCatalogUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(new GameModuleCatalog { SchemaVersion = 5, HostApiVersion = 7, Modules = [entries[0]] })) });
            if (gated) return downloadGate.Task;
            var entry = entries.Single(item => item.DownloadUrl == request.RequestUri.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(File.ReadAllBytes(paths[Array.IndexOf(entries, entry)])) });
        }));
        var installRoot = Path.Combine(root, "install");
        var catalog = new GameModuleCatalogService(Path.Combine(installRoot, "modules"), client);
        using var registry = new GameAdapterRegistry(Path.Combine(installRoot, "modules"));
        var vm = CreateViewModel(installRoot, catalog, registry);
        var build = entries[0].CompatibleBuilds[0];
        var a = new GameProfile { Name = "A", ProcessName = entries[0].ProcessNames[0], Versions = [new()
        { ExecutableSha256 = build.ExecutableSha256, GameAssemblySha256 = build.GameAssemblySha256, MetadataSha256 = build.MetadataSha256 }] };
        var b = new GameProfile { Name = "B", ProcessName = "other", Versions = [new() { ExecutableSha256 = "OTHER" }] };
        vm.Games.Add(a); vm.Games.Add(b); vm.SelectedGame = a; vm.SelectedVersion = a.Versions[0];
        await vm.CheckGameModuleUpdatesAsync();
        await Task.Delay(3100);
        gated = true;
        var install = vm.InstallAvailableGameModuleAsync();
        vm.SelectedGame = b; vm.SelectedVersion = b.Versions[0];
        downloadGate.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(paths[0])) });
        await install;
        if (!a.IsModuleLoaded || b.IsModuleLoaded)
            throw new Exception("Initial module load marker was missing or rebound to another game.");
        if (a.ModuleId != entries[0].Id || b.ModuleId.Length != 0 || vm.CanInstallGameModule ||
            vm.ModuleStatusText.Contains(entries[0].DisplayName, StringComparison.Ordinal))
            throw new Exception("Download completion rebound the module or status to another game.");
        gated = false;
        foreach (var entry in entries.Skip(1)) { await catalog.InstallAsync(entry); registry.LoadInstalledModule(entry.Id); }
        var moduleGames = entries.Skip(1).Select(entry => new GameProfile { Name = entry.Id, ModuleId = entry.Id }).ToArray();
        foreach (var game in moduleGames) vm.Games.Add(game);
        RefreshModuleMarkers(vm);
        if (moduleGames.Any(game => !game.IsModuleLoaded)) throw new Exception("Loaded module markers were not refreshed.");
        var other = entries.Length >= 3 ? registry.FindById(entries[2].Id) : null;
        typeof(MainViewModel).GetMethod("DeactivateModuleUntilRestart", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, new object[] { entries[0].Id });
        if (a.IsModuleLoaded) throw new Exception("Module waiting for restart still showed a loaded marker.");
        await (Task<bool>)typeof(MainViewModel).GetMethod("RemoveInstalledModuleCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, new object[] { entries[1].Id })!;
        if (moduleGames[0].IsModuleLoaded || moduleGames.Skip(1).Any(game => !game.IsModuleLoaded))
            throw new Exception("Uninstall retained a loaded marker or removed another module marker.");
        registry.Reload(); registry.LoadInstalledModule(entries[0].Id);
        if (registry.FindById(entries[0].Id) is not null || !registry.IsRestartRequired(entries[0].Id) ||
            other is not null && !ReferenceEquals(other, registry.FindById(entries[2].Id)))
            throw new Exception("Reload/uninstall revived a stopped module or recreated another WPF module.");
        catalog.Unregister(entries[0].Id);
        await catalog.InstallAsync(entries[0]); registry.LoadInstalledModule(entries[0].Id);
        if (registry.FindById(entries[0].Id) is not null || !registry.RequiresRestart(entries[0].Id))
            throw new Exception("Reinstallation bypassed the restart requirement.");
        using var afterRestart = new GameAdapterRegistry(Path.Combine(installRoot, "modules"));
        if (afterRestart.FindById(entries[0].Id) is null) throw new Exception(string.Join(";", afterRestart.LoadErrors));
        var restartedVm = CreateViewModel(installRoot, catalog, afterRestart);
        var restartedGame = new GameProfile { Name = "restart", ModuleId = entries[0].Id };
        restartedVm.Games.Add(restartedGame);
        RefreshModuleMarkers(restartedVm, reconcile: true);
        if (!restartedGame.IsModuleLoaded || !restartedGame.IsModuleInstalled || restartedGame.IsConnected)
            throw new Exception("Startup did not distinguish loaded module from game connection.");
        File.Delete(Path.Combine(installRoot, "modules", "installed.json"));
        afterRestart.Reload();
        RefreshModuleMarkers(restartedVm, reconcile: true);
        if (restartedGame.IsModuleLoaded || restartedGame.IsModuleInstalled)
            throw new Exception("Missing registration retained the library module marker.");
        if (afterRestart.FindById(entries[0].Id) is not null || !afterRestart.IsRestartRequired(entries[0].Id))
            throw new Exception("Missing installation record retained a live WPF module.");
        restartedVm.Shutdown();
        foreach (var scenario in new[] { "missing-dll", "invalid-dll", "host-incompatible" })
        {
            var failureRoot = Path.Combine(root, scenario);
            var failureModules = Path.Combine(failureRoot, "modules");
            var failureCatalog = new GameModuleCatalogService(failureModules, client);
            await failureCatalog.InstallAsync(entries[0]);
            var package = Path.Combine(failureModules, "packages", entries[0].Id, entries[0].Version);
            if (scenario == "host-incompatible")
            {
                var manifestPath = Path.Combine(package, "module.json");
                var manifest = JsonSerializer.Deserialize<InstalledModuleManifest>(File.ReadAllText(manifestPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                manifest.MinimumHostVersion = "999.0.0";
                File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
            }
            else
            {
                var dll = Directory.GetFiles(package, "*.dll").Single();
                if (scenario == "missing-dll") File.Delete(dll);
                else File.WriteAllText(dll, "invalid test DLL");
            }
            using var failedRegistry = new GameAdapterRegistry(failureModules);
            var failedVm = CreateViewModel(failureRoot, failureCatalog, failedRegistry);
            try
            {
                var failedGame = new GameProfile { ModuleId = entries[0].Id, IsModuleLoaded = true };
                failedVm.Games.Add(failedGame);
                RefreshModuleMarkers(failedVm, reconcile: true);
                if (!failedGame.IsModuleInstalled || failedGame.IsModuleLoaded)
                    throw new Exception($"Installation without a successful module load showed a marker: {scenario}.");
                if (scenario != "missing-dll" && failedRegistry.LoadErrors.Count == 0)
                    throw new Exception("Failed module load did not retain diagnostic information.");
            }
            finally { failedVm.Shutdown(); }
        }
        vm.Shutdown();
    }

    private static void RefreshModuleMarkers(MainViewModel vm, bool reconcile = false) =>
        typeof(MainViewModel).GetMethod(reconcile ? "ReconcileInstalledModulesWithLibrary" : "RefreshLibraryModuleLoadStates",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);

    private static GameModuleCatalogEntry ReadEntry(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry("module.json")!.Open();
        var entry = JsonSerializer.Deserialize<GameModuleCatalogEntry>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        entry.DownloadUrl = "https://example.invalid/" + Path.GetFileName(path);
        entry.SizeBytes = new FileInfo(path).Length;
        entry.Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        if (entry.Id == "game.last-epoch") CheckLastEpochBuildGuard(zip);
        return entry;
    }

    private static async Task CheckRecoveryMarkerAsync(string root)
    {
        var updates = Path.Combine(root, "recovery", "updates");
        Directory.CreateDirectory(updates);
        var runner = Path.Combine(updates, "updater-preserved.exe");
        var failed = Path.Combine(updates, "failed-update-preserved.json");
        var archiveDirectory = Path.Combine(updates, "0.4.7");
        Directory.CreateDirectory(archiveDirectory);
        var archive = Path.Combine(archiveDirectory, "preserved.zip");
        await File.WriteAllTextAsync(runner, "runner");
        await File.WriteAllTextAsync(failed, "record");
        await File.WriteAllTextAsync(archive, "archive");
        await File.WriteAllTextAsync(Path.Combine(updates, "recovery-required.json"), "{}");
        var service = new ApplicationUpdateService(updates);
        if (!File.Exists(runner) || !File.Exists(failed) || !File.Exists(archive))
            throw new Exception("Startup cleanup erased recovery evidence.");
        var target = new ApplicationReleaseTarget("0.4.7", "GameValueEditor-v0.4.7-win-x64.zip",
            "https://example.invalid/package.zip", 1, new string('A', 64), new string('a', 40), 2, 7, 5);
        try { await service.DownloadAsync(target, ApplicationUpdateOperation.Update); throw new Exception("Update bypassed recovery marker."); }
        catch (InvalidOperationException exception) when (exception.Message.Contains("恢复", StringComparison.Ordinal)) { }
        try { service.LaunchPendingUpdate(false); throw new Exception("Pending install bypassed recovery marker."); }
        catch (InvalidOperationException exception) when (exception.Message.Contains("恢复", StringComparison.Ordinal)) { }
    }

    private static void CheckLastEpochBuildGuard(ZipArchive zip)
    {
        var context = new System.Runtime.Loader.AssemblyLoadContext("guard-" + Guid.NewGuid(), true);
        try
        {
            using var dll = zip.GetEntry("GameValueEditor.Modules.LastEpoch.dll")!.Open();
            using var assemblyBytes = new MemoryStream();
            dll.CopyTo(assemblyBytes); assemblyBytes.Position = 0;
            var assembly = context.LoadFromStream(assemblyBytes);
            var guard = assembly.GetType("GameValueEditor.Modules.LastEpoch.LastEpochBuildGuard", true)!;
            var match = guard.GetMethod("IsVerifiedBuild", BindingFlags.Static | BindingFlags.NonPublic)!;
            using var manifest = zip.GetEntry("module.json")!.Open();
            var entry = JsonSerializer.Deserialize<GameModuleCatalogEntry>(manifest, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            foreach (var build in entry.CompatibleBuilds)
            {
                var identity = new GameValueEditor.ModuleSdk.GameBuildIdentity(build.ExecutableSha256.ToLowerInvariant(), "", build.GameAssemblySha256, build.MetadataSha256);
                if (!(bool)match.Invoke(null, [identity])!) throw new Exception("Verified build was rejected.");
            }
            var first = entry.CompatibleBuilds[0]; var last = entry.CompatibleBuilds[^1];
            foreach (var identity in new[]
            {
                new GameValueEditor.ModuleSdk.GameBuildIdentity(first.ExecutableSha256, "", first.GameAssemblySha256, last.MetadataSha256),
                new GameValueEditor.ModuleSdk.GameBuildIdentity(first.ExecutableSha256, "", "", ""),
                new GameValueEditor.ModuleSdk.GameBuildIdentity(first.ExecutableSha256, "", new string('F', 64), last.MetadataSha256)
            }) if ((bool)match.Invoke(null, [identity])!) throw new Exception("Unverified/mixed build was accepted.");
        }
        finally { context.Unload(); }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => factory(request);
    }
}
