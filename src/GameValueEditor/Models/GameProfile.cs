using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using GameValueEditor.Infrastructure;

namespace GameValueEditor.Models;

public sealed class GameProfile : ObservableObject
{
    private string _name = string.Empty;
    private string _executablePath = string.Empty;
    private string _processName = string.Empty;
    private bool _isPinned;
    private bool _isLocked;
    private DateTime _lastUsedUtc = DateTime.UtcNow;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string ExecutablePath { get => _executablePath; set => SetProperty(ref _executablePath, value); }
    public string ProcessName { get => _processName; set => SetProperty(ref _processName, value); }
    public bool IsPinned { get => _isPinned; set { if (SetProperty(ref _isPinned, value)) OnPropertyChanged(nameof(Badges)); } }
    public bool IsLocked { get => _isLocked; set { if (SetProperty(ref _isLocked, value)) OnPropertyChanged(nameof(Badges)); } }
    public DateTime LastUsedUtc { get => _lastUsedUtc; set => SetProperty(ref _lastUsedUtc, value); }
    public ObservableCollection<GameVersionProfile> Versions { get; set; } = [];

    [JsonIgnore]
    public string Badges => $"{(IsPinned ? "📌 " : string.Empty)}{(IsLocked ? "🔒 " : string.Empty)}";

    [JsonIgnore]
    public string Summary => $"{Versions.Count} 个版本 · {Versions.Sum(version => version.Fields.Count)} 个字段";

    public void NotifySummaryChanged() => OnPropertyChanged(nameof(Summary));
}

public sealed class GameVersionProfile : ObservableObject
{
    private string _displayName = string.Empty;
    private DateTime _lastVerifiedUtc = DateTime.UtcNow;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }
    public string FileVersion { get; set; } = string.Empty;
    public string ProductVersion { get; set; } = string.Empty;
    public string ExecutableSha256 { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Architecture { get; set; } = "Unknown";
    public DateTime LastVerifiedUtc { get => _lastVerifiedUtc; set => SetProperty(ref _lastVerifiedUtc, value); }
    public ObservableCollection<SavedField> Fields { get; set; } = [];

    public override string ToString() => DisplayName;
}

public sealed class SavedField : ObservableObject
{
    private string _name = string.Empty;
    private string _group = "未分组";
    private string _note = string.Empty;
    private string _currentValue = "—";
    private string _status = "等待连接";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Group { get => _group; set => SetProperty(ref _group, value); }
    public string Note { get => _note; set => SetProperty(ref _note, value); }
    public MemoryValueType ValueType { get; set; }
    public ulong LastAddress { get; set; }
    public string LocatorKind { get; set; } = "SessionAddress";
    public string ModuleName { get; set; } = string.Empty;
    public long ModuleOffset { get; set; }
    public long ProcessStartTimeUtcTicks { get; set; }
    public DateTime LastVerifiedUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public string AddressDisplay => $"0x{LastAddress:X}";

    [JsonIgnore]
    public string TypeDisplay => ValueType.ToDisplayName();

    [JsonIgnore]
    public string CurrentValue { get => _currentValue; set => SetProperty(ref _currentValue, value); }

    [JsonIgnore]
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    public void NotifyAddressChanged() => OnPropertyChanged(nameof(AddressDisplay));
}
