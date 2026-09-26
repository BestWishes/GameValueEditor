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
    private bool _isCurrentBuild;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }
    public string FileVersion { get; set; } = string.Empty;
    public string ProductVersion { get; set; } = string.Empty;
    public string ExecutableSha256 { get; set; } = string.Empty;
    public string BuildFingerprint { get; set; } = string.Empty;
    public string GameAssemblySha256 { get; set; } = string.Empty;
    public string MetadataSha256 { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Architecture { get; set; } = "Unknown";
    public DateTime LastVerifiedUtc
    {
        get => _lastVerifiedUtc;
        set
        {
            if (!SetProperty(ref _lastVerifiedUtc, value)) return;
            OnPropertyChanged(nameof(VersionChoiceText));
            OnPropertyChanged(nameof(TechnicalDetails));
        }
    }
    public string PreferredSearchRoutineId { get; set; } = SearchRoutineIds.All;
    public double PreferredScaleMultiplier { get; set; } = 2d;
    public ObservableCollection<SavedField> Fields { get; set; } = [];

    [JsonIgnore]
    public bool IsCurrentBuild
    {
        get => _isCurrentBuild;
        set
        {
            if (!SetProperty(ref _isCurrentBuild, value)) return;
            OnPropertyChanged(nameof(VersionChoiceText));
        }
    }

    [JsonIgnore]
    public string VersionChoiceText => IsCurrentBuild
        ? $"当前运行版本 · {Fields.Count} 个字段"
        : $"历史版本 · {LastVerifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {Fields.Count} 个字段";

    [JsonIgnore]
    public string TechnicalDetails
    {
        get
        {
            var rawVersion = !string.IsNullOrWhiteSpace(ProductVersion)
                ? ProductVersion
                : !string.IsNullOrWhiteSpace(FileVersion) ? FileVersion : DisplayName;
            var effectiveHash = string.IsNullOrWhiteSpace(BuildFingerprint) ? ExecutableSha256 : BuildFingerprint;
            var shortHash = effectiveHash[..Math.Min(12, effectiveHash.Length)];
            return $"程序版本：{rawVersion}\n架构：{Architecture}\n构建指纹：{shortHash}";
        }
    }

    public void NotifyChoiceChanged()
    {
        OnPropertyChanged(nameof(VersionChoiceText));
        OnPropertyChanged(nameof(TechnicalDetails));
    }

    public override string ToString() => VersionChoiceText;
}

public sealed class SavedField : ObservableObject
{
    private string _name = string.Empty;
    private string _group = "未分组";
    private string _note = string.Empty;
    private string _currentValue = "—";
    private string _status = "等待连接";
    private bool _isValueLocked;
    private string _lockedValue = string.Empty;

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
    public string SearchRoutineId { get; set; } = SearchRoutineIds.DirectNumeric;
    public double ScaleMultiplier { get; set; } = 1d;
    public string AdapterId { get; set; } = string.Empty;
    public string AdapterFieldKey { get; set; } = string.Empty;
    public bool IsValueLocked
    {
        get => _isValueLocked;
        set
        {
            if (!SetProperty(ref _isValueLocked, value)) return;
            OnPropertyChanged(nameof(LockDisplay));
        }
    }
    public string LockedValue { get => _lockedValue; set => SetProperty(ref _lockedValue, value); }

    [JsonIgnore]
    public string AddressDisplay => LocatorKind == "GameAdapter"
        ? AdapterFieldKey
        : $"0x{LastAddress:X}";

    [JsonIgnore]
    public string TypeDisplay => LocatorKind == "GameAdapter"
        ? "专属适配器"
        : ValueType.ToDisplayName();

    [JsonIgnore]
    public string CurrentValue { get => _currentValue; set => SetProperty(ref _currentValue, value); }

    [JsonIgnore]
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    [JsonIgnore]
    public string LockDisplay => IsValueLocked ? "🔒" : "🔓";

    public void NotifyAddressChanged() => OnPropertyChanged(nameof(AddressDisplay));
}
