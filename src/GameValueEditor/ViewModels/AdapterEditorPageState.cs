using GameValueEditor.Infrastructure;
using GameValueEditor.ModuleSdk;
using System.Windows;

namespace GameValueEditor.ViewModels;

public abstract class AdapterEditorPageState(
    GameEditorDescriptor descriptor,
    GameEditorPageRegistration? registration,
    bool isSupported) : ObservableObject
{
    public GameEditorDescriptor Descriptor { get; } = descriptor;
    public GameEditorPageRegistration? Registration { get; } = registration;
    public bool IsSupported { get; } = isSupported;
    public bool IsUnsupported => !IsSupported;
}

public sealed class AdapterInventoryEditorPageState(
    GameEditorDescriptor descriptor,
    GameEditorPageRegistration registration) : AdapterEditorPageState(descriptor, registration, true);

public sealed class AdapterCharacterEditorPageState(
    GameEditorDescriptor descriptor,
    GameEditorPageRegistration registration,
    bool isSupported) : AdapterEditorPageState(descriptor, registration, isSupported);

public sealed class AdapterModuleEditorPageState : AdapterEditorPageState, IDisposable
{
    private readonly CancellationTokenSource _lifetime;
    private readonly IGameEditorPage _page;
    private bool _disposed;

    public AdapterModuleEditorPageState(
        GameEditorDescriptor descriptor,
        CancellationTokenSource lifetime,
        IGameEditorPage page) : base(descriptor, null, true)
    {
        _lifetime = lifetime;
        _page = page;
        Content = page.View ?? throw new InvalidOperationException(
            $"模块页面 {descriptor.Id} 返回了空的 WPF 视图。");
    }

    public FrameworkElement Content { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        Content.DataContext = null;
        _page.Dispose();
        _lifetime.Dispose();
    }
}
