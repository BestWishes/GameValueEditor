using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using GameValueEditor.Models;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Services;
using GameValueEditor.ViewModels;

internal static class RefreshBoundaryRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _assertions;
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    private static void Check(bool value, string message) { Interlocked.Increment(ref _assertions); if (!value) throw new InvalidOperationException(message); }
    private static void Set(MainViewModel vm, string name, object? value) => typeof(MainViewModel).GetField(name, Private)!.SetValue(vm, value);
    private static object? Invoke(MainViewModel vm, string name, params object?[] args)
    {
        try { return typeof(MainViewModel).GetMethod(name, Private)!.Invoke(vm, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static async Task ExpectAsync<T>(Task task) where T : Exception
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (T) { _assertions++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    internal static async Task RunAsync(string root, string[]? args)
    {
        _assertions = 0;
        var area = args?.SingleOrDefault(a => a.StartsWith("--refresh-area=", StringComparison.Ordinal))?["--refresh-area=".Length..];
        if (area is null or "unlock") await CheckUnlockAsync(root);
        if (area is null or "legacy") await CheckLegacyStopAsync(root);
        if (area is null or "legacy") await CheckLegacyChangedAsync(root);
        if (area is null or "snapshots") await CheckSnapshotsAsync(root);
        if (area is null or "snapshots") await CheckRevisionAsync();
        Console.WriteLine($"Refresh/unlock boundary regressions passed: {_assertions} assertions.");
    }
    private static async Task CheckUnlockAsync(string root)
    {
        foreach (var scenario in new[] { "stopped", "other-adapter", "offline", "unavailable-native", "save-error" })
        {
            using var f = new FieldOrderRegressionTests.Fixture(Path.Combine(root, "unlock-boundary-" + scenario));
            var alias = new SavedField { LocatorKind = "GameAdapter", AdapterId = f.Adapter.Id,
                AdapterFieldKey = f.Field.AdapterFieldKey, CurrentValue = "10", IsValueLocked = true, LockedValue = "10" };
            f.Field.IsValueLocked = true; f.Field.LockedValue = "10"; f.Version.Fields.Add(alias);
            if (scenario == "stopped") Invoke(f.Vm, "DeactivateModuleUntilRestart", f.Adapter.Id);
            if (scenario == "other-adapter") Set(f.Vm, "_activeAdapter", new SmokeTestModuleAdapter { IdentityOnly = true, IdentityId = "game.other" });
            if (scenario == "offline") typeof(MainViewModel).GetProperty(nameof(MainViewModel.AttachedProcess))!.SetValue(f.Vm, null);
            if (scenario == "unavailable-native")
            {
                f.Field.LocatorKind = alias.LocatorKind = "Absolute";
                f.Field.LastAddress = alias.LastAddress = 1234;
                f.Field.ProcessStartTimeUtcTicks = alias.ProcessStartTimeUtcTicks = f.Inner.Process.StartTimeUtc.Ticks;
                f.Vm.MemoryWriteAccessFactory = _ => throw new IOException("process unavailable");
            }
            if (scenario == "save-error") Directory.CreateDirectory(f.Vm.LibraryPath);
            var unlocking = f.Vm.ToggleSelectedFieldValueLockAsync();
            if (scenario == "save-error") await ExpectAsync<UnauthorizedAccessException>(unlocking);
            else await unlocking.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!f.Field.IsValueLocked && !alias.IsValueLocked && f.Field.LockedValue == "" && alias.LockedValue == "",
                "Unlock required a live adapter/process or left equivalent aliases locked: " + scenario);
            Check(f.Writes == 0, "Unlock performed a game write: " + scenario);
            if (scenario == "save-error")
            {
                Check(!f.Vm.StatusText.Contains("已解除", StringComparison.Ordinal), "Failed unlock persistence reported success.");
                Directory.Delete(f.Vm.LibraryPath, false);
            }
            else
            {
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(f.Vm.LibraryPath));
                Check(json.RootElement.GetProperty("Games")[0].GetProperty("Versions")[0].GetProperty("Fields")
                    .EnumerateArray().All(field => !field.GetProperty("IsValueLocked").GetBoolean()), "Unlock intent was not persisted.");
            }
        }
    }
    private static IGameAdapter Adapter(string kind) => kind switch
    {
        "inventory" => DispatchProxy.Create<IInventoryGameAdapter, LegacyReadProxy>(),
        "characters" => DispatchProxy.Create<ICharacterAttributesGameAdapter, LegacyReadProxy>(),
        _ => DispatchProxy.Create<IEntityEditorsGameAdapter, LegacyReadProxy>()
    };
    private static async Task CheckLegacyStopAsync(string root)
    {
        foreach (var kind in new[] { "inventory", "characters", "entity" })
        foreach (var replace in new[] { false, true })
        foreach (var fail in new[] { false, true })
        {
            using var f = new SafetyBoundaryRegressionTests.WriteFixture(Path.Combine(root, $"legacy-stop-{kind}-{replace}-{fail}"));
            var adapter = Adapter(kind); var proxy = (LegacyReadProxy)(object)adapter;
            var entered = Gate(); var release = Gate();
            proxy.BeforeRead = () => { entered.TrySetResult(); Wait(release.Task); if (fail) throw new IOException("old read error"); };
            Set(f.ViewModel, "_activeAdapter", adapter);
            var editor = new AdapterEntityEditorState(adapter.Editors.Single(), new("review.legacy", GameEditorPageRole.Entity), true);
            var reading = kind switch
            {
                "inventory" => f.ViewModel.RefreshAdapterInventoryAsync(),
                "characters" => f.ViewModel.RefreshAdapterCharactersAsync(),
                _ => f.ViewModel.RefreshEntityEditorAsync(editor)
            };
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                if (replace) Set(f.ViewModel, "_activeAdapter", Adapter(kind));
                else Invoke(f.ViewModel, "DeactivateModuleUntilRestart", adapter.Id);
                f.ViewModel.ReportModulePageStatus("current module status");
            }
            finally { release.TrySetResult(); }
            await ExpectAsync<OperationCanceledException>(reading);
            Check(f.ViewModel.AdapterInventoryItems.Count == 0 && f.ViewModel.AdapterCharacters.Count == 0 && editor.Entities.Count == 0,
                "Inactive/replaced legacy module repopulated a page: " + kind);
            Check(f.ViewModel.StatusText == "current module status", "Old legacy value/error/completion replaced current status: " + kind);
        }
    }
    private static async Task CheckLegacyChangedAsync(string root)
    {
        foreach (var kind in new[] { "inventory", "characters", "entity" })
        foreach (var fail in new[] { false, true })
        {
            using var f = new SafetyBoundaryRegressionTests.WriteFixture(Path.Combine(root, $"legacy-changed-{kind}-{fail}"));
            var adapter = Adapter(kind); var proxy = (LegacyReadProxy)(object)adapter;
            Set(f.ViewModel, "_activeAdapter", adapter);
            var field = new SavedField { Name = "count", LocatorKind = "GameAdapter", AdapterId = adapter.Id,
                AdapterFieldKey = "key", CurrentValue = "10" };
            f.Version.Fields.Add(field); f.ViewModel.SelectedSavedField = field;
            var editor = new AdapterEntityEditorState(adapter.Editors.Single(), new("review.legacy", GameEditorPageRole.Entity), true);
            var entered = Gate(); var release = Gate();
            proxy.BeforeRead = () => { entered.TrySetResult(); Wait(release.Task); if (fail) throw new IOException("old snapshot error"); };
            var reading = kind switch { "inventory" => f.ViewModel.RefreshAdapterInventoryAsync(),
                "characters" => f.ViewModel.RefreshAdapterCharactersAsync(), _ => f.ViewModel.RefreshEntityEditorAsync(editor) };
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try { await f.ViewModel.WriteSelectedFieldAsync("20"); }
            finally { release.TrySetResult(); }
            await ExpectAsync<GameEditorSnapshotChangedException>(reading);
            Check(field.CurrentValue == "20" && f.ViewModel.AdapterInventoryItems.Count == 0 &&
                f.ViewModel.AdapterCharacters.Count == 0 && editor.Entities.Count == 0, "Legacy bulk snapshot replaced a newer field write.");
            Check(f.ViewModel.StatusText.Contains("重新刷新", StringComparison.Ordinal) && !f.ViewModel.StatusText.Contains("old snapshot error", StringComparison.Ordinal),
                "Obsolete legacy read error was displayed as a game error.");
            proxy.BeforeRead = null;
            await (kind switch { "inventory" => f.ViewModel.RefreshAdapterInventoryAsync(),
                "characters" => f.ViewModel.RefreshAdapterCharactersAsync(), _ => f.ViewModel.RefreshEntityEditorAsync(editor) });
            Check(f.ViewModel.StatusText.Contains("已读取", StringComparison.Ordinal), "Explicit legacy refresh did not recover after invalidation.");
        }
    }
    private static async Task CheckRevisionAsync()
    {
        var hub = new FieldOperationCoordinator(); var scope = hub.For(1, 2, "build");
        var baseline = scope.CaptureModuleSnapshot("game.one");
        using var read = scope.Module("game.one", "key").EnterRead();
        Check(baseline.IsCurrent, "Read-only field turn invalidated a module snapshot.");
        using var other = scope.Module("game.other", "key").EnterWrite();
        Check(baseline.IsCurrent, "Another module invalidated this snapshot.");
        using var isolated = hub.For(1, 3, "build").Module("game.one", "key").EnterWrite();
        Check(baseline.IsCurrent, "Another process instance invalidated this snapshot.");
        using var queued = scope.Module("game.one", "key").EnterWrite();
        Check(!baseline.IsCurrent && !queued.Ready.IsCompleted, "Queued semantic write did not invalidate a bulk snapshot.");
        var busy = scope.CaptureModuleSnapshot("game.one");
        queued.Dispose();
        Check(!scope.CaptureModuleSnapshot("game.one").IsCurrent, "Cancelling a queued write bypassed its active predecessor.");
        read.Dispose();
        using var after = scope.Module("game.one", "key").EnterRead();
        await after.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!scope.CaptureModuleSnapshot("game.one").IsCurrent && DateTime.UtcNow < timeout) await Task.Delay(10);
        Check(scope.CaptureModuleSnapshot("game.one").IsCurrent && !busy.IsCurrent && !baseline.IsCurrent,
            "Cancelled write leaked the busy count or revived an old snapshot.");
        after.Dispose();
        var target = scope.Module("game.one", "key");
        Check(target.TryEnterMaintenance(out var maintenance), "Maintenance failed to reserve an idle target.");
        using var turn = maintenance!;
        var beforeMaintenance = scope.CaptureModuleSnapshot("game.one");
        Check(beforeMaintenance.IsCurrent, "Read-only maintenance invalidated a snapshot.");
        using (target.BeginMutation())
            Check(!beforeMaintenance.IsCurrent && !scope.CaptureModuleSnapshot("game.one").IsCurrent, "Actual maintenance write did not invalidate/busy a snapshot.");
        var fresh = scope.CaptureModuleSnapshot("game.one"); var applied = 0;
        Check(fresh.TryApply(() => applied++) && applied == 1 && !beforeMaintenance.TryApply(() => applied++),
            "Snapshot application did not reject an obsolete revision.");
    }
    private static async Task CheckSnapshotsAsync(string root)
    {
        foreach (var scenario in new[] { "normal", "real-error", "saved-write", "page-write", "other-field", "late-error", "stopped", "stopped-error", "pending-write", "write-error", "maintenance", "maintenance-read" })
        {
            using var f = new SafetyBoundaryRegressionTests.WriteFixture(Path.Combine(root, "snapshot-" + scenario));
            var adapter = DispatchProxy.Create<ICoordinatedGameEditorPageProvider, FieldOrderRegressionTests.PageFactoryProxy>();
            var proxy = (FieldOrderRegressionTests.PageFactoryProxy)(object)adapter;
            const string key = "review.page|item|count";
            var field = new SavedField { Name = "count", LocatorKind = "GameAdapter", AdapterId = adapter.Id, AdapterFieldKey = key, CurrentValue = "10" };
            f.Version.Fields.Add(field); f.ViewModel.SelectedSavedField = field; Set(f.ViewModel, "_activeAdapter", adapter);
            var host = new TestHost(); f.ViewModel.SetEditorHostServices(host); Invoke(f.ViewModel, "RebuildEditorPages");
            var context = proxy.Contexts.Single(); var snapshots = (IGameEditorSnapshotOperations)context.Host;
            var writes = (IGameEditorFieldOperations)context.Host;
            var entered = Gate(); var release = Gate(); var wrote = Gate(); var uiThread = Environment.CurrentManagedThreadId;
            var value = "10"; var applied = 0; var reads = 0;
            proxy.Read = target => new(target, value, "read");
            proxy.Write = (target, updated) =>
            {
                if (scenario == "pending-write") { entered.TrySetResult(); Wait(release.Task); }
                if (scenario == "write-error") throw new IOException("write failure");
                value = updated; wrote.TrySetResult(); return new(target, value, "write");
            };
            if (scenario == "pending-write")
            {
                var writing = writes.WriteFieldAsync(key, "20");
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                try { await ExpectAsync<GameEditorSnapshotChangedException>(snapshots.ReadSnapshotAsync(() => ++reads, _ => applied++)); }
                finally { release.TrySetResult(); await writing; }
                Check(reads == 0 && applied == 0, "Snapshot read started while a module write was pending.");
                await snapshots.ReadSnapshotAsync(() => value, result => { Check(result == "20", "Fresh snapshot did not see completed write."); applied++; });
                Check(applied == 1, "Busy snapshot left a permanent read blocker.");
                continue;
            }
            if (scenario == "maintenance-read") value = "20";
            var reading = snapshots.ReadSnapshotAsync(() =>
            {
                Check(Environment.CurrentManagedThreadId != uiThread, "Snapshot game read ran on the page Dispatcher.");
                Interlocked.Increment(ref reads); var old = value; entered.TrySetResult(); Wait(release.Task);
                if (scenario is "late-error" or "stopped-error" or "real-error") throw new IOException("old read error");
                return old;
            }, result => { Check(Dispatcher.CurrentDispatcher.CheckAccess() && Environment.CurrentManagedThreadId == uiThread,
                "Snapshot applied off the page Dispatcher."); applied++; host.ReportStatus(result); });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                if (scenario is "stopped" or "stopped-error")
                { Invoke(f.ViewModel, "DeactivateModuleUntilRestart", adapter.Id); host.ReportStatus("stopped"); }
                else if (scenario is "maintenance" or "maintenance-read")
                {
                    field.IsValueLocked = true; field.LockedValue = "20";
                    if (scenario == "maintenance-read") field.PropertyChanged += (_, e) =>
                    { if (e.PropertyName == nameof(SavedField.Status) && field.Status.StartsWith("锁定中", StringComparison.Ordinal)) wrote.TrySetResult(); };
                    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var maintaining = Task.Run(() => (Task)Invoke(f.ViewModel, "MaintainLockedValuesAsync", f.Process, f.Version, adapter, cancellation.Token)!);
                    try { await wrote.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                    finally { cancellation.Cancel(); await maintaining; }
                }
                else if (scenario is not ("normal" or "real-error"))
                {
                    if (scenario == "other-field")
                    { var otherField = new SavedField { LocatorKind = "GameAdapter", AdapterId = adapter.Id, AdapterFieldKey = "other", CurrentValue = "1" };
                        f.Version.Fields.Add(otherField); f.ViewModel.SelectedSavedField = otherField; }
                    var writing = scenario == "page-write" ? writes.WriteFieldAsync(key, "20") : f.ViewModel.WriteSelectedFieldAsync("20");
                    if (scenario == "write-error") await ExpectAsync<IOException>(writing);
                    else await writing;
                }
            }
            finally { release.TrySetResult(); }
            if (scenario is "normal" or "maintenance-read")
            { await reading; Check(applied == 1, "Unchanged snapshot was wrongly discarded."); }
            else if (scenario == "real-error")
            { await ExpectAsync<IOException>(reading); Check(applied == 0, "Current snapshot error applied data or was incorrectly suppressed."); }
            else if (scenario is "stopped" or "stopped-error")
            { await ExpectAsync<OperationCanceledException>(reading); Check(applied == 0 && host.LastStatus == "stopped", "Expired page result/error replaced stop feedback."); }
            else
            { await ExpectAsync<GameEditorSnapshotChangedException>(reading); Check(applied == 0, "Snapshot overwrote a newer coordinated write."); }
            Check(reads == 1, "Obsolete/busy read was automatically retried.");
            if (scenario is not ("stopped" or "stopped-error"))
            { await snapshots.ReadSnapshotAsync(() => value, result => applied++); Check(applied >= 1, "Explicit new snapshot could not recover."); }
        }
    }
    private sealed class TestHost : IGameEditorHostServices
    {
        internal string LastStatus = "";
        public Task<string?> PromptValueAsync(GameEditorTextPrompt prompt) => Task.FromResult<string?>(null);
        public Task SaveFieldAsync(GameEditorSavedFieldRequest request) => Task.CompletedTask;
        public void ReportStatus(string message) => LastStatus = message;
        public void ShowError(string title, string message) => throw new InvalidOperationException(message);
    }
    public class LegacyReadProxy : DispatchProxy
    {
        internal Action? BeforeRead;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name is "ReadInventory" or "ReadCharacters" or "ReadEditorEntities") BeforeRead?.Invoke();
            return method.Name switch
            {
                "get_Id" => "game.legacy-review", "get_DisplayName" => "test", "get_Description" => "test",
                "get_LegacyIds" => Array.Empty<string>(), "get_Editors" => new[] { new GameEditorDescriptor("review.legacy", "test", GameEditorKind.Collection, 1, "test") },
                "Supports" or "SupportsCharacterAttributes" or "SupportsEntityEditor" => true,
                "ReadField" => new AdapterFieldValue((string)args![1]!, "10", "read"),
                "WriteField" => new AdapterFieldValue((string)args![1]!, (string)args[2]!, "write"),
                "ReadInventory" => new[] { new AdapterInventoryItem("key", "old item", 42) },
                "ReadCharacters" => new[] { new AdapterCharacterItem("item", "old character", 1, [new("count", "count", 42, 42, 0)]) },
                "ReadEditorEntities" => new[] { new AdapterEditorEntity("item", "old entity", "test", [new("count", "count", 42, 0, 100)]) },
                _ => throw new InvalidOperationException("Unexpected probe call: " + method.Name)
            };
        }
    }
}
