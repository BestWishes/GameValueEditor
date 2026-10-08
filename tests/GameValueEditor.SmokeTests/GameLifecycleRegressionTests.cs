using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using GameValueEditor;
using GameValueEditor.Models;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Services;
using GameValueEditor.Services.Adapters;
using GameValueEditor.ViewModels;
using Microsoft.Win32.SafeHandles;

internal static class GameLifecycleRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _assertions;

    internal static async Task RunAsync(string root)
    {
        CheckIdentityRules();
        await CheckConnectionsAsync(Path.Combine(root, "connections"));
        await CheckLegacyIdentityUpgradeAsync(Path.Combine(root, "legacy-upgrade"));
        CheckModuleAssociation(Path.Combine(root, "module-association"));
        foreach (var scenario in new[] { "stay", "switch", "return", "not-connected", "exit", "normalization-failure", "speed-busy" })
            await CheckRemovalAsync(Path.Combine(root, "removal-" + scenario), scenario);
        CheckShutdown(Path.Combine(root, "shutdown"));
        Console.WriteLine($"Game lifecycle regressions passed: {_assertions} assertions (identity, removal ownership, recoverable shutdown).");
    }

    private static void CheckIdentityRules()
    {
        var process = ProcessItem("Game", "B", @"C:\games\B\Game.exe");
        var a = new GameProfile { Name = "A", ProcessName = "Game", ExecutablePath = @"C:\games\A\Game.exe" };
        var b = new GameProfile { Name = "B", ProcessName = "Game", ExecutablePath = process.ExecutablePath };
        Check(GameIdentityResolver.Resolve([a], process) is null, "Same process name merged different games.");
        Check(GameIdentityResolver.Resolve([a, b], process) == b, "An exact path was overridden by a same-name entry.");
        Check(GameIdentityResolver.Resolve([b, new() { Name = "B", ExecutablePath = b.ExecutablePath }], process) is null, "Ambiguous names picked the first game.");
        Check(!GameIdentityResolver.PathsEqual("", "") && !GameIdentityResolver.PathsEqual(" ", " "), "Empty paths matched.");
        Check(GameIdentityResolver.PathsEqual(@"C:\games\B\..\A\Game.exe", a.ExecutablePath), "Normalized paths stopped matching.");
        var fingerprint = Fingerprint("EXE", "ASM", "META");
        a.Versions.Add(new() { ExecutableSha256 = "EXE" });
        Check(GameIdentityResolver.Resolve([a], process, fingerprint) is null, "EXE-only legacy record identified IL2CPP.");
        a.Versions[0].GameAssemblySha256 = "ASM"; a.Versions[0].MetadataSha256 = "META";
        Check(GameIdentityResolver.Resolve([a], process, fingerprint) is null, "Hashes identified a differently named game.");
        Check(GameIdentityResolver.Resolve([a], process, Fingerprint("EXE", "ASM2", "META")) is null, "A changed IL2CPP build reused an old identity.");
        Check(!GameIdentityResolver.MatchesVersion(new(), Fingerprint("")), "Empty hashes matched.");
        Check(!GameIdentityResolver.MatchesInstalledBuild(new(), new()), "An empty compatible-build row matched a game.");
        Check(GameIdentityResolver.MatchesInstalledBuild(new() { BuildFingerprint = fingerprint.BuildSha256 }, a.Versions[0]), "Legacy component combination did not match a manifest.");
        b.ExecutablePath = @"C:\games\old\Game.exe"; b.Versions.Add(new() { BuildFingerprint = fingerprint.BuildSha256 });
        Check(GameIdentityResolver.Resolve([a, b], process, fingerprint) == b, "Changed path/hash vetoed matching name.");
        a.ModuleId = "game.a"; b.ModuleId = "game.b";
        Check(GameIdentityResolver.Resolve([a, b], process, fingerprint, ["game.b"]) == b, "Confirmed module identity was not preferred.");
        Check(GameIdentityResolver.Resolve([a], process, fingerprint, ["game.b"]) is null, "Different module identity was overwritten by matching hashes.");
        a.ModuleId = "game.old";
        Check(GameIdentityResolver.Resolve([a], process, fingerprint, ["game.a", "game.old"]) is null, "Module identity overrode a different game name.");

        var pathGame = new GameProfile { Name = "B", ExecutablePath = a.ExecutablePath, ProcessName = "Game" };
        var exact = ProcessItem("Game", "different-title", a.ExecutablePath);
        Check(ProcessService.FindRunningGame(pathGame, [process, exact]) == process, "Old path overrode the actual game name.");
        var second = new ProcessItem { ProcessId = 12345, ProcessName = "Game", WindowTitle = "B", ExecutablePath = a.ExecutablePath,
            StartTimeUtc = process.StartTimeUtc, Role = GameProcessRole.Main, RuntimeKind = GameRuntimeKind.Native };
        Check(ProcessService.FindRunningGame(new() { Name = "B" }, [process, second]) is null, "Multiple same-name game instances were guessed.");
        Check(ProcessService.FindRunningGame(new() { Name = "B" }, [process]) == process, "Unique name candidate could no longer be discovered.");
    }

    private static async Task CheckConnectionsAsync(string root)
    {
        var executable = typeof(MainViewModel).Assembly.Location;
        var actual = await new VersionFingerprintService().CreateAsync(executable);
        foreach (var scenario in new[] { "different-game", "moved", "module", "legacy-module" })
        {
            using var fixture = new Fixture(Path.Combine(root, scenario));
            var game = new GameProfile { Name = "A", ProcessName = "Game", ExecutablePath = Path.Combine(root, "A", "Game.exe"),
                Versions = [new() { BuildFingerprint = scenario == "moved" ? actual.BuildSha256 : "OTHER" }] };
            if (scenario != "different-game") game.ProcessName = "ActualGame";
            if (scenario.Contains("module", StringComparison.Ordinal))
            {
                game.ExecutablePath = ""; game.Versions.Clear();
                game.ModuleId = scenario == "legacy-module" ? "game.old" : "game.fixture";
                fixture.Adapters.Add(new SmokeTestModuleAdapter { IdentityOnly = true, IdentityId = "game.fixture", IdentityLegacyIds = ["game.old"] });
            }
            fixture.Vm.Games.Add(game); fixture.Vm.SelectedGame = game;
            var process = ProcessItem("ActualGame", "B", executable);
            var originalPath = game.ExecutablePath;
            if (scenario == "different-game")
            {
                var context = Invoke(fixture.Vm, "CaptureGameOperation");
                try { await Connect(fixture.Vm, process, game); throw new Exception("Wrong game was accepted by a library connection."); }
                catch (InvalidOperationException exception) when (exception.Message.Contains("游戏名称", StringComparison.Ordinal)) { }
                Check(fixture.Vm.SelectedGame == game && fixture.Vm.AttachedProcess is null && game.ExecutablePath == originalPath,
                    "Rejected library connection changed the old entry or view.");
                Invoke(fixture.Vm, "RequireCurrentGameOperation", context);
                await Connect(fixture.Vm, process, null);
                Check(fixture.Vm.SelectedGame is null && fixture.Vm.AttachedProcess == process && game.Versions.Count == 1, "Unknown top-level connection merged a same-name game.");
                var saved = await fixture.Vm.AddCurrentProcessToLibraryAsync("B");
                Check(saved != game && fixture.Vm.Games.Count == 2 && game.Name == "A" && game.ExecutablePath == originalPath,
                    "Saving B overwrote A.");
                Check(fixture.Sessions[saved.Id] == Active(fixture.Vm) && Active(fixture.Vm)!.GameId == saved.Id, "New entry and session were not bound together.");
            }
            else
            {
                await Connect(fixture.Vm, process, game);
                Check(fixture.Vm.SelectedGame == game && game.ExecutablePath == executable && game.IsConnected, "Verified moved/module game failed to retain identity.");
                Check(fixture.Vm.Games.Count == 1 && fixture.Sessions[game.Id] == Active(fixture.Vm) && Active(fixture.Vm)!.VersionId == fixture.Vm.SelectedVersion!.Id,
                    "Verified game connection left a transient or duplicate session.");
                Check(fixture.Vm.SelectedVersion!.BuildFingerprint == actual.BuildSha256, "Verified connection did not use the actual build.");
            }
        }
    }

    private static void CheckModuleAssociation(string root)
    {
        foreach (var scenario in new[] { "name-only", "other-module", "known-build", "ambiguous-build" })
        {
            using var fixture = new Fixture(Path.Combine(root, scenario));
            var version = new GameVersionProfile { ExecutableSha256 = "EXE", GameAssemblySha256 = "ASM", MetadataSha256 = "META" };
            var game = new GameProfile { Name = "A", ProcessName = "Game", Versions = [scenario == "name-only" ? new() : version],
                ModuleId = scenario == "other-module" ? "game.other" : "" };
            fixture.Vm.Games.Add(game);
            if (scenario == "ambiguous-build") fixture.Vm.Games.Add(new() { Name = "A", Versions = [version] });
            var manifest = new InstalledModuleManifest { Id = "game.fixture", Version = "1.0.0", DisplayName = "Fixture", GameDisplayName = "A", ProcessNames = ["Game"],
                CompatibleBuilds = [new() { BuildFingerprint = Fingerprint("EXE", "ASM", "META").BuildSha256,
                    ExecutableSha256 = "EXE", GameAssemblySha256 = "ASM", MetadataSha256 = "META" }] };
            fixture.Register(manifest);
            Invoke(fixture.Vm, "ReconcileInstalledModulesWithLibrary");
            if (scenario is "known-build" or "name-only") Check(game.ModuleId == manifest.Id && fixture.Vm.Games.Count == 1, "Matching name did not associate its module.");
            else Check(game.ModuleId == (scenario == "other-module" ? "game.other" : "") && fixture.Vm.Games.Count == (scenario == "ambiguous-build" ? 3 : 2),
                "Module reconciliation claimed an unrelated or ambiguous game.");
            var count = fixture.Vm.Games.Count;
            Invoke(fixture.Vm, "ReconcileInstalledModulesWithLibrary");
            Check(fixture.Vm.Games.Count == count, "Repeated reconciliation duplicated an already identified module.");
        }
    }

    private static async Task CheckLegacyIdentityUpgradeAsync(string root)
    {
        using var fixture = new Fixture(root);
        var installation = Path.Combine(root, "installation"); Directory.CreateDirectory(installation);
        var executable = Path.Combine(installation, "Game.exe"); File.Copy(typeof(MainViewModel).Assembly.Location, executable);
        File.WriteAllBytes(Path.Combine(installation, "GameAssembly.dll"), [1, 2, 3]);
        var metadataDirectory = Path.Combine(installation, "Game_Data", "il2cpp_data", "Metadata"); Directory.CreateDirectory(metadataDirectory);
        File.WriteAllBytes(Path.Combine(metadataDirectory, "global-metadata.dat"), [4, 5, 6]);
        var fingerprint = await new VersionFingerprintService().CreateAsync(executable);
        var exeOnly = new GameVersionProfile { ExecutableSha256 = fingerprint.Sha256 };
        var different = new GameVersionProfile { ExecutableSha256 = fingerprint.Sha256, GameAssemblySha256 = "OLD-ASSEMBLY", MetadataSha256 = fingerprint.MetadataSha256 };
        var current = new GameVersionProfile { ExecutableSha256 = fingerprint.Sha256, GameAssemblySha256 = fingerprint.GameAssemblySha256, MetadataSha256 = fingerprint.MetadataSha256 };
        var game = new GameProfile { Name = "fixture", ExecutablePath = executable, Versions = [exeOnly, different, current] };
        fixture.Vm.Games.Add(game); fixture.Vm.SelectedGame = game;
        await Connect(fixture.Vm, ProcessItem("Game", "fixture", executable), game);
        Check(exeOnly.BuildFingerprint.Length == 0 && different.BuildFingerprint.Length == 0 && different.GameAssemblySha256 == "OLD-ASSEMBLY",
            "Legacy migration guessed an IL2CPP identity from only the EXE hash.");
        Check(current.BuildFingerprint == fingerprint.BuildSha256 && fixture.Vm.SelectedVersion == current, "Complete current legacy identity did not migrate.");
        Check(game.Versions.Count == 3, "Legacy migration duplicated or erased historical versions.");
    }

    private static async Task CheckRemovalAsync(string root, string scenario)
    {
        using var fixture = new Fixture(root);
        var a = new GameProfile { Name = "A", ModuleId = "game.fixture", IsModuleInstalled = true, Versions = [new()] };
        var b = new GameProfile { Name = "B", Versions = [new()] };
        fixture.Vm.Games.Add(a); fixture.Vm.Games.Add(b);
        var sa = fixture.AddSession(a, ProcessItem("GameA", "A", "A.exe"));
        var sb = fixture.AddSession(b, ProcessItem("GameB", "B", "B.exe"));
        if (scenario == "not-connected") { fixture.Sessions.Remove(a.Id); a.IsConnected = false; }
        fixture.Vm.SelectedGame = a;
        var scanA = new ScanCandidate { Address = 1, ValueType = MemoryValueType.Int32, CurrentBytes = BitConverter.GetBytes(1) };
        var scanB = new ScanCandidate { Address = 2, ValueType = MemoryValueType.Int32, CurrentBytes = BitConverter.GetBytes(2) };
        sa.VisibleScanResults.Add(scanA); sb.VisibleScanResults.Add(scanB);
        fixture.Register(new() { Id = a.ModuleId, Version = "1.0.0" });
        var package = Path.Combine(root, "modules", "packages", a.ModuleId);
        var blockedPath = Path.Combine(package, "occupied.bin");
        File.WriteAllBytes(blockedPath, [1, 2, 3]);
        if (scenario == "normalization-failure") SetFault(sa.SpeedService);
        if (scenario == "speed-busy") sa.IsSpeedOperationRunning = true;
        try
        {
            using (var occupied = new FileStream(blockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var removal = fixture.Vm.DeleteSelectedGameAsync();
                Check(!removal.IsCompleted, "Removal fixture did not reach asynchronous file-occupancy retry.");
                if (scenario is not "stay") fixture.Vm.SelectedGame = b;
                if (scenario == "return") fixture.Vm.SelectedGame = a;
                if (scenario == "exit") fixture.Sessions.Remove(a.Id);
                occupied.Dispose();
                if (scenario is "normalization-failure" or "speed-busy")
                {
                    try { await removal; throw new Exception("Failed normalization removed a live inactive session."); }
                    catch (InvalidOperationException exception) when (exception.Message.Contains("安全回正", StringComparison.Ordinal) ||
                        exception.Message.Contains("正在调整倍速", StringComparison.Ordinal)) { }
                    Check(fixture.Vm.Games.Contains(a) && fixture.Sessions[a.Id] == sa, "Failed normalization orphaned A or erased its entry.");
                    Check(!a.IsModuleInstalled && fixture.Catalog.FindInstalled(a.ModuleId) is null, "Failed normalization fabricated a still-installed module and prevented retry.");
                }
                else { await removal; Check(!fixture.Vm.Games.Contains(a), "Requested A entry was not removed."); }
            }
            if (scenario is "stay" or "return")
            {
                Check(fixture.Vm.SelectedGame is null && fixture.Vm.AttachedProcess == sa.Process && Active(fixture.Vm) == sa && sa.GameId is null,
                    "Active A was not atomically restored as a transient connection.");
                Check(Get<ProcessSpeedService>(fixture.Vm, "_speedService") == sa.SpeedService && fixture.Vm.SelectedVersion is null &&
                      fixture.Vm.VisibleScanResults.Contains(scanA), "Transient A lost its scan or used another speed/version state.");
            }
            else
            {
                Check(fixture.Vm.SelectedGame == b && fixture.Vm.AttachedProcess == sb.Process && Active(fixture.Vm) == sb &&
                      Get<ProcessSpeedService>(fixture.Vm, "_speedService") == sb.SpeedService, "Late removal rebound the B view or speed service to A.");
                Check(fixture.Vm.SelectedVersion == b.Versions[0] && fixture.Vm.VisibleScanResults.Contains(scanB) && fixture.Sessions[b.Id] == sb,
                    "Late removal changed B's version, scan or session registration.");
            }
            Invoke(fixture.Vm, "RequireCurrentGameOperation", Invoke(fixture.Vm, "CaptureGameOperation"));
            Check(!fixture.Vm.IsShutdownCommitted && fixture.Vm.SynchronizeConnectionStates() == 0, "Removal left new operations or liveness synchronization invalid.");
            if (scenario is "normalization-failure" or "speed-busy")
            {
                ClearFault(sa.SpeedService); sa.IsSpeedOperationRunning = false;
                await Task.Delay(2100); // Honor the library button cooldown before the real retry.
                fixture.Vm.SelectedGame = a;
                await fixture.Vm.DeleteSelectedGameAsync();
                Check(!fixture.Vm.Games.Contains(a) && Active(fixture.Vm) == sa && sa.GameId is null, "Retry after a completed module uninstall could not remove A.");
            }
        }
        finally { ClearFault(sa.SpeedService); sa.IsSpeedOperationRunning = false; }
    }

    private static void CheckShutdown(string root)
    {
        using var fixture = new Fixture(root);
        var a = new GameProfile { Name = "A", Versions = [new()] };
        var b = new GameProfile { Name = "B", Versions = [new()] };
        fixture.Vm.Games.Add(a); fixture.Vm.Games.Add(b);
        var sa = fixture.AddSession(a, ProcessItem("A", "A", "A.exe"));
        var sb = fixture.AddSession(b, ProcessItem("B", "B", "B.exe"));
        sa.IsSpeedActive = true;
        fixture.Vm.SelectedGame = b;
        SetFault(sb.SpeedService);
        using var scan = new CancellationTokenSource();
        using var live = new CancellationTokenSource();
        using var locked = new CancellationTokenSource();
        Set(fixture.Vm, "_scanCancellation", scan); Set(fixture.Vm, "_liveRefreshCancellation", live); sb.LockMaintenanceCancellation = locked;
        var download = Invoke(fixture.Vm, "BeginDownload", new Action<DownloadProgressSnapshot>(_ => { }))!;
        var context = Invoke(fixture.Vm, "CaptureGameOperation");
        var generation = Get<long>(fixture.Vm, "_gameOperationGeneration");
        try
        {
            try { fixture.Vm.Shutdown(); throw new Exception("Faulted shutdown unexpectedly committed."); }
            catch (InvalidOperationException exception) when (exception.Message.Contains("安全回正", StringComparison.Ordinal)) { }
            Check(!fixture.Vm.IsShutdownCommitted && generation == Get<long>(fixture.Vm, "_gameOperationGeneration"), "Failed shutdown invalidated game operations.");
            Invoke(fixture.Vm, "RequireCurrentGameOperation", context);
            Invoke(fixture.Vm, "RequireCurrentGameOperation", Invoke(fixture.Vm, "CaptureGameOperation"));
            Check(!scan.IsCancellationRequested && !live.IsCancellationRequested && !locked.IsCancellationRequested &&
                  !Get<bool>(download, "_cancellation", cancellation: true), "Failed shutdown canceled a scan, refresh, lock or download.");
            Check(fixture.Sessions.Count == 2 && Active(fixture.Vm) == sb && fixture.Vm.SelectedGame == b && !sa.IsSpeedActive,
                "Preparation failure destroyed sessions or fabricated the already-normalized speed state.");
            Check(fixture.Vm.IsDownloadActive && fixture.Vm.CanCancelDownload, "Preparation failure disabled download controls.");
            ClearFault(sb.SpeedService);
            Set(fixture.Vm, "_speedOperationsInFlight", 1);
            try { fixture.Vm.Shutdown(); throw new Exception("Shutdown raced an active speed operation."); }
            catch (InvalidOperationException exception) when (exception.Message.Contains("正在调整", StringComparison.Ordinal)) { }
            Check(!fixture.Vm.IsShutdownCommitted && !scan.IsCancellationRequested, "Busy-speed close attempt canceled work.");
            Set(fixture.Vm, "_speedOperationsInFlight", 0); Set(fixture.Vm, "_isSpeedControlBlocked", true);
            fixture.Vm.Shutdown();
            Check(fixture.Vm.IsShutdownCommitted && scan.IsCancellationRequested && live.IsCancellationRequested && locked.IsCancellationRequested &&
                  Get<bool>(download, "_cancellation", cancellation: true), "Successful shutdown did not cancel all owned operations.");
            Check(fixture.Sessions.Count == 0, "Successful shutdown retained library sessions.");
            fixture.Vm.Shutdown();
            Check(fixture.Vm.IsShutdownCommitted, "Repeated shutdown was not idempotent.");
        }
        finally { ClearFault(sb.SpeedService); Set(fixture.Vm, "_speedOperationsInFlight", 0); Invoke(fixture.Vm, "EndDownload", download); }
    }

    internal static void CheckWindowShutdown()
    {
        var root = Path.Combine(Path.GetTempPath(), "gve-close-coordinator-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var fixture = new Fixture(root);
            var game = new GameProfile { Versions = [new()] }; fixture.Vm.Games.Add(game);
            var session = fixture.AddSession(game, ProcessItem("fixture", "fixture", "fixture.exe")); fixture.Vm.SelectedGame = game;
            SetFault(session.SpeedService);
            var window = new MainWindow(fixture.Vm);
            var timer = Get<DispatcherTimer>(window, "_connectionMonitorTimer"); timer.Start();
            try
            {
                Check(!window.TryShutdown(out var failure) && failure is not null && timer.IsEnabled && !fixture.Vm.IsShutdownCommitted,
                    "Failed window close stopped the monitor or permitted a broken view to close.");
                ClearFault(session.SpeedService);
                Check(window.TryShutdown(out failure) && failure is null && !timer.IsEnabled && fixture.Vm.IsShutdownCommitted,
                    "Successful close retry did not stop the connection monitor.");
                Check(window.TryShutdown(out failure) && failure is null, "Repeated window close failed.");
            }
            finally { ClearFault(session.SpeedService); timer.Stop(); window.Close(); }
            using var cleanupFixture = new Fixture(Path.Combine(root, "cleanup-failure"));
            using var scan = new CancellationTokenSource();
            using var registration = scan.Token.Register(() => throw new InvalidOperationException("Injected cleanup failure"));
            Set(cleanupFixture.Vm, "_scanCancellation", scan);
            var cleanupWindow = new MainWindow(cleanupFixture.Vm);
            var cleanupTimer = Get<DispatcherTimer>(cleanupWindow, "_connectionMonitorTimer"); cleanupTimer.Start();
            try
            {
                Check(cleanupWindow.TryShutdown(out var error) && error is AggregateException && cleanupFixture.Vm.IsShutdownCommitted && !cleanupTimer.IsEnabled,
                    "Post-commit cleanup failure left a partially destroyed live window.");
                Check(cleanupWindow.TryShutdown(out error) && error is null, "Repeated cleanup after a committed failure was not safe.");
            }
            finally { cleanupTimer.Stop(); cleanupWindow.Close(); }
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("Window close regressions passed: preparation failure preserves monitor, successful retry stops it.");
    }

    private static Task Connect(MainViewModel vm, ProcessItem process, GameProfile? preferred)
        => (Task)Invoke(vm, "ConnectToProcessAsync", process, preferred)!;
    private static GameConnectionSession? Active(MainViewModel vm) => Get<GameConnectionSession?>(vm, "_activeSession");
    private static VersionFingerprint Fingerprint(string exe, string assembly = "", string metadata = "")
        => new("fixture", "", "", exe, 1, "x64", string.IsNullOrEmpty(exe) ? "" : VersionFingerprintService.CreateBuildFingerprint(exe, assembly, metadata), assembly, metadata);
    private static ProcessItem ProcessItem(string name, string title, string path)
    {
        using var self = Process.GetCurrentProcess();
        return new() { ProcessId = self.Id, StartTimeUtc = self.StartTime.ToUniversalTime(), ProcessName = name,
            WindowTitle = title, ExecutablePath = path, Role = GameProcessRole.Main, RuntimeKind = GameRuntimeKind.Native };
    }
    private static void SetFault(ProcessSpeedService speed)
    {
        // Invalid managed state fails before CreateRemoteThread: no game or native target is written.
        Set(speed, "_processHandle", new SafeProcessHandle(new IntPtr(1), false));
        Set(speed, "_stateAddress", (ulong)1); Set(speed, "_updaterAddress", (ulong)1);
        Set(speed, "<Multiplier>k__BackingField", double.NaN); Set(speed, "<ActiveProcessId>k__BackingField", Environment.ProcessId);
        var kind = typeof(ProcessSpeedService).GetNestedType("ClockKind", BindingFlags.NonPublic)!;
        var slot = Activator.CreateInstance(typeof(ProcessSpeedService).GetNestedType("PatchedSlot", BindingFlags.NonPublic)!,
            [Enum.ToObject(kind, 0), (ulong)0, (ulong)0, (ulong)0]);
        ((System.Collections.IList)Get<object>(speed, "_patchedSlots")).Add(slot);
    }
    private static void ClearFault(ProcessSpeedService speed) => Set(speed, "<Multiplier>k__BackingField", 1d);
    private static object? Invoke(object target, string name, params object?[] args)
    {
        try { return target.GetType().GetMethod(name, Private)!.Invoke(target, args); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
    }
    private static T Get<T>(object target, string name, bool cancellation = false)
    {
        var value = target.GetType().GetField(name, Private)!.GetValue(target)!;
        return cancellation ? (T)(object)((CancellationTokenSource)value).IsCancellationRequested : (T)value;
    }
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static void Check(bool condition, string message) { _assertions++; if (!condition) throw new InvalidOperationException(message); }

    private sealed class Fixture : IDisposable
    {
        internal readonly MainViewModel Vm;
        internal readonly GameModuleCatalogService Catalog;
        internal readonly GameAdapterRegistry Registry;
        private readonly string _root;
        internal Dictionary<Guid, GameConnectionSession> Sessions => Get<Dictionary<Guid, GameConnectionSession>>(Vm, "_sessions");
        internal List<IGameAdapter> Adapters => Get<List<IGameAdapter>>(Registry, "_adapters");
        internal Fixture(string root)
        {
            _root = root; Catalog = new(Path.Combine(root, "modules")); Registry = new(Path.Combine(root, "modules"));
            Vm = ModuleLifecycleRegressionTests.CreateViewModel(root, Catalog, Registry);
        }
        internal GameConnectionSession AddSession(GameProfile game, ProcessItem process)
        {
            var session = new GameConnectionSession(new() { SeedProcess = process, RootProcess = process, DataProcess = process, Members = [process], RuntimeKind = GameRuntimeKind.Native }, game.Id)
            { VersionId = game.Versions[0].Id };
            Sessions.Add(game.Id, session); game.IsConnected = true; return session;
        }
        internal void Register(InstalledModuleManifest manifest)
        {
            var directory = Path.Combine(_root, "modules", "packages", manifest.Id, manifest.Version);
            Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "module.json"), JsonSerializer.Serialize(manifest));
            Catalog.RestoreRegistration(new(manifest.Id, manifest.Version, DateTime.UtcNow));
        }
        public void Dispose() { Vm.Shutdown(); Registry.Dispose(); }
    }

}
