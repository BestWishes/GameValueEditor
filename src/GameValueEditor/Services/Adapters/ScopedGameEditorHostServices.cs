using System.Windows.Threading;
using GameValueEditor.ModuleSdk;

namespace GameValueEditor.Services.Adapters;

// Lifetime protection also covers old packages; only API 8 pages use the write bridge.
internal sealed class ScopedGameEditorHostServices(IGameEditorHostServices inner, CancellationToken lifetime,
    Func<bool> isCurrent, Dispatcher dispatcher,
    Func<string, string, Action, Task<AdapterFieldValue>>? writeField = null,
    Func<FieldOperationCoordinator.Scope.Snapshot>? beginSnapshot = null,
    Func<bool>? isVisible = null)
    : IGameEditorHostServices, IGameEditorFieldOperations, IGameEditorSnapshotOperations
{
    private bool CannotDispatch => lifetime.IsCancellationRequested || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished;
    private bool IsCurrent => !CannotDispatch && isCurrent();
    private bool IsInteractive => IsCurrent && (isVisible?.Invoke() ?? true);
    private void RequireCurrent()
    {
        if (!IsCurrent) throw new OperationCanceledException("模块页面已失效，原请求已取消。", lifetime);
    }
    private void RequireInteractive()
    {
        RequireCurrent();
        if (!IsInteractive) throw new OperationCanceledException("模块页面当前未显示，原交互已取消。", lifetime);
    }
    public Task<string?> PromptValueAsync(GameEditorTextPrompt prompt) =>
        OnUiAsync(() => inner.PromptValueAsync(prompt));
    public Task SaveFieldAsync(GameEditorSavedFieldRequest request) => OnUiAsync(async () =>
    {
        if (inner is WpfGameEditorHostServices wpf) await wpf.SaveFieldAsync(request, RequireInteractive);
        else await inner.SaveFieldAsync(request);
        return true;
    });
    public void ReportStatus(string message) => InvokeIfCurrent(() => inner.ReportStatus(message));
    public void ShowError(string title, string message) => InvokeIfCurrent(() => inner.ShowError(title, message));

    public Task<AdapterFieldValue> WriteFieldAsync(string fieldKey, string displayValue) => OnUiAsync(() =>
        writeField is not null ? writeField(fieldKey, displayValue, RequireInteractive)
            : throw new InvalidOperationException("当前宿主没有提供字段写入协调能力。"));

    public Task ReadSnapshotAsync<T>(Func<T> read, Action<T> apply) => OnUiAsync(async () =>
    {
        var snapshot = beginSnapshot?.Invoke()
            ?? throw new InvalidOperationException("当前宿主没有提供安全刷新能力，请更新主程序。");
        if (!snapshot.IsCurrent) throw new GameEditorSnapshotChangedException();
        T result;
        try { result = await snapshot.ReadAsync(read, lifetime); }
        catch (Exception) when (!snapshot.IsCurrent) { throw new GameEditorSnapshotChangedException(); }
        RequireCurrent();
        if (!snapshot.TryApply(() => { RequireCurrent(); apply(result); }))
            throw new GameEditorSnapshotChangedException();
        return true;
    }, interactive: false);

    private Task<T> OnUiAsync<T>(Func<Task<T>> action, bool interactive = true)
    {
        if (CannotDispatch) return Task.FromException<T>(new OperationCanceledException("模块页面已失效，原请求已取消。", lifetime));
        return dispatcher.InvokeAsync(async () =>
        {
            if (interactive) RequireInteractive(); else RequireCurrent();
            T result;
            try { result = await action(); }
            catch (Exception error) when (!IsCurrent || interactive && !IsInteractive)
            { throw new OperationCanceledException("模块页面已失效，忽略迟到错误。", error, lifetime); }
            if (interactive) RequireInteractive(); else RequireCurrent();
            return result;
        }, DispatcherPriority.Normal, lifetime).Task.Unwrap();
    }

    private void InvokeIfCurrent(Action action)
    {
        if (CannotDispatch) return;
        try { dispatcher.Invoke(() => { if (IsInteractive) action(); }); }
        catch (Exception error) when (CannotDispatch && error is OperationCanceledException or InvalidOperationException) { }
    }
}
