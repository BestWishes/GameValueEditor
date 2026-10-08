using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Http;
using GameValueEditor.ModuleSdk;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameValueEditor.Dialogs;
using GameValueEditor.Models;
using GameValueEditor.Services;
using GameValueEditor.Services.Adapters;
using GameValueEditor.ViewModels;

internal static class GameRecognitionRegressionTests
{
    private static int _assertions;

    internal static async Task InspectProcessIdentityAsync(int processId)
    {
        var service = new ProcessService();
        var snapshot = service.GetProcesses();
        var seed = snapshot.Single(process => process.ProcessId == processId);
        var group = service.ResolveLogicalGame(seed, snapshot);
        var identity = new GameIdentityEvidenceService().Read(group, snapshot);
        var fingerprint = await new VersionFingerprintService().CreateAsync(group.DataProcess.ExecutablePath);
        Check(identity.InstallationExecutablePath.Length > 0 && identity.PackageId.Length > 0 && fingerprint.PackageSha256.Length == 64,
            "Live read-only package/source evidence was incomplete.");
        // This is a hypothetical future build after learning, not a migration of the user's legacy profile.
        var learned = new GameProfile { ProcessName = seed.ProcessName, Identity = identity, ExecutablePath = Path.Combine(Path.GetTempPath(), "previous-run", identity.ExecutableName),
            Versions = [new() { BuildFingerprint = "PREVIOUS-BUILD" }] };
        var next = Item(1, 0, Path.Combine(Path.GetTempPath(), "next-run", identity.ExecutableName));
        Check(GameIdentityResolver.Resolve([learned], next, fingerprint with { BuildSha256 = "DIFFERENT-FUTURE-BUILD", Identity = identity }) == learned,
            "Learned live source failed a hypothetical temporary-directory/build update.");
        Console.WriteLine(JsonSerializer.Serialize(new { ReadOnly = true, Runtime = group.RuntimeKind.ToString(), DataRole = group.DataProcess.Role.ToString(),
            Members = group.Members.Count, LaunchSource = Path.GetFileName(identity.InstallationExecutablePath), identity.PackageId,
            identity.PlatformName, identity.PlatformAppId, Version = fingerprint.DisplayName, PackageHashPresent = true,
            LearnedIdentitySurvivesHypotheticalUpdate = true }));
    }

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        CheckRules(root);
        CheckNamesWithoutAdapter(Path.Combine(root, "names-only"));
        CheckSourcesAndDiscovery();
        await CheckPackagesAsync(Path.Combine(root, "packages"));
        await CheckModulePackageContractsAsync(Path.Combine(root, "module-package"));
        CheckConnectionCheckpoint();
        await CheckBackgroundSupportAsync(Path.Combine(root, "support"));
        await CheckMultipleInstancesAsync(Path.Combine(root, "multiple-instances"));
        await CheckCommittedAssociationAsync(Path.Combine(root, "committed-association"));
        await CheckManualAssociationAsync(Path.Combine(root, "manual"));
        Console.WriteLine($"Game recognition regressions passed: {_assertions} assertions (download/temporary origins, ambiguity, package builds, manual association and stale contexts).");
    }

    private static void CheckNamesWithoutAdapter(string root)
    {
        var store = new ModuleStateStore(root);
        store.SaveInstalled(new InstalledModuleDocument { Modules = [new("game.named", "0.0.1", DateTime.UnixEpoch)] });
        var directory = store.Resolve("packages/game.named/0.0.1"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "module.json"), JsonSerializer.Serialize(new InstalledModuleManifest
        { Id = "game.named", Version = "0.0.1", GameDisplayName = "别名游戏", ProcessNames = ["AliasGame"], HostApiVersion = 99 }));
        using var registry = new GameAdapterRegistry(root);
        var game = new GameProfile { Name = "别名游戏", ModuleId = "game.named" };
        Check(registry.FindById(game.ModuleId) is null && registry.LoadErrors.Count == 1, "Invalid optional module unexpectedly loaded");
        Check(GameIdentityResolver.Resolve([game], Item(10, 0, @"C:\games\AliasGame.exe"), moduleNames: registry.GetGameNames) == game,
            "Game name recognition depended on a successfully loaded optional DLL");
    }
    internal static void CheckDialog()
    {
        var application = Application.Current;
        var previousMode = application.ShutdownMode;
        var previousWindow = application.MainWindow;
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var before = _assertions;
        var first = Item(2, 0, @"C:\download\Adventure\Game.exe");
        var second = Item(3, 0, @"C:\download\Other\Game.exe");
        var launcher = new ProcessItem { ProcessId = 10, ExecutablePath = @"C:\download\Start.exe", ProcessName = "Start", Role = GameProcessRole.Main,
            RuntimeKind = GameRuntimeKind.Native, StartTimeUtc = DateTime.UnixEpoch };
        var webOne = new ProcessItem { ProcessId = 20, ParentProcessId = 10, ExecutablePath = Path.Combine(Path.GetTempPath(), "one", "Game.exe"),
            Role = GameProcessRole.Main, RuntimeKind = GameRuntimeKind.Electron, StartTimeUtc = DateTime.UnixEpoch.AddSeconds(1), WindowTitle = "Game one" };
        var webTwo = new ProcessItem { ProcessId = 30, ParentProcessId = 10, ExecutablePath = Path.Combine(Path.GetTempPath(), "two", "Game.exe"),
            Role = GameProcessRole.Main, RuntimeKind = GameRuntimeKind.Electron, StartTimeUtc = DateTime.UnixEpoch.AddSeconds(1), WindowTitle = "Game two" };
        var rendererOne = new ProcessItem { ProcessId = 21, ParentProcessId = 20, ExecutablePath = webOne.ExecutablePath, Role = GameProcessRole.Renderer,
            RuntimeKind = GameRuntimeKind.Electron, StartTimeUtc = DateTime.UnixEpoch.AddSeconds(2) };
        var rendererTwo = new ProcessItem { ProcessId = 31, ParentProcessId = 30, ExecutablePath = webTwo.ExecutablePath, Role = GameProcessRole.Renderer,
            RuntimeKind = GameRuntimeKind.Electron, StartTimeUtc = DateTime.UnixEpoch.AddSeconds(2) };
        var themes = new ThemeService();
        foreach (var theme in Enum.GetValues<ApplicationTheme>())
        {
            themes.Apply(theme);
            var dialog = new GameAssociationDialog(new("原游戏条目", [first, second, launcher, webOne, webTwo, rendererOne, rendererTwo], [first]));
            try
            {
                var choice = (ComboBox)dialog.FindName("ProcessComboBox");
                var confirm = (Button)dialog.FindName("ConfirmButton");
                var checkbox = (CheckBox)dialog.FindName("ConfirmationCheckBox");
                var search = (TextBox)dialog.FindName("SearchTextBox");
                var details = (TextBox)dialog.FindName("DetailsText");
                Check(choice.SelectedIndex == -1 && !confirm.IsEnabled, "Recommended candidate silently authorized association.");
                choice.SelectedItem = first;
                Check(details.Text.Contains(first.ExecutablePath) && details.Text.Contains("原游戏条目") && !confirm.IsEnabled,
                    "Association dialog omitted target/path or allowed an unchecked selection.");
                checkbox.IsChecked = true;
                Check(confirm.IsEnabled, "Explicitly checked association remained disabled.");
                choice.SelectedItem = second;
                Check(checkbox.IsChecked == false && !confirm.IsEnabled, "Changing candidate retained stale confirmation.");
                choice.SelectedItem = launcher; checkbox.IsChecked = true;
                Check(details.Text.Contains("多个游戏实例") && !details.Text.Contains(first.ExecutablePath) && !confirm.IsEnabled,
                    "Ambiguous selection retained stale details or authorized confirmation.");
                choice.SelectedItem = rendererOne; checkbox.IsChecked = true;
                Check(confirm.IsEnabled && details.Text.Contains(webOne.ExecutablePath), "Explicit renderer could not recover from launcher ambiguity.");
                choice.SelectedItem = first;
                var content = (FrameworkElement)dialog.Content;
                content.Measure(new Size(632, double.PositiveInfinity));
                var height = Math.Max(1, (int)Math.Ceiling(content.DesiredSize.Height));
                content.Arrange(new Rect(0, 0, 632, height)); content.UpdateLayout();
                Check(height < 700 && confirm.ActualWidth >= 100 && details.ActualHeight == 148,
                    "Association layout overflowed or collapsed its fixed controls.");
                var bitmap = new RenderTargetBitmap(632, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                var output = Path.GetFullPath("artifacts"); Directory.CreateDirectory(output);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.Combine(output, $"game-association-{theme.ToString().ToLowerInvariant()}.png"))) encoder.Save(file);
                checkbox.IsChecked = true; search.Text = "no-such-game";
                Check(choice.Items.Count == 0 && !confirm.IsEnabled && choice.SelectedIndex == -1, "Filtering retained an invisible authorized candidate.");
            }
            finally { dialog.Close(); }
        }
        themes.Apply(ApplicationTheme.Light);
        application.MainWindow = previousWindow;
        application.ShutdownMode = previousMode;
        Check(!application.Dispatcher.HasShutdownStarted, "Headless association windows shut down the shared test dispatcher.");
        Console.WriteLine($"Game association dialog passed: {_assertions - before} assertions and 5 theme renders.");
    }

    private static void CheckRules(string root)
    {
        var process = Item(2, 0, @"C:\\new-location\\Adventure.exe");
        var old = Evidence(@"C:\\old-location\\Adventure.exe");
        var game = new GameProfile { Name = "User rename", ProcessName = "Adventure", ExecutablePath = old.InstallationExecutablePath,
            Identity = old, Versions = [new() { BuildFingerprint = "OLD", Fields = [new() { Name = "one" }, new() { Name = "two" }] }] };
        var current = old with { InstallationExecutablePath = process.ExecutablePath, PackageId = "changed-package" };
        var fingerprint = Fingerprint("NEW", current);
        Check(GameIdentityResolver.Resolve([game], process) == game, "Name recognition required a fingerprint.");
        Check(GameIdentityResolver.Resolve([game], process, fingerprint) == game, "Updated/moved same-name game was rejected.");
        Check(GameIdentityResolver.Resolve([game], process, fingerprint with { Identity = current with { ProductName = "changed-product" } }) == game,
            "Matching executable name was vetoed by changed product metadata.");
        Check(GameIdentityResolver.Resolve([game], process, fingerprint with { Identity = current with { PlatformName = "Steam", PlatformAppId = "456" } }) == game,
            "Matching game name depended on its launcher/platform.");
        Check(GameIdentityResolver.Resolve([game], Item(3, 0, @"C:\\other\\Different.exe"), Fingerprint("OLD", null)) is null,
            "Identical historical hash identified a differently named game.");
        Check(GameIdentityResolver.Resolve([new() { ExecutablePath = process.ExecutablePath, Versions = [new() { BuildFingerprint = "NEW" }] }],
            process, fingerprint with { Identity = null }) is null, "Path/hash alone identified a game without a name.");
        Check(GameIdentityResolver.Resolve([game, new() { ProcessName = "Adventure" }], process) is null,
            "Duplicate named library records silently selected the first.");
        var duplicate = new GameProfile { ProcessName = "Adventure" };
        Check(GameIdentityResolver.Resolve([game, duplicate], process, preferredGame: game) == game,
            "An explicitly selected same-name record was not preferred.");
        Check(GameIdentityResolver.Resolve([game, duplicate], process, preferredGame: new() { Name = "Unrelated" }) is null,
            "An unrelated preference bypassed name matching.");
        var seeded = new GameProfile { Name = "最后纪元", ModuleId = "game.last-epoch" };
        IReadOnlyList<string> Aliases(string id) => id == seeded.ModuleId ? ["最后纪元", "Last Epoch"] : [];
        var epoch = Item(4, 0, @"G:\\new\\Last Epoch.exe");
        Check(GameIdentityResolver.Resolve([seeded], epoch, moduleNames: Aliases) == seeded,
            "Chinese seeded profile failed its declared English alias without a historical path/build.");
        Check(GameIdentityResolver.Resolve([seeded], epoch, confirmedModuleIds: ["unrelated"], moduleNames: Aliases) == seeded,
            "Optional adapter compatibility vetoed name recognition.");
        var expedition = new GameProfile { Name = "再刷一把·远征" };
        var title = new ProcessItem { ProcessName = "Game", WindowTitle = "再刷一把：远征" };
        Check(GameIdentityResolver.Resolve([expedition], title) == expedition, "Equivalent punctuation in a game name did not normalize.");
        Check(GameIdentityResolver.Resolve([expedition], new() { ProcessName = "Game", Role = GameProcessRole.Renderer },
            runningNames: [title.WindowTitle]) == expedition, "Renderer routing lost the actual game's window name.");
        Check(GameIdentityResolver.Resolve([expedition], new() { ProcessName = "Game", WindowTitle = "再刷一把：远征2" }) is null,
            "Substring matching confused a sequel with its predecessor.");
        Check(GameIdentityResolver.NormalizeName("Last Epoch.exe") == GameIdentityResolver.NormalizeName("last epoch"),
            "Executable suffix/case/spacing normalization differed.");
        Check(game.Versions.Count == 1 && game.Versions[0].Fields.Count == 2 && game.ExecutablePath == old.InstallationExecutablePath,
            "Pure recognition mutated historical data.");
        Check(!GameIdentityResolver.MatchesVersion(game.Versions[0], fingerprint),
            "Name recognition reused version-bound scan addresses after an update.");
        var merged = GameIdentityResolver.Merge(old, new() { IsTemporaryExecutable = true });
        Check(merged.PackageId == old.PackageId && merged.InstallationExecutablePath == old.InstallationExecutablePath,
            "Missing optional metadata erased previous information.");
    }

    private static void CheckSourcesAndDiscovery()
    {
        var launcher = Item(1, 0, @"C:\download\Adventure\Start.exe");
        var runtime = Item(2, 1, Path.Combine(Path.GetTempPath(), "extract", "RenamedGame.exe"));
        var renderer = Item(3, 2, runtime.ExecutablePath, GameProcessRole.Renderer);
        var processes = new[] { launcher, runtime, renderer };
        Check(GameIdentityEvidenceService.FindLaunchSource(runtime, processes) == launcher, "Download launcher was not discovered.");
        var reused = withItemTime(launcher, runtime.StartTimeUtc.AddSeconds(1));
        Check(GameIdentityEvidenceService.FindLaunchSource(runtime, [reused, runtime]) is null, "Reused parent PID was accepted.");
        foreach (var common in new[] { "steam", "explorer", "cmd", "pwsh", "chrome", "node" })
            Check(GameIdentityEvidenceService.FindLaunchSource(runtime, [Item(1, 0, @"C:\shared\" + common + ".exe"), runtime]) is null,
                "A shared launcher became persistent game identity: " + common);
        var cycle = Item(1, 2, Path.Combine(Path.GetTempPath(), "cycle", "Start.exe"));
        Check(GameIdentityEvidenceService.FindLaunchSource(runtime, [cycle, runtime]) is null, "A cyclic parent chain was trusted.");
        Check(GameIdentityEvidenceService.FindLaunchSource(runtime, [runtime]) is null, "Missing ancestry invented a source.");
        Check(GameIdentityEvidenceService.IsTemporaryPath(runtime.ExecutablePath) && !GameIdentityEvidenceService.IsTemporaryPath(@"C:\download\Game.exe"), "Temporary path classification failed.");
        var game = new GameProfile { ProcessName = "RenamedGame", Identity = Evidence(launcher.ExecutablePath) };
        var candidates = new ProcessService().FindGameCandidates(game, processes);
        Check(candidates.Count == 1 && candidates[0] == renderer, "Renamed runtime discovery failed to deduplicate or route to renderer.");
        var group = new ProcessService().ResolveLogicalGame(renderer, processes);
        Check(group.RootProcess == runtime && group.DataProcess == renderer && group.Members.Count == 2, "Source discovery changed runtime lifetime/data routing.");
        var evidence = new GameIdentityEvidenceService().Read(group, processes);
        Check(evidence.InstallationExecutablePath == launcher.ExecutablePath && evidence.PlatformAppId.Length == 0, "Non-Steam launch source required a platform ID.");
    }

    private static async Task CheckPackagesAsync(string root)
    {
        Directory.CreateDirectory(root);
        var exe = Path.Combine(root, "Game.exe"); File.Copy(typeof(MainViewModel).Assembly.Location, exe);
        var service = new VersionFingerprintService();
        var native = await service.CreateAsync(exe);
        Check(native.BuildSha256 == native.Sha256 && native.PackageSha256.Length == 0, "Native fingerprints changed without a game package.");
        var asar = Path.Combine(root, "resources", "app.asar"); Directory.CreateDirectory(Path.GetDirectoryName(asar)!);
        WriteAsar(asar, "{\"name\":\"adventure-pc\",\"version\":\"1\"}");
        var first = await service.CreateAsync(exe);
        Check(first.Identity!.PackageId == "adventure-pc" && first.PackageSha256.Length == 64 && first.BuildSha256 != first.Sha256, "Electron identity/content fingerprint was missing.");
        WriteAsar(asar, "{\"name\":\"adventure-pc\",\"version\":\"2\"}");
        var second = await service.CreateAsync(exe);
        Check(first.Sha256 == second.Sha256 && first.PackageSha256 != second.PackageSha256 && first.BuildSha256 != second.BuildSha256, "A game-only ASAR update reused old build identity.");
        Check(!GameIdentityResolver.MatchesVersion(new() { ExecutableSha256 = first.Sha256 }, second), "Legacy EXE-only data proved an Electron package build.");
        Check(!GameIdentityResolver.MatchesVersion(new() { BuildFingerprint = first.BuildSha256, PackageSha256 = first.PackageSha256 }, second), "Updated package reused historical fields.");
        Check(GameIdentityResolver.MatchesVersion(new() { ExecutableSha256 = second.Sha256, PackageSha256 = second.PackageSha256 }, second), "Complete new component hashes failed to match.");
        foreach (var json in new[] { "{\"name\":\"one\",\"name\":\"two\"}", "{\"name\":42}", "{}", "[]", "not-json" })
        {
            WriteAsar(asar, json);
            Check(new GameIdentityEvidenceService().Read(exe).PackageId.Length == 0, "Malformed/ambiguous package identity was accepted.");
        }
        WriteAsar(asar, "{\"name\":\"good\"}", long.MaxValue.ToString());
        Check(new GameIdentityEvidenceService().Read(exe).PackageId.Length == 0, "Out-of-range ASAR entry was read.");
        WriteAsar(asar, "{\"name\":\"good\"}", link: "elsewhere");
        Check(new GameIdentityEvidenceService().Read(exe).PackageId.Length == 0, "ASAR link was followed as identity.");
        File.WriteAllBytes(asar, [4, 0, 0, 0, 255, 255, 255, 127]);
        Check(new GameIdentityEvidenceService().Read(exe).PackageId.Length == 0, "Oversized/truncated ASAR header was trusted.");
        var nwRoot = Path.Combine(root, "nw"); Directory.CreateDirectory(nwRoot);
        var nwExe = Path.Combine(nwRoot, "Game.exe"); File.Copy(exe, nwExe);
        using (var zip = ZipFile.Open(Path.Combine(nwRoot, "package.nw"), ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("package.json").Open())) writer.Write("{\"name\":\"download-nw-game\"}");
        var nw = await service.CreateAsync(nwExe);
        Check(nw.Identity!.PackageId == "download-nw-game" && nw.PackageSha256.Length == 64 && nw.BuildSha256 != nw.Sha256, "Packaged NW.js was not covered.");
        var store = new ProfileStore(Path.Combine(root, "store"));
        await store.SaveAsync(new() { Games = [new() { Identity = Evidence(@"C:\download\Launch.exe"), Versions = [new() { PackageSha256 = second.PackageSha256 }] }] });
        var restored = await store.LoadAsync();
        Check(restored.SchemaVersion == 7 && restored.Games[0].Identity!.PackageId == "adventure-pc" && restored.Games[0].Versions[0].PackageSha256 == second.PackageSha256,
            "Optional identity/build evidence did not round-trip through existing schema.");
    }

    private static async Task CheckManualAssociationAsync(string root)
    {
        foreach (var scenario in new[] { "cancel", "confirm", "switch", "exit", "foreign", "spoof", "save-failure", "save-failure-fields", "module-conflict", "session-owner" })
        {
            using var target = StartTarget();
            Check(await target.StandardOutput.ReadLineAsync() == "READY", "Isolated target failed to start.");
            var directory = Path.Combine(root, scenario);
            using var registry = new GameAdapterRegistry(Path.Combine(directory, "modules"));
            var catalog = new GameModuleCatalogService(Path.Combine(directory, "modules"));
            var vm = ModuleLifecycleRegressionTests.CreateViewModel(directory, catalog, registry);
            var historical = new GameVersionProfile { BuildFingerprint = "OLD", Fields = [new() { Name = "one" }, new() { Name = "two" }] };
            var game = new GameProfile { Name = "Original", ProcessName = "UnregisteredOldName", ExecutablePath = Path.Combine(directory, "old.exe"), Versions = [historical] };
            vm.Games.Add(game); vm.SelectedGame = game;
            var oldPath = game.ExecutablePath;
            var store = new ProfileStore(directory);
            try
            {
                if (scenario == "foreign")
                {
                    var live = new ProcessService().GetProcesses().Single(item => item.ProcessId == target.Id);
                    vm.Games.Add(new() { Name = "Other", ProcessName = live.ProcessName, ExecutablePath = live.ExecutablePath });
                }
                if (scenario == "session-owner")
                {
                    var processes = new ProcessService().GetProcesses();
                    var live = processes.Single(item => item.ProcessId == target.Id);
                    var other = new GameProfile { Name = "Other", IsConnected = true, Versions = [new() { BuildFingerprint = "OTHER-OLD" }] };
                    vm.Games.Add(other);
                    var sessions = (Dictionary<Guid, GameConnectionSession>)typeof(MainViewModel)
                        .GetField("_sessions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm)!;
                    sessions.Add(other.Id, new(new ProcessService().ResolveLogicalGame(live, processes), other.Id) { VersionId = other.Versions[0].Id });
                }
                if (scenario is "save-failure" or "save-failure-fields") File.WriteAllText(store.LibraryPath, "damaged-original");
                if (scenario == "save-failure-fields")
                {
                    historical.Fields[0].LocatorKind = "GameAdapter"; historical.Fields[0].AdapterId = "game.fixture";
                    historical.Fields[0].AdapterFieldKey = "fixture|item|count";
                    var adapters = (List<IGameAdapter>)typeof(GameAdapterRegistry).GetField("_adapters", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(registry)!;
                    adapters.Add(new SmokeTestModuleAdapter { IdentityId = "game.fixture" });
                }
                if (scenario == "module-conflict")
                {
                    game.ModuleId = "game.other";
                    var adapters = (List<GameValueEditor.ModuleSdk.IGameAdapter>)typeof(GameAdapterRegistry)
                        .GetField("_adapters", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(registry)!;
                    adapters.Add(new SmokeTestModuleAdapter { IdentityId = "game.fixture" });
                }
                var called = false;
                try
                {
                    await vm.AttachSelectedGameWithAssociationAsync(request =>
                    {
                        called = true;
                        var live = request.Processes.Single(item => item.ProcessId == target.Id);
                        if (scenario == "cancel") return null;
                        if (scenario == "switch") { var other = new GameProfile { Name = "Other" }; vm.Games.Add(other); vm.SelectedGame = other; }
                        if (scenario == "exit") { target.StandardInput.WriteLine(); target.WaitForExit(5000); }
                        if (scenario == "spoof") return withItemTime(live, live.StartTimeUtc.AddSeconds(1));
                        return live;
                    });
                    Check(scenario is "confirm" or "cancel", "Unsafe association completed: " + scenario);
                }
                catch (Exception exception) when (scenario is not "confirm" and not "cancel" &&
                    exception is InvalidOperationException or OperationCanceledException or InvalidDataException) { }
                Check(called, "Unproven legacy association bypassed explicit confirmation.");
                Check(vm.Games.Contains(game) && game.Versions.Contains(historical) && historical.Fields.Count == 2 && historical.BuildFingerprint == "OLD",
                    "Association removed or changed old version/fields: " + scenario);
                if (scenario == "confirm")
                {
                    Check(vm.SelectedGame == game && game.IsConnected && vm.SelectedVersion != historical && vm.SelectedVersion!.Fields.Count == 0,
                        "Confirmed association failed to keep entry and isolate new raw fields.");
                    var restored = await store.LoadAsync();
                    Check(restored.Games.Single().Id == game.Id && restored.Games.Single().Identity is not null && restored.Games.Single().Versions.Count == 2,
                        "Confirmed identity did not persist on original entry.");
                    vm.Shutdown();
                    using var secondRegistry = new GameAdapterRegistry(Path.Combine(directory, "modules"));
                    var secondVm = ModuleLifecycleRegressionTests.CreateViewModel(directory, catalog, secondRegistry);
                    try
                    {
                        secondVm.Games.Add(restored.Games.Single()); secondVm.SelectedGame = secondVm.Games.Single();
                        await secondVm.AttachSelectedGameWithAssociationAsync(_ => throw new InvalidOperationException("Learned association asked again."));
                        Check(secondVm.SelectedGame!.IsConnected && secondVm.SelectedGame.Versions.Count == 2, "Learned association did not survive reload.");
                    }
                    finally { secondVm.Shutdown(); }
                }
                else
                {
                    Check(vm.AttachedProcess is null && game.ExecutablePath == oldPath && game.Identity is null && !game.IsConnected,
                        "Canceled/rejected/failed association changed identity or attached: " + scenario);
                    if (scenario == "switch") Check(vm.SelectedGame!.Name == "Other", "Late association replaced the new selection.");
                    if (scenario == "session-owner") Check(vm.Games.Single(profile => profile.Name == "Other").IsConnected, "Failed association disconnected the existing owner.");
                    if (scenario is "save-failure" or "save-failure-fields") Check(File.ReadAllText(store.LibraryPath) == "damaged-original" && game.Versions.Count == 1,
                        "Save failure overwrote damaged original or retained an unpersisted version/semantic fields.");
                    else Check(!File.Exists(store.LibraryPath), "Rejected association persisted user data.");
                }
            }
            finally
            {
                vm.Shutdown();
                if (!target.HasExited) { target.StandardInput.WriteLine(); if (!target.WaitForExit(5000)) target.Kill(true); }
            }
        }
    }

    private static async Task CheckModulePackageContractsAsync(string root)
    {
        Directory.CreateDirectory(root);
        var hash = new string('A', 64); var package = new string('B', 64);
        var build = JsonSerializer.Deserialize<GameModuleBuildMatch>($"{{\"executableSha256\":\"{hash}\",\"packageSha256\":\"{package}\"}}",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Check(build.PackageSha256 == package, "Host deserialization lost the module's package constraint.");
        var version = new GameVersionProfile { ExecutableSha256 = hash, PackageSha256 = package };
        Check(GameIdentityResolver.MatchesInstalledBuild(build, version), "Exact installed package was rejected.");
        version.PackageSha256 = new string('C', 64);
        Check(!GameIdentityResolver.MatchesInstalledBuild(build, version), "Installed EXE match ignored a different package.");
        version.PackageSha256 = "";
        Check(!GameIdentityResolver.MatchesInstalledBuild(build, version), "Missing package hash satisfied a declared package constraint.");
        Check(GameIdentityResolver.MatchesInstalledBuild(new() { ExecutableSha256 = hash }, version), "Legacy native module compatibility changed.");
        var entry = new GameModuleCatalogEntry { Id = "game.package-fixture", DisplayName = "Fixture", Version = "0.0.3", HostApiVersion = 8,
            MinimumHostVersion = "0.5.1", CompatibleBuilds = [build], ProcessNames = ["Fixture"], Releases =
            [new() { Version = "0.0.3", HostApiVersion = 8, MinimumHostVersion = "0.5.1", CompatibleBuilds = [build] },
             new() { Version = "0.0.2", HostApiVersion = 8, MinimumHostVersion = "0.5.1", CompatibleBuilds = [build] },
             new() { Version = "0.0.1", HostApiVersion = 8, MinimumHostVersion = "0.5.1", CompatibleBuilds = [new() { ExecutableSha256 = hash, PackageSha256 = new string('D', 64) }] }] };
        using var client = new HttpClient(new CatalogFixtureHandler(JsonSerializer.Serialize(new GameModuleCatalog { SchemaVersion = 5, Modules = [entry] })));
        var catalog = new GameModuleCatalogService(root, client, currentHostVersion: "0.5.1");
        var fingerprint = Fingerprint("COMBINED", null) with { Sha256 = hash, PackageSha256 = package };
        var result = await catalog.CheckAsync("Fixture", fingerprint);
        Check(result.Availability == GameModuleAvailability.Available && result.IsExactBuildMatch, "Catalog failed to select the exact package.");
        result = await catalog.CheckAsync("Fixture", fingerprint with { PackageSha256 = "" });
        Check(result.Availability == GameModuleAvailability.NotAvailable && result.RollbackModule is null, "Catalog offered an unproven package.");
        new ModuleStateStore(root).SaveInstalled(new() { Modules = [new(entry.Id, "0.0.2", DateTime.UtcNow)] });
        version.PackageSha256 = package;
        result = await catalog.CheckAsync(new() { ProcessName = "Fixture" }, version);
        Check(result.Availability == GameModuleAvailability.UpdateAvailable && result.RemoteModule!.Version == "0.0.3" && result.RollbackModule is null,
            "Update/rollback selection included a retained release from another package.");
        new ModuleStateStore(root).SaveInstalled(new() { Modules = [new(entry.Id, "0.0.3", DateTime.UtcNow)] });
        result = await catalog.CheckAsync(new() { ProcessName = "Fixture" }, version);
        Check(result.RollbackModule?.Version == "0.0.2", "Manual rollback lost its compatible retained package.");
        version.PackageSha256 = new string('C', 64);
        result = await catalog.CheckAsync(new() { ProcessName = "Fixture" }, version);
        Check(result.Availability == GameModuleAvailability.NotAvailable && result.RollbackModule is null, "History-based checks ignored package changes.");
    }

    private static void CheckConnectionCheckpoint()
    {
        var field = new SavedField { Name = "Original", IsValueLocked = true, LockedValue = "2" };
        var version = new GameVersionProfile { DisplayName = "Old", BuildFingerprint = "OLD", PackageSha256 = "old-package",
            LastVerifiedUtc = DateTime.UnixEpoch, Fields = [field], IsCurrentBuild = false };
        var game = new GameProfile { Name = "User name", ExecutablePath = "old.exe", ProcessName = "Old", LastUsedUtc = DateTime.UnixEpoch, Versions = [version] };
        var versions = game.Versions; var fields = version.Fields;
        var checkpoint = new GameConnectionProfileCheckpoint(game);
        game.Identity = new() { PackageId = "new" }; game.ExecutablePath = "new.exe"; game.ProcessName = "New"; game.LastUsedUtc = DateTime.UtcNow;
        version.DisplayName = "New"; version.BuildFingerprint = "NEW"; version.PackageSha256 = "new-package"; version.IsCurrentBuild = true;
        version.LastVerifiedUtc = DateTime.UtcNow; version.Fields.Add(new() { LocatorKind = "GameAdapter", AdapterFieldKey = "migrated" });
        game.Versions.Add(new() { Fields = [new() { LocatorKind = "GameAdapter", AdapterFieldKey = "new-version-field" }] });
        checkpoint.Restore();
        Check(game.Versions == versions && game.Versions.Count == 1 && game.Versions[0] == version && version.Fields == fields && version.Fields.Single() == field,
            "Connection rollback replaced old objects or retained migrated/new version fields.");
        Check(version.BuildFingerprint == "OLD" && version.PackageSha256 == "old-package" && version.DisplayName == "Old" && !version.IsCurrentBuild &&
            version.LastVerifiedUtc == DateTime.UnixEpoch && game.LastUsedUtc == DateTime.UnixEpoch && game.Identity is null && game.ExecutablePath == "old.exe" && game.ProcessName == "Old",
            "Connection rollback failed to restore identity or existing version metadata.");
        Check(field.IsValueLocked && field.LockedValue == "2" && field.Name == "Original" && game.Name == "User name", "Rollback modified original fields/user naming.");
    }

    private static async Task CheckBackgroundSupportAsync(string root)
    {
        using var registry = new GameAdapterRegistry(Path.Combine(root, "modules"));
        var adapters = (List<IGameAdapter>)typeof(GameAdapterRegistry).GetField("_adapters", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(registry)!;
        var uiThread = Environment.CurrentManagedThreadId;
        var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var adapter = new SmokeTestModuleAdapter { IdentityOnly = true, IdentityId = "game.identity-probe",
            SupportsOverride = () => { entered.TrySetResult(Environment.CurrentManagedThreadId); return release.Wait(TimeSpan.FromSeconds(10)); } };
        adapters.Add(adapter);
        var pending = registry.ResolveAsync(Item(2, 0, "fixture.exe"), Fingerprint("fixture", null));
        try
        {
            Check(await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)) != uiThread && !pending.IsCompleted, "Expensive Supports blocked the dispatcher.");
            await Task.Yield();
            Check(Environment.CurrentManagedThreadId == uiThread, "Background support lost the caller's dispatcher context.");
            registry.Deactivate(adapter.Id);
        }
        finally { release.Set(); }
        Check(await pending is null, "Removed module's late Supports result reactivated the adapter.");

        var catalog = new GameModuleCatalogService(Path.Combine(root, "vm-modules"));
        using var vmRegistry = new GameAdapterRegistry(Path.Combine(root, "vm-modules"));
        var vmAdapters = (List<IGameAdapter>)typeof(GameAdapterRegistry).GetField("_adapters", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vmRegistry)!;
        var supportsThread = 0; var metadataThread = 0; var supportCalls = 0;
        vmAdapters.Add(new SmokeTestModuleAdapter { IdentityOnly = true, IdentityId = "game.identity-probe",
            SupportsOverride = () => { supportsThread = Environment.CurrentManagedThreadId; Interlocked.Increment(ref supportCalls); return true; },
            MetadataOverride = () => { metadataThread = Environment.CurrentManagedThreadId; return new("fixture version", "fixture", ""); } });
        var vm = ModuleLifecycleRegressionTests.CreateViewModel(root, catalog, vmRegistry);
        using var target = StartTarget();
        try
        {
            Check(await target.StandardOutput.ReadLineAsync() == "READY", "Background callback target failed to start.");
            vm.SelectedProcess = new ProcessService().GetProcesses().Single(item => item.ProcessId == target.Id);
            await vm.AttachSelectedProcessAsync();
            Check(supportCalls == 1 && supportsThread != uiThread && metadataThread != uiThread && vm.AttachedProcess?.ProcessId == target.Id,
                "Connection repeated Supports or ran module hash/metadata callbacks on the UI thread.");
        }
        finally { vm.Shutdown(); target.StandardInput.WriteLine(); if (!target.WaitForExit(5000)) target.Kill(true); }
    }

    private sealed class CatalogFixtureHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    private static async Task CheckMultipleInstancesAsync(string root)
    {
        using var first = StartTarget(); using var second = StartTarget();
        Check(await first.StandardOutput.ReadLineAsync() == "READY" && await second.StandardOutput.ReadLineAsync() == "READY", "Multi-instance targets failed to start.");
        using var registry = new GameAdapterRegistry(Path.Combine(root, "modules"));
        var vm = ModuleLifecycleRegressionTests.CreateViewModel(root, new GameModuleCatalogService(Path.Combine(root, "modules")), registry);
        try
        {
            var live = new ProcessService().GetProcesses().Single(process => process.ProcessId == first.Id);
            var game = new GameProfile { Name = "Multiple instances", ExecutablePath = live.ExecutablePath, ProcessName = live.ProcessName };
            vm.Games.Add(game); vm.SelectedGame = game;
            var asked = false;
            await vm.AttachSelectedGameWithAssociationAsync(request =>
            {
                asked = true;
                Check(request.SuggestedProcesses.Any(process => process.ProcessId == first.Id) && request.SuggestedProcesses.Any(process => process.ProcessId == second.Id),
                    "Ambiguous library connection lost valid actual instances.");
                return request.Processes.Single(process => process.ProcessId == second.Id);
            });
            Check(asked && vm.AttachedProcess?.ProcessId == second.Id && game.IsConnected, "Multiple verified instances silently selected the first or ignored explicit choice.");
        }
        finally
        {
            vm.Shutdown();
            foreach (var process in new[] { first, second }) { process.StandardInput.WriteLine(); if (!process.WaitForExit(5000)) process.Kill(true); }
        }
    }

    private static async Task CheckCommittedAssociationAsync(string root)
    {
        using var target = StartTarget();
        Check(await target.StandardOutput.ReadLineAsync() == "READY", "Commit-boundary target failed to start.");
        using var registry = new GameAdapterRegistry(Path.Combine(root, "modules"));
        var vm = ModuleLifecycleRegressionTests.CreateViewModel(root, new GameModuleCatalogService(Path.Combine(root, "modules")), registry);
        var store = new ProfileStore(root);
        var old = new GameVersionProfile { BuildFingerprint = "OLD", Fields = [new() { Name = "Historical" }] };
        var game = new GameProfile { Name = "Original", ExecutablePath = "missing-old.exe", Versions = [old] };
        var other = new GameProfile { Name = "Other" };
        vm.Games.Add(game); vm.Games.Add(other); vm.SelectedGame = game;
        try
        {
            Task pending;
            using (var lease = new FileStream(store.LibraryPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                pending = vm.AttachSelectedGameWithAssociationAsync(request => request.Processes.Single(process => process.ProcessId == target.Id));
                var timer = Stopwatch.StartNew();
                while (game.Identity is null && !pending.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
                Check(game.Identity is not null && !pending.IsCompleted, "Association did not reach the isolated blocked save.");
                vm.SelectedGame = other;
            }
            try { await pending; throw new InvalidOperationException("Stale association unexpectedly updated its UI."); }
            catch (OperationCanceledException) { }
            var persisted = (await store.LoadAsync()).Games.Single(item => item.Id == game.Id);
            Check(game.Identity == persisted.Identity && game.ExecutablePath == persisted.ExecutablePath && game.Versions.Count == persisted.Versions.Count && game.Versions.Count == 2,
                "Successfully committed association was rolled back in memory after a selection change.");
            Check(vm.SelectedGame == other && vm.AttachedProcess is null && old.Fields.Single().Name == "Historical", "Committed stale result overwrote the new selection or history.");
        }
        finally { vm.Shutdown(); target.StandardInput.WriteLine(); if (!target.WaitForExit(5000)) target.Kill(true); }
    }

    private static Process StartTarget()
    {
        var info = new ProcessStartInfo(Path.ChangeExtension(typeof(GameRecognitionRegressionTests).Assembly.Location, ".exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("--game-identity-target");
        return Process.Start(info)!;
    }

    private static void WriteAsar(string path, string package, string offset = "0", string? link = null)
    {
        var bytes = Encoding.UTF8.GetBytes(package);
        var entry = new Dictionary<string, object> { ["size"] = bytes.Length, ["offset"] = offset };
        if (link is not null) entry["link"] = link;
        var json = JsonSerializer.SerializeToUtf8Bytes(new { files = new Dictionary<string, object> { ["package.json"] = entry } });
        var headerSize = (json.Length + 8 + 3) / 4 * 4;
        using var stream = File.Create(path); using var writer = new BinaryWriter(stream);
        writer.Write(4); writer.Write(headerSize); writer.Write(headerSize - 4); writer.Write(json.Length); writer.Write(json);
        writer.Write(new byte[headerSize - 8 - json.Length]); writer.Write(bytes);
    }

    private static GameIdentityEvidence Evidence(string origin) => new() { InstallationExecutablePath = origin, ExecutableName = "Game.exe", ProductName = "Adventure", PackageId = "adventure-pc" };
    private static VersionFingerprint Fingerprint(string build, GameIdentityEvidence? identity) => new("fixture", "", "", build, 1, "x64", build, "", "", Identity: identity);
    private static ProcessItem Item(int id, int parent, string path, GameProcessRole role = GameProcessRole.Main) => new()
    { ProcessId = id, ParentProcessId = parent, ExecutablePath = path, ProcessName = Path.GetFileNameWithoutExtension(path),
        StartTimeUtc = new DateTime(2026, 1, 1, 0, 0, id, DateTimeKind.Utc), Role = role, RuntimeKind = GameRuntimeKind.Electron };
    private static ProcessItem withItemTime(ProcessItem source, DateTime time) => new()
    { ProcessId = source.ProcessId, ParentProcessId = source.ParentProcessId, ExecutablePath = source.ExecutablePath, ProcessName = source.ProcessName,
        StartTimeUtc = time, Role = source.Role, RuntimeKind = source.RuntimeKind };
    private static void Check(bool condition, string message) { _assertions++; if (!condition) throw new InvalidOperationException(message); }
}
