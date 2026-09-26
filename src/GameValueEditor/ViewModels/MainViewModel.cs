using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using GameValueEditor.Infrastructure;
using GameValueEditor.Models;
using GameValueEditor.Services;
using GameValueEditor.Services.Adapters;

namespace GameValueEditor.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly ProfileStore _profileStore = new();
    private readonly ProcessService _processService = new();
    private readonly VersionFingerprintService _fingerprintService = new();
    private readonly MemoryScanService _scanService = new();
    private readonly GameAdapterRegistry _adapterRegistry = new();
    private readonly ThemeService _themeService = new();
    private readonly ProcessSpeedService _speedService = new();
    private LibraryDocument _document = new();
    private List<ScanCandidate> _scanCandidates = [];
    private readonly Stack<List<ScanCandidate>> _scanHistory = new();
    private CancellationTokenSource? _liveRefreshCancellation;
    private CancellationTokenSource? _lockMaintenanceCancellation;
    private ICollectionView? _gamesView;
    private GameProfile? _selectedGame;
    private GameProfile? _selectedLibraryGame;
    private GameVersionProfile? _selectedVersion;
    private ProcessItem? _selectedProcess;
    private ProcessItem? _attachedProcess;
    private ScanCandidate? _selectedScanResult;
    private SavedField? _selectedSavedField;
    private ObservableCollection<ScanCandidate> _visibleScanResults = [];
    private readonly ObservableCollection<AdapterInventoryItem> _adapterInventoryItems = [];
    private ICollectionView? _adapterItemsView;
    private AdapterInventoryItem? _selectedAdapterItem;
    private string _adapterItemNameFilter = string.Empty;
    private string _adapterItemCountFilter = string.Empty;
    private string _searchText = string.Empty;
    private string _scanValue = string.Empty;
    private MemoryValueType? _selectedValueType;
    private ScanComparison _selectedComparison = ScanComparison.Exact;
    private SearchRoutineOption? _selectedSearchRoutine;
    private string _scaleMultiplier = "2";
    private ThemeChoice? _selectedTheme;
    private IGameAdapter? _activeAdapter;
    private VersionFingerprint? _attachedFingerprint;
    private Guid? _attachedGameId;
    private bool _writableOnly = true;
    private bool _alignedOnly = true;
    private bool _isBusy;
    private double _progressPercentage;
    private string _statusText = "请选择或刷新一个游戏进程";
    private string _connectionText = "未连接";
    private string _speedMultiplier = "2";
    private bool _isSpeedActive;
    private bool _isSpeedControlBlocked;
    private int _scanResultCount;
    private CancellationTokenSource? _scanCancellation;

    public ObservableCollection<GameProfile> Games => _document.Games;
    public ObservableCollection<ProcessItem> Processes { get; } = [];
    public IReadOnlyList<Choice<MemoryValueType?>> ValueTypes { get; } =
    [
        new(null, "所有类型"),
        new(MemoryValueType.Int32, "4 Bytes"),
        new(MemoryValueType.Int64, "8 Bytes"),
        new(MemoryValueType.Float, "Float"),
        new(MemoryValueType.Double, "Double")
    ];
    public IReadOnlyList<SearchRoutineOption> SearchRoutines { get; } =
    [
        new(SearchRoutineIds.All, "所有套路", "同时使用全部已启用的通用搜索套路。"),
        new(SearchRoutineIds.DirectNumeric, "直接数值", "界面数值与内存中的数值相同。"),
        new(SearchRoutineIds.ScaledNumeric, "按比例存储", "内存值等于界面值乘以可配置倍数。")
    ];
    public IReadOnlyList<ThemeChoice> Themes { get; } =
    [
        new(ApplicationTheme.Light, "浅色"),
        new(ApplicationTheme.Dark, "深色")
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
    public ICollectionView AdapterItemsView => _adapterItemsView ??= CreateAdapterItemsView();
    public ObservableCollection<AdapterInventoryItem> AdapterInventoryItems => _adapterInventoryItems;
    public ObservableCollection<ScanCandidate> VisibleScanResults { get => _visibleScanResults; private set => SetProperty(ref _visibleScanResults, value); }
    public GameProfile? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (!SetProperty(ref _selectedGame, value)) return;
            if (value is null)
            {
                if (_selectedLibraryGame is not null)
                {
                    _selectedLibraryGame = null;
                    OnPropertyChanged(nameof(SelectedLibraryGame));
                }
            }
            else if (IsGameVisible(value) && !ReferenceEquals(_selectedLibraryGame, value))
            {
                _selectedLibraryGame = value;
                OnPropertyChanged(nameof(SelectedLibraryGame));
            }
            SelectedVersion = value?.Versions.OrderByDescending(version => version.LastVerifiedUtc).FirstOrDefault();
            OnPropertyChanged(nameof(HasSelectedGame));
            OnPropertyChanged(nameof(ActiveGameDisplayName));
        }
    }
    public GameProfile? SelectedLibraryGame
    {
        get => _selectedLibraryGame;
        set
        {
            if (!SetProperty(ref _selectedLibraryGame, value)) return;
            if (value is not null) SelectedGame = value;
        }
    }
    public GameVersionProfile? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (!SetProperty(ref _selectedVersion, value)) return;
            SelectedSavedField = null;
            if (value is not null)
            {
                SelectedSearchRoutine = SearchRoutines.FirstOrDefault(item => item.Id == value.PreferredSearchRoutineId)
                                        ?? SearchRoutines[0];
                ScaleMultiplier = value.PreferredScaleMultiplier.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
            }
            OnPropertyChanged(nameof(SavedFields));
            OnPropertyChanged(nameof(VersionDetails));
            RestartLockMaintenance();
        }
    }
    public ObservableCollection<SavedField> SavedFields => SelectedVersion?.Fields ?? [];
    public ProcessItem? SelectedProcess { get => _selectedProcess; set => SetProperty(ref _selectedProcess, value); }
    public ProcessItem? AttachedProcess
    {
        get => _attachedProcess;
        private set
        {
            if (!SetProperty(ref _attachedProcess, value)) return;
            OnPropertyChanged(nameof(HasAttachedProcess));
            OnPropertyChanged(nameof(CanAccelerate));
            OnPropertyChanged(nameof(ActiveGameDisplayName));
        }
    }
    public ScanCandidate? SelectedScanResult { get => _selectedScanResult; set => SetProperty(ref _selectedScanResult, value); }
    public SavedField? SelectedSavedField { get => _selectedSavedField; set => SetProperty(ref _selectedSavedField, value); }
    public AdapterInventoryItem? SelectedAdapterItem { get => _selectedAdapterItem; set => SetProperty(ref _selectedAdapterItem, value); }
    public string AdapterItemNameFilter
    {
        get => _adapterItemNameFilter;
        set { if (SetProperty(ref _adapterItemNameFilter, value)) AdapterItemsView.Refresh(); }
    }
    public string AdapterItemCountFilter
    {
        get => _adapterItemCountFilter;
        set { if (SetProperty(ref _adapterItemCountFilter, value)) AdapterItemsView.Refresh(); }
    }
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            GamesView.Refresh();
            var visibleSelection = SelectedGame is not null && IsGameVisible(SelectedGame) ? SelectedGame : null;
            if (!ReferenceEquals(_selectedLibraryGame, visibleSelection))
            {
                _selectedLibraryGame = visibleSelection;
                OnPropertyChanged(nameof(SelectedLibraryGame));
            }
        }
    }
    public string ScanValue { get => _scanValue; set => SetProperty(ref _scanValue, value); }
    public MemoryValueType? SelectedValueType { get => _selectedValueType; set => SetProperty(ref _selectedValueType, value); }
    public ScanComparison SelectedComparison { get => _selectedComparison; set => SetProperty(ref _selectedComparison, value); }
    public SearchRoutineOption SelectedSearchRoutine
    {
        get => _selectedSearchRoutine ?? SearchRoutines[0];
        set => SetProperty(ref _selectedSearchRoutine, value);
    }
    public string ScaleMultiplier { get => _scaleMultiplier; set => SetProperty(ref _scaleMultiplier, value); }
    public ThemeChoice SelectedTheme
    {
        get => _selectedTheme ?? Themes[0];
        set
        {
            if (!SetProperty(ref _selectedTheme, value)) return;
            _themeService.Apply(value.Value);
            _document.Theme = value.Value.ToString();
        }
    }
    public bool WritableOnly { get => _writableOnly; set => SetProperty(ref _writableOnly, value); }
    public bool AlignedOnly { get => _alignedOnly; set => SetProperty(ref _alignedOnly, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsNotBusy));
            OnPropertyChanged(nameof(CanUndoScan));
        }
    }
    public bool IsNotBusy => !IsBusy;
    public double ProgressPercentage { get => _progressPercentage; private set => SetProperty(ref _progressPercentage, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string ConnectionText { get => _connectionText; private set => SetProperty(ref _connectionText, value); }
    public string SpeedMultiplier { get => _speedMultiplier; set => SetProperty(ref _speedMultiplier, value); }
    public bool IsSpeedActive
    {
        get => _isSpeedActive;
        private set
        {
            if (!SetProperty(ref _isSpeedActive, value)) return;
            OnPropertyChanged(nameof(CanAccelerate));
            OnPropertyChanged(nameof(CanRestoreSpeed));
            OnPropertyChanged(nameof(SpeedStatusText));
        }
    }
    public bool CanAccelerate => !_isSpeedControlBlocked;
    public bool CanRestoreSpeed => !_isSpeedControlBlocked;
    public string SpeedStatusText => IsSpeedActive ? $"{_speedService.Multiplier} 倍加速中" : "正常倍速中";
    public int ScanResultCount { get => _scanResultCount; private set => SetProperty(ref _scanResultCount, value); }
    public bool HasSelectedGame => SelectedGame is not null;
    public bool HasAttachedProcess => AttachedProcess is not null;
    public bool HasScanSession => _scanCandidates.Count > 0;
    public bool CanUndoScan => !IsBusy && _scanHistory.Count > 0;
    public bool HasActiveAdapter => _activeAdapter is not null;
    public bool HasActiveInventoryAdapter => _activeAdapter is IInventoryGameAdapter;
    public string ActiveGameDisplayName => !string.IsNullOrWhiteSpace(SelectedGame?.Name)
        ? SelectedGame.Name
        : !string.IsNullOrWhiteSpace(AttachedProcess?.WindowTitle)
            ? AttachedProcess.WindowTitle
            : !string.IsNullOrWhiteSpace(AttachedProcess?.ProcessName)
                ? AttachedProcess.ProcessName
                : "尚未选择游戏";
    public string ActiveAdapterText => _activeAdapter is null
        ? "未检测到适用于当前构建的游戏专属适配器"
        : $"已启用：{_activeAdapter.DisplayName}";
    public string VersionDetails => SelectedVersion is null
        ? "尚未选择版本"
        : $"{SelectedVersion.Architecture} · 构建 {GetVersionIdentity(SelectedVersion)[..Math.Min(12, GetVersionIdentity(SelectedVersion).Length)]}";
    public string LibraryPath => _profileStore.LibraryPath;

    public void ClearSearch() => SearchText = string.Empty;

    public IReadOnlyList<string> GetAvailableGroups() =>
        (SelectedVersion?.Fields ?? [])
        .Select(field => NormalizeGroup(field.Group))
        .Append("未分组")
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .OrderBy(group => group, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public async Task InitializeAsync()
    {
        _document = await _profileStore.LoadAsync();
        if (!Enum.TryParse<ApplicationTheme>(_document.Theme, true, out var theme)) theme = ApplicationTheme.Light;
        _selectedTheme = Themes.First(item => item.Value == theme);
        _themeService.Apply(theme);
        OnPropertyChanged(nameof(SelectedTheme));
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

    public void Attach(ProcessItem process, GameProfile? preferredGame = null)
    {
        RestoreSpeedCore();
        StopLiveCandidateRefresh();
        StopLockMaintenance();
        using var _ = new ProcessMemoryAccessor(process.ProcessId);
        _activeAdapter = null;
        _attachedFingerprint = null;
        _attachedGameId = null;
        UpdateCurrentVersionMarkers(null, null);
        OnPropertyChanged(nameof(HasActiveAdapter));
        OnPropertyChanged(nameof(HasActiveInventoryAdapter));
        OnPropertyChanged(nameof(ActiveAdapterText));
        AdapterInventoryItems.Clear();
        AttachedProcess = process;
        SelectedProcess = process;
        ConnectionText = $"已连接 · {process.ProcessName} · PID {process.ProcessId}";
        StatusText = "进程连接成功，可以开始扫描";

        var matchingGame = ResolveGameForProcess(process, preferredGame);
        if (matchingGame is not null)
        {
            _attachedGameId = matchingGame.Id;
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
        var game = SelectedGame ?? throw new InvalidOperationException("请先选择游戏条目。");
        var process = _processService.FindRunningGame(game)
                      ?? throw new InvalidOperationException("没有检测到这个游戏正在运行。");
        Attach(process, game);
        await MatchAttachedVersionAsync();
    }

    public async Task MatchAttachedVersionAsync()
    {
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        var game = _attachedGameId is Guid attachedGameId
            ? Games.FirstOrDefault(item => item.Id == attachedGameId)
            : ResolveGameForProcess(process, SelectedGame);
        if (game is null)
        {
            SelectedVersion = null;
            StatusText = "这个游戏尚未进入游戏库；扫描后可点击“保存到游戏库”";
            return;
        }

        SelectedGame = game;
        _attachedGameId = game.Id;
        var libraryChanged = await UpgradeLegacyVersionFingerprintsAsync(game);
        var fingerprint = await _fingerprintService.CreateAsync(process.ExecutablePath);
        _attachedFingerprint = fingerprint;
        UpdateCurrentVersionMarkers(game, fingerprint.BuildSha256);
        _activeAdapter = _adapterRegistry.Resolve(process, fingerprint);
        OnPropertyChanged(nameof(HasActiveAdapter));
        OnPropertyChanged(nameof(HasActiveInventoryAdapter));
        OnPropertyChanged(nameof(ActiveAdapterText));
        var version = FindMatchingVersion(game, fingerprint);
        var migratedFieldCount = 0;
        if (version is null)
        {
            var sourceVersion = game.Versions.OrderByDescending(item => item.LastVerifiedUtc).FirstOrDefault();
            version = CreateVersionProfile(fingerprint);
            if (_activeAdapter is not null && sourceVersion is not null)
            {
                foreach (var field in CloneAdapterFields(sourceVersion, _activeAdapter.Id))
                {
                    version.Fields.Add(field);
                    migratedFieldCount++;
                }
                version.PreferredSearchRoutineId = sourceVersion.PreferredSearchRoutineId;
                version.PreferredScaleMultiplier = sourceVersion.PreferredScaleMultiplier;
            }
            game.Versions.Add(version);
            libraryChanged = true;
        }
        else
        {
            ApplyFingerprint(version, fingerprint);
        }
        UpdateCurrentVersionMarkers(game, fingerprint.BuildSha256);
        SelectedVersion = version;
        version.LastVerifiedUtc = DateTime.UtcNow;
        version.NotifyChoiceChanged();
        if (!PathsEqual(game.ExecutablePath, process.ExecutablePath))
        {
            game.ExecutablePath = process.ExecutablePath;
            libraryChanged = true;
        }
        if (!string.Equals(game.ProcessName, process.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            game.ProcessName = process.ProcessName;
            libraryChanged = true;
        }
        game.LastUsedUtc = DateTime.UtcNow;
        game.NotifySummaryChanged();
        GamesView.Refresh();
        if (libraryChanged) await SaveLibraryAsync();
        StatusText = migratedFieldCount > 0
            ? $"已识别 {game.Name} 的新构建，并迁移 {migratedFieldCount} 个专属字段"
            : _activeAdapter is null
                ? $"版本匹配：{game.Name} · {version.DisplayName}"
                : $"版本匹配：{game.Name} · {version.DisplayName} · {_activeAdapter.DisplayName}";
        RestartLockMaintenance();
    }

    public async Task<GameProfile> AddCurrentProcessToLibraryAsync(string userName)
    {
        var process = AttachedProcess ?? throw new InvalidOperationException("请先连接一个游戏进程。");
        if (string.IsNullOrWhiteSpace(userName)) throw new InvalidOperationException("游戏名称不能为空。");
        var fingerprint = await _fingerprintService.CreateAsync(process.ExecutablePath);
        _attachedFingerprint = fingerprint;
        _activeAdapter = _adapterRegistry.Resolve(process, fingerprint);
        OnPropertyChanged(nameof(HasActiveAdapter));
        OnPropertyChanged(nameof(HasActiveInventoryAdapter));
        OnPropertyChanged(nameof(ActiveAdapterText));

        var game = _attachedGameId is Guid attachedGameId
            ? Games.FirstOrDefault(item => item.Id == attachedGameId)
            : ResolveGameForProcess(process, SelectedGame)
              ?? Games.FirstOrDefault(item => item.Versions.Any(version => VersionMatches(version, fingerprint)));
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
            _attachedGameId = game.Id;
        }
        else
        {
            game.ExecutablePath = process.ExecutablePath;
            game.ProcessName = process.ProcessName;
            game.LastUsedUtc = DateTime.UtcNow;
        }
        _attachedGameId = game.Id;

        var version = FindMatchingVersion(game, fingerprint);
        if (version is null)
        {
            version = CreateVersionProfile(fingerprint);
            game.Versions.Add(version);
        }
        else ApplyFingerprint(version, fingerprint);

        UpdateCurrentVersionMarkers(game, fingerprint.BuildSha256);

        SelectedGame = game;
        SelectedVersion = version;
        game.NotifySummaryChanged();
        version.NotifyChoiceChanged();
        GamesView.Refresh();
        await SaveLibraryAsync();
        RestartLockMaintenance();
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
        if (IsBusy) return;
        var process = AttachedProcess ?? throw new InvalidOperationException("请先连接游戏进程。");
        if (!isNewScan && _scanCandidates.Count == 0)
            throw new InvalidOperationException("请先执行首次扫描。");

        StopLiveCandidateRefresh();
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
            ScanRunResult result;
            if (isNewScan)
            {
                var targets = BuildInitialScanTargets();
                if (targets.Count == 0) throw new InvalidOperationException("输入值无法转换为所选类型或搜索套路。");
                var combined = new List<ScanCandidate>();
                var truncated = false;
                ulong scannedBytes = 0;

                for (var index = 0; index < targets.Count; index++)
                {
                    var target = targets[index];
                    var currentIndex = index;
                    var routineProgress = new Progress<ScanProgress>(item =>
                    {
                        ProgressPercentage = (currentIndex + item.Percentage / 100d) * 100d / targets.Count;
                        StatusText = $"扫描中 · {ProgressPercentage:F1}% · 已找到 {combined.Count + item.ResultCount:N0} 个";
                    });
                    var partial = await _scanService.InitialExactScanAsync(
                        process.ProcessId,
                        target.ValueType,
                        target.Bytes,
                        WritableOnly,
                        AlignedOnly,
                        routineProgress,
                        _scanCancellation.Token);
                    combined.AddRange(partial.Candidates.Select(candidate => new ScanCandidate
                    {
                        Address = candidate.Address,
                        FirstBytes = (candidate.FirstBytes.Length > 0 ? candidate.FirstBytes : target.Bytes).ToArray(),
                        PreviousBytes = candidate.PreviousBytes,
                        CurrentBytes = candidate.CurrentBytes,
                        ValueType = candidate.ValueType,
                        SearchRoutineId = target.Routine.Id,
                        SearchRoutineName = target.Routine.DisplayName,
                        ScaleMultiplier = target.Multiplier
                    }));
                    truncated |= partial.Truncated;
                    scannedBytes += partial.ScannedBytes;
                    if (combined.Count >= 250_000)
                    {
                        combined = combined.Take(250_000).ToList();
                        truncated = true;
                        break;
                    }
                }
                result = new ScanRunResult(combined, truncated, scannedBytes);
            }
            else
            {
                Func<ScanCandidate, byte[]?> exactTargetFactory = candidate =>
                {
                    if (SelectedComparison != ScanComparison.Exact) return null;
                    return MemoryValueCodec.TryParseEncoded(ScanValue, candidate.ValueType, candidate.ScaleMultiplier, out var bytes)
                        ? bytes
                        : null;
                };
                if (SelectedComparison == ScanComparison.Exact && _scanCandidates.Any(candidate => exactTargetFactory(candidate) is null))
                    throw new InvalidOperationException("输入值无法转换为当前扫描会话中的某些类型或套路。");
                result = await _scanService.NextScanAsync(
                    process.ProcessId,
                    _scanCandidates,
                    SelectedComparison,
                    exactTargetFactory,
                    progress,
                    _scanCancellation.Token);
            }

            if (isNewScan)
            {
                _scanHistory.Clear();
            }
            else
            {
                _scanHistory.Push(CloneCandidates(_scanCandidates));
            }
            OnPropertyChanged(nameof(CanUndoScan));
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
            StartLiveCandidateRefresh();
        }
    }

    private List<ScanTarget> BuildInitialScanTargets()
    {
        var valueTypes = SelectedValueType is { } selected
            ? [selected]
            : Enum.GetValues<MemoryValueType>().ToList();
        var routines = SelectedSearchRoutine.Id == SearchRoutineIds.All
            ? SearchRoutines.Where(item => item.Id != SearchRoutineIds.All).ToList()
            : [SelectedSearchRoutine];
        var targets = new List<ScanTarget>();

        foreach (var routine in routines)
        {
            var multiplier = 1d;
            if (routine.Id == SearchRoutineIds.ScaledNumeric)
            {
                if (!double.TryParse(ScaleMultiplier, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out multiplier) ||
                    multiplier <= 0 || double.IsNaN(multiplier) || double.IsInfinity(multiplier))
                {
                    if (SelectedSearchRoutine.Id == SearchRoutineIds.ScaledNumeric)
                        throw new InvalidOperationException("“按比例存储”的倍数必须是大于 0 的数字，例如 2、3 或 100。");
                    continue;
                }
                if (Math.Abs(multiplier - 1d) < double.Epsilon) continue;
            }

            foreach (var valueType in valueTypes)
            {
                if (MemoryValueCodec.TryParseEncoded(ScanValue, valueType, multiplier, out var bytes))
                    targets.Add(new ScanTarget(valueType, routine, multiplier, bytes));
            }
        }
        return targets;
    }

    public void CancelScan() => _scanCancellation?.Cancel();

    public void ResetScan()
    {
        StopLiveCandidateRefresh();
        _scanCancellation?.Cancel();
        _scanCandidates = [];
        _scanHistory.Clear();
        VisibleScanResults = [];
        ScanResultCount = 0;
        SelectedScanResult = null;
        StatusText = "已清除本次扫描";
        OnPropertyChanged(nameof(HasScanSession));
        OnPropertyChanged(nameof(CanUndoScan));
    }

    public void UndoScan()
    {
        if (IsBusy || _scanHistory.Count == 0) return;
        StopLiveCandidateRefresh();
        _scanCandidates = _scanHistory.Pop();
        VisibleScanResults = new ObservableCollection<ScanCandidate>(_scanCandidates.Take(50_000));
        ScanResultCount = _scanCandidates.Count;
        SelectedScanResult = VisibleScanResults.FirstOrDefault();
        StatusText = $"已撤销上一次过滤 · {_scanCandidates.Count:N0} 个候选地址";
        OnPropertyChanged(nameof(HasScanSession));
        OnPropertyChanged(nameof(CanUndoScan));
        StartLiveCandidateRefresh();
    }

    public async Task<SavedField> SaveSelectedCandidateAsync(string name, string group)
    {
        var candidate = SelectedScanResult ?? throw new InvalidOperationException("请先选择一个扫描结果。");
        var game = SelectedGame ?? throw new InvalidOperationException("请先把当前进程保存到游戏库。");
        var version = SelectedVersion ?? throw new InvalidOperationException("请先选择游戏版本。");
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        EnsureSelectedVersionMatchesAttached();
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("字段备注名称必须由玩家填写。");

        using var memory = new ProcessMemoryAccessor(process.ProcessId);
        var module = memory.FindContainingModule(candidate.Address);
        var field = new SavedField
        {
            Name = name.Trim(),
            Group = NormalizeGroup(group),
            ValueType = candidate.ValueType,
            LastAddress = candidate.Address,
            LocatorKind = module is null ? "SessionAddress" : "ModuleOffset",
            ModuleName = module?.ModuleName ?? string.Empty,
            ModuleOffset = module?.Offset ?? 0,
            ProcessStartTimeUtcTicks = process.StartTimeUtc.Ticks,
            LastVerifiedUtc = DateTime.UtcNow,
            SearchRoutineId = candidate.SearchRoutineId,
            ScaleMultiplier = candidate.ScaleMultiplier,
            CurrentValue = candidate.CurrentDisplay,
            Status = module is null ? "本次会话有效" : "模块偏移已保存"
        };
        version.Fields.Add(field);
        version.PreferredSearchRoutineId = candidate.SearchRoutineId;
        version.PreferredScaleMultiplier = candidate.ScaleMultiplier;
        SelectedSearchRoutine = SearchRoutines.First(item => item.Id == candidate.SearchRoutineId);
        ScaleMultiplier = candidate.ScaleMultiplier.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
        game.NotifySummaryChanged();
        version.NotifyChoiceChanged();
        OnPropertyChanged(nameof(SavedFields));
        SelectedSavedField = field;
        await SaveLibraryAsync();
        return field;
    }

    public async Task DeleteSelectedFieldAsync()
    {
        var field = SelectedSavedField ?? throw new InvalidOperationException("请先选择字段。");
        var game = SelectedGame ?? throw new InvalidOperationException("请先选择游戏。");
        var version = SelectedVersion ?? throw new InvalidOperationException("请先选择版本。");
        version.Fields.Remove(field);
        SelectedSavedField = version.Fields.FirstOrDefault();
        game.NotifySummaryChanged();
        version.NotifyChoiceChanged();
        await SaveLibraryAsync();
        RestartLockMaintenance();
    }

    public async Task RefreshSavedValuesAsync()
    {
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        var version = SelectedVersion ?? throw new InvalidOperationException("请先选择游戏版本。");
        EnsureSelectedVersionMatchesAttached();
        using var memory = new ProcessMemoryAccessor(process.ProcessId);

        foreach (var field in version.Fields)
        {
            if (field.LocatorKind == "GameAdapter")
            {
                var adapter = ResolveFieldAdapter(field);
                try
                {
                    var current = await Task.Run(() => adapter.ReadField(process, field.AdapterFieldKey));
                    field.CurrentValue = current.DisplayValue;
                    field.Status = current.Status;
                    field.LastVerifiedUtc = DateTime.UtcNow;
                }
                catch (Exception exception)
                {
                    field.CurrentValue = "—";
                    field.Status = exception.Message;
                }
                continue;
            }
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
                field.CurrentValue = MemoryValueCodec.FormatDecoded(bytes, field.ValueType, field.ScaleMultiplier);
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

    public Task WriteSelectedCandidateAsync(string value)
    {
        var candidate = SelectedScanResult ?? throw new InvalidOperationException("请先选择一个扫描结果。");
        return WriteCandidatesAsync([candidate], value);
    }

    public Task WriteCandidatesAsync(IReadOnlyList<ScanCandidate> candidates, string value)
    {
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        if (candidates.Count == 0) throw new InvalidOperationException("请至少选择一个扫描结果。");
        var writes = candidates.Select(candidate =>
        {
            if (!MemoryValueCodec.TryParseEncoded(value, candidate.ValueType, candidate.ScaleMultiplier, out var bytes))
                throw new InvalidOperationException($"输入值无法按 {candidate.AddressDisplay} 的类型和搜索套路进行编码。");
            return (Candidate: candidate, Bytes: bytes);
        }).ToList();

        StopLiveCandidateRefresh();
        try
        {
            using var memory = new ProcessMemoryAccessor(process.ProcessId);
            var failures = new List<string>();
            foreach (var write in writes)
            {
                if (!memory.TryWrite(write.Candidate.Address, write.Bytes, out var error))
                {
                    failures.Add($"{write.Candidate.AddressDisplay}: {error}");
                    continue;
                }
                if (!memory.TryRead(write.Candidate.Address, write.Candidate.ValueType.Size(), out var current))
                    current = write.Bytes;
                write.Candidate.CurrentBytes = current;
            }

            if (failures.Count > 0)
                throw new InvalidOperationException($"已写入 {writes.Count - failures.Count} 个地址，{failures.Count} 个失败：{string.Join("；", failures.Take(3))}");
            StatusText = writes.Count == 1
                ? $"已临时写入 {writes[0].Candidate.AddressDisplay}；确认有效后可保存为字段"
                : $"已把 {writes.Count:N0} 个候选地址的界面值批量修改为 {value.Trim()}";
        }
        finally
        {
            StartLiveCandidateRefresh();
        }
        return Task.CompletedTask;
    }

    public async Task WriteSelectedFieldAsync(string value)
    {
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        var field = SelectedSavedField ?? throw new InvalidOperationException("请先选择字段。");
        EnsureSelectedVersionMatchesAttached();
        if (field.LocatorKind == "GameAdapter")
        {
            var adapter = ResolveFieldAdapter(field);
            var updated = await Task.Run(() => adapter.WriteField(process, field.AdapterFieldKey, value));
            field.CurrentValue = updated.DisplayValue;
            field.Status = updated.Status;
            field.LastVerifiedUtc = DateTime.UtcNow;
            field.ProcessStartTimeUtcTicks = process.StartTimeUtc.Ticks;
            if (field.IsValueLocked) field.LockedValue = updated.DisplayValue;
            await SaveLibraryAsync();
            StatusText = $"已实时修改并保存 {field.Name}";
            return;
        }

        if (!MemoryValueCodec.TryParseEncoded(value, field.ValueType, field.ScaleMultiplier, out var bytes))
            throw new InvalidOperationException("输入值无法按该字段保存的搜索套路进行编码。");

        using var memory = new ProcessMemoryAccessor(process.ProcessId);
        if (!TryResolveAddress(memory, process, field, out var address))
            throw new InvalidOperationException("该字段是动态地址，游戏重启后需要重新扫描并更新位置。");
        if (!memory.TryWrite(address, bytes, out var error))
            throw new InvalidOperationException($"写入失败：{error}");
        field.LastAddress = address;
        field.CurrentValue = MemoryValueCodec.FormatDecoded(bytes, field.ValueType, field.ScaleMultiplier);
        field.Status = "刚刚写入";
        field.LastVerifiedUtc = DateTime.UtcNow;
        field.ProcessStartTimeUtcTicks = process.StartTimeUtc.Ticks;
        if (field.IsValueLocked) field.LockedValue = field.CurrentValue;
        await SaveLibraryAsync();
        StatusText = $"已修改 {field.Name}";
    }

    public async Task<SavedField> AddAdapterFieldAsync(string fieldKey, string displayName, string group)
    {
        var adapter = _activeAdapter ?? throw new InvalidOperationException("当前游戏构建没有可用的专属适配器。");
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        var game = SelectedGame ?? throw new InvalidOperationException("请先把当前进程保存到游戏库。");
        var version = SelectedVersion ?? throw new InvalidOperationException("请先保存并选择当前游戏版本。");
        EnsureSelectedVersionMatchesAttached();
        if (string.IsNullOrWhiteSpace(fieldKey)) throw new InvalidOperationException("请填写游戏内的精确字段键，例如物品名“赤阳花”。");
        if (string.IsNullOrWhiteSpace(displayName)) throw new InvalidOperationException("字段备注名称必须由玩家填写。");

        var current = await Task.Run(() => adapter.ReadField(process, fieldKey.Trim()));
        var field = new SavedField
        {
            Name = displayName.Trim(),
            Group = NormalizeGroup(group),
            ValueType = MemoryValueType.Int32,
            LocatorKind = "GameAdapter",
            AdapterId = adapter.Id,
            AdapterFieldKey = current.FieldKey,
            ProcessStartTimeUtcTicks = process.StartTimeUtc.Ticks,
            LastVerifiedUtc = DateTime.UtcNow,
            CurrentValue = current.DisplayValue,
            Status = current.Status
        };
        version.Fields.Add(field);
        game.NotifySummaryChanged();
        version.NotifyChoiceChanged();
        OnPropertyChanged(nameof(SavedFields));
        SelectedSavedField = field;
        await SaveLibraryAsync();
        StatusText = $"已通过专属适配器保存字段 {field.Name}";
        return field;
    }

    public async Task RefreshAdapterInventoryAsync()
    {
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        if (_activeAdapter is not IInventoryGameAdapter adapter)
            throw new InvalidOperationException("当前游戏构建没有可用的背包专属修改器。");

        StatusText = "正在读取游戏背包…";
        var items = await Task.Run(() => adapter.ReadInventory(process));
        _adapterInventoryItems.Clear();
        foreach (var item in items) _adapterInventoryItems.Add(item);
        AdapterItemsView.Refresh();
        SelectedAdapterItem = _adapterInventoryItems.FirstOrDefault();
        StatusText = $"已读取 {_adapterInventoryItems.Count:N0} 种背包物品";
    }

    public async Task WriteSelectedAdapterItemAsync(string value)
    {
        var item = SelectedAdapterItem ?? throw new InvalidOperationException("请先选择一个背包物品。");
        await WriteAdapterItemsAsync([item], value);
    }

    public async Task WriteAdapterItemsAsync(IReadOnlyList<AdapterInventoryItem> items, string value)
    {
        if (items.Count == 0) throw new InvalidOperationException("请至少选择一个背包物品。");
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        var adapter = _activeAdapter ?? throw new InvalidOperationException("当前游戏构建没有可用的专属适配器。");
        var updated = await Task.Run(() => items.Select(item => adapter.WriteField(process, item.FieldKey, value)).ToList());
        await RefreshAdapterInventoryAsync();
        SelectedAdapterItem = _adapterInventoryItems.FirstOrDefault(candidate =>
            string.Equals(candidate.FieldKey, items[0].FieldKey, StringComparison.Ordinal));
        StatusText = items.Count == 1
            ? $"已实时修改并保存 {items[0].DisplayName} = {updated[0].DisplayValue}"
            : $"已把 {items.Count:N0} 种物品的物品总数批量修改并保存为 {value.Trim()}";
    }

    public async Task AccelerateGameAsync()
    {
        var cooldown = BeginSpeedControlInteraction();
        try
        {
            var process = AttachedProcess ?? throw new InvalidOperationException("请先连接游戏进程。");
            if (!int.TryParse(SpeedMultiplier.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var multiplier) ||
                multiplier is < 1 or > 100)
                throw new InvalidOperationException("加速倍数必须是 1 到 100 的整数。");
            if (multiplier == 1)
            {
                await Task.Run(_speedService.Normalize);
                IsSpeedActive = false;
                StatusText = "1 倍就是正常游戏速度";
                return;
            }
            var result = await Task.Run(() => _speedService.Accelerate(process.ProcessId, multiplier));
            IsSpeedActive = true;
            StatusText = $"游戏已切换为 {multiplier} 倍速度 · 已挂接 {result.PatchedImportCount} 个计时入口";
        }
        finally
        {
            ReleaseSpeedControlAfter(cooldown);
        }
    }

    public async Task RestoreGameSpeedAsync()
    {
        var cooldown = BeginSpeedControlInteraction();
        try
        {
            _ = AttachedProcess ?? throw new InvalidOperationException("请先连接游戏进程。");
            if (!_speedService.HasHooks)
            {
                IsSpeedActive = false;
                StatusText = "当前已经是正常倍速";
                return;
            }
            await Task.Run(_speedService.Normalize);
            IsSpeedActive = false;
            StatusText = "游戏速度已回正";
        }
        finally
        {
            ReleaseSpeedControlAfter(cooldown);
        }
    }

    private Task BeginSpeedControlInteraction()
    {
        if (_isSpeedControlBlocked) throw new InvalidOperationException("倍速正在切换，请稍候再试。");
        _isSpeedControlBlocked = true;
        OnPropertyChanged(nameof(CanAccelerate));
        OnPropertyChanged(nameof(CanRestoreSpeed));
        return Task.Delay(TimeSpan.FromSeconds(2));
    }

    private async void ReleaseSpeedControlAfter(Task cooldown)
    {
        try
        {
            await cooldown;
        }
        finally
        {
            _isSpeedControlBlocked = false;
            OnPropertyChanged(nameof(CanAccelerate));
            OnPropertyChanged(nameof(CanRestoreSpeed));
        }
    }

    public async Task RenameSelectedFieldAsync(string name)
    {
        var field = SelectedSavedField ?? throw new InvalidOperationException("请先选择字段。");
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("备注名称不能为空。");
        field.Name = name.Trim();
        await SaveLibraryAsync();
        StatusText = "已修改字段备注名称";
    }

    public async Task ChangeSelectedFieldGroupAsync(string group)
    {
        var field = SelectedSavedField ?? throw new InvalidOperationException("请先选择字段。");
        field.Group = NormalizeGroup(group);
        await SaveLibraryAsync();
        StatusText = "已修改字段分组";
    }

    public async Task UpdateSelectedFieldAsync(string name, string group, string value)
    {
        var field = SelectedSavedField ?? throw new InvalidOperationException("请先选择字段。");
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("备注名称不能为空。");
        field.Name = name.Trim();
        field.Group = NormalizeGroup(group);
        if (!string.IsNullOrWhiteSpace(value) && !string.Equals(value.Trim(), field.CurrentValue, StringComparison.Ordinal))
            await WriteSelectedFieldAsync(value.Trim());
        else
            await SaveLibraryAsync();
        StatusText = $"已更新字段 {field.Name}";
    }

    public async Task ToggleSelectedFieldValueLockAsync()
    {
        var field = SelectedSavedField ?? throw new InvalidOperationException("请先选择字段。");
        if (!field.IsValueLocked)
        {
            if (string.IsNullOrWhiteSpace(field.CurrentValue) || field.CurrentValue == "—")
                throw new InvalidOperationException("请先刷新或修改字段数值，再启用锁定。");
            field.LockedValue = field.CurrentValue;
            field.IsValueLocked = true;
            StatusText = $"已锁定 {field.Name} = {field.LockedValue}";
        }
        else
        {
            field.IsValueLocked = false;
            field.LockedValue = string.Empty;
            StatusText = $"已解除 {field.Name} 的数值锁定";
        }
        await SaveLibraryAsync();
        RestartLockMaintenance();
    }

    public async Task ChangeThemeAsync(ThemeChoice choice)
    {
        SelectedTheme = choice;
        await SaveLibraryAsync();
    }

    private IGameAdapter ResolveFieldAdapter(SavedField field)
    {
        var adapter = _activeAdapter;
        if (adapter is null || !string.Equals(adapter.Id, field.AdapterId, StringComparison.Ordinal))
            throw new InvalidOperationException("当前进程或游戏构建与该字段保存的专属适配器不匹配。");
        return adapter;
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
        view.Filter = item => item is GameProfile game && IsGameVisible(game);
        if (view is ListCollectionView listView) listView.CustomSort = new GameProfileComparer();
        return view;
    }

    private bool IsGameVisible(GameProfile game) =>
        string.IsNullOrWhiteSpace(SearchText) ||
        game.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ||
        game.ProcessName.Contains(SearchText, StringComparison.OrdinalIgnoreCase);

    private GameProfile? ResolveGameForProcess(ProcessItem process, GameProfile? preferredGame)
    {
        if (preferredGame is not null && Games.Contains(preferredGame) &&
            (PathsEqual(preferredGame.ExecutablePath, process.ExecutablePath) ||
             string.Equals(preferredGame.ProcessName, process.ProcessName, StringComparison.OrdinalIgnoreCase)))
            return preferredGame;

        var pathMatch = Games.FirstOrDefault(game => PathsEqual(game.ExecutablePath, process.ExecutablePath));
        if (pathMatch is not null) return pathMatch;

        var processNameMatches = Games
            .Where(game => string.Equals(game.ProcessName, process.ProcessName, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return processNameMatches.Count == 1 ? processNameMatches[0] : null;
    }

    private async Task<bool> UpgradeLegacyVersionFingerprintsAsync(GameProfile game)
    {
        var legacyVersions = game.Versions.Where(version => string.IsNullOrWhiteSpace(version.BuildFingerprint)).ToList();
        if (legacyVersions.Count == 0 || !File.Exists(game.ExecutablePath)) return false;
        try
        {
            var savedBuild = await _fingerprintService.CreateAsync(game.ExecutablePath);
            var changed = false;
            foreach (var version in legacyVersions.Where(version =>
                         string.Equals(version.ExecutableSha256, savedBuild.Sha256, StringComparison.OrdinalIgnoreCase)))
            {
                ApplyFingerprint(version, savedBuild);
                changed = true;
            }
            return changed;
        }
        catch
        {
            // A moved or partially replaced installation must not prevent connecting to the running build.
            return false;
        }
    }

    private static GameVersionProfile? FindMatchingVersion(GameProfile game, VersionFingerprint fingerprint) =>
        game.Versions.FirstOrDefault(version => VersionMatches(version, fingerprint));

    private static bool VersionMatches(GameVersionProfile version, VersionFingerprint fingerprint)
    {
        if (!string.IsNullOrWhiteSpace(version.BuildFingerprint))
            return string.Equals(version.BuildFingerprint, fingerprint.BuildSha256, StringComparison.OrdinalIgnoreCase);

        // Legacy libraries only knew the EXE hash. That is sufficient for ordinary native games, but not for
        // IL2CPP games whose executable can stay unchanged while GameAssembly and metadata are replaced.
        return string.IsNullOrWhiteSpace(fingerprint.GameAssemblySha256) &&
               string.IsNullOrWhiteSpace(fingerprint.MetadataSha256) &&
               string.Equals(version.ExecutableSha256, fingerprint.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static GameVersionProfile CreateVersionProfile(VersionFingerprint fingerprint)
    {
        var version = new GameVersionProfile();
        ApplyFingerprint(version, fingerprint);
        return version;
    }

    private static void ApplyFingerprint(GameVersionProfile version, VersionFingerprint fingerprint)
    {
        version.DisplayName = fingerprint.DisplayName;
        version.FileVersion = fingerprint.FileVersion;
        version.ProductVersion = fingerprint.ProductVersion;
        version.ExecutableSha256 = fingerprint.Sha256;
        version.BuildFingerprint = fingerprint.BuildSha256;
        version.GameAssemblySha256 = fingerprint.GameAssemblySha256;
        version.MetadataSha256 = fingerprint.MetadataSha256;
        version.FileSize = fingerprint.FileSize;
        version.Architecture = fingerprint.Architecture;
        version.LastVerifiedUtc = DateTime.UtcNow;
    }

    private static IEnumerable<SavedField> CloneAdapterFields(GameVersionProfile source, string adapterId) =>
        source.Fields
            .Where(field => field.LocatorKind == "GameAdapter" &&
                            string.Equals(field.AdapterId, adapterId, StringComparison.Ordinal))
            .Select(field => new SavedField
            {
                Name = field.Name,
                Group = field.Group,
                Note = field.Note,
                ValueType = field.ValueType,
                LocatorKind = "GameAdapter",
                SearchRoutineId = field.SearchRoutineId,
                ScaleMultiplier = field.ScaleMultiplier,
                AdapterId = field.AdapterId,
                AdapterFieldKey = field.AdapterFieldKey,
                LastVerifiedUtc = DateTime.UtcNow,
                CurrentValue = "—",
                Status = "已迁移，等待刷新",
                IsValueLocked = false,
                LockedValue = string.Empty
            });

    private static string GetVersionIdentity(GameVersionProfile version) =>
        string.IsNullOrWhiteSpace(version.BuildFingerprint) ? version.ExecutableSha256 : version.BuildFingerprint;

    private void UpdateCurrentVersionMarkers(GameProfile? currentGame, string? buildFingerprint)
    {
        foreach (var game in Games)
        foreach (var version in game.Versions)
            version.IsCurrentBuild = ReferenceEquals(game, currentGame) &&
                                     !string.IsNullOrWhiteSpace(buildFingerprint) &&
                                     string.Equals(GetVersionIdentity(version), buildFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    private void RestoreSpeedCore()
    {
        _speedService.Restore();
        IsSpeedActive = false;
    }

    private ICollectionView CreateAdapterItemsView()
    {
        var view = CollectionViewSource.GetDefaultView(_adapterInventoryItems);
        view.Filter = item =>
        {
            if (item is not AdapterInventoryItem inventoryItem) return false;
            if (!string.IsNullOrWhiteSpace(AdapterItemNameFilter) &&
                !inventoryItem.DisplayName.Contains(AdapterItemNameFilter.Trim(), StringComparison.CurrentCultureIgnoreCase))
                return false;
            if (string.IsNullOrWhiteSpace(AdapterItemCountFilter)) return true;
            return long.TryParse(AdapterItemCountFilter.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) &&
                   inventoryItem.Count == count;
        };
        return view;
    }

    private static string NormalizeGroup(string? group) =>
        string.IsNullOrWhiteSpace(group) ? "未分组" : group.Trim();

    private static List<ScanCandidate> CloneCandidates(IEnumerable<ScanCandidate> candidates) =>
        candidates.Select(candidate => new ScanCandidate
        {
            Address = candidate.Address,
            FirstBytes = candidate.FirstBytes.ToArray(),
            PreviousBytes = candidate.PreviousBytes.ToArray(),
            CurrentBytes = candidate.CurrentBytes.ToArray(),
            ValueType = candidate.ValueType,
            SearchRoutineId = candidate.SearchRoutineId,
            SearchRoutineName = candidate.SearchRoutineName,
            ScaleMultiplier = candidate.ScaleMultiplier
        }).ToList();

    private void StartLiveCandidateRefresh()
    {
        StopLiveCandidateRefresh();
        if (IsBusy || AttachedProcess is null || _scanCandidates.Count is <= 0 or >= 100) return;
        var process = AttachedProcess;
        var candidates = _scanCandidates.ToList();
        var cancellation = new CancellationTokenSource();
        _liveRefreshCancellation = cancellation;
        _ = Task.Run(async () =>
        {
            try
            {
                using var memory = new ProcessMemoryAccessor(process.ProcessId);
                while (!cancellation.IsCancellationRequested)
                {
                    foreach (var candidate in candidates)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        if (!memory.TryRead(candidate.Address, candidate.ValueType.Size(), out var bytes)) continue;
                        await RunOnUiAsync(() => candidate.CurrentBytes = bytes);
                    }
                    await Task.Delay(500, cancellation.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                // The game may close or replace its process while live refresh is active.
            }
        });
    }

    private void StopLiveCandidateRefresh()
    {
        _liveRefreshCancellation?.Cancel();
        _liveRefreshCancellation?.Dispose();
        _liveRefreshCancellation = null;
    }

    private void RestartLockMaintenance()
    {
        StopLockMaintenance();
        if (AttachedProcess is null || SelectedVersion?.Fields.Any(field => field.IsValueLocked) != true) return;
        if (SelectedGame is null || _attachedGameId != SelectedGame.Id ||
            _attachedFingerprint is null ||
            !VersionMatches(SelectedVersion, _attachedFingerprint)) return;
        var process = AttachedProcess;
        var version = SelectedVersion;
        var cancellation = new CancellationTokenSource();
        _lockMaintenanceCancellation = cancellation;
        _ = Task.Run(() => MaintainLockedValuesAsync(process, version, cancellation.Token));
    }

    private void StopLockMaintenance()
    {
        _lockMaintenanceCancellation?.Cancel();
        _lockMaintenanceCancellation?.Dispose();
        _lockMaintenanceCancellation = null;
    }

    private async Task MaintainLockedValuesAsync(
        ProcessItem process,
        GameVersionProfile version,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IReadOnlyList<SavedField> lockedFields = [];
                await RunOnUiAsync(() => lockedFields = version.Fields.Where(field => field.IsValueLocked).ToList());
                if (lockedFields.Count == 0) return;

                foreach (var field in lockedFields)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (field.LocatorKind == "GameAdapter")
                        {
                            var adapter = ResolveFieldAdapter(field);
                            var current = adapter.ReadField(process, field.AdapterFieldKey);
                            var final = current;
                            if (!string.Equals(current.DisplayValue, field.LockedValue, StringComparison.Ordinal))
                                final = adapter.WriteField(process, field.AdapterFieldKey, field.LockedValue);
                            await RunOnUiAsync(() =>
                            {
                                field.CurrentValue = final.DisplayValue;
                                field.Status = $"锁定中 · {final.Status}";
                                field.LastVerifiedUtc = DateTime.UtcNow;
                            });
                            continue;
                        }

                        if (!MemoryValueCodec.TryParseEncoded(field.LockedValue, field.ValueType, field.ScaleMultiplier, out var expected))
                            throw new InvalidOperationException("锁定目标值无效。");
                        using var memory = new ProcessMemoryAccessor(process.ProcessId);
                        if (!TryResolveAddress(memory, process, field, out var address))
                            throw new InvalidOperationException("动态地址需要重新定位。");
                        if (!memory.TryRead(address, expected.Length, out var currentBytes))
                            throw new InvalidOperationException("读取失败。");
                        if (!currentBytes.AsSpan().SequenceEqual(expected) && !memory.TryWrite(address, expected, out var error))
                            throw new InvalidOperationException($"写入失败：{error}");
                        await RunOnUiAsync(() =>
                        {
                            field.CurrentValue = MemoryValueCodec.FormatDecoded(expected, field.ValueType, field.ScaleMultiplier);
                            field.Status = "锁定中";
                            field.LastVerifiedUtc = DateTime.UtcNow;
                        });
                    }
                    catch (Exception exception)
                    {
                        await RunOnUiAsync(() => field.Status = $"锁定失败：{exception.Message}");
                    }
                }
                await Task.Delay(750, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task RunOnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }
        await dispatcher.InvokeAsync(action);
    }

    private void EnsureSelectedVersionMatchesAttached()
    {
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        var game = SelectedGame ?? throw new InvalidOperationException("请先选择当前游戏。");
        var version = SelectedVersion ?? throw new InvalidOperationException("请先选择当前游戏版本。");
        if (_attachedGameId != game.Id || _attachedFingerprint is null || !VersionMatches(version, _attachedFingerprint))
            throw new InvalidOperationException("当前选择的游戏版本与已连接进程不匹配，请重新连接或选择匹配版本。");
    }

    public void Shutdown()
    {
        _scanCancellation?.Cancel();
        StopLiveCandidateRefresh();
        StopLockMaintenance();
        RestoreSpeedCore();
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

    private sealed record ScanTarget(
        MemoryValueType ValueType,
        SearchRoutineOption Routine,
        double Multiplier,
        byte[] Bytes);
}

public sealed record Choice<T>(T Value, string Display);
