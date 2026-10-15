using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using GameValueEditor.Models;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Services.Adapters;
using GameValueEditor.ViewModels;

internal static class ModuleDiagnosticsRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _assertions;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    internal static async Task RunAsync(string root)
    {
        _assertions = 0;
        await CheckResponsiveSnapshotAsync(Path.Combine(root, "responsive"));
        await CheckLegacyCallbacksAsync(Path.Combine(root, "legacy-callbacks"));
        foreach (var scenario in new[] { "other-game", "return-game", "version", "process", "disconnect", "fingerprint",
                     "session", "module-id", "deactivate", "replace", "shutdown", "late-error",
                     "provider-return-game", "provider-shutdown", "provider-late-error" })
            await CheckStaleAsync(Path.Combine(root, scenario), scenario);
        await CheckQueuedCancellationAsync(Path.Combine(root, "queued"));
        await CheckFailuresAsync(Path.Combine(root, "failures"));
        await CheckRejectedCloseAsync(Path.Combine(root, "rejected-close"));
        Console.WriteLine($"Module diagnostics regressions passed: {_assertions} assertions (isolated callbacks; no game access).");
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task CheckResponsiveSnapshotAsync(string root)
    {
        var entered = Signal(); var release = Signal();
        var uiThread = Environment.CurrentManagedThreadId;
        var supportsThread = 0; var providerThread = 0; var calls = 0;
        var adapter = new SmokeTestModuleAdapter
        {
            IdentityOnly = true, IdentityId = "game.diagnostics",
            SupportsOverride = () =>
            {
                supportsThread = Environment.CurrentManagedThreadId;
                Interlocked.Increment(ref calls); entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); return true;
            },
            DiagnosticsOverride = () => { providerThread = Environment.CurrentManagedThreadId; return []; }
        };
        using var fixture = new Fixture(root, adapter);
        fixture.A.Versions[0].GameDeclaredVersion = "snapshot-version";
        fixture.A.Versions[0].PlatformName = "fixture-platform";
        fixture.A.Versions[0].PlatformBuildId = "snapshot-build";
        var queuedEntered = Signal(); var queuedRelease = Signal();
        var preceding = fixture.Registry.RunCompatibilityCheckAsync(() =>
        { queuedEntered.TrySetResult(); queuedRelease.Task.GetAwaiter().GetResult(); return 1; });
        await queuedEntered.Task.WaitAsync(Deadline);
        ModuleCompatibilityReport? report = null;
        var presentationThread = 0;
        var task = fixture.Vm.ShowModuleCompatibilityReportAsync(value =>
        { report = value; presentationThread = Environment.CurrentManagedThreadId; });
        try
        {
            // Mutate before the worker can read anything, not after AddBuildItems.
            fixture.A.Versions[0].GameDeclaredVersion = "changed-version";
            fixture.A.Versions[0].PlatformBuildId = "changed-build";
            Get<List<string>>(fixture.Registry, "_loadErrors").Add("game.diagnostics v0.1.0: later-error");
            queuedRelease.TrySetResult();
            await entered.Task.WaitAsync(Deadline);
            Check(!fixture.Vm.CanViewModuleCompatibilityDiagnostics, "Diagnosis allowed duplicate work.");
            Check(!fixture.Vm.IsBusy, "Diagnosis blocked unrelated operations with the global busy flag.");
            var heartbeat = false;
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => heartbeat = true);
            Check(heartbeat && !task.IsCompleted, "UI dispatcher stalled during a slow diagnostic callback.");
            await fixture.Vm.ShowModuleCompatibilityReportAsync(_ => throw new InvalidOperationException("Duplicate report."));
            Check(calls == 1, "Duplicate click scheduled another diagnostic callback.");
        }
        finally { queuedRelease.TrySetResult(); release.TrySetResult(); await preceding.WaitAsync(Deadline); await task.WaitAsync(Deadline); }
        Check(report is not null && report.Text.Contains("snapshot-version", StringComparison.Ordinal) &&
              report.Text.Contains("snapshot-build", StringComparison.Ordinal) &&
              !report.Text.Contains("changed-version", StringComparison.Ordinal) &&
              !report.Text.Contains("later-error", StringComparison.Ordinal), "Report read mutable UI metadata or error collections.");
        Check(supportsThread != uiThread && providerThread != uiThread && providerThread != 0,
            "Module compatibility or extended diagnosis ran on the UI thread.");
        Check(presentationThread == uiThread, "Report presentation left the original UI dispatcher.");
        Check(fixture.Vm.CanViewModuleCompatibilityDiagnostics, "Diagnosis button did not recover after completion.");
    }

    private static async Task CheckLegacyCallbacksAsync(string root)
    {
        var uiThread = Environment.CurrentManagedThreadId;
        var entered = Signal(); var release = Signal(); var characterThread = 0;
        using var fixture = new Fixture(root, new SmokeTestModuleAdapter
        {
            IdentityId = "game.diagnostics",
            CharacterSupportOverride = () =>
            { characterThread = Environment.CurrentManagedThreadId; entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); return true; },
            ReadFieldOverride = (_, _) => throw new InvalidOperationException("Diagnosis must not read fields."),
            WriteFieldOverride = (_, _, _) => throw new InvalidOperationException("Diagnosis must not write fields.")
        });
        var presented = false;
        var task = fixture.Vm.ShowModuleCompatibilityReportAsync(_ => presented = true);
        try
        {
            await entered.Task.WaitAsync(Deadline);
            var responsive = false;
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => responsive = true);
            Check(responsive && !task.IsCompleted && characterThread != uiThread,
                "Legacy editor support callback blocked the UI thread.");
        }
        finally { release.TrySetResult(); await task.WaitAsync(Deadline); }
        Check(presented && fixture.Vm.CanViewModuleCompatibilityDiagnostics, "Legacy diagnosis did not finish normally.");
    }

    private static async Task CheckStaleAsync(string root, string scenario)
    {
        var entered = Signal(); var release = Signal(); var providerCalls = 0;
        var blockProvider = scenario.StartsWith("provider-", StringComparison.Ordinal);
        var adapter = new SmokeTestModuleAdapter
        {
            IdentityOnly = true, IdentityId = "game.diagnostics",
            DisplayNameOverride = scenario == "late-error" ? () =>
            { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); throw new InvalidOperationException("Late diagnosis failure."); } : null,
            SupportsOverride = () =>
            { if (!blockProvider) { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); } return true; },
            DiagnosticsOverride = () =>
            {
                Interlocked.Increment(ref providerCalls);
                if (blockProvider)
                {
                    entered.TrySetResult(); release.Task.GetAwaiter().GetResult();
                    if (scenario == "provider-late-error") throw new InvalidOperationException("Late provider failure.");
                }
                return [];
            }
        };
        using var fixture = new Fixture(root, adapter);
        var presented = false;
        var task = fixture.Vm.ShowModuleCompatibilityReportAsync(_ => presented = true);
        Task<int>? nextCheck = null;
        try
        {
            await entered.Task.WaitAsync(Deadline);
            switch (scenario)
            {
                case "other-game": case "late-error": case "provider-late-error": fixture.Vm.SelectedGame = fixture.B; break;
                case "return-game": case "provider-return-game": fixture.Vm.SelectedGame = fixture.B; fixture.Vm.SelectedGame = fixture.A; break;
                case "version": fixture.Vm.SelectedVersion = fixture.A.Versions[1]; break;
                case "process": fixture.SetProcess(Fixture.SelfProcess("replacement")); break;
                case "disconnect": fixture.SetProcess(null); break;
                case "fingerprint": Set(fixture.Vm, "_attachedFingerprint", new VersionFingerprint("", "", "", "new", 0, "x64", "new", "", "")); break;
                case "session": Set(fixture.Vm, "_activeSession", null); break;
                case "module-id": fixture.A.ModuleId = "game.changed"; break;
                case "deactivate": fixture.Registry.Deactivate(adapter.Id); break;
                case "replace":
                    fixture.Registry.Deactivate(adapter.Id);
                    fixture.Adapters.Add(new SmokeTestModuleAdapter { IdentityOnly = true, IdentityId = adapter.Id }); break;
                case "shutdown": case "provider-shutdown":
                    var elapsed = Stopwatch.StartNew(); fixture.Vm.Shutdown();
                    Check(elapsed.Elapsed < TimeSpan.FromSeconds(2), "Shutdown waited for the synchronous diagnostic callback.");
                    Check(!fixture.Vm.CanViewModuleCompatibilityDiagnostics, "Shutdown left diagnosis enabled."); break;
            }
            Check(!task.IsCompleted, "Cancellation abandoned an entered synchronous call before it completed.");
            Check(Get<SemaphoreSlim>(fixture.Registry, "_supportGate").CurrentCount == 0, "Canceled running diagnosis lost the gate.");
            nextCheck = fixture.Registry.RunCompatibilityCheckAsync(() => 1);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { });
            Check(!nextCheck.IsCompleted, "Compatibility gate was released before the real callback finished.");
        }
        finally
        {
            release.TrySetResult(); await task.WaitAsync(Deadline);
            if (nextCheck is not null) await nextCheck.WaitAsync(Deadline);
        }
        Check(!presented, "Stale report or failure was presented: " + scenario);
        if (scenario is "other-game" or "return-game" or "version" or "process" or "disconnect" or "shutdown")
            Check(providerCalls == 0, "Canceled diagnosis entered another module callback: " + scenario);
        if (scenario is not "shutdown" and not "provider-shutdown")
            Check(fixture.Vm.CanViewModuleCompatibilityDiagnostics, "Canceled diagnosis stayed busy: " + scenario);
    }

    private static async Task CheckQueuedCancellationAsync(string root)
    {
        var entered = Signal(); var release = Signal(); var calls = 0;
        var adapter = new SmokeTestModuleAdapter
        {
            IdentityOnly = true, IdentityId = "game.diagnostics",
            SupportsOverride = () => { Interlocked.Increment(ref calls); return true; }
        };
        using var fixture = new Fixture(root, adapter);
        var preceding = fixture.Registry.RunCompatibilityCheckAsync(() =>
        { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); return 1; });
        var presented = false;
        try
        {
            await entered.Task.WaitAsync(Deadline);
            var queued = fixture.Vm.ShowModuleCompatibilityReportAsync(_ => presented = true);
            fixture.Vm.SelectedGame = fixture.B;
            await queued.WaitAsync(Deadline);
            Check(!presented && calls == 0, "Canceled queued diagnosis still called the adapter.");
            Check(!preceding.IsCompleted, "Canceling queued diagnosis canceled somebody else's compatibility check.");
            Check(fixture.Vm.CanViewModuleCompatibilityDiagnostics, "Queued cancellation did not restore the diagnosis button.");
        }
        finally { release.TrySetResult(); await preceding.WaitAsync(Deadline); }
    }

    private static async Task CheckFailuresAsync(string root)
    {
        foreach (var scenario in new[] { "supports", "provider", "unexpected" })
        {
            var fail = true;
            var adapter = new SmokeTestModuleAdapter
            {
                IdentityOnly = true, IdentityId = "game.diagnostics",
                DisplayNameOverride = scenario == "unexpected" ? () => fail ? throw new InvalidOperationException("Unexpected diagnosis error.") : "Recovered" : null,
                SupportsOverride = () => scenario == "supports" && fail ? throw new InvalidOperationException("Supports failed.") : true,
                DiagnosticsOverride = () => scenario == "provider" && fail ? throw new InvalidOperationException("Provider failed.") : []
            };
            using var fixture = new Fixture(Path.Combine(root, scenario), adapter);
            ModuleCompatibilityReport? report = null;
            try
            {
                await fixture.Vm.ShowModuleCompatibilityReportAsync(value => report = value);
                Check(scenario != "unexpected" && report!.Items.Any(item => item.Status == GameCompatibilityDiagnosticStatus.Failed),
                    "Module diagnostic exception was not isolated as a failure item: " + scenario);
            }
            catch (InvalidOperationException error) when (scenario == "unexpected" && error.Message == "Unexpected diagnosis error.")
            { Check(report is null, "Unexpected current failure was hidden by a report."); }
            Check(fixture.Vm.CanViewModuleCompatibilityDiagnostics, "Failure left diagnosis busy: " + scenario);
            fail = false; report = null;
            await fixture.Vm.ShowModuleCompatibilityReportAsync(value => report = value);
            Check(report is not null, "Diagnosis could not run again after failure: " + scenario);
        }
        using var absent = new Fixture(Path.Combine(root, "absent"), null);
        await absent.Vm.ShowModuleCompatibilityReportAsync(report =>
            Check(report.Items.Any(item => item.DisplayName == "本地安装" && item.Status == GameCompatibilityDiagnosticStatus.Information),
                "Missing optional module prevented basic diagnosis."));
    }

    private static async Task CheckRejectedCloseAsync(string root)
    {
        var entered = Signal(); var release = Signal();
        using var fixture = new Fixture(root, new SmokeTestModuleAdapter
        {
            IdentityOnly = true, IdentityId = "game.diagnostics",
            SupportsOverride = () => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); return true; }
        });
        var presented = false;
        var task = fixture.Vm.ShowModuleCompatibilityReportAsync(_ => presented = true);
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Set(fixture.Vm, "_speedOperationsInFlight", 1);
            try { fixture.Vm.Shutdown(); throw new Exception("Shutdown preparation unexpectedly succeeded."); }
            catch (InvalidOperationException) { Check(!fixture.Vm.IsShutdownCommitted, "Rejected close committed shutdown."); }
        }
        finally { Set(fixture.Vm, "_speedOperationsInFlight", 0); release.TrySetResult(); await task.WaitAsync(Deadline); }
        Check(presented && fixture.Vm.CanViewModuleCompatibilityDiagnostics, "Rejected close canceled usable diagnosis.");
    }

    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);

    private sealed class Fixture : IDisposable
    {
        internal GameAdapterRegistry Registry { get; }
        internal MainViewModel Vm { get; }
        internal List<IGameAdapter> Adapters => Get<List<IGameAdapter>>(Registry, "_adapters");
        internal GameProfile A { get; }
        internal GameProfile B { get; }
        internal Fixture(string root, IGameAdapter? adapter)
        {
            var modules = Path.Combine(root, "modules");
            Registry = new(modules);
            Vm = ModuleLifecycleRegressionTests.CreateViewModel(root, new GameModuleCatalogService(modules), Registry);
            A = new() { Name = "A", ModuleId = "game.diagnostics", Versions = [new(), new()] };
            B = new() { Name = "B", Versions = [new()] };
            Vm.Games.Add(A); Vm.Games.Add(B);
            if (adapter is not null) Adapters.Add(adapter);
            var process = SelfProcess("fixture");
            var session = new GameConnectionSession(new()
            { SeedProcess = process, RootProcess = process, DataProcess = process, Members = [process], RuntimeKind = GameRuntimeKind.Native }, A.Id)
            { Adapter = null, VersionId = A.Versions[0].Id, Fingerprint = new("", "", "", "fixture", 0, "x64", "fixture", "", "") };
            // No live module pages, reads, writes, saves or hooks are created by this fixture.
            Get<Dictionary<Guid, GameConnectionSession>>(Vm, "_sessions").Add(A.Id, session);
            A.IsConnected = true; Vm.SelectedGame = A; Vm.SelectedVersion = A.Versions[0];
        }
        internal static ProcessItem SelfProcess(string name)
        {
            using var process = Process.GetCurrentProcess();
            return new() { ProcessId = process.Id, StartTimeUtc = process.StartTime.ToUniversalTime(), ProcessName = name };
        }
        internal void SetProcess(ProcessItem? process) => typeof(MainViewModel).GetProperty(nameof(MainViewModel.AttachedProcess))!.SetValue(Vm, process);
        public void Dispose() { Vm.Shutdown(); Registry.Dispose(); }
    }
}
