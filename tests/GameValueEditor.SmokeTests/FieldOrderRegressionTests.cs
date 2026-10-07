using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GameValueEditor.Models;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Services;
using GameValueEditor.Services.Adapters;
using GameValueEditor.ViewModels;

internal static class FieldOrderRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _assertions;
    internal static async Task RunAsync(string root, string[]? args = null)
    {
        _assertions = 0;
        var area = args?.SingleOrDefault(arg => arg.StartsWith("--field-area=", StringComparison.Ordinal))?["--field-area=".Length..];
        if (area is not (null or "fields" or "pages" or "identity")) throw new ArgumentException("Unknown field test area.");
        if (area is null or "fields") { await CheckQueueAsync(); await CheckRefreshOrderAsync(root); await CheckWriteOrderAsync(root); await CheckSaveCompletionAsync(root); await CheckTargetChangeAsync(root); await CheckLockCoordinationAsync(root); }
        if (area is null or "pages") { await CheckPageLifetimeAsync(root); await CheckPageDispatchAsync(); await CheckSaveLifetimeAsync(root); }
        if (area is null or "identity") await CheckRefreshIdentityAsync(root);
        Console.WriteLine($"Field order/page lifetime regressions passed: {_assertions} assertions.");
    }
    private static void Check(bool value, string message) { _assertions++; if (!value) throw new InvalidOperationException(message); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    private static async Task ExpectAsync<T>(Task task) where T : Exception
    { try { await task.WaitAsync(TimeSpan.FromSeconds(5)); } catch (T) { _assertions++; return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static object? Invoke(MainViewModel vm, string name, params object?[] args)
    {
        try { return typeof(MainViewModel).GetMethod(name, Private)!.Invoke(vm, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private static async Task CheckQueueAsync()
    {
        var queue = new SavedFieldOperationQueue();
        using var read = queue.EnterRead();
        Check(read.Ready.IsCompleted && read.IsCurrent, "First read did not acquire its field.");
        using var first = queue.EnterWrite();
        using var second = queue.EnterWrite();
        using var later = queue.EnterRead();
        Check(!read.IsCurrent && !first.Ready.IsCompleted && !second.Ready.IsCompleted && !later.Ready.IsCompleted,
            "New writes did not invalidate the old read or preserve FIFO.");
        Check(!queue.TryEnterMaintenance(out _), "Maintenance entered a field with pending writes.");
        first.Dispose(); // Cancel a queued node: it must not let the next writer bypass the active read.
        Check(!second.Ready.IsCompleted, "Disposing a queued writer bypassed the active operation.");
        read.Dispose(); await second.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!later.Ready.IsCompleted && second.IsCurrent, "Queued read bypassed the second write.");
        second.Dispose(); await later.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        Check(later.IsCurrent, "A read after both writes was incorrectly stale.");
        queue.Invalidate(); Check(!later.IsCurrent, "Explicit target change did not invalidate an old read.");
        later.Dispose(); Check(queue.TryEnterMaintenance(out var maintenance), "Queue stayed busy after its last operation.");
        using var held = maintenance!;
        Check(held.IsCurrent && !queue.TryEnterMaintenance(out _), "Two maintenance operations acquired the same field.");
        using var final = queue.EnterWrite();
        Check(!held.IsCurrent && !final.Ready.IsCompleted, "Manual write did not supersede existing maintenance.");
        held.Dispose(); await final.Ready.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task CheckRefreshOrderAsync(string root)
    {
        foreach (var scenario in new[] { "normal", "late-error", "deleted", "different-field" })
        {
            using var fixture = new Fixture(Path.Combine(root, "refresh-" + scenario));
            var entered = Gate(); var release = Gate();
            fixture.Read = key =>
            {
                var old = fixture.Value;
                entered.TrySetResult(); Wait(release.Task);
                if (scenario == "late-error") throw new IOException("old read error");
                return new(key, old, "old read");
            };
            var refreshing = fixture.Vm.RefreshSavedValuesAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task writing = Task.CompletedTask;
            try
            {
                if (scenario == "deleted") fixture.Version.Fields.Remove(fixture.Field);
                else
                {
                    if (scenario == "different-field")
                    {
                        var other = new SavedField { LocatorKind = "GameAdapter", AdapterId = fixture.Adapter.Id, AdapterFieldKey = "other", CurrentValue = "1" };
                        fixture.Version.Fields.Add(other); fixture.Vm.SelectedSavedField = other;
                    }
                    writing = fixture.Vm.WriteSelectedFieldAsync("20");
                    if (scenario == "different-field")
                    { await writing.WaitAsync(TimeSpan.FromSeconds(2)); Check(fixture.Writes == 1, "Different field was blocked by an unrelated read."); }
                    else Check(fixture.Writes == 0, "New manual write overlapped the old read of the same field.");
                }
            }
            finally { release.TrySetResult(); }
            await refreshing; await writing;
            if (scenario == "deleted") Check(fixture.Field.CurrentValue == "10", "Removed field received a late refresh result.");
            else if (scenario != "different-field")
            {
                Check(fixture.Field.CurrentValue == "20" && fixture.Value == "20", "Stale refresh replaced the new field value.");
                Check(fixture.Vm.StatusText.Contains("已实时修改", StringComparison.Ordinal), "Old refresh masked the new write feedback.");
            }
        }
    }

    private static async Task CheckWriteOrderAsync(string root)
    {
        foreach (var scenario in new[] { "normal", "failure", "save-failure", "deleted", "shutdown", "unlock", "changed-target", "switch", "return", "version", "disconnect", "module" })
        {
            using var fixture = new Fixture(Path.Combine(root, "write-" + scenario));
            fixture.Field.IsValueLocked = true; fixture.Field.LockedValue = "1";
            if (scenario == "save-failure") Directory.CreateDirectory(fixture.Vm.LibraryPath);
            var entered = Gate(); var release = Gate();
            var calls = new List<string>();
            fixture.Write = (key, value) =>
            {
                lock (calls) calls.Add(value);
                if (value == "10") { entered.TrySetResult(); Wait(release.Task); if (scenario == "failure") throw new IOException("first write failed"); }
                fixture.Value = value; return new(key, value, "write confirmed");
            };
            var first = fixture.Vm.WriteSelectedFieldAsync("10");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = fixture.Vm.WriteSelectedFieldAsync("20");
            try
            {
                Check(fixture.Writes == 1, "Two writes of the same saved field ran concurrently.");
                if (scenario == "deleted") fixture.Version.Fields.Remove(fixture.Field);
                if (scenario == "shutdown") fixture.Vm.Shutdown();
                if (scenario == "unlock") { fixture.Field.IsValueLocked = false; fixture.Field.LockedValue = ""; }
                if (scenario == "changed-target") fixture.Field.LockedValue = "99";
                if (scenario is "switch" or "return")
                {
                    var original = fixture.Vm.SelectedGame;
                    var other = new GameProfile { Name = "other", Versions = [new()] };
                    fixture.Vm.Games.Add(other); fixture.Vm.SelectedGame = other;
                    if (scenario == "return") { fixture.Vm.SelectedGame = original; fixture.Vm.SelectedVersion = fixture.Version; }
                }
                if (scenario == "version") fixture.Vm.SelectedVersion = new();
                if (scenario == "disconnect") typeof(MainViewModel).GetProperty(nameof(MainViewModel.AttachedProcess))!.SetValue(fixture.Vm, null);
                if (scenario == "module") Set(fixture.Vm, "_activeAdapter", null);
            }
            finally { release.TrySetResult(); }
            if (scenario is "deleted" or "shutdown" or "switch" or "return" or "version" or "disconnect" or "module")
            { await ExpectAsync<OperationCanceledException>(first); await ExpectAsync<OperationCanceledException>(second); Check(fixture.Writes == 1, "An invalid queued write reached the adapter."); }
            else
            {
                if (scenario == "save-failure")
                {
                    await ExpectAsync<UnauthorizedAccessException>(first); await ExpectAsync<UnauthorizedAccessException>(second);
                    Directory.Delete(fixture.Vm.LibraryPath, false);
                    await fixture.Vm.WriteSelectedFieldAsync("30");
                    Check(fixture.Value == "30" && fixture.Field.CurrentValue == "30", "Persistence failure left the field queue stuck.");
                    continue;
                }
                if (scenario == "failure") await ExpectAsync<IOException>(first); else await first;
                await second;
                Check(calls.SequenceEqual(["10", "20"]) && fixture.Value == "20" && fixture.Field.CurrentValue == "20", "Write queue reordered calls, stuck on an error or showed an old value.");
                // The second request starts after the explicit target change, so it may update that new target.
                Check(scenario == "unlock" ? !fixture.Field.IsValueLocked && fixture.Field.LockedValue == "" : fixture.Field.LockedValue == "20", "A late write restored an obsolete lock target.");
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.Vm.LibraryPath));
                Check(json.RootElement.GetProperty("Games")[0].GetProperty("Versions")[0].GetProperty("Fields")[0].GetProperty("LockedValue").GetString() == fixture.Field.LockedValue,
                    "Write order was not preserved in the saved lock target.");
            }
        }
    }

    private static async Task CheckLockCoordinationAsync(string root)
    {
        foreach (var scenario in new[] { "manual-first", "lock-first", "other-lock" })
        {
            using var fixture = new Fixture(Path.Combine(root, "locks-" + scenario));
            fixture.Field.IsValueLocked = true; fixture.Field.LockedValue = "10";
            var entered = Gate(); var release = Gate(); var otherDone = Gate();
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            fixture.Read = key =>
            {
                if (scenario == "lock-first" && key == fixture.Field.AdapterFieldKey) { entered.TrySetResult(); Wait(release.Task); }
                return new(key, key == "other" ? "30" : fixture.Value, "read");
            };
            fixture.Write = (key, value) =>
            {
                fixture.Value = value;
                if (value == "20") { entered.TrySetResult(); Wait(release.Task); }
                return new(key, value, "write");
            };
            if (scenario != "lock-first")
            {
                var other = new SavedField { LocatorKind = "GameAdapter", AdapterId = fixture.Adapter.Id, AdapterFieldKey = "other", IsValueLocked = true, LockedValue = "30" };
                other.PropertyChanged += (_, change) => { if (change.PropertyName == nameof(SavedField.Status)) otherDone.TrySetResult(); };
                fixture.Version.Fields.Add(other);
            }
            Task locking = Task.CompletedTask; Task writing = Task.CompletedTask;
            try
            {
                if (scenario == "lock-first")
                {
                    fixture.Value = "7";
                    locking = StartMaintenance(fixture, cancel.Token);
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    writing = fixture.Vm.WriteSelectedFieldAsync("20");
                    Check(fixture.Writes == 0, "Manual write overlapped a lock read already in flight.");
                }
                else
                {
                    writing = fixture.Vm.WriteSelectedFieldAsync("20");
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    locking = StartMaintenance(fixture, cancel.Token);
                    await otherDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    Check(fixture.Writes == 1, "Background lock wrote its old target during the manual write.");
                }
            }
            finally { if (scenario != "lock-first") cancel.Cancel(); release.TrySetResult(); }
            try { await writing; } finally { cancel.Cancel(); }
            await locking;
            Check(fixture.Writes == 1 && fixture.Value == "20" && fixture.Field.CurrentValue == "20" && fixture.Field.LockedValue == "20",
                "Manual write and lock ended with different targets or an old lock write still ran.");
        }
    }
    private static Task StartMaintenance(Fixture fixture, CancellationToken token) =>
        Task.Run(() => (Task)Invoke(fixture.Vm, "MaintainLockedValuesAsync", fixture.Inner.Process, fixture.Version, fixture.Adapter, token)!);

    private static async Task CheckTargetChangeAsync(string root)
    {
        using var fixture = new Fixture(Path.Combine(root, "explicit-target"));
        fixture.Field.IsValueLocked = true; fixture.Field.LockedValue = "10";
        var entered = Gate(); var release = Gate();
        fixture.Write = (key, value) => { entered.TrySetResult(); Wait(release.Task); return new(key, fixture.Value = value, "write"); };
        var writing = fixture.Vm.WriteSelectedFieldAsync("20");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { fixture.Field.LockedValue = "99"; } finally { release.TrySetResult(); }
        await writing;
        Check(fixture.Field.CurrentValue == "20" && fixture.Field.LockedValue == "99", "Write completion replaced an explicitly changed lock target.");
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.Vm.LibraryPath));
        Check(json.RootElement.GetProperty("Games")[0].GetProperty("Versions")[0].GetProperty("Fields")[0].GetProperty("LockedValue").GetString() == "99",
            "Explicit target change was not retained in the saved library.");
    }

    private static async Task CheckSaveCompletionAsync(string root)
    {
        foreach (var scenario in new[] { "deleted", "module" })
        {
            using var fixture = new Fixture(Path.Combine(root, "save-completion-" + scenario));
            using var fileLease = new FileStream(fixture.Vm.LibraryPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var reachedUi = Gate();
            fixture.Field.PropertyChanged += (_, change) => { if (change.PropertyName == nameof(SavedField.Status)) reachedUi.TrySetResult(); };
            var writing = fixture.Vm.WriteSelectedFieldAsync("20");
            await reachedUi.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                Check(!writing.IsCompleted && fixture.Value == "20", "Slow-save fixture did not hold persistence after a completed write.");
                if (scenario == "deleted") fixture.Version.Fields.Remove(fixture.Field);
                else Set(fixture.Vm, "_activeAdapter", null);
                fixture.Vm.ReportModulePageStatus("new state");
            }
            finally { fileLease.Dispose(); }
            await ExpectAsync<OperationCanceledException>(writing);
            Check(fixture.Vm.StatusText == "new state", "Slow save reported completion after its field/module expired.");
        }
    }

    private static async Task CheckRefreshIdentityAsync(string root)
    {
        foreach (var stale in new[] { false, true })
        {
            using var fixture = new SafetyBoundaryRegressionTests.WriteFixture(Path.Combine(root, stale ? "stale-pid" : "valid-pid"));
            var process = fixture.Process;
            if (stale)
            {
                process = new ProcessItem { ProcessId = process.ProcessId, StartTimeUtc = process.StartTimeUtc.AddTicks(-1) };
                typeof(MainViewModel).GetProperty(nameof(MainViewModel.AttachedProcess))!.SetValue(fixture.ViewModel, process);
            }
            var address = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(address, 42);
                var field = new SavedField { CurrentValue = "old", LastAddress = unchecked((ulong)address.ToInt64()), ProcessStartTimeUtcTicks = process.StartTimeUtc.Ticks };
                fixture.Version.Fields.Add(field);
                if (stale)
                { await ExpectAsync<InvalidOperationException>(fixture.ViewModel.RefreshSavedValuesAsync()); Check(field.CurrentValue == "old", "Invalid process instance changed the field display."); }
                else
                { await fixture.ViewModel.RefreshSavedValuesAsync(); Check(field.CurrentValue == "42" && field.Status == "可用", "Valid self-process refresh stopped working."); }
            }
            finally { Marshal.FreeHGlobal(address); }
        }
    }

    private static async Task CheckPageLifetimeAsync(string root)
    {
        using var fixture = new SafetyBoundaryRegressionTests.WriteFixture(Path.Combine(root, "pages"));
        var host = new ProbeHost();
        var factory = DispatchProxy.Create<IGameEditorPageFactoryProvider, PageFactoryProxy>();
        var proxy = (PageFactoryProxy)(object)factory;
        fixture.ViewModel.SetEditorHostServices(host);
        Set(fixture.ViewModel, "_activeAdapter", factory);
        Invoke(fixture.ViewModel, "RebuildEditorPages");
        var first = proxy.Contexts.Single();
        first.Host.ReportStatus("current"); first.Host.ShowError("current", "error");
        Check(host.Statuses == 1 && host.Errors == 1, "Current page lost host status/error services.");
        Check(await first.Host.PromptValueAsync(new("input", "message", "1")) == "42", "Current page lost input service.");
        await first.Host.SaveFieldAsync(new("key", "name"));
        Check(host.Saves == 1, "Current page lost save service.");
        var delayed = Gate(); host.PromptGate = delayed.Task; host.PromptEntered = Gate();
        var prompt = first.Host.PromptValueAsync(new("input", "message", "1"));
        await host.PromptEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Invoke(fixture.ViewModel, "RebuildEditorPages");
        Check(first.Lifetime.IsCancellationRequested, "Rebuilt page kept its old lifetime.");
        first.Host.ReportStatus("stale"); first.Host.ShowError("stale", "old error");
        Check(host.Statuses == 1 && host.Errors == 1, "Expired module page still reached host status/error UI.");
        delayed.SetResult(); await ExpectAsync<OperationCanceledException>(prompt);
        await ExpectAsync<OperationCanceledException>(first.Host.PromptValueAsync(new("old", "old", "")));
        await ExpectAsync<OperationCanceledException>(first.Host.SaveFieldAsync(new("old", "old")));
        Check(host.Saves == 1 && host.Prompts == 2, "Expired page started a new input/save operation.");
        var current = proxy.Contexts.Last(); current.Host.ReportStatus("new");
        Check(host.Statuses == 2, "New page was invalidated together with the old page.");
        fixture.ViewModel.Shutdown();
        await Task.Run(() => current.Host.ShowError("closing", "late"));
        Check(host.Errors == 1, "Closing page produced a late host error.");
    }

    private static async Task CheckPageDispatchAsync()
    {
        using var lifetime = new CancellationTokenSource();
        var current = true; var host = new ProbeHost(); var dispatcher = Dispatcher.CurrentDispatcher;
        var scoped = new ScopedGameEditorHostServices(host, lifetime.Token, () => current, dispatcher);
        var posted = Gate();
        DispatcherHookEventHandler onPosted = (_, e) => { if (e.Operation.Priority == DispatcherPriority.Send) posted.TrySetResult(); };
        dispatcher.Hooks.OperationPosted += onPosted;
        var lateError = Task.Run(() => scoped.ShowError("old", "queued error"));
        try { Wait(posted.Task); current = false; }
        finally { dispatcher.Hooks.OperationPosted -= onPosted; }
        await lateError.WaitAsync(TimeSpan.FromSeconds(5));
        Check(host.Errors == 0, "Host validated a callback only before Dispatcher execution.");
        current = true;
        var queuedPrompt = scoped.PromptValueAsync(new("old", "queued", ""));
        current = false;
        await ExpectAsync<OperationCanceledException>(queuedPrompt);
        Check(host.Prompts == 0, "Queued input entered the host after its context changed.");
        current = true; host.PromptError = new IOException("current input error");
        await ExpectAsync<IOException>(scoped.PromptValueAsync(new("current", "error", "")));
        var entered = Gate(); var release = Gate(); host.SaveEntered = entered; host.SaveGate = release.Task;
        host.SaveError = new IOException("late save error");
        var saving = scoped.SaveFieldAsync(new("key", "name"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        current = false; release.SetResult();
        await ExpectAsync<OperationCanceledException>(saving);
        Check(host.Saves == 1, "Save service was unexpectedly started twice.");
    }

    private static async Task CheckSaveLifetimeAsync(string root)
    {
        foreach (var expires in new[] { false, true })
        {
            using var fixture = new Fixture(Path.Combine(root, expires ? "save-expired" : "save-current"));
            using var lifetime = new CancellationTokenSource();
            var entered = Gate(); var release = Gate();
            fixture.Read = key => { entered.TrySetResult(); Wait(release.Task); return new(key, "42", "read"); };
            var count = fixture.Version.Fields.Count;
            var saving = fixture.Vm.AddAdapterFieldAsync("new-key", "new field", "group", () => lifetime.Token.ThrowIfCancellationRequested());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try { if (expires) lifetime.Cancel(); } finally { release.TrySetResult(); }
            if (expires)
            {
                await ExpectAsync<OperationCanceledException>(saving);
                Check(fixture.Version.Fields.Count == count && !File.Exists(fixture.Vm.LibraryPath), "Expired page added or persisted a field after its read returned.");
                await ExpectAsync<OperationCanceledException>(fixture.Vm.AddAdapterFieldAsync("new-key", "old page", "group", () => lifetime.Token.ThrowIfCancellationRequested()));
            }
            else
            {
                var saved = await saving;
                Check(fixture.Version.Fields.Count == count + 1 && saved.CurrentValue == "42" && File.Exists(fixture.Vm.LibraryPath), "Current page no longer adds and persists a field.");
            }
            var validate = fixture.Vm.CaptureEditorHostOperation(); validate();
            fixture.Vm.SelectedVersion = new();
            await ExpectAsync<OperationCanceledException>(Task.Run(validate));
        }
        using var completedFixture = new Fixture(Path.Combine(root, "save-expired-after-add"));
        using var page = new CancellationTokenSource();
        completedFixture.Vm.ReportModulePageStatus("new page state");
        completedFixture.Version.Fields.CollectionChanged += (_, _) => page.Cancel();
        await ExpectAsync<OperationCanceledException>(completedFixture.Vm.AddAdapterFieldAsync("new-key", "field", "group", () => page.Token.ThrowIfCancellationRequested()));
        Check(completedFixture.Vm.StatusText == "new page state" && completedFixture.Version.Fields.Count == 2 && File.Exists(completedFixture.Vm.LibraryPath),
            "Expired save completion replaced page status or rolled back an already-added field.");
    }

    internal sealed class Fixture : IDisposable
    {
        internal SafetyBoundaryRegressionTests.WriteFixture Inner { get; }
        internal MainViewModel Vm => Inner.ViewModel;
        internal GameVersionProfile Version => Inner.Version;
        internal SavedField Field { get; }
        internal SmokeTestModuleAdapter Adapter { get; }
        internal string Value = "10";
        internal int Writes;
        internal Func<string, AdapterFieldValue>? Read;
        internal Func<string, string, AdapterFieldValue>? Write;
        internal Fixture(string root)
        {
            Inner = new(root);
            Adapter = new SmokeTestModuleAdapter { IdentityOnly = true, IdentityId = "game.field-review",
                ReadFieldOverride = (_, key) => Read?.Invoke(key) ?? new(key, Value, "read"),
                WriteFieldOverride = (_, key, value) => { Interlocked.Increment(ref Writes); return Write?.Invoke(key, value) ?? new(key, Value = value, "write"); } };
            Set(Vm, "_activeAdapter", Adapter);
            Field = new SavedField { LocatorKind = "GameAdapter", AdapterId = Adapter.Id,
                AdapterFieldKey = ModuleFieldKey.Create("review.inventory", "item", "count"), CurrentValue = "10", Name = "test" };
            Version.Fields.Add(Field); Vm.SelectedSavedField = Field;
        }
        public void Dispose() => Inner.Dispose();
    }

    // The runtime proxy lives in a generated assembly, not in the sample module DLL.
    public class PageFactoryProxy : DispatchProxy
    {
        internal List<GameEditorPageContext> Contexts { get; } = [];
        internal IReadOnlyList<string> Aliases { get; set; } = [];
        internal Func<string, AdapterFieldValue>? Read;
        internal Func<string, string, AdapterFieldValue>? Write;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_Id" => "game.page-review", "get_DisplayName" => "page probe", "get_Description" => "test",
            "get_LegacyIds" => Aliases, "get_Editors" => new[] { new GameEditorDescriptor("review.page", "page", GameEditorKind.Custom, 1, "test") },
            "Supports" => true, "CreateEditorPage" => Create((GameEditorPageContext)args![1]!),
            "ReadField" => Read?.Invoke((string)args![1]!) ?? new AdapterFieldValue((string)args![1]!, "10", "read"),
            "WriteField" => Write?.Invoke((string)args![1]!, (string)args[2]!) ?? new AdapterFieldValue((string)args![1]!, (string)args[2]!, "write"),
            "GetCompatibilityDiagnostics" => Array.Empty<GameCompatibilityDiagnostic>(),
            _ => throw new InvalidOperationException("Unexpected page probe call: " + method.Name)
        };
        private IGameEditorPage Create(GameEditorPageContext context) { Contexts.Add(context); return new ProbePage(); }
    }
    private sealed class ProbePage : IGameEditorPage { public FrameworkElement View { get; } = new TextBlock(); public void Dispose() { } }
    private sealed class ProbeHost : IGameEditorHostServices
    {
        internal int Statuses, Errors, Saves, Prompts;
        internal Task? PromptGate;
        internal TaskCompletionSource? PromptEntered;
        internal Exception? PromptError, SaveError;
        internal Task? SaveGate;
        internal TaskCompletionSource? SaveEntered;
        public async Task<string?> PromptValueAsync(GameEditorTextPrompt prompt)
        { Prompts++; PromptEntered?.TrySetResult(); if (PromptGate is not null) await PromptGate; if (PromptError is not null) throw PromptError; return "42"; }
        public async Task SaveFieldAsync(GameEditorSavedFieldRequest request)
        { Saves++; SaveEntered?.TrySetResult(); if (SaveGate is not null) await SaveGate; if (SaveError is not null) throw SaveError; }
        public void ReportStatus(string message) => Statuses++;
        public void ShowError(string title, string message) => Errors++;
    }
}
