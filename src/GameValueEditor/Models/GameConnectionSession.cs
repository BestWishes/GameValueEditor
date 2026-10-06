using System.Collections.ObjectModel;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Services;
using GameValueEditor.Services.Adapters;

namespace GameValueEditor.Models;

internal sealed class GameConnectionSession(LogicalGameProcessGroup processGroup, Guid? gameId)
{
    public LogicalGameProcessGroup ProcessGroup { get; set; } = processGroup;
    public ProcessItem Process { get; set; } = processGroup.DataProcess;
    public Guid? GameId { get; set; } = gameId;
    public Guid? VersionId { get; set; }
    public VersionFingerprint? Fingerprint { get; set; }
    public IGameAdapter? Adapter { get; set; }
    public ProcessSpeedService SpeedService { get; set; } = new();
    public bool IsSpeedActive { get; set; }
    public bool IsSpeedOperationRunning { get; set; }
    public string SpeedMultiplierInput { get; set; } = "2";
    public ScanCandidateStore? ScanCandidates { get; set; }
    public Stack<ScanCandidateStore> ScanHistory { get; set; } = new();
    public ObservableCollection<ScanCandidate> VisibleScanResults { get; set; } = [];
    public ScanCandidate? SelectedScanResult { get; set; }
    public List<AdapterInventoryItem> AdapterItems { get; set; } = [];
    public List<AdapterCharacterItem> AdapterCharacters { get; set; } = [];
    public string? SelectedAdapterFieldKey { get; set; }
    public string? SelectedCharacterId { get; set; }
    public string? SelectedCharacterAttributeKey { get; set; }
    public string AdapterItemNameFilter { get; set; } = string.Empty;
    public string AdapterItemCountFilter { get; set; } = string.Empty;
    public string ScanValue { get; set; } = string.Empty;
    public MemoryValueType? SelectedValueType { get; set; }
    public ScanComparison SelectedComparison { get; set; } = ScanComparison.Exact;
    public string SelectedSearchRoutineId { get; set; } = SearchRoutineIds.All;
    public string ScaleMultiplier { get; set; } = "2";
    public CancellationTokenSource? LockMaintenanceCancellation { get; set; }
}
