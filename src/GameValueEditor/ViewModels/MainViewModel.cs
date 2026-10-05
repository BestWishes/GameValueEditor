using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameValueEditor.Infrastructure;
using GameValueEditor.Models;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Services;
using GameValueEditor.Services.Adapters;

namespace GameValueEditor.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const int ScanResultPreviewLimit = 1_000;

    private static readonly Lazy<ImageSource?> DefaultGameIconSource = new(() =>
    {
        try
        {
            var image = new BitmapImage(new Uri(
                "pack://application:,,,/GameValueEditor;component/Assets/GameValueEditorIcon.png",
                UriKind.Absolute));
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    });
    private readonly ProfileStore _profileStore;
    private readonly ProcessService _processService;
    private readonly VersionFingerprintService _fingerprintService;
    private readonly MemoryScanService _scanService;
    private readonly GameAdapterRegistry _adapterRegistry;
    private readonly ThemeService _themeService;
    private readonly ProcessSpeedService _idleSpeedService;
    private ProcessSpeedService _speedService;
    private readonly GameIconService _gameIconService;
    private readonly GameModuleCatalogService _moduleCatalogService;
    private readonly ApplicationUpdateService _applicationUpdateService;
    private IGameEditorHostServices? _editorHostServices;
    private readonly Dictionary<Guid, GameConnectionSession> _sessions = [];
    private GameConnectionSession? _activeSession;
    private LibraryDocument _document = new();
    private ScanCandidateStore? _scanCandidates;
    private Stack<ScanCandidateStore> _scanHistory = new();
    private CancellationTokenSource? _liveRefreshCancellation;
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
    private readonly ObservableCollection<AdapterCharacterItem> _adapterCharacters = [];
    private ICollectionView? _adapterItemsView;
    private ICollectionView? _characterAttributesView;
    private AdapterInventoryItem? _selectedAdapterItem;
    private AdapterCharacterItem? _selectedAdapterCharacter;
    private AdapterCharacterAttribute? _selectedCharacterAttribute;
    private readonly ObservableCollection<AdapterEditorPageState> _adapterEditorPages = [];
    private AdapterCharacterEditorPageState? _characterEditorPage;
    private AdapterEditorPageState? _selectedAdapterEditorPage;
    private string _adapterItemNameFilter = string.Empty;
    private string _adapterItemCountFilter = string.Empty;
    private string _characterAttributeNameFilter = string.Empty;
    private string _searchText = string.Empty;
    private string _scanValue = string.Empty;
    private MemoryValueType? _selectedValueType;
    private ScanComparison _selectedComparison = ScanComparison.Exact;
    private SearchRoutineOption? _selectedSearchRoutine;
    private string _scaleMultiplier = "2";
    private ThemeChoice? _selectedTheme;
    private Choice<GameLibrarySortMode>? _selectedLibrarySort;
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
    private bool _isProcessRefreshBlocked;
    private bool _isConnectionControlBlocked;
    private bool _isLibraryControlBlocked;
    private long _scanResultCount;
    private CancellationTokenSource? _scanCancellation;
    private bool _suppressGameActivation;
    private string _moduleStatusText = "尚未检查当前游戏的专属模块";
    private bool _isModuleControlBlocked;
    private GameModuleCheckResult? _moduleCheckResult;
    private string _applicationUpdateStatusText = string.Empty;
    private string _applicationUpdateActionText = "检查更新";
    private bool _isApplicationUpdateBusy;
    private bool _isApplicationUpdateCheckCooldown;
    private ApplicationUpdateCheckResult? _applicationUpdateResult;
    private bool _applicationUpdateDownloaded;
    private bool _isOfficialWebsiteControlBlocked;

    public MainViewModel(ApplicationUpdateService? applicationUpdateService = null)
        : this(MainViewModelServices.CreateDefault(applicationUpdateService)) { }

    public MainViewModel(MainViewModelServices services)
    {
        _profileStore = services.ProfileStore;
        _processService = services.ProcessService;
        _fingerprintService = services.FingerprintService;
        _scanService = services.ScanService;
        _adapterRegistry = services.AdapterRegistry;
        _themeService = services.ThemeService;
        _idleSpeedService = services.IdleSpeedService;
        _speedService = _idleSpeedService;
        _gameIconService = services.GameIconService;
        _moduleCatalogService = services.ModuleCatalogService;
        _applicationUpdateService = services.ApplicationUpdateService;
    }

    internal void SetEditorHostServices(IGameEditorHostServices services) =>
        _editorHostServices = services ?? throw new ArgumentNullException(nameof(services));

    internal void ReportModulePageStatus(string message) => StatusText = message;

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
        new(ApplicationTheme.Dark, "深色"),
        new(ApplicationTheme.EyeCareGreen, "护眼墨绿"),
        new(ApplicationTheme.WarmSand, "暖砂纸张"),
        new(ApplicationTheme.MistBlue, "雾蓝灰")
    ];
    public IReadOnlyList<Choice<GameLibrarySortMode>> LibrarySortChoices { get; } =
    [
        new(GameLibrarySortMode.Connection, "连接状态"),
        new(GameLibrarySortMode.Name, "名称"),
        new(GameLibrarySortMode.LocalModule, "本地专属模块")
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
    public ICollectionView? CharacterAttributesView => _characterAttributesView;
    public ObservableCollection<AdapterInventoryItem> AdapterInventoryItems => _adapterInventoryItems;
    public ObservableCollection<AdapterCharacterItem> AdapterCharacters => _adapterCharacters;
    public ObservableCollection<AdapterEditorPageState> AdapterEditorPages => _adapterEditorPages;
    public AdapterEditorPageState? SelectedAdapterEditorPage
    {
        get => _selectedAdapterEditorPage;
        set => SetProperty(ref _selectedAdapterEditorPage, value);
    }
    public ObservableCollection<ScanCandidate> VisibleScanResults { get => _visibleScanResults; private set => SetProperty(ref _visibleScanResults, value); }
    public GameProfile? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (ReferenceEquals(_selectedGame, value)) return;
            if (!_suppressGameActivation) CaptureActiveSession();
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
            if (!_suppressGameActivation) ActivateSelectedGameSession(value);
            OnPropertyChanged(nameof(HasSelectedGame));
            OnPropertyChanged(nameof(ActiveGameDisplayName));
            OnPropertyChanged(nameof(ActiveGameIcon));
            OnPropertyChanged(nameof(HasCharacterEditor));
            OnPropertyChanged(nameof(HasActiveCharacterAdapter));
            OnPropertyChanged(nameof(HasUnsupportedCharacterAdapter));
            NotifyLibraryControls();
            NotifyConnectionControls();
            ResetModuleCheckState();
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
            ResetModuleCheckState();
            RestartLockMaintenance();
        }
    }
    public ObservableCollection<SavedField> SavedFields => SelectedVersion?.Fields ?? [];
    public ProcessItem? SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            if (SetProperty(ref _selectedProcess, value)) NotifyConnectionControls();
        }
    }
    public ProcessItem? AttachedProcess
    {
        get => _attachedProcess;
        private set
        {
            if (!SetProperty(ref _attachedProcess, value)) return;
            OnPropertyChanged(nameof(HasAttachedProcess));
            OnPropertyChanged(nameof(CanAccelerate));
            OnPropertyChanged(nameof(CanRestoreSpeed));
            OnPropertyChanged(nameof(ActiveGameDisplayName));
            OnPropertyChanged(nameof(ActiveGameIcon));
            NotifyLibraryControls();
            NotifyConnectionControls();
            NotifyModuleControls();
        }
    }
    public ScanCandidate? SelectedScanResult { get => _selectedScanResult; set => SetProperty(ref _selectedScanResult, value); }
    public SavedField? SelectedSavedField { get => _selectedSavedField; set => SetProperty(ref _selectedSavedField, value); }
    public AdapterInventoryItem? SelectedAdapterItem { get => _selectedAdapterItem; set => SetProperty(ref _selectedAdapterItem, value); }
    public AdapterCharacterItem? SelectedAdapterCharacter
    {
        get => _selectedAdapterCharacter;
        set
        {
            if (!SetProperty(ref _selectedAdapterCharacter, value)) return;
            RebuildCharacterAttributesView(value);
        }
    }
    public AdapterCharacterAttribute? SelectedCharacterAttribute
    {
        get => _selectedCharacterAttribute;
        set
        {
            if (!SetProperty(ref _selectedCharacterAttribute, value)) return;
            OnPropertyChanged(nameof(IsSelectedCharacterFieldSessionOnly));
        }
    }
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
    public string CharacterAttributeNameFilter
    {
        get => _characterAttributeNameFilter;
        set
        {
            if (!SetProperty(ref _characterAttributeNameFilter, value)) return;
            _characterAttributesView?.Refresh();
            if (SelectedCharacterAttribute is null || !MatchesCharacterAttributeFilter(SelectedCharacterAttribute))
                SelectedCharacterAttribute = SelectedAdapterCharacter?.Attributes.FirstOrDefault(MatchesCharacterAttributeFilter);
        }
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
    public Choice<GameLibrarySortMode> SelectedLibrarySort
    {
        get => _selectedLibrarySort ?? LibrarySortChoices[0];
        set
        {
            if (!SetProperty(ref _selectedLibrarySort, value)) return;
            ApplyGameLibrarySort();
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
    public bool CanAccelerate => !_isSpeedControlBlocked && AttachedProcess is not null;
    public bool CanRestoreSpeed => !_isSpeedControlBlocked && AttachedProcess is not null;
    public string SpeedStatusText => _speedService.Multiplier switch
    {
        < 1d => $"{FormatSpeedMultiplier(_speedService.Multiplier)} 倍减速中",
        > 1d => $"{FormatSpeedMultiplier(_speedService.Multiplier)} 倍加速中",
        _ => "正常倍速中"
    };
    public long ScanResultCount { get => _scanResultCount; private set => SetProperty(ref _scanResultCount, value); }
    public bool HasSelectedGame => SelectedGame is not null;
    public bool HasAttachedProcess => AttachedProcess is not null;
    public string LibraryStatusText => SelectedGame is null ? "未入库" : "已入库";
    public bool CanRefreshProcesses => !_isProcessRefreshBlocked;
    public bool CanConnectProcess => !_isConnectionControlBlocked && SelectedProcess is not null &&
        (_activeSession is null || !_activeSession.ProcessGroup.Contains(SelectedProcess));
    public bool CanConnectSelectedGame => !_isConnectionControlBlocked && SelectedGame is not null &&
        !_sessions.ContainsKey(SelectedGame.Id);
    public bool CanDisconnectSelectedGame => !_isConnectionControlBlocked && SelectedGame is not null &&
        _sessions.ContainsKey(SelectedGame.Id);
    public bool CanDisconnectCurrentProcess => !_isConnectionControlBlocked && AttachedProcess is not null && _activeSession is not null;
    public bool CanSaveCurrentGame => !_isLibraryControlBlocked && AttachedProcess is not null;
    public bool CanRemoveCurrentGame => !_isLibraryControlBlocked && SelectedGame is { IsPinned: false, IsLocked: false };
    public bool HasScanSession => _scanCandidates is { Count: > 0 };
    public bool CanUndoScan => !IsBusy && _scanHistory.Count > 0;
    public bool HasActiveAdapter => _activeAdapter is not null;
    public bool HasActiveInventoryAdapter => AdapterEditorPages.OfType<AdapterInventoryEditorPageState>().Any();
    public bool HasCharacterEditor => _characterEditorPage is not null;
    public bool HasActiveCharacterAdapter => _characterEditorPage?.IsSupported == true;
    public bool HasUnsupportedCharacterAdapter => _characterEditorPage?.IsUnsupported == true;
    public bool IsCharacterEditorSessionOnly => ActiveCharacterEditorDescriptor?.SessionOnly == true;
    public bool IsSelectedCharacterFieldSessionOnly => GetSelectedCharacterFieldPolicy().SessionOnly;
    public string ActiveCharacterEditorId => ActiveCharacterEditorDescriptor?.Id ?? string.Empty;
    public bool HasNoActiveAdapter => _activeAdapter is null;
    public string ActiveGameDisplayName => !string.IsNullOrWhiteSpace(SelectedGame?.Name)
        ? SelectedGame.Name
        : !string.IsNullOrWhiteSpace(_activeSession?.ProcessGroup.RootProcess.WindowTitle)
            ? _activeSession.ProcessGroup.RootProcess.WindowTitle
            : !string.IsNullOrWhiteSpace(AttachedProcess?.WindowTitle)
                ? AttachedProcess.WindowTitle
            : !string.IsNullOrWhiteSpace(AttachedProcess?.ProcessName)
                ? AttachedProcess.ProcessName
                : "尚未选择游戏";
    public string ActiveAdapterText => _activeAdapter is null
        ? "本地未装配当前游戏和版本的专属修改模块"
        : $"已启用：{_activeAdapter.DisplayName}";
    public string VersionDetails => SelectedVersion is null
        ? "尚未选择版本"
        : $"{SelectedVersion.Architecture} · 构建 {GetVersionIdentity(SelectedVersion)[..Math.Min(12, GetVersionIdentity(SelectedVersion).Length)]}";
    public string LibraryPath => _profileStore.LibraryPath;
    private static ImageSource? DefaultGameIcon => DefaultGameIconSource.Value;
    public ImageSource? ActiveGameIcon => SelectedGame?.IconSource ??
                                          _activeSession?.ProcessGroup.RootProcess.Icon ??
                                          AttachedProcess?.Icon ?? DefaultGameIcon;
    public string CurrentApplicationVersion => ApplicationVersion.Current;
    public string ModuleStatusText { get => _moduleStatusText; private set => SetProperty(ref _moduleStatusText, value); }
    public bool CanCheckGameModules => !_isModuleControlBlocked &&
        ((AttachedProcess is not null && _attachedFingerprint is not null) ||
         (SelectedGame is not null && SelectedVersion is not null));
    public bool CanInstallGameModule => !_isModuleControlBlocked && _moduleCheckResult?.Availability is
        GameModuleAvailability.Available or GameModuleAvailability.UpdateAvailable;
    public string ModuleInstallActionText => _moduleCheckResult?.Availability == GameModuleAvailability.Available
        ? "可下载"
        : "可更新";
    public bool CanUninstallGameModule => !_isModuleControlBlocked &&
        !string.IsNullOrWhiteSpace(ResolveActiveModuleId()) &&
        _moduleCatalogService.FindInstalled(ResolveActiveModuleId()) is not null;
    public bool CanViewModuleCompatibilityDiagnostics => !_isModuleControlBlocked &&
        ((AttachedProcess is not null && _attachedFingerprint is not null) || SelectedVersion is not null);
    public bool CanViewModuleContributors => !_isModuleControlBlocked && GetModuleContributors().Count > 0;
    public string ApplicationUpdateStatusText
    {
        get => _applicationUpdateStatusText;
        private set => SetProperty(ref _applicationUpdateStatusText, value);
    }
    public string ApplicationUpdateActionText
    {
        get => _applicationUpdateActionText;
        private set => SetProperty(ref _applicationUpdateActionText, value);
    }
    public bool CanUseApplicationUpdate => !_isApplicationUpdateBusy && !_applicationUpdateDownloaded &&
        (HasApplicationUpdateAvailable || !_isApplicationUpdateCheckCooldown);
    public bool HasApplicationUpdateAvailable => _applicationUpdateResult?.IsUpdateAvailable == true && !_applicationUpdateDownloaded;
    public bool IsApplicationUpdateDownloaded => _applicationUpdateDownloaded;
    public bool CanOpenOfficialWebsite => !_isOfficialWebsiteControlBlocked;

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
        foreach (var game in _document.Games)
        {
            game.IsConnected = false;
            game.IconSource = _gameIconService.Load(game.IconFileName) ?? DefaultGameIcon;
        }
        var libraryChanged = ReconcileInstalledModulesWithLibrary();
        if (!Enum.TryParse<ApplicationTheme>(_document.Theme, true, out var theme) || !Enum.IsDefined(theme))
            theme = ApplicationTheme.Light;
        _selectedTheme = Themes.First(item => item.Value == theme);
        _themeService.Apply(theme);
        OnPropertyChanged(nameof(SelectedTheme));
        _gamesView = null;
        OnPropertyChanged(nameof(Games));
        OnPropertyChanged(nameof(GamesView));
        GamesView.Refresh();
        if (libraryChanged) await SaveLibraryAsync();
        RefreshProcesses();
        SelectedGame = Games.OrderByDescending(game => game.IsPinned).ThenByDescending(game => game.LastUsedUtc).FirstOrDefault();
    }

    public void RefreshProcesses()
    {
        SynchronizeConnectionStates();
        var previousId = SelectedProcess?.ProcessId;
        Processes.Clear();
        foreach (var process in _processService.GetProcesses()) Processes.Add(process);
        SelectedProcess = Processes.FirstOrDefault(process => process.ProcessId == previousId)
                          ?? Processes.FirstOrDefault();
        StatusText = $"发现 {Processes.Count} 个可访问进程";
    }

    public int SynchronizeConnectionStates()
    {
        var sessions = _sessions.Values
            .Append(_activeSession)
            .Where(session => session is not null)
            .Cast<GameConnectionSession>()
            .Distinct()
            .ToList();
        var staleSessions = sessions
            .Where(session => !IsProcessRunning(session.ProcessGroup.RootProcess))
            .ToList();
        var rebindCandidates = sessions
            .Except(staleSessions)
            .Where(session => !IsProcessRunning(session.Process))
            .ToList();

        var reboundCount = 0;
        if (rebindCandidates.Count > 0)
        {
            var snapshot = _processService.GetProcesses();
            foreach (var session in rebindCandidates)
            {
                var refreshedRoot = snapshot.FirstOrDefault(item =>
                    item.ProcessId == session.ProcessGroup.RootProcess.ProcessId &&
                    item.StartTimeUtc == session.ProcessGroup.RootProcess.StartTimeUtc);
                if (refreshedRoot is null)
                {
                    staleSessions.Add(session);
                    continue;
                }

                var refreshedGroup = _processService.ResolveLogicalGame(refreshedRoot, snapshot);
                if (refreshedGroup.RuntimeKind is GameRuntimeKind.Electron or GameRuntimeKind.NwJs &&
                    refreshedGroup.DataProcess.Role != GameProcessRole.Renderer)
                {
                    // Chromium may briefly have no renderer while reloading. Keep trying on the
                    // next liveness tick instead of binding scans to the browser main process.
                    if (ReferenceEquals(_activeSession, session))
                        StatusText = "游戏正在重建数据进程，肝肾大圣将自动重新连接";
                    continue;
                }
                RebindSession(session, refreshedGroup);
                reboundCount++;
            }
        }

        if (staleSessions.Count == 0) return reboundCount;

        var activeStaleSession = staleSessions.FirstOrDefault(session => ReferenceEquals(_activeSession, session));
        var activeProcessName = activeStaleSession?.Process.ProcessName;
        foreach (var session in staleSessions)
            DisconnectSession(session, ReferenceEquals(_activeSession, session));

        var staleProcessKeys = staleSessions
            .SelectMany(session => session.ProcessGroup.Members)
            .Select(process => (process.ProcessId, process.StartTimeUtc))
            .ToHashSet();
        foreach (var process in Processes
                     .Where(process => staleProcessKeys.Contains((process.ProcessId, process.StartTimeUtc)))
                     .ToList())
            Processes.Remove(process);
        if (SelectedProcess is not null &&
            staleProcessKeys.Contains((SelectedProcess.ProcessId, SelectedProcess.StartTimeUtc)))
            SelectedProcess = Processes.FirstOrDefault();

        StatusText = !string.IsNullOrWhiteSpace(activeProcessName)
            ? $"游戏进程 {activeProcessName} 已退出，已自动断开连接"
            : $"检测到 {staleSessions.Count} 个游戏进程已退出，已同步连接状态";
        return staleSessions.Count + reboundCount;
    }

    public async Task RefreshProcessesWithCooldownAsync()
    {
        if (_isProcessRefreshBlocked) throw new InvalidOperationException("正在刷新进程，请稍候再试。");
        _isProcessRefreshBlocked = true;
        OnPropertyChanged(nameof(CanRefreshProcesses));
        var cooldown = Task.Delay(TimeSpan.FromSeconds(2));
        try { RefreshProcesses(); }
        finally
        {
            await cooldown;
            _isProcessRefreshBlocked = false;
            OnPropertyChanged(nameof(CanRefreshProcesses));
        }
    }

    public void Attach(ProcessItem process, GameProfile? preferredGame = null)
    {
        CaptureActiveSession();
        StopLiveCandidateRefresh();
        var snapshot = _processService.GetProcesses();
        var group = _processService.ResolveLogicalGame(process, snapshot);
        var dataProcess = group.DataProcess;
        if (_activeSession is { GameId: null } transient && !transient.ProcessGroup.IsSameInstance(group))
            DisconnectSession(transient, false);
        using var _ = new ProcessMemoryAccessor(dataProcess.ProcessId);
        var matchingGame = ResolveGameForProcess(dataProcess, preferredGame);
        if (matchingGame is not null && _sessions.TryGetValue(matchingGame.Id, out var previous) &&
            !previous.ProcessGroup.IsSameInstance(group))
            DisconnectSession(previous, false);

        var session = matchingGame is not null && _sessions.TryGetValue(matchingGame.Id, out var existing) &&
                      existing.ProcessGroup.IsSameInstance(group)
            ? existing
            : new GameConnectionSession(group, matchingGame?.Id);
        if (matchingGame is not null)
        {
            _sessions[matchingGame.Id] = session;
            matchingGame.IsConnected = true;
        }
        _activeSession = session;

        _suppressGameActivation = true;
        try { SelectedGame = matchingGame; }
        finally { _suppressGameActivation = false; }
        RestoreSessionIntoView(session, matchingGame);
        SelectedProcess = process;
        ConnectionText = BuildConnectionText(session);
        StatusText = group.Members.Count > 1
            ? $"已识别 {group.Members.Count} 个关联进程，自动使用{dataProcess.Role.DisplayName()} PID {dataProcess.ProcessId}"
            : "进程连接成功，可以开始通用扫描";

        if (matchingGame is not null)
        {
            _attachedGameId = matchingGame.Id;
            matchingGame.LastUsedUtc = DateTime.UtcNow;
            GamesView.Refresh();
        }
        else
        {
            SelectedVersion = null;
        }
    }

    public async Task AttachSelectedProcessAsync()
    {
        var process = SelectedProcess ?? throw new InvalidOperationException("请先从顶部选择一个进程。");
        if (!CanConnectProcess) throw new InvalidOperationException("当前进程已经连接，或连接操作尚未完成。");
        var cooldown = BeginConnectionControlInteraction();
        try
        {
            Attach(process);
            await MatchAttachedVersionAsync();
        }
        finally { ReleaseConnectionControlsAfter(cooldown); }
    }

    public async Task AttachSelectedGameAsync()
    {
        var game = SelectedGame ?? throw new InvalidOperationException("请先选择游戏条目。");
        if (!CanConnectSelectedGame) throw new InvalidOperationException("所选游戏已经连接，或连接操作尚未完成。");
        var cooldown = BeginConnectionControlInteraction();
        try
        {
            var process = _processService.FindRunningGame(game)
                          ?? throw new InvalidOperationException("没有检测到这个游戏正在运行。");
            Attach(process, game);
            await MatchAttachedVersionAsync();
        }
        finally { ReleaseConnectionControlsAfter(cooldown); }
    }

    public async Task DisconnectSelectedGameAsync()
    {
        var game = SelectedGame ?? throw new InvalidOperationException("请先选择游戏条目。");
        if (!_sessions.TryGetValue(game.Id, out var session))
            throw new InvalidOperationException("所选游戏当前未连接。");
        var cooldown = BeginConnectionControlInteraction();
        try
        {
            DisconnectSession(session, true);
            StatusText = $"已断开 {game.Name} 的连接";
        }
        finally { ReleaseConnectionControlsAfter(cooldown); }
        await Task.CompletedTask;
    }

    public async Task DisconnectCurrentProcessAsync()
    {
        var session = _activeSession ?? throw new InvalidOperationException("当前游戏尚未连接。");
        var displayName = ActiveGameDisplayName;
        var cooldown = BeginConnectionControlInteraction();
        try
        {
            DisconnectSession(session, true);
            StatusText = $"已断开 {displayName} 的连接";
        }
        finally { ReleaseConnectionControlsAfter(cooldown); }
        await Task.CompletedTask;
    }

    public async Task MatchAttachedVersionAsync()
    {
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        var fingerprint = await _fingerprintService.CreateAsync(process.ExecutablePath);
        _attachedFingerprint = fingerprint;
        if (_activeSession is not null) _activeSession.Fingerprint = fingerprint;
        SetActiveAdapter(_adapterRegistry.Resolve(process, fingerprint));
        var game = _attachedGameId is Guid attachedGameId
            ? Games.FirstOrDefault(item => item.Id == attachedGameId)
            : ResolveGameForProcess(process, SelectedGame);
        if (game is null)
        {
            SelectedVersion = null;
            ResetModuleCheckState();
            StatusText = _activeAdapter is null
                ? "这个游戏尚未进入游戏库；可使用通用扫描或点击“检查新有”"
                : $"已加载 {_activeAdapter.DisplayName}；下载模块后会自动保存入库";
            return;
        }

        _suppressGameActivation = true;
        try { SelectedGame = game; }
        finally { _suppressGameActivation = false; }
        _attachedGameId = game.Id;
        var libraryChanged = await UpgradeLegacyVersionFingerprintsAsync(game);
        UpdateCurrentVersionMarkers(game, fingerprint.BuildSha256);
        var version = FindMatchingVersion(game, fingerprint);
        var migratedFieldCount = 0;
        if (version is null)
        {
            version = CreateVersionProfile(fingerprint);
            game.Versions.Add(version);
            libraryChanged = true;
        }
        else
        {
            ApplyFingerprint(version, fingerprint);
        }
        libraryChanged |= ApplyGameDeclaredMetadata(version, process);
        if (_activeAdapter is not null)
        {
            migratedFieldCount += MigrateAdapterFields(game, version, _activeAdapter.Id);
            libraryChanged |= migratedFieldCount > 0;
        }
        UpdateCurrentVersionMarkers(game, fingerprint.BuildSha256);
        SelectedVersion = version;
        if (_activeSession is not null) _activeSession.VersionId = version.Id;
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
        if (string.IsNullOrWhiteSpace(userName)) throw new InvalidOperationException("备注名称不能为空。");
        var cooldown = BeginLibraryControlInteraction();
        try { return await AddCurrentProcessToLibraryCoreAsync(userName, process); }
        finally { ReleaseLibraryControlsAfter(cooldown); }
    }

    private async Task<GameProfile> AddCurrentProcessToLibraryCoreAsync(string userName, ProcessItem process)
    {
        var fingerprint = await _fingerprintService.CreateAsync(process.ExecutablePath);
        _attachedFingerprint = fingerprint;
        SetActiveAdapter(_adapterRegistry.Resolve(process, fingerprint));
        if (_activeSession is not null)
        {
            _activeSession.Fingerprint = fingerprint;
            _activeSession.Adapter = _activeAdapter;
        }

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
        game.Name = userName.Trim();
        if (_activeAdapter is not null)
        {
            game.ModuleId = _activeAdapter.Id;
            game.IsModuleInstalled = _moduleCatalogService.FindInstalled(_activeAdapter.Id) is not null;
        }
        _attachedGameId = game.Id;
        game.IsConnected = true;
        _gameIconService.Save(game, process.Icon);
        game.IconSource ??= DefaultGameIcon;
        if (_activeSession is not null)
        {
            _activeSession.GameId = game.Id;
            _sessions[game.Id] = _activeSession;
        }

        var version = FindMatchingVersion(game, fingerprint);
        if (version is null)
        {
            version = CreateVersionProfile(fingerprint);
            game.Versions.Add(version);
        }
        else ApplyFingerprint(version, fingerprint);
        ApplyGameDeclaredMetadata(version, process);
        if (_activeAdapter is not null) MigrateAdapterFields(game, version, _activeAdapter.Id);

        UpdateCurrentVersionMarkers(game, fingerprint.BuildSha256);

        SelectedGame = game;
        SelectedVersion = version;
        if (_activeSession is not null) _activeSession.VersionId = version.Id;
        OnPropertyChanged(nameof(ActiveGameIcon));
        game.NotifySummaryChanged();
        version.NotifyChoiceChanged();
        GamesView.Refresh();
        await SaveLibraryAsync();
        RestartLockMaintenance();
        StatusText = $"已保存入库：{game.Name} · {version.DisplayName}";
        return game;
    }

    public async Task TogglePinnedAsync()
    {
        if (SelectedGame is null) return;
        SelectedGame.IsPinned = !SelectedGame.IsPinned;
        GamesView.Refresh();
        NotifyLibraryControls();
        await SaveLibraryAsync();
    }

    public async Task ToggleLockedAsync()
    {
        if (SelectedGame is null) return;
        SelectedGame.IsLocked = !SelectedGame.IsLocked;
        NotifyLibraryControls();
        await SaveLibraryAsync();
    }

    public async Task DeleteSelectedGameAsync()
    {
        var game = SelectedGame ?? throw new InvalidOperationException("请先选择游戏条目。");
        if (game.IsPinned) throw new InvalidOperationException("置顶游戏不能从库移出，请先取消置顶。");
        if (game.IsLocked) throw new InvalidOperationException("锁定游戏不能从库移出，请先解锁。");
        var cooldown = BeginLibraryControlInteraction();
        try
        {
            var removedModule = false;
            var moduleFilesDeleted = true;
            if (game.IsModuleInstalled && !string.IsNullOrWhiteSpace(game.ModuleId))
            {
                if (_isModuleControlBlocked) throw new InvalidOperationException("专属模块正在更新，请稍候再试。");
                _isModuleControlBlocked = true;
                NotifyModuleControls();
                try
                {
                    moduleFilesDeleted = await RemoveInstalledModuleCoreAsync(game.ModuleId);
                    removedModule = true;
                }
                finally
                {
                    _isModuleControlBlocked = false;
                    NotifyModuleControls();
                }
            }
            var keptConnection = _sessions.Remove(game.Id, out var session);
            if (keptConnection && session is not null)
            {
                StopSessionLockMaintenance(session);
                session.GameId = null;
                session.VersionId = null;
                game.IsConnected = false;
                _activeSession = session;
                _speedService = session.SpeedService;
                _attachedGameId = null;
                _suppressGameActivation = true;
                try { SelectedGame = null; }
                finally { _suppressGameActivation = false; }
                SelectedVersion = null;
            }

            Games.Remove(game);
            if (!keptConnection) SelectedGame = Games.FirstOrDefault();
            GamesView.Refresh();
            await SaveLibraryAsync();
            var moduleSuffix = !removedModule
                ? string.Empty
                : moduleFilesDeleted
                    ? "及其专属模块"
                    : "及其专属模块（残留文件将在下次启动清理）";
            StatusText = keptConnection
                ? $"已将 {game.Name}{moduleSuffix}从本地移出，当前进程保持连接"
                : $"已将 {game.Name}{moduleSuffix}从游戏库移出";
        }
        finally { ReleaseLibraryControlsAfter(cooldown); }
    }

    public async Task RenameSelectedGameAsync(string name)
    {
        var game = SelectedGame ?? throw new InvalidOperationException("请先选择游戏条目。");
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("备注名称不能为空。");
        var cooldown = BeginLibraryControlInteraction();
        try
        {
            game.Name = name.Trim();
            await SaveLibraryAsync();
            OnPropertyChanged(nameof(ActiveGameDisplayName));
            GamesView.Refresh();
        }
        finally { ReleaseLibraryControlsAfter(cooldown); }
    }

    public async Task RunScanAsync(bool isNewScan)
    {
        if (IsBusy) return;
        var process = AttachedProcess ?? throw new InvalidOperationException("请先连接游戏进程。");
        if (!isNewScan && _scanCandidates is not { Count: > 0 })
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
                result = await _scanService.InitialExactScanAsync(
                    process.ProcessId,
                    targets,
                    WritableOnly,
                    AlignedOnly,
                    progress,
                    _scanCancellation.Token);
            }
            else
            {
                Func<ScanCandidatePartition, byte[]?> exactTargetFactory = partition =>
                {
                    if (SelectedComparison != ScanComparison.Exact) return null;
                    return MemoryValueCodec.TryParseEncoded(ScanValue, partition.ValueType, partition.ScaleMultiplier, out var bytes)
                        ? bytes
                        : null;
                };
                if (SelectedComparison == ScanComparison.Exact &&
                    _scanCandidates!.Partitions.Any(partition => exactTargetFactory(partition) is null))
                    throw new InvalidOperationException("输入值无法转换为当前扫描会话中的某些类型或套路。");
                result = await _scanService.NextScanAsync(
                    process.ProcessId,
                    _scanCandidates!,
                    SelectedComparison,
                    exactTargetFactory,
                    progress,
                    _scanCancellation.Token);
            }

            if (isNewScan)
            {
                DisposeScanStore(_scanCandidates);
                DisposeScanHistory(_scanHistory);
            }
            else if (_scanCandidates is not null)
            {
                _scanHistory.Push(_scanCandidates);
            }
            _scanCandidates = result.Candidates;
            RefreshScanPreview();
            StatusText = ScanResultCount > ScanResultPreviewLimit
                ? $"扫描完成 · {ScanResultCount:N0} 个候选地址 · 列表显示前 {ScanResultPreviewLimit:N0} 条"
                : $"扫描完成 · {ScanResultCount:N0} 个候选地址";
            CaptureActiveSession();
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

    private List<ScanTargetDefinition> BuildInitialScanTargets()
    {
        var valueTypes = SelectedValueType is { } selected
            ? [selected]
            : Enum.GetValues<MemoryValueType>().ToList();
        var routines = SelectedSearchRoutine.Id == SearchRoutineIds.All
            ? SearchRoutines.Where(item => item.Id != SearchRoutineIds.All).ToList()
            : [SelectedSearchRoutine];
        var targets = new List<ScanTargetDefinition>();

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
                    targets.Add(new ScanTargetDefinition(
                        valueType,
                        routine.Id,
                        routine.DisplayName,
                        multiplier,
                        bytes));
            }
        }
        return targets;
    }

    public void CancelScan() => _scanCancellation?.Cancel();

    public void ResetScan()
    {
        StopLiveCandidateRefresh();
        _scanCancellation?.Cancel();
        DisposeScanStore(_scanCandidates);
        _scanCandidates = null;
        DisposeScanHistory(_scanHistory);
        VisibleScanResults = [];
        ScanResultCount = 0;
        SelectedScanResult = null;
        StatusText = "已清除本次扫描";
        OnPropertyChanged(nameof(HasScanSession));
        OnPropertyChanged(nameof(CanUndoScan));
        CaptureActiveSession();
    }

    public void UndoScan()
    {
        if (IsBusy || _scanHistory.Count == 0) return;
        StopLiveCandidateRefresh();
        DisposeScanStore(_scanCandidates);
        _scanCandidates = _scanHistory.Pop();
        RefreshScanPreview();
        StatusText = ScanResultCount > ScanResultPreviewLimit
            ? $"已撤销上一次过滤 · {ScanResultCount:N0} 个候选地址 · 列表显示前 {ScanResultPreviewLimit:N0} 条"
            : $"已撤销上一次过滤 · {ScanResultCount:N0} 个候选地址";
        CaptureActiveSession();
        StartLiveCandidateRefresh();
    }

    private void RefreshScanPreview()
    {
        var preview = _scanCandidates?.ReadCandidates(ScanResultPreviewLimit) ?? [];
        VisibleScanResults = new ObservableCollection<ScanCandidate>(preview);
        ScanResultCount = _scanCandidates?.Count ?? 0;
        SelectedScanResult = VisibleScanResults.FirstOrDefault();
        OnPropertyChanged(nameof(HasScanSession));
        OnPropertyChanged(nameof(CanUndoScan));
    }

    public async Task<SavedField> SaveSelectedCandidateAsync(string name, string group)
    {
        var candidate = SelectedScanResult ?? throw new InvalidOperationException("请先选择一个扫描结果。");
        var game = SelectedGame ?? throw new InvalidOperationException("请先把当前进程保存入库。");
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
        var game = SelectedGame ?? throw new InvalidOperationException("请先把当前进程保存入库。");
        var version = SelectedVersion ?? throw new InvalidOperationException("请先保存并选择当前游戏版本。");
        EnsureSelectedVersionMatchesAttached();
        if (string.IsNullOrWhiteSpace(fieldKey)) throw new InvalidOperationException("请填写模块定义的稳定字段键；物品模块通常使用精确物品名。");
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

    public async Task RefreshAdapterCharactersAsync()
    {
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        if (_activeAdapter is not ICharacterAttributesGameAdapter adapter || !adapter.SupportsCharacterAttributes(process))
            throw new InvalidOperationException("当前游戏构建没有可用的人物属性编辑模块。");

        var selectedId = SelectedAdapterCharacter?.CharacterId;
        var selectedAttribute = SelectedCharacterAttribute?.Key;
        StatusText = "正在读取游戏人物与属性…";
        var characters = await Task.Run(() => adapter.ReadCharacters(process));
        _adapterCharacters.Clear();
        foreach (var character in characters) _adapterCharacters.Add(character);
        SelectedAdapterCharacter = _adapterCharacters.FirstOrDefault(item =>
            string.Equals(item.CharacterId, selectedId, StringComparison.Ordinal)) ?? _adapterCharacters.FirstOrDefault();
        if (SelectedAdapterCharacter is not null && !string.IsNullOrWhiteSpace(selectedAttribute))
            SelectedCharacterAttribute = SelectedAdapterCharacter.Attributes.FirstOrDefault(item =>
                string.Equals(item.Key, selectedAttribute, StringComparison.Ordinal)) ?? SelectedAdapterCharacter.Attributes.FirstOrDefault();
        StatusText = IsCharacterEditorSessionOnly
            ? $"已读取 {_adapterCharacters.Count:N0} 个人物；属性修改仅本次游戏运行有效"
            : $"已读取 {_adapterCharacters.Count:N0} 个人物与属性";
    }

    public async Task WriteSelectedCharacterAttributeAsync(string value)
    {
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        if (_activeAdapter is not ICharacterAttributesGameAdapter adapter || !adapter.SupportsCharacterAttributes(process))
            throw new InvalidOperationException("当前游戏构建没有可用的人物属性编辑模块。");
        var character = SelectedAdapterCharacter ?? throw new InvalidOperationException("请先选择一个人物。");
        var attribute = SelectedCharacterAttribute ?? throw new InvalidOperationException("请先选择一个人物属性。");
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var target))
            throw new InvalidOperationException("人物属性必须是整数；具体允许范围由游戏专属模块校验。");

        StatusText = $"正在修改 {character.DisplayName} 的{attribute.DisplayName}…";
        await Task.Run(() => adapter.WriteCharacterAttribute(process, character.CharacterId, attribute.Key, target));
        await RefreshAdapterCharactersAsync();
        SelectedAdapterCharacter = _adapterCharacters.FirstOrDefault(item =>
            string.Equals(item.CharacterId, character.CharacterId, StringComparison.Ordinal));
        SelectedCharacterAttribute = SelectedAdapterCharacter?.Attributes.FirstOrDefault(item =>
            string.Equals(item.Key, attribute.Key, StringComparison.Ordinal));
        StatusText = IsSelectedCharacterFieldSessionOnly
            ? $"已实时修改 {character.DisplayName} 的{attribute.DisplayName}；关闭游戏后会失效"
            : $"已实时修改并保存 {character.DisplayName} 的{attribute.DisplayName}";
    }

    public async Task<SavedField> AddCharacterAttributeFieldAsync(string displayName, string group)
    {
        var adapter = _activeAdapter ?? throw new InvalidOperationException("当前游戏构建没有可用的专属适配器。");
        var character = SelectedAdapterCharacter ?? throw new InvalidOperationException("请先选择一个人物。");
        var attribute = SelectedCharacterAttribute ?? throw new InvalidOperationException("请先选择一个人物属性。");
        if (string.IsNullOrWhiteSpace(ActiveCharacterEditorId))
            throw new InvalidOperationException("当前模块没有注册人物属性页面。");
        var fieldKey = ModuleFieldKey.Create(ActiveCharacterEditorId, character.CharacterId, attribute.Key);
        return await AddAdapterFieldAsync(fieldKey, displayName, group);
    }

    public async Task RefreshEntityEditorAsync(AdapterEntityEditorState editor)
    {
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        if (_activeAdapter is not IEntityEditorsGameAdapter adapter ||
            !adapter.SupportsEntityEditor(process, editor.Descriptor.Id))
            throw new InvalidOperationException("当前游戏构建没有可用的该项专属修改器。");

        var selectedEntityId = editor.SelectedEntity?.EntityId;
        var selectedFieldKey = editor.SelectedField?.Key;
        StatusText = $"正在读取{editor.Descriptor.DisplayName}…";
        var entities = await Task.Run(() => adapter.ReadEditorEntities(process, editor.Descriptor.Id));
        editor.ReplaceEntities(entities, selectedEntityId, selectedFieldKey);
        StatusText = entities.Count == 0
            ? $"当前没有可显示的{editor.Descriptor.DisplayName}数据"
            : $"已读取 {entities.Count:N0} 项{editor.Descriptor.DisplayName}数据";
    }

    public async Task WriteEntityEditorFieldAsync(AdapterEntityEditorState editor, string value)
    {
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        if (_activeAdapter is not IEntityEditorsGameAdapter adapter ||
            !adapter.SupportsEntityEditor(process, editor.Descriptor.Id))
            throw new InvalidOperationException("当前游戏构建没有可用的该项专属修改器。");
        var entity = editor.SelectedEntity ?? throw new InvalidOperationException("请先选择一个修改项目。");
        var field = editor.SelectedField ?? throw new InvalidOperationException("请先选择一个可修改字段。");
        if (!field.CanWrite) throw new InvalidOperationException("该字段当前不可修改。");
        if (!long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var target) ||
            target < field.Minimum || target > field.Maximum)
            throw new InvalidOperationException(
                $"{field.DisplayName}必须是 {field.Minimum:N0} 到 {field.Maximum:N0} 之间的整数。");

        StatusText = $"正在修改 {entity.DisplayName} 的{field.DisplayName}…";
        await Task.Run(() => adapter.WriteEditorField(
            process, editor.Descriptor.Id, entity.EntityId, field.Key, target));
        await RefreshEntityEditorAsync(editor);
        editor.SelectedEntity = editor.Entities.FirstOrDefault(item =>
            string.Equals(item.EntityId, entity.EntityId, StringComparison.Ordinal));
        editor.SelectedField = editor.SelectedEntity?.Fields.FirstOrDefault(item =>
            string.Equals(item.Key, field.Key, StringComparison.Ordinal));
        StatusText = editor.Descriptor.SessionOnly
            ? $"已实时修改 {entity.DisplayName} 的{field.DisplayName} = {target:N0}；场景切换或关闭游戏后可能恢复"
            : $"已实时修改并保存 {entity.DisplayName} 的{field.DisplayName} = {target:N0}";
    }

    public async Task<SavedField> AddEntityEditorFieldAsync(
        AdapterEntityEditorState editor,
        string displayName,
        string group)
    {
        var entity = editor.SelectedEntity ?? throw new InvalidOperationException("请先选择一个修改项目。");
        var field = editor.SelectedField ?? throw new InvalidOperationException("请先选择一个可修改字段。");
        var fieldKey = ModuleFieldKey.Create(editor.Descriptor.Id, entity.EntityId, field.Key);
        return await AddAdapterFieldAsync(fieldKey, displayName, group);
    }

    public async Task AccelerateGameAsync()
    {
        var process = AttachedProcess ?? throw new InvalidOperationException("请先连接游戏进程。");
        var multiplier = ParseSpeedMultiplier(SpeedMultiplier);
        var cooldown = BeginSpeedControlInteraction();
        try
        {
            SpeedMultiplier = FormatSpeedMultiplier(multiplier);
            if (multiplier == 1d)
            {
                await Task.Run(_speedService.Normalize);
                IsSpeedActive = false;
                if (_activeSession is not null) _activeSession.IsSpeedActive = false;
                OnPropertyChanged(nameof(SpeedStatusText));
                StatusText = "游戏已切换为正常倍速";
                return;
            }
            var result = await Task.Run(() => _speedService.Accelerate(process.ProcessId, multiplier));
            IsSpeedActive = true;
            if (_activeSession is not null)
            {
                _activeSession.IsSpeedActive = true;
                _activeSession.SpeedMultiplierInput = SpeedMultiplier;
            }
            // IsSpeedActive stays true when changing an active multiplier, so notify explicitly.
            OnPropertyChanged(nameof(SpeedStatusText));
            var mode = multiplier < 1d ? "减速" : "加速";
            StatusText = $"游戏已切换为 {FormatSpeedMultiplier(multiplier)} 倍{mode} · 已挂接 {result.PatchedImportCount} 个计时入口";
        }
        finally
        {
            ReleaseSpeedControlAfter(cooldown);
        }
    }

    public async Task RestoreGameSpeedAsync()
    {
        _ = AttachedProcess ?? throw new InvalidOperationException("请先连接游戏进程。");
        var cooldown = BeginSpeedControlInteraction();
        try
        {
            if (!_speedService.HasHooks || _speedService.Multiplier == 1d)
            {
                IsSpeedActive = false;
                if (_activeSession is not null) _activeSession.IsSpeedActive = false;
                SpeedMultiplier = "1";
                StatusText = "当前已经是正常倍速";
                return;
            }
            await Task.Run(_speedService.Normalize);
            IsSpeedActive = false;
            if (_activeSession is not null) _activeSession.IsSpeedActive = false;
            SpeedMultiplier = "1";
            OnPropertyChanged(nameof(SpeedStatusText));
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

    private Task BeginConnectionControlInteraction()
    {
        if (_isConnectionControlBlocked) throw new InvalidOperationException("连接状态正在切换，请稍候再试。");
        _isConnectionControlBlocked = true;
        NotifyConnectionControls();
        return Task.Delay(TimeSpan.FromSeconds(2));
    }

    private async void ReleaseConnectionControlsAfter(Task cooldown)
    {
        try { await cooldown; }
        finally
        {
            _isConnectionControlBlocked = false;
            NotifyConnectionControls();
        }
    }

    private Task BeginLibraryControlInteraction()
    {
        if (_isLibraryControlBlocked) throw new InvalidOperationException("游戏库正在更新，请稍候再试。");
        _isLibraryControlBlocked = true;
        NotifyLibraryControls();
        return Task.Delay(TimeSpan.FromSeconds(2));
    }

    private async void ReleaseLibraryControlsAfter(Task cooldown)
    {
        try { await cooldown; }
        finally
        {
            _isLibraryControlBlocked = false;
            NotifyLibraryControls();
        }
    }

    private void NotifyConnectionControls()
    {
        OnPropertyChanged(nameof(CanConnectProcess));
        OnPropertyChanged(nameof(CanConnectSelectedGame));
        OnPropertyChanged(nameof(CanDisconnectSelectedGame));
        OnPropertyChanged(nameof(CanDisconnectCurrentProcess));
    }

    private void NotifyLibraryControls()
    {
        OnPropertyChanged(nameof(LibraryStatusText));
        OnPropertyChanged(nameof(CanSaveCurrentGame));
        OnPropertyChanged(nameof(CanRemoveCurrentGame));
    }

    private static double ParseSpeedMultiplier(string text)
    {
        var valueText = text.Trim();
        var decimalPoint = valueText.IndexOf('.');
        if (decimalPoint >= 0 && (valueText.LastIndexOf('.') != decimalPoint || valueText.Length - decimalPoint - 1 > 2) ||
            !decimal.TryParse(valueText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ||
            value is < 0.01m or > 100m)
            throw new InvalidOperationException("倍数必须是 0.01 到 100.00 之间、最多两位小数的数字。");
        return decimal.ToDouble(value);
    }

    private static string FormatSpeedMultiplier(double multiplier) =>
        multiplier.ToString("0.##", CultureInfo.InvariantCulture);

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
            if (!GetAdapterFieldPolicy(_activeAdapter, field).CanLock)
                throw new InvalidOperationException("该游戏专属字段仅本次运行有效，不支持锁定或在重启后自动重应用。");
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

    public async Task CheckGameModuleUpdatesAsync()
    {
        var game = SelectedGame;
        var version = SelectedVersion;
        var process = AttachedProcess;
        var fingerprint = _attachedFingerprint;
        if ((process is null || fingerprint is null) && (game is null || version is null))
            throw new InvalidOperationException("请先连接一个游戏，或选择带有版本信息的游戏库条目。");
        if (_isModuleControlBlocked) return;
        _isModuleControlBlocked = true;
        NotifyModuleControls();
        var cooldown = Task.Delay(TimeSpan.FromSeconds(3));
        try
        {
            ModuleStatusText = "正在从 GitHub 检查当前游戏的专属模块…";
            var result = process is not null && fingerprint is not null
                ? await _moduleCatalogService.CheckAsync(process.ProcessName, fingerprint)
                : await _moduleCatalogService.CheckAsync(game!, version!);
            _moduleCheckResult = result;
            ModuleStatusText = result.StatusText;
            NotifyModuleControls();
        }
        catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _moduleCheckResult = null;
            ModuleStatusText = "检查失败：服务器暂未发布专属模块清单。";
            NotifyModuleControls();
            throw new InvalidOperationException("服务器暂未发布专属模块清单，请稍后重试。", exception);
        }
        catch (Exception exception)
        {
            _moduleCheckResult = null;
            ModuleStatusText = $"检查失败：{exception.Message}";
            NotifyModuleControls();
            throw new InvalidOperationException($"检查专属模块失败：{exception.Message}", exception);
        }
        finally
        {
            ReleaseModuleControlsAfter(cooldown);
        }
    }

    public async Task InstallAvailableGameModuleAsync()
    {
        var module = _moduleCheckResult?.RemoteModule
                     ?? throw new InvalidOperationException("请先点击“检查新有”。");
        var game = SelectedGame;
        var version = SelectedVersion;
        var process = AttachedProcess;
        var originalSession = _activeSession;
        if (_moduleCheckResult?.Availability is not (GameModuleAvailability.Available or GameModuleAvailability.UpdateAvailable))
            throw new InvalidOperationException("当前没有可下载或更新的专属模块。");
        if (_isModuleControlBlocked) return;
        _isModuleControlBlocked = true;
        NotifyModuleControls();
        var cooldown = Task.Delay(TimeSpan.FromSeconds(3));
        try
        {
            ModuleStatusText = $"正在下载并校验 {module.DisplayName} v{module.Version}…";
            var progress = new Progress<DownloadProgressSnapshot>(snapshot =>
            {
                ModuleStatusText = $"正在下载 {module.DisplayName} v{module.Version} · {snapshot.DisplayText}";
                StatusText = ModuleStatusText;
            });
            await _moduleCatalogService.InstallAsync(module, progress);
            SetActiveAdapter(null);
            _adapterRegistry.Reload();
            ReloadAdaptersForSessions();
            if (game is null)
            {
                if (process is null) throw new InvalidOperationException("模块已下载，但当前游戏连接已断开，无法自动保存入库。");
                game = await AddCurrentProcessToLibraryCoreAsync(
                    string.IsNullOrWhiteSpace(module.GameDisplayName) ? process.ProcessName : module.GameDisplayName,
                    process);
                version = SelectedVersion;
            }
            game.ModuleId = module.Id;
            game.IsModuleInstalled = true;
            var installedAdapter = originalSession?.Adapter;
            if (installedAdapter is not null && version is not null)
            {
                var migrated = MigrateAdapterFields(game, version, installedAdapter.Id);
                if (migrated > 0)
                {
                    game.NotifySummaryChanged();
                    await SaveLibraryAsync();
                }
            }
            await SaveLibraryAsync();
            GamesView.Refresh();
            _moduleCheckResult = new GameModuleCheckResult(GameModuleAvailability.Current, module,
                _moduleCatalogService.FindInstalled(module.Id), $"已安装最新专属模块：{module.DisplayName} v{module.Version}");
            ModuleStatusText = installedAdapter is null
                ? $"已安装 {module.DisplayName} v{module.Version}，连接兼容游戏版本后启用。"
                : _moduleCheckResult.StatusText;
            NotifyModuleControls();
        }
        finally
        {
            ReleaseModuleControlsAfter(cooldown);
        }
    }

    public async Task UninstallCurrentGameModuleAsync()
    {
        var moduleId = ResolveActiveModuleId();
        if (string.IsNullOrWhiteSpace(moduleId)) throw new InvalidOperationException("当前游戏没有本地专属模块。");
        var record = _moduleCatalogService.FindInstalled(moduleId)
                     ?? throw new InvalidOperationException("当前游戏没有本地专属模块。");
        if (_isModuleControlBlocked) return;
        _isModuleControlBlocked = true;
        NotifyModuleControls();
        var cooldown = Task.Delay(TimeSpan.FromSeconds(3));
        try
        {
            ModuleStatusText = "正在卸载当前游戏的专属模块…";
            var packageDeleted = await RemoveInstalledModuleCoreAsync(moduleId);
            foreach (var game in Games.Where(item => string.Equals(item.ModuleId, moduleId, StringComparison.Ordinal)))
                game.IsModuleInstalled = false;
            _moduleCheckResult = null;
            ModuleStatusText = packageDeleted
                ? $"已卸载专属模块 v{record.Version}；游戏库和快捷入口已保留。"
                : $"已停用专属模块 v{record.Version}；残留文件将在下次启动自动清理。";
            GamesView.Refresh();
            NotifyModuleControls();
        }
        finally { ReleaseModuleControlsAfter(cooldown); }
    }

    private async Task<bool> RemoveInstalledModuleCoreAsync(string moduleId)
    {
        var sessions = _sessions.Values.Append(_activeSession).Where(item => item is not null)
            .Cast<GameConnectionSession>().Distinct().ToList();
        foreach (var session in sessions) StopSessionLockMaintenance(session);
        var removed = _moduleCatalogService.Unregister(moduleId)
                      ?? throw new InvalidOperationException("模块安装记录不存在。");
        try
        {
            foreach (var session in sessions.Where(session =>
                         string.Equals(session.Adapter?.Id, moduleId, StringComparison.Ordinal)))
            {
                session.Adapter = null;
                session.AdapterItems = [];
                session.AdapterCharacters = [];
                session.SelectedAdapterFieldKey = null;
                session.SelectedCharacterId = null;
                session.SelectedCharacterAttributeKey = null;
            }
            if (string.Equals(_activeAdapter?.Id, moduleId, StringComparison.Ordinal))
                SetActiveAdapter(null);
            _adapterRegistry.Reload();
            ReloadAdaptersForSessions();
        }
        catch
        {
            _moduleCatalogService.RestoreRegistration(removed);
            _adapterRegistry.Reload();
            ReloadAdaptersForSessions();
            RestartLockMaintenance();
            throw;
        }
        try
        {
            return await _moduleCatalogService.DeletePackageAsync(moduleId);
        }
        finally
        {
            RestartLockMaintenance();
        }
    }

    public IReadOnlyList<GameModuleContributor> GetModuleContributors()
    {
        var moduleId = ResolveActiveModuleId();
        if (!string.IsNullOrWhiteSpace(moduleId))
        {
            var local = _moduleCatalogService.GetInstalledManifest(moduleId)?.Contributors;
            if (local is { Count: > 0 }) return local.Where(IsSafeContributor).ToList();
        }
        return _moduleCheckResult?.RemoteModule?.Contributors.Where(IsSafeContributor).ToList() ?? [];
    }

    internal ModuleCompatibilityReport CreateModuleCompatibilityReport()
    {
        if (!CanViewModuleCompatibilityDiagnostics)
            throw new InvalidOperationException("请先连接一个游戏，或选择带有版本信息的游戏库条目。");

        var process = AttachedProcess;
        var moduleId = ResolveActiveModuleId();
        InstalledModuleManifest? manifest = null;
        if (!string.IsNullOrWhiteSpace(moduleId))
            manifest = _moduleCatalogService.GetInstalledManifest(moduleId);

        if (string.IsNullOrWhiteSpace(moduleId))
        {
            var processName = process?.ProcessName ?? SelectedGame?.ProcessName ?? string.Empty;
            var matches = _moduleCatalogService.GetInstalledManifests()
                .Where(candidate => candidate.ProcessNames.Any(name =>
                    string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)))
                .Take(2)
                .ToList();
            if (matches.Count == 1)
            {
                manifest = matches[0];
                moduleId = manifest.Id;
            }
        }

        var installed = string.IsNullOrWhiteSpace(moduleId)
            ? null
            : _moduleCatalogService.FindInstalled(moduleId);
        var adapter = !string.IsNullOrWhiteSpace(moduleId)
            ? _adapterRegistry.FindById(moduleId)
            : _activeAdapter;
        var checkResult = _moduleCheckResult;
        if (checkResult?.RemoteModule is not null && !string.IsNullOrWhiteSpace(moduleId) &&
            !string.Equals(checkResult.RemoteModule.Id, moduleId, StringComparison.Ordinal))
            checkResult = null;

        return ModuleCompatibilityDiagnosticsService.Create(new(
            ApplicationVersion.Current,
            SelectedVersion,
            process,
            _attachedFingerprint,
            moduleId,
            installed,
            manifest,
            checkResult,
            adapter,
            _adapterRegistry.LoadErrors));
    }

    private static bool IsSafeContributor(GameModuleContributor contributor) =>
        contributor.GithubId > 0 &&
        !string.IsNullOrWhiteSpace(contributor.DisplayName) &&
        Uri.TryCreate(contributor.ProfileUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.AbsolutePath.Trim('/'), contributor.GithubLogin, StringComparison.OrdinalIgnoreCase);

    public async Task CheckApplicationUpdateAsync()
    {
        if (_isApplicationUpdateBusy || _isApplicationUpdateCheckCooldown || _applicationUpdateDownloaded) return;
        _isApplicationUpdateBusy = true;
        _isApplicationUpdateCheckCooldown = true;
        ReleaseApplicationUpdateCheckCooldownAfterDelay();
        NotifyApplicationUpdateState();
        try
        {
            ApplicationUpdateStatusText = "　检查中";
            _applicationUpdateResult = await _applicationUpdateService.CheckAsync();
            if (_applicationUpdateResult.IsUpdateAvailable)
            {
                ApplicationUpdateStatusText = $"　v{_applicationUpdateResult.Version}";
                ApplicationUpdateActionText = "更新";
                StatusText = $"发现肝肾大圣新版本 v{_applicationUpdateResult.Version}";
            }
            else
            {
                ApplicationUpdateStatusText = "　已最新";
                ApplicationUpdateActionText = "检查更新";
                StatusText = "肝肾大圣当前已是最新版本";
            }
        }
        catch
        {
            _applicationUpdateResult = null;
            ApplicationUpdateStatusText = "　检查失败";
            ApplicationUpdateActionText = "检查更新";
            StatusText = "检查肝肾大圣更新失败，请稍后重试";
            throw;
        }
        finally
        {
            _isApplicationUpdateBusy = false;
            NotifyApplicationUpdateState();
        }
    }

    public async Task<bool> DownloadApplicationUpdateAsync()
    {
        var update = _applicationUpdateResult;
        if (update?.IsUpdateAvailable != true) throw new InvalidOperationException("请先检查更新。");
        if (_isApplicationUpdateBusy || _applicationUpdateDownloaded) return false;
        _isApplicationUpdateBusy = true;
        NotifyApplicationUpdateState();
        try
        {
            ApplicationUpdateStatusText = $"　v{update.Version} 下载中";
            var progress = new Progress<DownloadProgressSnapshot>(snapshot =>
            {
                var progressText = snapshot.Percentage is { } percentage
                    ? $"下载 {percentage}% "
                    : "下载中 ";
                ApplicationUpdateStatusText = $"　v{update.Version} {progressText.Trim()}";
                StatusText = $"正在下载肝肾大圣 v{update.Version} · {snapshot.DisplayText}";
            });
            await _applicationUpdateService.DownloadAsync(update, progress);
            _applicationUpdateDownloaded = true;
            ApplicationUpdateStatusText = $"　v{update.Version} 已下载";
            ApplicationUpdateActionText = "待重启更新";
            StatusText = $"已下载并校验 v{update.Version}，可立即重启或下次启动时更新";
            return true;
        }
        catch
        {
            ApplicationUpdateStatusText = $"　v{update.Version} 下载失败";
            ApplicationUpdateActionText = "更新";
            StatusText = $"下载肝肾大圣 v{update.Version} 失败，请稍后重试";
            throw;
        }
        finally
        {
            _isApplicationUpdateBusy = false;
            NotifyApplicationUpdateState();
        }
    }

    public bool LaunchPendingApplicationUpdate() => _applicationUpdateService.LaunchPendingUpdate(true);

    public ApplicationUpdateFailure? TakeLastApplicationUpdateFailure() =>
        _applicationUpdateService.TakeLastFailure();

    public void OpenOfficialWebsite()
    {
        if (_isOfficialWebsiteControlBlocked) return;
        _isOfficialWebsiteControlBlocked = true;
        OnPropertyChanged(nameof(CanOpenOfficialWebsite));
        var cooldown = Task.Delay(TimeSpan.FromSeconds(2));
        try
        {
            _ = Process.Start(new ProcessStartInfo("https://github.com/BestWishes/GameValueEditor")
            {
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("无法打开默认浏览器。");
        }
        finally
        {
            ReleaseOfficialWebsiteControlAfter(cooldown);
        }
    }

    private async void ReleaseOfficialWebsiteControlAfter(Task cooldown)
    {
        try { await cooldown; }
        finally
        {
            _isOfficialWebsiteControlBlocked = false;
            OnPropertyChanged(nameof(CanOpenOfficialWebsite));
        }
    }

    private void ResetModuleCheckState()
    {
        _moduleCheckResult = null;
        var moduleId = ResolveActiveModuleId();
        var installed = string.IsNullOrWhiteSpace(moduleId) ? null : _moduleCatalogService.FindInstalled(moduleId);
        ModuleStatusText = _activeAdapter is not null
            ? $"本地已装配：{_activeAdapter.DisplayName}（尚未检查更新）"
            : installed is not null
                ? $"本地已安装专属模块 v{installed.Version}，连接兼容游戏版本后启用"
                : "本地未装配当前游戏和版本的专属修改模块";
        NotifyModuleControls();
    }

    private string ResolveActiveModuleId() =>
        !string.IsNullOrWhiteSpace(SelectedGame?.ModuleId) ? SelectedGame.ModuleId :
        !string.IsNullOrWhiteSpace(_activeAdapter?.Id) ? _activeAdapter.Id :
        _moduleCheckResult?.RemoteModule?.Id ?? string.Empty;

    private void ReloadAdaptersForSessions()
    {
        var sessions = _sessions.Values
            .Append(_activeSession)
            .Where(session => session is not null)
            .Cast<GameConnectionSession>()
            .Distinct()
            .ToList();
        foreach (var session in sessions)
        {
            var previousAdapterId = session.Adapter?.Id;
            session.Adapter = session.Fingerprint is null
                ? null
                : _adapterRegistry.Resolve(session.Process, session.Fingerprint);
            if (string.Equals(previousAdapterId, session.Adapter?.Id, StringComparison.Ordinal)) continue;
            session.AdapterItems = [];
            session.AdapterCharacters = [];
            session.SelectedAdapterFieldKey = null;
            session.SelectedCharacterId = null;
            session.SelectedCharacterAttributeKey = null;
        }
        if (_activeSession is not null && _activeSession.AdapterItems.Count == 0 && _activeSession.AdapterCharacters.Count == 0)
        {
            _adapterInventoryItems.Clear();
            _adapterCharacters.Clear();
            SelectedAdapterItem = null;
            SelectedAdapterCharacter = null;
        }
        SetActiveAdapter(_activeSession?.Adapter);
    }

    private void SetActiveAdapter(IGameAdapter? adapter)
    {
        _activeAdapter = adapter;
        if (_activeSession is not null) _activeSession.Adapter = adapter;
        try
        {
            RebuildEditorPages();
        }
        catch
        {
            _activeAdapter = null;
            if (_activeSession is not null) _activeSession.Adapter = null;
            DisposeEditorPages();
            throw;
        }
        OnPropertyChanged(nameof(HasActiveAdapter));
        OnPropertyChanged(nameof(HasActiveInventoryAdapter));
        OnPropertyChanged(nameof(HasCharacterEditor));
        OnPropertyChanged(nameof(HasActiveCharacterAdapter));
        OnPropertyChanged(nameof(HasUnsupportedCharacterAdapter));
        OnPropertyChanged(nameof(IsCharacterEditorSessionOnly));
        OnPropertyChanged(nameof(IsSelectedCharacterFieldSessionOnly));
        OnPropertyChanged(nameof(ActiveCharacterEditorId));
        OnPropertyChanged(nameof(HasNoActiveAdapter));
        OnPropertyChanged(nameof(ActiveAdapterText));
        NotifyEditorPageProperties();
        NotifyModuleControls();
    }

    private void RebuildEditorPages()
    {
        var selectedEditorId = SelectedAdapterEditorPage?.Descriptor.Id;
        DisposeEditorPages();
        _characterEditorPage = null;
        if (_activeAdapter is null)
        {
            SelectedAdapterEditorPage = null;
            return;
        }

        var descriptors = _activeAdapter.Editors.ToDictionary(editor => editor.Id, StringComparer.Ordinal);
        if (_activeAdapter is IGameEditorPageFactoryProvider factory)
        {
            var process = AttachedProcess ?? throw new InvalidOperationException("模块页面需要已连接的游戏进程。");
            var fingerprint = _attachedFingerprint ?? throw new InvalidOperationException("模块页面需要已识别的游戏构建。");
            var host = _editorHostServices ?? throw new InvalidOperationException("模块页面宿主服务尚未初始化。");
            try
            {
                foreach (var descriptor in _activeAdapter.Editors.OrderBy(editor => editor.Order))
                {
                    var lifetime = new CancellationTokenSource();
                    IGameEditorPage? modulePage = null;
                    try
                    {
                        var context = new GameEditorPageContext(
                            process.ToModuleContext(),
                            fingerprint.ToModuleIdentity(),
                            host,
                            lifetime.Token);
                        modulePage = factory.CreateEditorPage(descriptor.Id, context)
                                     ?? throw new InvalidOperationException($"模块页面工厂没有创建 {descriptor.Id}。");
                        AdapterEditorPages.Add(new AdapterModuleEditorPageState(descriptor, lifetime, modulePage));
                        modulePage = null;
                    }
                    catch
                    {
                        lifetime.Cancel();
                        try { modulePage?.Dispose(); }
                        catch (Exception exception) { Debug.WriteLine(exception); }
                        lifetime.Dispose();
                        throw;
                    }
                }
            }
            catch
            {
                DisposeEditorPages();
                throw;
            }
            SelectedAdapterEditorPage = AdapterEditorPages.FirstOrDefault(page =>
                                            string.Equals(page.Descriptor.Id, selectedEditorId, StringComparison.Ordinal))
                                        ?? AdapterEditorPages.FirstOrDefault();
            return;
        }

        foreach (var registration in GameEditorPageResolver.Resolve(_activeAdapter)
                     .OrderBy(page => descriptors.TryGetValue(page.EditorId, out var descriptor)
                         ? descriptor.Order
                         : int.MaxValue))
        {
            if (!descriptors.TryGetValue(registration.EditorId, out var descriptor)) continue;
            AdapterEditorPageState? page = registration.Role switch
            {
                GameEditorPageRole.Inventory when _activeAdapter is IInventoryGameAdapter =>
                    new AdapterInventoryEditorPageState(descriptor, registration),
                GameEditorPageRole.CharacterAttributes when _activeAdapter is ICharacterAttributesGameAdapter characters =>
                    new AdapterCharacterEditorPageState(
                        descriptor,
                        registration,
                        AttachedProcess is not null && characters.SupportsCharacterAttributes(AttachedProcess)),
                GameEditorPageRole.Entity when _activeAdapter is IEntityEditorsGameAdapter entities =>
                    new AdapterEntityEditorState(
                        descriptor,
                        registration,
                        AttachedProcess is not null && entities.SupportsEntityEditor(AttachedProcess, descriptor.Id)),
                _ => null
            };
            if (page is null) continue;
            AdapterEditorPages.Add(page);
            if (page is AdapterCharacterEditorPageState characterPage) _characterEditorPage = characterPage;
        }
        SelectedAdapterEditorPage = AdapterEditorPages.FirstOrDefault(page =>
                                        string.Equals(page.Descriptor.Id, selectedEditorId, StringComparison.Ordinal))
                                    ?? AdapterEditorPages.FirstOrDefault();
    }

    private void DisposeEditorPages()
    {
        SelectedAdapterEditorPage = null;
        var disposablePages = AdapterEditorPages.OfType<IDisposable>().ToArray();
        AdapterEditorPages.Clear();
        foreach (var page in disposablePages)
        {
            try { page.Dispose(); }
            catch (Exception exception) { Debug.WriteLine(exception); }
        }
    }

    private void NotifyEditorPageProperties()
    {
        OnPropertyChanged(nameof(AdapterEditorPages));
        OnPropertyChanged(nameof(HasActiveInventoryAdapter));
        OnPropertyChanged(nameof(HasCharacterEditor));
        OnPropertyChanged(nameof(HasActiveCharacterAdapter));
        OnPropertyChanged(nameof(HasUnsupportedCharacterAdapter));
        OnPropertyChanged(nameof(IsCharacterEditorSessionOnly));
        OnPropertyChanged(nameof(IsSelectedCharacterFieldSessionOnly));
        OnPropertyChanged(nameof(ActiveCharacterEditorId));
    }

    private void NotifyModuleControls()
    {
        OnPropertyChanged(nameof(CanCheckGameModules));
        OnPropertyChanged(nameof(CanInstallGameModule));
        OnPropertyChanged(nameof(ModuleInstallActionText));
        OnPropertyChanged(nameof(CanUninstallGameModule));
        OnPropertyChanged(nameof(CanViewModuleCompatibilityDiagnostics));
        OnPropertyChanged(nameof(CanViewModuleContributors));
    }

    private async void ReleaseModuleControlsAfter(Task cooldown)
    {
        try { await cooldown; }
        finally
        {
            _isModuleControlBlocked = false;
            NotifyModuleControls();
        }
    }

    private void NotifyApplicationUpdateState()
    {
        OnPropertyChanged(nameof(CanUseApplicationUpdate));
        OnPropertyChanged(nameof(HasApplicationUpdateAvailable));
        OnPropertyChanged(nameof(IsApplicationUpdateDownloaded));
    }

    private async void ReleaseApplicationUpdateCheckCooldownAfterDelay()
    {
        try { await Task.Delay(TimeSpan.FromSeconds(10)); }
        finally
        {
            _isApplicationUpdateCheckCooldown = false;
            NotifyApplicationUpdateState();
        }
    }

    private IGameAdapter ResolveFieldAdapter(SavedField field, IGameAdapter? adapterOverride = null)
    {
        var adapter = adapterOverride ?? _activeAdapter;
        if (adapter is null ||
            (!string.Equals(adapter.Id, field.AdapterId, StringComparison.Ordinal) &&
             !adapter.LegacyIds.Any(alias => string.Equals(alias, field.AdapterId, StringComparison.Ordinal))))
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

    private void CaptureActiveSession()
    {
        var session = _activeSession;
        if (session is null) return;
        session.Fingerprint = _attachedFingerprint;
        session.Adapter = _activeAdapter;
        session.VersionId = SelectedVersion?.Id;
        session.ScanCandidates = _scanCandidates;
        session.ScanHistory = _scanHistory;
        session.VisibleScanResults = VisibleScanResults;
        session.SelectedScanResult = SelectedScanResult;
        session.AdapterItems = _adapterInventoryItems.ToList();
        session.AdapterCharacters = _adapterCharacters.ToList();
        session.SelectedAdapterFieldKey = SelectedAdapterItem?.FieldKey;
        session.SelectedCharacterId = SelectedAdapterCharacter?.CharacterId;
        session.SelectedCharacterAttributeKey = SelectedCharacterAttribute?.Key;
        session.AdapterItemNameFilter = AdapterItemNameFilter;
        session.AdapterItemCountFilter = AdapterItemCountFilter;
        session.ScanValue = ScanValue;
        session.SelectedValueType = SelectedValueType;
        session.SelectedComparison = SelectedComparison;
        session.SelectedSearchRoutineId = SelectedSearchRoutine.Id;
        session.ScaleMultiplier = ScaleMultiplier;
        session.SpeedMultiplierInput = SpeedMultiplier;
        session.IsSpeedActive = IsSpeedActive;
    }

    private void ActivateSelectedGameSession(GameProfile? game)
    {
        CaptureActiveSession();
        StopLiveCandidateRefresh();
        _scanCancellation?.Cancel();
        if (_activeSession is { GameId: null } transient && game is not null)
            DisconnectSession(transient, false);
        if (game is not null && _sessions.TryGetValue(game.Id, out var session))
        {
            if (IsProcessRunning(session.ProcessGroup.RootProcess) && IsProcessRunning(session.Process))
            {
                RestoreSessionIntoView(session, game);
                return;
            }
            DisconnectSession(session, false);
        }
        ClearActiveView(game);
    }

    private void RestoreSessionIntoView(GameConnectionSession session, GameProfile? game)
    {
        _activeSession = session;
        _speedService = session.SpeedService;
        _attachedFingerprint = session.Fingerprint;
        _attachedGameId = session.GameId;
        _activeAdapter = session.Adapter;
        AttachedProcess = session.Process;
        RebuildEditorPages();
        _scanCandidates = session.ScanCandidates;
        _scanHistory = session.ScanHistory;
        VisibleScanResults = session.VisibleScanResults;
        ScanResultCount = _scanCandidates?.Count ?? 0;
        SelectedScanResult = session.SelectedScanResult ?? VisibleScanResults.FirstOrDefault();
        _adapterInventoryItems.Clear();
        foreach (var item in session.AdapterItems) _adapterInventoryItems.Add(item);
        AdapterItemsView.Refresh();
        SelectedAdapterItem = _adapterInventoryItems.FirstOrDefault(item =>
            string.Equals(item.FieldKey, session.SelectedAdapterFieldKey, StringComparison.Ordinal));
        _adapterCharacters.Clear();
        foreach (var character in session.AdapterCharacters) _adapterCharacters.Add(character);
        SelectedAdapterCharacter = _adapterCharacters.FirstOrDefault(item =>
            string.Equals(item.CharacterId, session.SelectedCharacterId, StringComparison.Ordinal));
        SelectedCharacterAttribute = SelectedAdapterCharacter?.Attributes.FirstOrDefault(item =>
            string.Equals(item.Key, session.SelectedCharacterAttributeKey, StringComparison.Ordinal) &&
            MatchesCharacterAttributeFilter(item));
        AdapterItemNameFilter = session.AdapterItemNameFilter;
        AdapterItemCountFilter = session.AdapterItemCountFilter;
        ScanValue = session.ScanValue;
        SelectedValueType = session.SelectedValueType;
        SelectedComparison = session.SelectedComparison;
        SelectedSearchRoutine = SearchRoutines.FirstOrDefault(item => item.Id == session.SelectedSearchRoutineId)
                                ?? SearchRoutines[0];
        ScaleMultiplier = session.ScaleMultiplier;
        SpeedMultiplier = session.SpeedMultiplierInput;
        IsSpeedActive = session.IsSpeedActive && session.SpeedService.Multiplier != 1;
        var version = game?.Versions.FirstOrDefault(item => item.Id == session.VersionId)
                      ?? (session.Fingerprint is null || game is null
                          ? game?.Versions.OrderByDescending(item => item.LastVerifiedUtc).FirstOrDefault()
                          : FindMatchingVersion(game, session.Fingerprint));
        SelectedVersion = version;
        ConnectionText = BuildConnectionText(session);
        OnPropertyChanged(nameof(HasActiveAdapter));
        OnPropertyChanged(nameof(HasActiveInventoryAdapter));
        OnPropertyChanged(nameof(HasCharacterEditor));
        OnPropertyChanged(nameof(HasActiveCharacterAdapter));
        OnPropertyChanged(nameof(HasUnsupportedCharacterAdapter));
        OnPropertyChanged(nameof(IsCharacterEditorSessionOnly));
        OnPropertyChanged(nameof(ActiveCharacterEditorId));
        OnPropertyChanged(nameof(HasNoActiveAdapter));
        OnPropertyChanged(nameof(ActiveAdapterText));
        NotifyEditorPageProperties();
        OnPropertyChanged(nameof(SpeedStatusText));
        OnPropertyChanged(nameof(HasScanSession));
        OnPropertyChanged(nameof(CanUndoScan));
        OnPropertyChanged(nameof(ActiveGameIcon));
        StartLiveCandidateRefresh();
        RestartLockMaintenance();
    }

    private void RebindSession(GameConnectionSession session, LogicalGameProcessGroup processGroup)
    {
        var wasActive = ReferenceEquals(_activeSession, session);
        if (wasActive)
        {
            CaptureActiveSession();
            StopLiveCandidateRefresh();
            _scanCancellation?.Cancel();
        }
        StopSessionLockMaintenance(session);
        try { session.SpeedService.DetachSafely(); }
        catch (Exception exception) { Debug.WriteLine(exception); }
        session.SpeedService.Dispose();
        session.SpeedService = new ProcessSpeedService();
        session.IsSpeedActive = false;
        session.ProcessGroup = processGroup;
        session.Process = processGroup.DataProcess;
        DisposeSessionScanState(session);
        session.ScanCandidates = null;
        session.ScanHistory = new Stack<ScanCandidateStore>();
        session.VisibleScanResults = [];
        session.SelectedScanResult = null;
        session.AdapterItems = [];
        session.AdapterCharacters = [];
        session.SelectedAdapterFieldKey = null;
        session.SelectedCharacterId = null;
        session.SelectedCharacterAttributeKey = null;
        session.Adapter = session.Fingerprint is null
            ? null
            : _adapterRegistry.Resolve(session.Process, session.Fingerprint);

        if (!wasActive) return;
        var game = session.GameId is Guid gameId ? Games.FirstOrDefault(item => item.Id == gameId) : SelectedGame;
        SelectedProcess = processGroup.SeedProcess;
        RestoreSessionIntoView(session, game);
        StatusText = $"游戏数据进程已重建，已自动改用 PID {session.Process.ProcessId}；临时扫描结果已清空";
    }

    private static string BuildConnectionText(GameConnectionSession session) =>
        session.ProcessGroup.Members.Count > 1
            ? $"已连接 · {session.ProcessGroup.RootProcess.ProcessName} · {session.ProcessGroup.Members.Count} 个关联进程 · 数据 PID {session.Process.ProcessId}"
            : $"已连接 · {session.Process.ProcessName} · PID {session.Process.ProcessId}";

    private void ClearActiveView(GameProfile? selectedGame)
    {
        _activeSession = null;
        _speedService = _idleSpeedService;
        _attachedFingerprint = null;
        _attachedGameId = null;
        _activeAdapter = null;
        RebuildEditorPages();
        AttachedProcess = null;
        _scanCandidates = null;
        _scanHistory = new Stack<ScanCandidateStore>();
        VisibleScanResults = [];
        ScanResultCount = 0;
        SelectedScanResult = null;
        _adapterInventoryItems.Clear();
        SelectedAdapterItem = null;
        _adapterCharacters.Clear();
        SelectedAdapterCharacter = null;
        IsSpeedActive = false;
        SelectedVersion = selectedGame?.Versions.OrderByDescending(item => item.LastVerifiedUtc).FirstOrDefault();
        ConnectionText = "未连接";
        OnPropertyChanged(nameof(HasActiveAdapter));
        OnPropertyChanged(nameof(HasActiveInventoryAdapter));
        OnPropertyChanged(nameof(HasCharacterEditor));
        OnPropertyChanged(nameof(HasActiveCharacterAdapter));
        OnPropertyChanged(nameof(HasUnsupportedCharacterAdapter));
        OnPropertyChanged(nameof(IsCharacterEditorSessionOnly));
        OnPropertyChanged(nameof(ActiveCharacterEditorId));
        OnPropertyChanged(nameof(HasNoActiveAdapter));
        OnPropertyChanged(nameof(ActiveAdapterText));
        NotifyEditorPageProperties();
        OnPropertyChanged(nameof(SpeedStatusText));
        OnPropertyChanged(nameof(HasScanSession));
        OnPropertyChanged(nameof(CanUndoScan));
        OnPropertyChanged(nameof(ActiveGameIcon));
    }

    private void DisconnectSession(GameConnectionSession session, bool clearActiveView)
    {
        StopSessionLockMaintenance(session);
        if (ReferenceEquals(_activeSession, session))
        {
            CaptureActiveSession();
            StopLiveCandidateRefresh();
            _scanCancellation?.Cancel();
        }
        try { session.SpeedService.DetachSafely(); }
        finally { session.SpeedService.Dispose(); }
        DisposeSessionScanState(session);
        if (session.GameId is Guid gameId)
        {
            _sessions.Remove(gameId);
            var game = Games.FirstOrDefault(item => item.Id == gameId);
            if (game is not null) game.IsConnected = false;
        }
        if (ReferenceEquals(_activeSession, session) && clearActiveView) ClearActiveView(SelectedGame);
        else if (ReferenceEquals(_activeSession, session))
        {
            _activeSession = null;
            _speedService = _idleSpeedService;
        }
        UpdateCurrentVersionMarkers(null, null);
        GamesView.Refresh();
        NotifyConnectionControls();
        NotifyLibraryControls();
    }

    private static bool IsProcessRunning(ProcessItem process)
    {
        try
        {
            using var native = Process.GetProcessById(process.ProcessId);
            return !native.HasExited && native.StartTime.ToUniversalTime() == process.StartTimeUtc;
        }
        catch
        {
            return false;
        }
    }

    private ICollectionView CreateGamesView()
    {
        var view = CollectionViewSource.GetDefaultView(Games);
        view.Filter = item => item is GameProfile game && IsGameVisible(game);
        if (view is ListCollectionView listView) listView.CustomSort = new GameProfileConnectionComparer(SelectedLibrarySort.Value);
        return view;
    }

    private void ApplyGameLibrarySort()
    {
        if (GamesView is ListCollectionView listView)
            listView.CustomSort = new GameProfileConnectionComparer(SelectedLibrarySort.Value);
        GamesView.Refresh();
    }

    private bool ReconcileInstalledModulesWithLibrary()
    {
        var changed = false;
        var installedManifests = _moduleCatalogService.GetInstalledManifests();
        var installedIds = installedManifests.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var game in Games) game.IsModuleInstalled = false;

        foreach (var manifest in installedManifests)
        {
            var game = Games.FirstOrDefault(item => string.Equals(item.ModuleId, manifest.Id, StringComparison.Ordinal))
                       ?? Games.FirstOrDefault(item => manifest.ProcessNames.Any(name =>
                           string.Equals(name, item.ProcessName, StringComparison.OrdinalIgnoreCase)))
                       ?? Games.FirstOrDefault(item => item.Versions.Any(version =>
                           manifest.CompatibleBuilds.Any(build => InstalledBuildMatches(build, version))));
            if (game is null)
            {
                game = new GameProfile
                {
                    Name = string.IsNullOrWhiteSpace(manifest.GameDisplayName) ? manifest.DisplayName : manifest.GameDisplayName,
                    ProcessName = manifest.ProcessNames.FirstOrDefault() ?? string.Empty,
                    ModuleId = manifest.Id,
                    LastUsedUtc = DateTime.UtcNow,
                    IconSource = DefaultGameIcon
                };
                Games.Add(game);
                changed = true;
            }
            else if (!string.Equals(game.ModuleId, manifest.Id, StringComparison.Ordinal))
            {
                game.ModuleId = manifest.Id;
                changed = true;
            }
            game.IsModuleInstalled = true;
        }

        foreach (var game in Games)
            game.IsModuleInstalled = !string.IsNullOrWhiteSpace(game.ModuleId) && installedIds.Contains(game.ModuleId);
        return changed;
    }

    private static bool InstalledBuildMatches(GameModuleBuildMatch build, GameVersionProfile version) =>
        MatchOptionalHash(build.BuildFingerprint, version.BuildFingerprint) &&
        MatchOptionalHash(build.ExecutableSha256, version.ExecutableSha256) &&
        MatchOptionalHash(build.GameAssemblySha256, version.GameAssemblySha256) &&
        MatchOptionalHash(build.MetadataSha256, version.MetadataSha256);

    private static bool MatchOptionalHash(string expected, string actual) =>
        string.IsNullOrWhiteSpace(expected) || string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

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
        var version = new GameVersionProfile { CollectedUtc = DateTime.UtcNow };
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
        version.PlatformName = fingerprint.PlatformName;
        version.PlatformAppId = fingerprint.PlatformAppId;
        version.PlatformBuildId = fingerprint.PlatformBuildId;
        version.PlatformDisplayName = fingerprint.PlatformDisplayName;
        version.FileSize = fingerprint.FileSize;
        version.Architecture = fingerprint.Architecture;
        version.LastVerifiedUtc = DateTime.UtcNow;
    }

    private bool ApplyGameDeclaredMetadata(GameVersionProfile version, ProcessItem process)
    {
        if (_activeAdapter is not IGameVersionMetadataProvider provider) return false;
        try
        {
            var metadata = provider.ReadGameVersionMetadata(process.ToModuleContext());
            var changed = !string.Equals(version.GameDeclaredVersion, metadata.Version, StringComparison.Ordinal) ||
                          !string.Equals(version.GameDeclaredProductName, metadata.ProductName, StringComparison.Ordinal) ||
                          !string.Equals(version.GameDeclaredBuildGuid, metadata.BuildGuid, StringComparison.Ordinal);
            version.GameDeclaredVersion = metadata.Version;
            version.GameDeclaredProductName = metadata.ProductName;
            version.GameDeclaredBuildGuid = metadata.BuildGuid;
            version.NotifyChoiceChanged();
            return changed;
        }
        catch
        {
            // Optional metadata must never prevent connecting to or editing a supported game build.
            return false;
        }
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

    private static int MigrateAdapterFields(GameProfile game, GameVersionProfile target, string adapterId)
    {
        var source = game.Versions
            .Where(version => !ReferenceEquals(version, target) && version.Fields.Any(field =>
                field.LocatorKind == "GameAdapter" && string.Equals(field.AdapterId, adapterId, StringComparison.Ordinal)))
            .OrderByDescending(version => version.LastVerifiedUtc)
            .FirstOrDefault();
        if (source is null) return 0;

        var existingKeys = target.Fields
            .Where(field => field.LocatorKind == "GameAdapter" && string.Equals(field.AdapterId, adapterId, StringComparison.Ordinal))
            .Select(field => field.AdapterFieldKey)
            .ToHashSet(StringComparer.Ordinal);
        var cloned = CloneAdapterFields(source, adapterId)
            .Where(field => existingKeys.Add(field.AdapterFieldKey))
            .ToList();
        foreach (var field in cloned) target.Fields.Add(field);
        if (cloned.Count > 0)
        {
            target.PreferredSearchRoutineId = source.PreferredSearchRoutineId;
            target.PreferredScaleMultiplier = source.PreferredScaleMultiplier;
        }
        return cloned.Count;
    }

    private static string GetVersionIdentity(GameVersionProfile version) =>
        string.IsNullOrWhiteSpace(version.BuildFingerprint) ? version.ExecutableSha256 : version.BuildFingerprint;

    private void UpdateCurrentVersionMarkers(GameProfile? currentGame, string? buildFingerprint)
    {
        foreach (var game in Games)
        foreach (var version in game.Versions)
        {
            var sessionFingerprint = _sessions.TryGetValue(game.Id, out var session)
                ? session.Fingerprint?.BuildSha256
                : ReferenceEquals(game, currentGame) ? buildFingerprint : null;
            version.IsCurrentBuild = !string.IsNullOrWhiteSpace(sessionFingerprint) &&
                                     string.Equals(GetVersionIdentity(version), sessionFingerprint, StringComparison.OrdinalIgnoreCase);
        }
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

    private GameEditorDescriptor? ActiveCharacterEditorDescriptor => _characterEditorPage?.Descriptor;

    private void RebuildCharacterAttributesView(AdapterCharacterItem? character)
    {
        _characterAttributesView = character is null
            ? null
            : CollectionViewSource.GetDefaultView(character.Attributes);
        if (_characterAttributesView is not null)
            _characterAttributesView.Filter = item =>
                item is AdapterCharacterAttribute attribute && MatchesCharacterAttributeFilter(attribute);
        OnPropertyChanged(nameof(CharacterAttributesView));
        SelectedCharacterAttribute = character?.Attributes.FirstOrDefault(MatchesCharacterAttributeFilter);
    }

    private bool MatchesCharacterAttributeFilter(AdapterCharacterAttribute attribute) =>
        string.IsNullOrWhiteSpace(CharacterAttributeNameFilter) ||
        attribute.DisplayName.Contains(CharacterAttributeNameFilter.Trim(), StringComparison.CurrentCultureIgnoreCase);

    private static string NormalizeGroup(string? group) =>
        string.IsNullOrWhiteSpace(group) ? "未分组" : group.Trim();

    private static void DisposeScanStore(ScanCandidateStore? store) => store?.Dispose();

    private static void DisposeScanHistory(Stack<ScanCandidateStore> history)
    {
        while (history.TryPop(out var store)) store.Dispose();
    }

    private static void DisposeSessionScanState(GameConnectionSession session)
    {
        DisposeScanStore(session.ScanCandidates);
        session.ScanCandidates = null;
        DisposeScanHistory(session.ScanHistory);
        session.VisibleScanResults = [];
        session.SelectedScanResult = null;
    }

    private void StartLiveCandidateRefresh()
    {
        StopLiveCandidateRefresh();
        if (IsBusy || AttachedProcess is null || _scanCandidates?.Count is not (> 0 and < 100)) return;
        var process = AttachedProcess;
        var candidates = VisibleScanResults.ToList();
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
        var session = _activeSession;
        if (session is null) return;
        StopSessionLockMaintenance(session);
        if (AttachedProcess is null || SelectedVersion?.Fields.Any(field => field.IsValueLocked) != true) return;
        if (SelectedGame is null || _attachedGameId != SelectedGame.Id ||
            _attachedFingerprint is null ||
            !VersionMatches(SelectedVersion, _attachedFingerprint)) return;
        var process = AttachedProcess;
        var version = SelectedVersion;
        var cancellation = new CancellationTokenSource();
        session.LockMaintenanceCancellation = cancellation;
        _ = Task.Run(() => MaintainLockedValuesAsync(process, version, session.Adapter, cancellation.Token));
    }

    private void StopLockMaintenance()
    {
        if (_activeSession is not null) StopSessionLockMaintenance(_activeSession);
    }

    private static void StopSessionLockMaintenance(GameConnectionSession session)
    {
        session.LockMaintenanceCancellation?.Cancel();
        session.LockMaintenanceCancellation?.Dispose();
        session.LockMaintenanceCancellation = null;
    }

    private async Task MaintainLockedValuesAsync(
        ProcessItem process,
        GameVersionProfile version,
        IGameAdapter? sessionAdapter,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IReadOnlyList<SavedField> lockedFields = [];
                await RunOnUiAsync(() => lockedFields = version.Fields
                    .Where(field => field.IsValueLocked && GetAdapterFieldPolicy(sessionAdapter, field).CanLock &&
                                    (field.LocatorKind != "GameAdapter" || sessionAdapter is not null)).ToList());
                if (lockedFields.Count == 0) return;

                foreach (var field in lockedFields)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (field.LocatorKind == "GameAdapter")
                        {
                            var adapter = ResolveFieldAdapter(field, sessionAdapter);
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

    private GameEditorFieldPolicy GetSelectedCharacterFieldPolicy()
    {
        if (_characterEditorPage is null || SelectedAdapterCharacter is null || SelectedCharacterAttribute is null)
        {
            var sessionOnly = _characterEditorPage?.Descriptor.SessionOnly == true;
            return new(sessionOnly, !sessionOnly);
        }
        return GetAdapterFieldPolicy(
            _activeAdapter,
            _characterEditorPage.Descriptor.Id,
            SelectedAdapterCharacter.CharacterId,
            SelectedCharacterAttribute.Key);
    }

    private static GameEditorFieldPolicy GetAdapterFieldPolicy(IGameAdapter? adapter, SavedField field)
    {
        if (!string.Equals(field.LocatorKind, "GameAdapter", StringComparison.Ordinal) ||
            !ModuleFieldKey.TryParse(field.AdapterFieldKey, out var editorId, out var entityId, out var fieldId))
            return new(false, true);
        return GetAdapterFieldPolicy(adapter, editorId, entityId, fieldId);
    }

    private static GameEditorFieldPolicy GetAdapterFieldPolicy(
        IGameAdapter? adapter,
        string editorId,
        string entityId,
        string fieldId)
    {
        var descriptor = adapter?.Editors.FirstOrDefault(editor =>
            string.Equals(editor.Id, editorId, StringComparison.Ordinal));
        var fallbackSessionOnly = descriptor?.SessionOnly == true;
        return adapter is IGameEditorFieldPolicyProvider provider
            ? provider.GetFieldPolicy(editorId, entityId, fieldId) ?? new(fallbackSessionOnly, !fallbackSessionOnly)
            : new(fallbackSessionOnly, !fallbackSessionOnly);
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
        CaptureActiveSession();
        var sessions = _sessions.Values
            .Append(_activeSession)
            .Where(session => session is not null)
            .Cast<GameConnectionSession>()
            .Distinct()
            .ToList();
        foreach (var session in sessions)
        {
            StopSessionLockMaintenance(session);
            session.SpeedService.DetachSafely();
            session.SpeedService.Dispose();
            DisposeSessionScanState(session);
        }
        _sessions.Clear();
        _activeAdapter = null;
        DisposeEditorPages();
        _adapterRegistry.Dispose();
        _idleSpeedService.Dispose();
    }

    private static bool PathsEqual(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
    }

}

public sealed record Choice<T>(T Value, string Display);
