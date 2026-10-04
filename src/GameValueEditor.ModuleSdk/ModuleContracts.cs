using System.Globalization;

namespace GameValueEditor.ModuleSdk;

public static class ModuleHostApi
{
    public const int CurrentVersion = 3;
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
    PropertyGrid
}

public sealed record GameEditorDescriptor(
    string Id,
    string DisplayName,
    GameEditorKind Kind,
    int Order,
    string Description,
    bool SessionOnly = false);

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
