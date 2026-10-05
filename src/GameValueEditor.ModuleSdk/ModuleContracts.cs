using System.Globalization;
using System.Windows;

namespace GameValueEditor.ModuleSdk;

public static class ModuleHostApi
{
    public const int CurrentVersion = 6;
}

public static class ModuleFieldKey
{
    public static string Create(string editorId, string entityId, string fieldId) =>
        $"{editorId}|{Uri.EscapeDataString(entityId)}|{Uri.EscapeDataString(fieldId)}";

    public static bool TryParse(string value, out string editorId, out string entityId, out string fieldId)
    {
        editorId = entityId = fieldId = string.Empty;
        var parts = value.Split('|', 3, StringSplitOptions.None);
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace)) return false;
        try
        {
            editorId = parts[0];
            entityId = Uri.UnescapeDataString(parts[1]);
            fieldId = Uri.UnescapeDataString(parts[2]);
            return true;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }
}

public sealed record GameProcessContext(
    int ProcessId,
    string ProcessName,
    string ExecutablePath,
    DateTime StartTimeUtc);

public sealed record GameBuildIdentity(
    string ExecutableSha256,
    string BuildFingerprint,
    string GameAssemblySha256,
    string MetadataSha256);

public enum GameEditorKind
{
    Collection,
    MasterDetail,
    PropertyGrid,
    Custom
}

public sealed record GameEditorDescriptor(
    string Id,
    string DisplayName,
    GameEditorKind Kind,
    int Order,
    string Description,
    bool SessionOnly = false);

public enum GameEditorPageRole
{
    Inventory,
    CharacterAttributes,
    Entity
}

/// <summary>
/// Binds a module-owned editor identity to one of the host's reusable page templates.
/// The module controls which pages exist and their semantic identity; the host only
/// supplies the shared visual shell for the selected role.
/// </summary>
public sealed record GameEditorPageRegistration(
    string EditorId,
    GameEditorPageRole Role,
    string EmptyMessage = "");

public interface IGameEditorPageProvider : IGameAdapter
{
    IReadOnlyList<GameEditorPageRegistration> EditorPages { get; }
}

public sealed record GameEditorTextPrompt(
    string Title,
    string Message,
    string InitialValue = "");

public sealed record GameEditorSavedFieldRequest(
    string FieldKey,
    string SuggestedDisplayName);

/// <summary>
/// Stable host-owned facilities that a module page may use without referencing
/// the host executable, its view models, or its dialog implementations.
/// </summary>
public interface IGameEditorHostServices
{
    Task<string?> PromptValueAsync(GameEditorTextPrompt prompt);
    Task SaveFieldAsync(GameEditorSavedFieldRequest request);
    void ReportStatus(string message);
    void ShowError(string title, string message);
}

public sealed record GameEditorPageContext(
    GameProcessContext Process,
    GameBuildIdentity Build,
    IGameEditorHostServices Host,
    CancellationToken Lifetime);

/// <summary>
/// A real WPF page owned by the game module. The host only places View inside
/// its navigation shell and disposes the page when its process/module lifetime ends.
/// </summary>
public interface IGameEditorPage : IDisposable
{
    FrameworkElement View { get; }
}

/// <summary>
/// Host API 6 page contract. Unlike the legacy role provider, this factory does
/// not classify pages or select a host template; the module creates the complete UI.
/// </summary>
public interface IGameEditorPageFactoryProvider : IGameAdapter
{
    IGameEditorPage CreateEditorPage(string editorId, GameEditorPageContext context);
}

public sealed record GameEditorFieldPolicy(bool SessionOnly, bool CanLock);

/// <summary>
/// Optional per-field lifetime policy. Modules use this when one editor contains a
/// mix of persisted fields and fields that only live for the current game session.
/// </summary>
public interface IGameEditorFieldPolicyProvider : IGameAdapter
{
    GameEditorFieldPolicy? GetFieldPolicy(string editorId, string entityId, string fieldId);
}

public enum GameCompatibilityDiagnosticStatus
{
    Information,
    Passed,
    Warning,
    Failed
}

public sealed record GameCompatibilityDiagnostic(
    string DisplayName,
    GameCompatibilityDiagnosticStatus Status,
    string Message);

/// <summary>
/// Read-only compatibility diagnostics for the current process and build. This is
/// optional for older module contracts and required when module.json selects API 5.
/// Implementations must not write memory, invoke game save paths, or expose local
/// filesystem paths, process IDs, memory addresses, or save data in their messages.
/// </summary>
public interface IGameCompatibilityDiagnosticsProvider : IGameAdapter
{
    IReadOnlyList<GameCompatibilityDiagnostic> GetCompatibilityDiagnostics(
        GameProcessContext process,
        GameBuildIdentity fingerprint);
}

public interface IGameAdapter
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    IReadOnlyList<string> LegacyIds => [];
    IReadOnlyList<GameEditorDescriptor> Editors { get; }
    bool Supports(GameProcessContext process, GameBuildIdentity fingerprint);
    AdapterFieldValue ReadField(GameProcessContext process, string fieldKey);
    AdapterFieldValue WriteField(GameProcessContext process, string fieldKey, string displayValue);
}

public interface IInventoryGameAdapter : IGameAdapter
{
    IReadOnlyList<AdapterInventoryItem> ReadInventory(GameProcessContext process);
}

public interface ICharacterAttributesGameAdapter : IGameAdapter
{
    bool SupportsCharacterAttributes(GameProcessContext process);
    IReadOnlyList<AdapterCharacterItem> ReadCharacters(GameProcessContext process);
    AdapterCharacterItem WriteCharacterAttribute(
        GameProcessContext process,
        string characterId,
        string attributeKey,
        int targetValue);
}

/// <summary>
/// Optional contract for game-specific editors whose rows and numeric fields do not fit
/// the legacy inventory or character-attribute shapes. Editor, entity and field IDs are
/// semantic identities and must remain stable across supported builds.
/// </summary>
public interface IEntityEditorsGameAdapter : IGameAdapter
{
    bool SupportsEntityEditor(GameProcessContext process, string editorId);
    IReadOnlyList<AdapterEditorEntity> ReadEditorEntities(GameProcessContext process, string editorId);
    AdapterEditorEntity WriteEditorField(
        GameProcessContext process,
        string editorId,
        string entityId,
        string fieldKey,
        long targetValue);
}

/// <summary>
/// Optional metadata provider for versions declared by the game itself. The host keeps
/// platform build identifiers and executable/engine versions separate from these values.
/// </summary>
public interface IGameVersionMetadataProvider : IGameAdapter
{
    GameDeclaredVersionInfo ReadGameVersionMetadata(GameProcessContext process);
}

public sealed record GameDeclaredVersionInfo(
    string Version,
    string ProductName,
    string BuildGuid);

public sealed record AdapterFieldValue(string FieldKey, string DisplayValue, string Status);

public sealed record AdapterInventoryItem(string FieldKey, string DisplayName, long Count)
{
    public string CountDisplay => Count.ToString(CultureInfo.InvariantCulture);
}

public sealed record AdapterCharacterItem(
    string CharacterId,
    string DisplayName,
    int Level,
    IReadOnlyList<AdapterCharacterAttribute> Attributes)
{
    public string Summary => $"等级 {Level} · {Attributes.Count} 项可修改属性";
}

public sealed record AdapterCharacterAttribute(
    string Key,
    string DisplayName,
    int RawValue,
    int AggregatedValue,
    float GrowthValue,
    bool CanWrite = true,
    string Status = "")
{
    public string RawValueDisplay => RawValue.ToString(CultureInfo.InvariantCulture);
    public string AggregatedValueDisplay => AggregatedValue.ToString(CultureInfo.InvariantCulture);
    public string GrowthValueDisplay => GrowthValue.ToString("0.###", CultureInfo.InvariantCulture);
}

public sealed record AdapterEditorEntity(
    string EntityId,
    string DisplayName,
    string Summary,
    IReadOnlyList<AdapterEditorField> Fields);

public sealed record AdapterEditorField(
    string Key,
    string DisplayName,
    long Value,
    long Minimum,
    long Maximum,
    bool CanWrite = true,
    string Status = "")
{
    public string ValueDisplay => Value.ToString(CultureInfo.InvariantCulture);
    public string RangeDisplay => Minimum == Maximum
        ? string.Empty
        : $"{Minimum.ToString(CultureInfo.InvariantCulture)} ~ {Maximum.ToString(CultureInfo.InvariantCulture)}";
}
