using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using GameValueEditor.Infrastructure;
using GameValueEditor.Models;
using GameValueEditor.Services;

namespace GameValueEditor.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly ProfileStore _profileStore = new();
    private readonly ProcessService _processService = new();
    private readonly VersionFingerprintService _fingerprintService = new();
    private readonly MemoryScanService _scanService = new();
    private LibraryDocument _document = new();
    private List<ScanCandidate> _scanCandidates = [];
    private ICollectionView? _gamesView;
    private GameProfile? _selectedGame;
    private GameVersionProfile? _selectedVersion;
    private ProcessItem? _selectedProcess;
    private ProcessItem? _attachedProcess;
    private ScanCandidate? _selectedScanResult;
    private SavedField? _selectedSavedField;
    private ObservableCollection<ScanCandidate> _visibleScanResults = [];
    private string _searchText = string.Empty;
    private string _scanValue = string.Empty;
    private MemoryValueType _selectedValueType = MemoryValueType.Int32;
    private ScanComparison _selectedComparison = ScanComparison.Exact;
    private bool _writableOnly = true;
    private bool _alignedOnly = true;
    private bool _isBusy;
    private double _progressPercentage;
    private string _statusText = "请选择或刷新一个游戏进程";
    private string _connectionText = "未连接";
    private int _scanResultCount;
    private CancellationTokenSource? _scanCancellation;

    public ObservableCollection<GameProfile> Games => _document.Games;
    public ObservableCollection<ProcessItem> Processes { get; } = [];
    public IReadOnlyList<Choice<MemoryValueType>> ValueTypes { get; } =
    [
        new(MemoryValueType.Int32, "4 Bytes"),
        new(MemoryValueType.Int64, "8 Bytes"),
        new(MemoryValueType.Float, "Float"),
        new(MemoryValueType.Double, "Double")
    ];
    public IReadOnlyList<Choice<ScanComparison>> Comparisons { get; } =
    [
        new(ScanComparison.Exact, "精确值"),
        new(ScanComparison.Changed, "已改变"),
        new(ScanComparison.Unchanged, "未改变"),
        new(ScanComparison.Increased, "增加了"),
        new(ScanComparison.Decreased, "减少了")
    ];

    public ICollectionView GamesView => _gamesView ??= CreateGamesView();
    public ObservableCollection<ScanCandidate> VisibleScanResults { get => _visibleScanResults; private set => SetProperty(ref _visibleScanResults, value); }
    public GameProfile? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (!SetProperty(ref _selectedGame, value)) return;
            SelectedVersion = value?.Versions.OrderByDescending(version => version.LastVerifiedUtc).FirstOrDefault();
            OnPropertyChanged(nameof(HasSelectedGame));
        }
    }
    public GameVersionProfile? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (!SetProperty(ref _selectedVersion, value)) return;
            SelectedSavedField = null;
            OnPropertyChanged(nameof(SavedFields));
            OnPropertyChanged(nameof(VersionDetails));
        }
    }
    public ObservableCollection<SavedField> SavedFields => SelectedVersion?.Fields ?? [];
    public ProcessItem? SelectedProcess { get => _selectedProcess; set => SetProperty(ref _selectedProcess, value); }
    public ProcessItem? AttachedProcess { get => _attachedProcess; private set => SetProperty(ref _attachedProcess, value); }
    public ScanCandidate? SelectedScanResult { get => _selectedScanResult; set => SetProperty(ref _selectedScanResult, value); }
    public SavedField? SelectedSavedField { get => _selectedSavedField; set => SetProperty(ref _selectedSavedField, value); }
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) GamesView.Refresh(); } }
    public string ScanValue { get => _scanValue; set => SetProperty(ref _scanValue, value); }
    public MemoryValueType SelectedValueType { get => _selectedValueType; set => SetProperty(ref _selectedValueType, value); }
    public ScanComparison SelectedComparison { get => _selectedComparison; set => SetProperty(ref _selectedComparison, value); }
    public bool WritableOnly { get => _writableOnly; set => SetProperty(ref _writableOnly, value); }
    public bool AlignedOnly { get => _alignedOnly; set => SetProperty(ref _alignedOnly, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public double ProgressPercentage { get => _progressPercentage; private set => SetProperty(ref _progressPercentage, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string ConnectionText { get => _connectionText; private set => SetProperty(ref _connectionText, value); }
    public int ScanResultCount { get => _scanResultCount; private set => SetProperty(ref _scanResultCount, value); }
    public bool HasSelectedGame => SelectedGame is not null;
    public bool HasScanSession => _scanCandidates.Count > 0;
    public string VersionDetails => SelectedVersion is null
        ? "尚未选择版本"
        : $"{SelectedVersion.Architecture} · SHA-256 {SelectedVersion.ExecutableSha256[..Math.Min(12, SelectedVersion.ExecutableSha256.Length)]}";
    public string LibraryPath => _profileStore.LibraryPath;

    public async Task InitializeAsync()
    {
        _document = await _profileStore.LoadAsync();
        _gamesView = null;
        OnPropertyChanged(nameof(Games));
        OnPropertyChanged(nameof(GamesView));
        GamesView.Refresh();
        RefreshProcesses();
        SelectedGame = Games.OrderByDescending(game => game.IsPinned).ThenByDescending(game => game.LastUsedUtc).FirstOrDefault();
    }

    public void RefreshProcesses()
    {
        var previousId = SelectedProcess?.ProcessId;
        Processes.Clear();
        foreach (var process in _processService.GetProcesses()) Processes.Add(process);
        SelectedProcess = Processes.FirstOrDefault(process => process.ProcessId == previousId)
                          ?? Processes.FirstOrDefault();
        StatusText = $"发现 {Processes.Count} 个可访问进程";
    }

    public void Attach(ProcessItem process)
    {
        using var _ = new ProcessMemoryAccessor(process.ProcessId);
        AttachedProcess = process;
        SelectedProcess = process;
        ConnectionText = $"已连接 · {process.ProcessName} · PID {process.ProcessId}";
        StatusText = "进程连接成功，可以开始扫描";

        var matchingGame = Games.FirstOrDefault(game => PathsEqual(game.ExecutablePath, process.ExecutablePath));
        if (matchingGame is not null)
        {
            SelectedGame = matchingGame;
            matchingGame.LastUsedUtc = DateTime.UtcNow;
            GamesView.Refresh();
        }
        else
        {
            SelectedGame = null;
            SelectedVersion = null;
        }
    }

    public async Task AttachSelectedGameAsync()
    {
        if (SelectedGame is null) throw new InvalidOperationException("请先选择游戏条目。");
        var process = _processService.FindRunningGame(SelectedGame)
                      ?? throw new InvalidOperationException("没有检测到这个游戏正在运行。");
        Attach(process);
        await MatchAttachedVersionAsync();
    }

    public async Task MatchAttachedVersionAsync()
    {
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        var game = Games.FirstOrDefault(item => PathsEqual(item.ExecutablePath, process.ExecutablePath));
        if (game is null)
        {
            SelectedVersion = null;
            StatusText = "这个游戏尚未进入游戏库；扫描后可点击“保存到游戏库”";
            return;
        }

        SelectedGame = game;
        var fingerprint = await _fingerprintService.CreateAsync(process.ExecutablePath);
        var version = game.Versions.FirstOrDefault(item => item.ExecutableSha256 == fingerprint.Sha256);
        SelectedVersion = version;
        if (version is null)
        {
            StatusText = $"检测到未保存的游戏构建 {fingerprint.DisplayName}；旧版本字段不会自动套用";
        }
        else
        {
            version.LastVerifiedUtc = DateTime.UtcNow;
            StatusText = $"版本匹配：{game.Name} · {version.DisplayName}";
        }
    }

    public async Task<GameProfile> AddCurrentProcessToLibraryAsync(string userName)
    {
        var process = AttachedProcess ?? throw new InvalidOperationException("请先连接一个游戏进程。");
        if (string.IsNullOrWhiteSpace(userName)) throw new InvalidOperationException("游戏名称不能为空。");
        var fingerprint = await _fingerprintService.CreateAsync(process.ExecutablePath);

        var game = Games.FirstOrDefault(item => PathsEqual(item.ExecutablePath, process.ExecutablePath))
                   ?? Games.FirstOrDefault(item => item.Versions.Any(version => version.ExecutableSha256 == fingerprint.Sha256));
        if (game is null)
        {
            game = new GameProfile
            {
                Name = userName.Trim(),
                ExecutablePath = process.ExecutablePath,
                ProcessName = process.ProcessName,
                LastUsedUtc = DateTime.UtcNow
            };
            Games.Add(game);
        }
        else
        {
            game.ExecutablePath = process.ExecutablePath;
            game.ProcessName = process.ProcessName;
            game.LastUsedUtc = DateTime.UtcNow;
        }

        var version = game.Versions.FirstOrDefault(item => item.ExecutableSha256 == fingerprint.Sha256);
        if (version is null)
        {
            version = new GameVersionProfile
            {
                DisplayName = fingerprint.DisplayName,
                FileVersion = fingerprint.FileVersion,
                ProductVersion = fingerprint.ProductVersion,
                ExecutableSha256 = fingerprint.Sha256,
                FileSize = fingerprint.FileSize,
                Architecture = fingerprint.Architecture,
                LastVerifiedUtc = DateTime.UtcNow
            };
            game.Versions.Add(version);
        }

        SelectedGame = game;
        SelectedVersion = version;
        game.NotifySummaryChanged();
        GamesView.Refresh();
        await SaveLibraryAsync();
        StatusText = $"已保存 {game.Name} · {version.DisplayName}";
        return game;
    }

    public async Task TogglePinnedAsync()
    {
        if (SelectedGame is null) return;
        SelectedGame.IsPinned = !SelectedGame.IsPinned;
        GamesView.Refresh();
        await SaveLibraryAsync();
    }

    public async Task ToggleLockedAsync()
    {
        if (SelectedGame is null) return;
        SelectedGame.IsLocked = !SelectedGame.IsLocked;
        await SaveLibraryAsync();
    }

    public async Task DeleteSelectedGameAsync()
    {
        var game = SelectedGame ?? throw new InvalidOperationException("请先选择游戏条目。");
        if (game.IsPinned) throw new InvalidOperationException("置顶游戏不能删除，请先取消置顶。");
        if (game.IsLocked) throw new InvalidOperationException("锁定游戏不能删除，请先解锁。");
        Games.Remove(game);
        SelectedGame = Games.FirstOrDefault();
        await SaveLibraryAsync();
    }

    public async Task RenameSelectedGameAsync(string name)
    {
        var game = SelectedGame ?? throw new InvalidOperationException("请先选择游戏条目。");
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("游戏名称不能为空。");
        game.Name = name.Trim();
        await SaveLibraryAsync();
    }

    public async Task RunScanAsync(bool isNewScan)
    {
        var process = AttachedProcess ?? throw new InvalidOperationException("请先连接游戏进程。");
        byte[]? target = null;
        if (isNewScan || SelectedComparison == ScanComparison.Exact)
        {
            if (!MemoryValueCodec.TryParse(ScanValue, SelectedValueType, out target))
                throw new InvalidOperationException("输入的数值与当前数据类型不匹配。");
        }
        if (!isNewScan && _scanCandidates.Count == 0)
            throw new InvalidOperationException("请先执行首次扫描。");

        _scanCancellation?.Cancel();
        _scanCancellation = new CancellationTokenSource();
        IsBusy = true;
        ProgressPercentage = 0;
        StatusText = isNewScan ? "正在扫描可读内存区域…" : "正在过滤候选地址…";
        var progress = new Progress<ScanProgress>(item =>
        {
            ProgressPercentage = item.Percentage;
            StatusText = $"扫描中 · {item.Percentage:F1}% · 已找到 {item.ResultCount:N0} 个";
        });

        try
        {
            var result = isNewScan
                ? await _scanService.InitialExactScanAsync(process.ProcessId, SelectedValueType, target!, WritableOnly, AlignedOnly, progress, _scanCancellation.Token)
                : await _scanService.NextScanAsync(process.ProcessId, _scanCandidates, SelectedValueType, SelectedComparison, target, progress, _scanCancellation.Token);

            _scanCandidates = result.Candidates.ToList();
            VisibleScanResults = new ObservableCollection<ScanCandidate>(_scanCandidates.Take(50_000));
            ScanResultCount = _scanCandidates.Count;
            SelectedScanResult = VisibleScanResults.FirstOrDefault();
            OnPropertyChanged(nameof(HasScanSession));
            StatusText = result.Truncated
                ? $"结果超过上限，保留前 {result.Candidates.Count:N0} 个；请继续改变游戏数值并过滤"
                : $"扫描完成 · {_scanCandidates.Count:N0} 个候选地址";
        }
        catch (OperationCanceledException)
        {
            StatusText = "扫描已取消";
        }
        finally
        {
            IsBusy = false;
            ProgressPercentage = 0;
        }
    }

    public void CancelScan() => _scanCancellation?.Cancel();

    public void ResetScan()
    {
        _scanCancellation?.Cancel();
        _scanCandidates = [];
        VisibleScanResults = [];
        ScanResultCount = 0;
        SelectedScanResult = null;
        StatusText = "已清除本次扫描";
        OnPropertyChanged(nameof(HasScanSession));
    }

    public async Task<SavedField> SaveSelectedCandidateAsync(string name, string group, string note)
    {
        var candidate = SelectedScanResult ?? throw new InvalidOperationException("请先选择一个扫描结果。");
        var game = SelectedGame ?? throw new InvalidOperationException("请先把当前进程保存到游戏库。");
        var version = SelectedVersion ?? throw new InvalidOperationException("请先选择游戏版本。");
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("字段名称必须由玩家填写。");

        using var memory = new ProcessMemoryAccessor(process.ProcessId);
        var module = memory.FindContainingModule(candidate.Address);
        var field = new SavedField
        {
            Name = name.Trim(),
            Group = string.IsNullOrWhiteSpace(group) ? "未分组" : group.Trim(),
            Note = note.Trim(),
            ValueType = candidate.ValueType,
            LastAddress = candidate.Address,
            LocatorKind = module is null ? "SessionAddress" : "ModuleOffset",
            ModuleName = module?.ModuleName ?? string.Empty,
            ModuleOffset = module?.Offset ?? 0,
            ProcessStartTimeUtcTicks = process.StartTimeUtc.Ticks,
            LastVerifiedUtc = DateTime.UtcNow,
            CurrentValue = candidate.CurrentDisplay,
            Status = module is null ? "本次会话有效" : "模块偏移已保存"
        };
        version.Fields.Add(field);
        game.NotifySummaryChanged();
        OnPropertyChanged(nameof(SavedFields));
        SelectedSavedField = field;
        await SaveLibraryAsync();
        return field;
    }

    public async Task RelocateSelectedFieldAsync()
    {
        var field = SelectedSavedField ?? throw new InvalidOperationException("请先选择已保存字段。");
        var candidate = SelectedScanResult ?? throw new InvalidOperationException("请先选择新的扫描结果。");
        _ = SelectedGame ?? throw new InvalidOperationException("请先选择游戏。");
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");

        using var memory = new ProcessMemoryAccessor(process.ProcessId);
        var module = memory.FindContainingModule(candidate.Address);
        field.ValueType = candidate.ValueType;
        field.LastAddress = candidate.Address;
        field.LocatorKind = module is null ? "SessionAddress" : "ModuleOffset";
        field.ModuleName = module?.ModuleName ?? string.Empty;
        field.ModuleOffset = module?.Offset ?? 0;
        field.ProcessStartTimeUtcTicks = process.StartTimeUtc.Ticks;
        field.LastVerifiedUtc = DateTime.UtcNow;
        field.CurrentValue = candidate.CurrentDisplay;
        field.Status = "位置已更新";
        field.NotifyAddressChanged();
        await SaveLibraryAsync();
    }

    public async Task DeleteSelectedFieldAsync()
    {
        var field = SelectedSavedField ?? throw new InvalidOperationException("请先选择字段。");
        var game = SelectedGame ?? throw new InvalidOperationException("请先选择游戏。");
        var version = SelectedVersion ?? throw new InvalidOperationException("请先选择版本。");
        version.Fields.Remove(field);
        SelectedSavedField = version.Fields.FirstOrDefault();
        game.NotifySummaryChanged();
        await SaveLibraryAsync();
    }

    public void RefreshSavedValues()
    {
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        var version = SelectedVersion ?? throw new InvalidOperationException("请先选择游戏版本。");
        using var memory = new ProcessMemoryAccessor(process.ProcessId);

        foreach (var field in version.Fields)
        {
            if (!TryResolveAddress(memory, process, field, out var address))
            {
                field.CurrentValue = "—";
                field.Status = "需要重新扫描定位";
                continue;
            }
            field.LastAddress = address;
            field.NotifyAddressChanged();
            if (memory.TryRead(address, field.ValueType.Size(), out var bytes))
            {
                field.CurrentValue = MemoryValueCodec.Format(bytes, field.ValueType);
                field.Status = "可用";
            }
            else
            {
                field.CurrentValue = "—";
                field.Status = "读取失败";
            }
        }
        StatusText = "已刷新保存字段";
    }

    public async Task WriteSelectedFieldAsync(string value)
    {
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        var field = SelectedSavedField ?? throw new InvalidOperationException("请先选择字段。");
        if (!MemoryValueCodec.TryParse(value, field.ValueType, out var bytes))
            throw new InvalidOperationException("输入的值与字段数据类型不匹配。");

        using var memory = new ProcessMemoryAccessor(process.ProcessId);
        if (!TryResolveAddress(memory, process, field, out var address))
            throw new InvalidOperationException("该字段是动态地址，游戏重启后需要重新扫描并更新位置。");
        if (!memory.TryWrite(address, bytes, out var error))
            throw new InvalidOperationException($"写入失败：{error}");
        field.LastAddress = address;
        field.CurrentValue = MemoryValueCodec.Format(bytes, field.ValueType);
        field.Status = "刚刚写入";
        field.LastVerifiedUtc = DateTime.UtcNow;
        field.ProcessStartTimeUtcTicks = process.StartTimeUtc.Ticks;
        await SaveLibraryAsync();
        StatusText = $"已修改 {field.Name}";
    }

    private static bool TryResolveAddress(ProcessMemoryAccessor memory, ProcessItem process, SavedField field, out ulong address)
    {
        if (field.LocatorKind == "ModuleOffset" && memory.TryGetModuleBase(field.ModuleName, out var moduleBase))
        {
            address = unchecked((ulong)((long)moduleBase + field.ModuleOffset));
            return true;
        }
        if (field.ProcessStartTimeUtcTicks == process.StartTimeUtc.Ticks)
        {
            address = field.LastAddress;
            return true;
        }
        address = 0;
        return false;
    }

    private async Task SaveLibraryAsync() => await _profileStore.SaveAsync(_document);

    private ICollectionView CreateGamesView()
    {
        var view = CollectionViewSource.GetDefaultView(Games);
        view.Filter = item => item is GameProfile game &&
                              (string.IsNullOrWhiteSpace(SearchText) ||
                               game.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ||
                               game.ProcessName.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        if (view is ListCollectionView listView) listView.CustomSort = new GameProfileComparer();
        return view;
    }

    private static bool PathsEqual(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
    }

    private sealed class GameProfileComparer : System.Collections.IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not GameProfile left || y is not GameProfile right) return 0;
            var pinned = right.IsPinned.CompareTo(left.IsPinned);
            if (pinned != 0) return pinned;
            var used = right.LastUsedUtc.CompareTo(left.LastUsedUtc);
            return used != 0 ? used : string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
        }
    }
}

public sealed record Choice<T>(T Value, string Display);
