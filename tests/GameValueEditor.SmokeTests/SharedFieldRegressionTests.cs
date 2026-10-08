using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using GameValueEditor.Models;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Services;
using GameValueEditor.Services.Adapters;
using GameValueEditor.ViewModels;

internal static class SharedFieldRegressionTests
{
    private static int _assertions;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool value, string message) { _assertions++; if (!value) throw new InvalidOperationException(message); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    private static object? Invoke(MainViewModel vm, string name, params object?[] args)
    {
        try { return typeof(MainViewModel).GetMethod(name, Private)!.Invoke(vm, args); }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
    }
    internal static async Task RunAsync(string root, string[]? args = null)
    {
        _assertions = 0;
        var area = args?.SingleOrDefault(a => a.StartsWith("--shared-area=", StringComparison.Ordinal))?["--shared-area=".Length..];
        if (area is null or "refresh") await RefreshBoundaryRegressionTests.RunAsync(root, args);
        if (area is null or "aliases") await CheckAliasesAsync(root);
        if (area is null or "stop") await CheckStoppedRefreshAsync(root);
        if (area is null or "queues") await CheckTargetsAsync();
        if (area is null or "locks") await CheckConflictsAsync(root);
        if (area is null or "pages") { await CheckPageWritesAsync(root); await CheckPageFirstAsync(root); await CheckOldPageLocksAsync(root); CheckApi8Contract(); }
        if (area is null or "native") await CheckNativeAliasesAsync(root);
        Console.WriteLine($"Shared actual-field regressions passed: {_assertions} assertions.");
    }

    private static async Task CheckTargetsAsync()
    {
        var hub = new FieldOperationCoordinator();
        var scope = hub.For(1, 2, "build");
        Check(ReferenceEquals(scope, hub.For(1, 2, "BUILD")), "Same runtime scope was not reused.");
        Check(!ReferenceEquals(scope, hub.For(2, 2, "build")) && !ReferenceEquals(scope, hub.For(1, 3, "build")) &&
            !ReferenceEquals(scope, hub.For(1, 2, "other")), "Unrelated process/build shared a queue.");
        using var read = scope.Module("game.test", "editor|it%65m|count").EnterRead();
        using var write = scope.Module("game.test", "editor|item|%63ount").EnterWrite();
        Check(!read.IsCurrent && !write.Ready.IsCompleted, "Equivalent escaped keys used separate queues.");
        using var other = scope.Module("game.test", "editor|Item|count").EnterWrite();
        Check(other.Ready.IsCompleted, "Case-sensitive semantic identities were conflated.");
        using var module = scope.Module("game.other", "editor|item|count").EnterWrite();
        Check(module.Ready.IsCompleted, "Different modules were globally serialized.");
        read.Dispose(); await write.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        using var wide = scope.Native(100, 8).EnterRead();
        using var overlap = scope.Native(104, 4).EnterWrite();
        using var disjoint = scope.Native(108, 4).EnterWrite();
        Check(!wide.IsCurrent && !overlap.Ready.IsCompleted && disjoint.Ready.IsCompleted, "Native partial overlaps were not isolated by byte range.");
        Check(!scope.Native(102, 4).TryEnterMaintenance(out _), "Maintenance acquired an overlapping busy native range.");
        overlap.Dispose();
        using var next = scope.Native(102, 8).EnterWrite();
        Check(!next.Ready.IsCompleted, "Cancelled overlapping reservation bypassed its predecessor.");
        wide.Dispose(); disjoint.Dispose(); await next.Ready.WaitAsync(TimeSpan.FromSeconds(5)); next.Dispose();
        Check(scope.Native(100, 8).TryEnterMaintenance(out var final), "Overlapping queue leaked reservations after cancellation.");
        final!.Dispose();
        for (var i = 0; i < 20; i++)
        {
            using var left = scope.Native(100, 8).EnterWrite();
            using var right = scope.Native(104, 8).EnterWrite();
            Check(left.Ready.IsCompleted && !right.Ready.IsCompleted, "Atomic native reservation order changed.");
            left.Dispose(); await right.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static Task Maintain(SafetyBoundaryRegressionTests.WriteFixture f, IGameAdapter adapter, CancellationToken token) =>
        Task.Run(() => (Task)Invoke(f.ViewModel, "MaintainLockedValuesAsync", f.Process, f.Version, adapter, token)!);

    private static async Task CheckConflictsAsync(string root)
    {
        using var f = new FieldOrderRegressionTests.Fixture(Path.Combine(root, "conflicts"));
        var alias = await f.Vm.AddAdapterFieldAsync(f.Field.AdapterFieldKey, "alias", "group");
        f.Field.IsValueLocked = alias.IsValueLocked = true; f.Field.LockedValue = "10"; alias.LockedValue = "20";
        var paused = Gate();
        alias.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SavedField.Status) && alias.Status.Contains("冲突", StringComparison.Ordinal)) paused.TrySetResult(); };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var maintenance = Maintain(f.Inner, f.Adapter, cancellation.Token);
        try { await paused.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check(f.Writes == 0, "Conflicting lock aliases still alternated writes."); }
        finally { cancellation.Cancel(); await maintenance; }
        Check(f.Field.IsValueLocked && alias.IsValueLocked, "Conflict detection silently erased saved lock intent.");
        f.Vm.SelectedSavedField = f.Field;
        await f.Vm.WriteSelectedFieldAsync("30");
        Check(f.Field.LockedValue == "30" && alias.LockedValue == "30", "An explicit manual write did not resolve alias conflict.");
        await f.Vm.ToggleSelectedFieldValueLockAsync();
        Check(!f.Field.IsValueLocked && !alias.IsValueLocked, "Unlock did not apply to aliases of one actual target.");
        await f.Vm.ToggleSelectedFieldValueLockAsync();
        Check(f.Field.IsValueLocked && alias.IsValueLocked && alias.LockedValue == "30", "Lock did not apply one target to all aliases.");
        var readEntered = Gate(); var readRelease = Gate(); var pausedAgain = Gate();
        f.Read = key => { readEntered.TrySetResult(); Wait(readRelease.Task); return new(key, "7", "late read"); };
        f.Field.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SavedField.Status) && f.Field.Status.Contains("冲突", StringComparison.Ordinal)) pausedAgain.TrySetResult(); };
        using var nextCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var nextMaintenance = Maintain(f.Inner, f.Adapter, nextCancellation.Token);
        await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var initialWrites = f.Writes;
        alias.LockedValue = "99";
        readRelease.TrySetResult();
        try { await pausedAgain.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check(f.Writes == initialWrites, "An alias target change during a lock read still wrote the old goal."); }
        finally { nextCancellation.Cancel(); await nextMaintenance; }
    }

    private static async Task CheckPageWritesAsync(string root)
    {
        foreach (var scenario in new[] { "normal", "unlock", "target", "expired", "expired-active", "expired-error", "error", "lock-first", "save-error", "history", "no-library" })
        {
            using var f = new SafetyBoundaryRegressionTests.WriteFixture(Path.Combine(root, "page-" + scenario));
            var adapter = DispatchProxy.Create<ICoordinatedGameEditorPageProvider, FieldOrderRegressionTests.PageFactoryProxy>();
            var proxy = (FieldOrderRegressionTests.PageFactoryProxy)(object)adapter;
            proxy.Aliases = ["legacy.page"];
            const string key = "review.page|item|count";
            var field = new SavedField { Name = "primary", LocatorKind = "GameAdapter", AdapterId = adapter.Id, AdapterFieldKey = key,
                CurrentValue = "10", IsValueLocked = true, LockedValue = "10" };
            var alias = new SavedField { Name = "alias", LocatorKind = "GameAdapter", AdapterId = "legacy.page", AdapterFieldKey = "review.page|it%65m|%63ount",
                CurrentValue = "10", IsValueLocked = true, LockedValue = "10" };
            f.Version.Fields.Add(field); f.Version.Fields.Add(alias); f.ViewModel.SelectedSavedField = field;
            typeof(MainViewModel).GetField("_activeAdapter", Private)!.SetValue(f.ViewModel, adapter);
            f.ViewModel.SetEditorHostServices(new TestHost()); Invoke(f.ViewModel, "RebuildEditorPages");
            var context = proxy.Contexts.Single(); var operations = (IGameEditorFieldOperations)context.Host;
            var entered = Gate(); var release = Gate(); var calls = new List<string>();
            proxy.Write = (target, value) =>
            {
                lock (calls) calls.Add(value);
                if (value == "11") { entered.TrySetResult(); Wait(release.Task); if (scenario == "error") throw new IOException("write failure"); }
                if (value == "20" && scenario is "expired-active" or "expired-error")
                { entered.TrySetResult(); Wait(release.Task); if (scenario == "expired-error") throw new IOException("late page failure"); }
                return new(target, value, "write confirmed");
            };
            Task first;
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (scenario is "expired-active" or "expired-error")
            {
                var activeWrite = operations.WriteFieldAsync(key, "20");
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                FieldOrderRegressionTests.InvalidatePages(f.ViewModel);
                field.CurrentValue = "new state"; field.Status = "new status";
                release.TrySetResult(); await ExpectAsync<OperationCanceledException>(activeWrite);
                Check(calls.SequenceEqual(["20"]) && field.CurrentValue == "new state" && field.Status == "new status" && field.LockedValue == "10",
                    "Expired active page write/error overwrote the new state or retried the write.");
                await ((IGameEditorFieldOperations)proxy.Contexts.Last().Host).WriteFieldAsync(key, "30");
                Check(field.CurrentValue == "30", "An expired active operation left the shared target stuck.");
                continue;
            }
            if (scenario == "lock-first")
            {
                proxy.Read = target => { entered.TrySetResult(); Wait(release.Task); return new(target, "7", "read"); };
                first = Maintain(f, adapter, cancel.Token);
            }
            else first = f.ViewModel.WriteSelectedFieldAsync("11");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var editing = operations.WriteFieldAsync(key, "20");
            try
            {
                // The scoped call executes on this Dispatcher before this synchronization point.
                await System.Windows.Threading.Dispatcher.Yield();
                Check(!editing.IsCompleted && calls.Count == (scenario == "lock-first" ? 0 : 1), "Page write bypassed the saved-field/maintenance queue.");
                if (scenario == "unlock") { alias.IsValueLocked = false; alias.LockedValue = ""; }
                if (scenario == "target") alias.LockedValue = "99";
                if (scenario == "expired") FieldOrderRegressionTests.InvalidatePages(f.ViewModel);
                if (scenario == "save-error") Directory.CreateDirectory(f.ViewModel.LibraryPath);
            }
            finally { release.TrySetResult(); }
            if (scenario == "lock-first") { await editing; cancel.Cancel(); await first; }
            else
            {
                if (scenario == "error") await ExpectAsync<IOException>(first);
                else if (scenario == "save-error") await ExpectAsync<UnauthorizedAccessException>(first);
                else await first;
                if (scenario == "expired") await ExpectAsync<OperationCanceledException>(editing);
                else if (scenario == "save-error") await ExpectAsync<UnauthorizedAccessException>(editing);
                else await editing;
            }
            if (scenario == "expired") { Check(calls.SequenceEqual(["11"]), "Expired queued page still wrote."); continue; }
            Check(field.CurrentValue == "20" && alias.CurrentValue == "20", "Page result did not synchronize saved aliases.");
            Check(field.LockedValue == "20", "Page result did not update the saved-field lock goal.");
            Check(scenario == "unlock" ? !alias.IsValueLocked && alias.LockedValue == "" : alias.LockedValue == "20",
                "Page result restored an explicit unlock or kept an obsolete captured lock goal.");
            if (scenario == "save-error")
            {
                Directory.Delete(f.ViewModel.LibraryPath, false);
                await operations.WriteFieldAsync(key, "30"); Check(field.LockedValue == "30", "Page save failure left its queue stuck.");
            }
            else
            {
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(f.ViewModel.LibraryPath));
                Check(json.RootElement.GetProperty("Games")[0].GetProperty("Versions")[0].GetProperty("Fields")[0].GetProperty("LockedValue").GetString() == "20", "Page lock goal was not persisted.");
            }
            if (scenario == "history")
            {
                var historical = new GameVersionProfile { ExecutableSha256 = "other-build" };
                var historyField = new SavedField { LocatorKind = "GameAdapter", AdapterId = adapter.Id, AdapterFieldKey = key, CurrentValue = "99" };
                historical.Fields.Add(historyField); f.ViewModel.SelectedGame!.Versions.Add(historical);
                f.ViewModel.SelectedVersion = historical;
                await operations.WriteFieldAsync(key, "40");
                Check(field.CurrentValue == "40" && historyField.CurrentValue == "99", "Module page wrote its alias result into a selected historical build.");
            }
            if (scenario == "no-library")
            {
                typeof(MainViewModel).GetField("_attachedGameId", Private)!.SetValue(f.ViewModel, null);
                await operations.WriteFieldAsync(key, "40");
                Check(field.CurrentValue == "20", "A module page without a bound game profile mutated unrelated saved aliases.");
            }
        }
    }

    private static async Task CheckPageFirstAsync(string root)
    {
        using var f = new SafetyBoundaryRegressionTests.WriteFixture(Path.Combine(root, "page-first"));
        var adapter = DispatchProxy.Create<ICoordinatedGameEditorPageProvider, FieldOrderRegressionTests.PageFactoryProxy>();
        var proxy = (FieldOrderRegressionTests.PageFactoryProxy)(object)adapter;
        const string key = "review.page|item|count";
        var primary = new SavedField { LocatorKind = "GameAdapter", AdapterId = adapter.Id, AdapterFieldKey = key,
            CurrentValue = "10", IsValueLocked = true, LockedValue = "10" };
        var other = new SavedField { LocatorKind = "GameAdapter", AdapterId = adapter.Id, AdapterFieldKey = "other",
            CurrentValue = "30", IsValueLocked = true, LockedValue = "30" };
        f.Version.Fields.Add(primary); f.Version.Fields.Add(other);
        typeof(MainViewModel).GetField("_activeAdapter", Private)!.SetValue(f.ViewModel, adapter);
        f.ViewModel.SetEditorHostServices(new TestHost()); Invoke(f.ViewModel, "RebuildEditorPages");
        var entered = Gate(); var release = Gate(); var otherDone = Gate(); var calls = new List<string>();
        proxy.Read = target => new(target, "7", "read");
        proxy.Write = (target, value) => { lock (calls) calls.Add(value); if (value == "20") { entered.TrySetResult(); Wait(release.Task); } return new(target, value, "write"); };
        other.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SavedField.Status)) otherDone.TrySetResult(); };
        var editing = ((IGameEditorFieldOperations)proxy.Contexts.Single().Host).WriteFieldAsync(key, "20");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var maintaining = Maintain(f, adapter, cancel.Token);
        try
        {
            await otherDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(calls.SequenceEqual(["20", "30"]), "Old target lock ran during a pending module-page write or blocked unrelated locks.");
        }
        finally { cancel.Cancel(); release.TrySetResult(); }
        await editing; await maintaining;
        Check(primary.CurrentValue == "20" && primary.LockedValue == "20", "Page-first write did not finish with its new saved-field lock goal.");
    }
    private static async Task ExpectAsync<T>(Task task) where T : Exception
    { try { await task.WaitAsync(TimeSpan.FromSeconds(5)); } catch (T) { _assertions++; return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private static async Task CheckOldPageLocksAsync(string root)
    {
        using var f = new SafetyBoundaryRegressionTests.WriteFixture(Path.Combine(root, "old-page"));
        var adapter = DispatchProxy.Create<IGameEditorPageFactoryProvider, FieldOrderRegressionTests.PageFactoryProxy>();
        var proxy = (FieldOrderRegressionTests.PageFactoryProxy)(object)adapter;
        var writes = 0; proxy.Write = (key, value) => { writes++; return new(key, value, "write"); };
        var field = new SavedField { LocatorKind = "GameAdapter", AdapterId = adapter.Id, AdapterFieldKey = "review.page|item|count",
            CurrentValue = "10", IsValueLocked = true, LockedValue = "10" };
        f.Version.Fields.Add(field); f.ViewModel.SelectedSavedField = field;
        typeof(MainViewModel).GetField("_activeAdapter", Private)!.SetValue(f.ViewModel, adapter);
        await Maintain(f, adapter, CancellationToken.None);
        Check(writes == 0 && field.IsValueLocked && field.Status.Contains("锁定已暂停", StringComparison.Ordinal), "Old own-page module still ran unsafe locks or lost user intent.");
        await f.ViewModel.ToggleSelectedFieldValueLockAsync();
        await ExpectAsync<InvalidOperationException>(f.ViewModel.ToggleSelectedFieldValueLockAsync());
        Check(!field.IsValueLocked, "Old page lock was silently enabled.");
        await f.ViewModel.WriteSelectedFieldAsync("20");
        Check(writes == 1 && field.CurrentValue == "20", "Old module was entirely disabled instead of only unsafe locks.");
    }

    private static void CheckApi8Contract()
    {
        var method = typeof(GameAdapterRegistry).GetMethod("ValidateAdapter", BindingFlags.Static | BindingFlags.NonPublic)!;
        var old = DispatchProxy.Create<ITestLegacyPages, FieldOrderRegressionTests.PageFactoryProxy>();
        var updated = DispatchProxy.Create<ITestCoordinatedPages, FieldOrderRegressionTests.PageFactoryProxy>();
        var manifest = new InstalledModuleManifest { Id = old.Id, DisplayName = old.DisplayName, HostApiVersion = 7,
            Editors = [new InstalledEditorManifest { Id = "review.page", DisplayName = "page", Order = 1 }] };
        method.Invoke(null, [old, manifest]); Check(true, "API 7 page compatibility failed.");
        manifest.HostApiVersion = 8;
        try { method.Invoke(null, [old, manifest]); throw new InvalidOperationException("API 8 allowed a noncoordinated page provider."); }
        catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException error && error.Message.Contains("协调", StringComparison.Ordinal)) { _assertions++; }
        method.Invoke(null, [updated, manifest]); Check(true, "API 8 coordinated contract failed.");
    }

    public interface ITestLegacyPages : IGameEditorPageFactoryProvider, IGameCompatibilityDiagnosticsProvider { }
    public interface ITestCoordinatedPages : ICoordinatedGameEditorPageProvider, IGameCompatibilityDiagnosticsProvider { }

    private static async Task CheckNativeAliasesAsync(string root)
    {
        using var f = new SafetyBoundaryRegressionTests.WriteFixture(Path.Combine(root, "native"));
        var pointer = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt64(pointer, 7);
            var address = unchecked((ulong)pointer.ToInt64());
            SavedField Field(string name, MemoryValueType type, double scale = 1) => new() { Name = name, LocatorKind = "SessionAddress",
                LastAddress = address, ValueType = type, ScaleMultiplier = scale, ProcessStartTimeUtcTicks = f.Process.StartTimeUtc.Ticks,
                CurrentValue = scale == 1 ? "7" : "3.5", IsValueLocked = true, LockedValue = scale == 1 ? "7" : "3.5" };
            var first = Field("native", MemoryValueType.Int32);
            var scaled = Field("scaled", MemoryValueType.Int32, 2);
            var wide = Field("wide", MemoryValueType.Int64);
            f.Version.Fields.Add(first); f.Version.Fields.Add(scaled); f.Version.Fields.Add(wide);
            f.ViewModel.SelectedSavedField = first;
            await f.ViewModel.WriteSelectedFieldAsync("20");
            Check(Marshal.ReadInt32(pointer) == 20 && first.CurrentValue == "20" && scaled.CurrentValue == "10", "Native aliases did not format the same bytes with their own scales.");
            Check(first.LockedValue == "20" && scaled.LockedValue == "10", "Native alias goals diverged by display scale.");
            Check(!wide.IsValueLocked && wide.Status.Contains("暂停", StringComparison.Ordinal), "Partially overlapping wider lock was not paused after manual write.");
            await f.ViewModel.ToggleSelectedFieldValueLockAsync();
            Check(!first.IsValueLocked && !scaled.IsValueLocked, "Native alias unlock did not follow physical identity.");
            await f.ViewModel.ToggleSelectedFieldValueLockAsync();
            Check(first.IsValueLocked && scaled.IsValueLocked && scaled.LockedValue == "10", "Native alias lock did not use encoded bytes.");
            scaled.LockedValue = "99";
            Marshal.WriteInt32(pointer, 0);
            var paused = Gate();
            first.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SavedField.Status) && first.Status.Contains("冲突", StringComparison.Ordinal)) paused.TrySetResult(); };
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var maintenance = Maintain(f, new SmokeTestModuleAdapter { IdentityOnly = true }, cancellation.Token);
            try { await paused.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check(Marshal.ReadInt32(pointer) == 0, "Conflicting encoded native lock aliases still wrote memory."); }
            finally { cancellation.Cancel(); await maintenance; }
            var candidates = f.SetCandidates([address]);
            await f.ViewModel.WriteCandidatesAsync(candidates, "30");
            Check(Marshal.ReadInt32(pointer) == 30 && first.LockedValue == "30" && scaled.LockedValue == "15", "Scan candidate write bypassed saved native alias targets.");
        }
        finally { f.ViewModel.Shutdown(); Marshal.FreeHGlobal(pointer); }
    }

    private sealed class TestHost : IGameEditorHostServices
    {
        public Task<string?> PromptValueAsync(GameEditorTextPrompt prompt) => Task.FromResult<string?>("20");
        public Task SaveFieldAsync(GameEditorSavedFieldRequest request) => Task.CompletedTask;
        public void ReportStatus(string message) { }
        public void ShowError(string title, string message) { }
    }
    private static async Task CheckAliasesAsync(string root)
    {
        using var f = new FieldOrderRegressionTests.Fixture(Path.Combine(root, "aliases"));
        var alias = await f.Vm.AddAdapterFieldAsync(f.Field.AdapterFieldKey, "alias", "group");
        Check(f.Version.Fields.Count == 2 && alias != f.Field, "Aliases must preserve user entries.");
        f.Field.IsValueLocked = alias.IsValueLocked = true;
        f.Field.LockedValue = alias.LockedValue = "1";
        var entered = Gate(); var release = Gate();
        f.Write = (key, value) => { if (value == "10") { entered.TrySetResult(); Wait(release.Task); } return new(key, f.Value = value, "write"); };
        f.Vm.SelectedSavedField = f.Field;
        var first = f.Vm.WriteSelectedFieldAsync("10");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Vm.SelectedSavedField = alias;
        var second = f.Vm.WriteSelectedFieldAsync("20");
        try { Check(f.Writes == 1 && !second.IsCompleted, "Two aliases of one actual field wrote concurrently."); }
        finally { release.TrySetResult(); await first; await second; }
        Check(f.Value == "20" && f.Field.CurrentValue == "20" && alias.CurrentValue == "20", "Alias displays diverged after the last write.");
        Check(f.Field.LockedValue == "20" && alias.LockedValue == "20", "Alias lock targets diverged after the last write.");
    }
    private static async Task CheckStoppedRefreshAsync(string root)
    {
        foreach (var fail in new[] { false, true })
        {
            using var f = new FieldOrderRegressionTests.Fixture(Path.Combine(root, "stop-" + fail));
            var entered = Gate(); var release = Gate();
            f.Read = key => { entered.TrySetResult(); Wait(release.Task); if (fail) throw new IOException("late failure"); return new(key, "42", "late success"); };
            var refreshing = f.Vm.RefreshSavedValuesAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                Invoke(f.Vm, "DeactivateModuleUntilRestart", f.Adapter.Id);
                Check(!f.Vm.HasActiveAdapter, "Module stop did not clear the adapter.");
                f.Field.CurrentValue = "—"; f.Field.Status = "module unavailable";
                f.Vm.ReportModulePageStatus("module stopped");
            }
            finally { release.TrySetResult(); }
            await refreshing;
            Check(f.Field.CurrentValue == "—" && f.Field.Status == "module unavailable", "Stopped adapter's late value/error replaced unavailable status.");
            Check(f.Vm.StatusText == "module stopped", "Stopped adapter's old refresh replaced the current feedback.");
        }
    }
}
