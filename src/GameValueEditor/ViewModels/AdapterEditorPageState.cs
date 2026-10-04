using GameValueEditor.Infrastructure;
using GameValueEditor.ModuleSdk;

namespace GameValueEditor.ViewModels;

public abstract class AdapterEditorPageState(
    GameEditorDescriptor descriptor,
    GameEditorPageRegistration registration,
    bool isSupported) : ObservableObject
{
    public GameEditorDescriptor Descriptor { get; } = descriptor;
    public GameEditorPageRegistration Registration { get; } = registration;
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
