using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using GameValueEditor.Models;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Services;
using GameValueEditor.ViewModels;

internal static class PageSessionRegressionTests
{
    internal static async Task RunAsync(string root)
    {
        using var fixture = new SafetyBoundaryRegressionTests.WriteFixture(root);
        var vm = fixture.ViewModel;
        var original = vm.SelectedGame!;
        var other = new GameProfile { Name = "另一游戏" }; vm.Games.Add(other);
        var adapter = DispatchProxy.Create<ICoordinatedGameEditorPageProvider, FieldOrderRegressionTests.PageFactoryProxy>();
        var proxy = (FieldOrderRegressionTests.PageFactoryProxy)(object)adapter;
        var host = new Host(); vm.SetEditorHostServices(host);
        typeof(MainViewModel).GetMethod("SetActiveAdapter", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [adapter]);
        var page = (AdapterModuleEditorPageState)vm.AdapterEditorPages.Single();
        ((TextBlock)page.Content).Text = "未应用的用户输入";
        var context = proxy.Contexts.Single();
        var reads = (IGameEditorSnapshotOperations)context.Host;
        var entered = Gate(); var release = Gate(); var applied = 0;
        var reading = reads.ReadSnapshotAsync(() => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); return 42; }, value => applied = value);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            vm.SelectedGame = other;
            Check(vm.AdapterEditorPages.Count == 0 && !context.Lifetime.IsCancellationRequested, "Hiding a game canceled its page.");
            context.Host.ShowError("hidden", "error"); context.Host.ReportStatus("hidden");
            Check(host.Errors == 0 && host.Statuses == 0, "Hidden page interfered with another game.");
            await Canceled(context.Host.PromptValueAsync(new("hidden", "message", "1")));
            await Canceled(context.Host.SaveFieldAsync(new("hidden", "field")));
            await Canceled(((IGameEditorFieldOperations)context.Host).WriteFieldAsync("review.page|item|count", "20"));
            Check(host.Prompts == 0 && host.Saves == 0, "Hidden page began an interaction.");
        }
        finally { release.TrySetResult(); }
        await reading;
        Check(applied == 42, "Hidden page lost its already-started read.");
        for (var i = 0; i < 5; i++)
        {
            vm.SelectedGame = original;
            Check(vm.AdapterEditorPages.Single() == page && vm.SelectedAdapterEditorPage == page && proxy.Contexts.Count == 1 &&
                ((TextBlock)page.Content).Text == "未应用的用户输入", "Game switching recreated/reset its page.");
            vm.SelectedGame = other;
        }
        vm.SelectedGame = original;
        context.Host.ReportStatus("visible"); Check(host.Statuses == 1, "Returning page did not regain status access.");

        var nextEntered = Gate(); var nextRelease = Gate(); var queuedCalls = 0;
        var active = reads.ReadSnapshotAsync(() => { nextEntered.TrySetResult(); nextRelease.Task.GetAwaiter().GetResult(); return 1; }, _ => { });
        await nextEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = reads.ReadSnapshotAsync(() => { Interlocked.Increment(ref queuedCalls); return 2; }, _ => { });
        await Dispatcher.Yield();
        Check(queuedCalls == 0 && !queued.IsCompleted, "Same-module reads overlapped instead of waiting.");
        try { await vm.DisconnectSelectedGameAsync(); }
        finally { nextRelease.TrySetResult(); }
        await Canceled(active); await Canceled(queued);
        Check(context.Lifetime.IsCancellationRequested && vm.AdapterEditorPages.Count == 0 && queuedCalls == 0,
            "Disconnected page kept its lifetime or started a queued read.");
        Console.WriteLine("Page session regressions passed: hidden reads, preserved input/page/selection, 5 switches, serialized refresh, disconnect cancellation.");
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Canceled(Task task)
    { try { await task; } catch (OperationCanceledException) { return; } throw new InvalidOperationException("Expected canceled page interaction/read."); }
    private sealed class Host : IGameEditorHostServices
    {
        internal int Prompts, Saves, Statuses, Errors;
        public Task<string?> PromptValueAsync(GameEditorTextPrompt prompt) { Prompts++; return Task.FromResult<string?>(null); }
        public Task SaveFieldAsync(GameEditorSavedFieldRequest request) { Saves++; return Task.CompletedTask; }
        public void ReportStatus(string message) => Statuses++;
        public void ShowError(string title, string message) => Errors++;
    }
}
