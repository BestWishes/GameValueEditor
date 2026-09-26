using GameValueEditor.Models;

namespace GameValueEditor.Services.Adapters;

public interface IGameAdapter
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    bool Supports(ProcessItem process, VersionFingerprint fingerprint);
    AdapterFieldValue ReadField(ProcessItem process, string fieldKey);
    AdapterFieldValue WriteField(ProcessItem process, string fieldKey, string displayValue);
}

public interface IInventoryGameAdapter : IGameAdapter
{
    IReadOnlyList<AdapterInventoryItem> ReadInventory(ProcessItem process);
}

public sealed record AdapterFieldValue(string FieldKey, string DisplayValue, string Status);
public sealed record AdapterInventoryItem(string FieldKey, string DisplayName, long Count)
{
    public string CountDisplay => Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class GameAdapterRegistry
{
    private readonly IReadOnlyList<IGameAdapter> _adapters =
    [
        new FzzmlInventoryAdapter()
    ];

    public IGameAdapter? Resolve(ProcessItem process, VersionFingerprint fingerprint) =>
        _adapters.FirstOrDefault(adapter => adapter.Supports(process, fingerprint));

    public IGameAdapter? FindById(string id) =>
        _adapters.FirstOrDefault(adapter => string.Equals(adapter.Id, id, StringComparison.Ordinal));
}
