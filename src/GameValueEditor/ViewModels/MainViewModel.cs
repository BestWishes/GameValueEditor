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
using System.Windows.Threading;
using GameValueEditor.Infrastructure;
using GameValueEditor.Models;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Services;
using GameValueEditor.Services.Adapters;

namespace GameValueEditor.ViewModels;

public sealed partial class MainViewModel : ObservableObject
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
    private readonly FieldOperationCoordinator _fieldOperations = new();
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
    private int _speedOperationsInFlight;
    private bool _isProcessRefreshBlocked;
    private bool _isConnectionControlBlocked;
    private bool _isLibraryControlBlocked;
    private long _scanResultCount;
    private CancellationTokenSource? _scanCancellation;
    private long _gameOperationGeneration;
    private bool _isShuttingDown;
    private bool _suppressGameActivation;
    private string _moduleStatusText = "尚未检查当前游戏的专属模块";
    private bool _isModuleControlBlocked;
    private GameModuleCheckResult? _moduleCheckResult;
    private long _moduleViewGeneration;
    private ModuleOperationContext? _moduleCheckContext;
    private bool _moduleRestartRequired;
    private string _applicationUpdateStatusText = string.Empty;
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

    internal Action CaptureEditorHostOperation()
    {
        var operation = CaptureGameOperation();
        return () => RequireCurrentGameOperation(operation);
    }

    internal Func<int, IMemoryWriteAccess> MemoryWriteAccessFactory { get; set; } = processId => new ProcessMemoryAccessor(processId);

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
            InvalidateGameOperations();
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
            InvalidateGameOperations();
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
            InvalidateGameOperations();
            ResetModuleCheckState();
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
    public bool CanRemoveCurrentGame => !_isLibraryControlBlocked && SelectedGame is { IsPinned: false, IsLocked: false } &&
        (string.IsNullOrWhiteSpace(SelectedGame.ModuleId) || _moduleCatalogService.StorageErrors.Count == 0);
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
    public bool CanCheckGameModules => !IsDownloadActive && !_isModuleControlBlocked &&
        ((AttachedProcess is not null && _attachedFingerprint is not null) ||
         (SelectedGame is not null && SelectedVersion is not null));
    public bool CanInstallGameModule => _moduleCatalogService.StorageErrors.Count == 0 && !IsDownloadActive && !_isModuleControlBlocked && IsModuleContextCurrent(_moduleCheckContext) &&
        _moduleCheckResult?.Availability is GameModuleAvailability.Available or GameModuleAvailability.UpdateAvailable;
    public string ModuleInstallActionText => _moduleCheckResult?.Availability == GameModuleAvailability.Available ? "下载" : "更新";
    public string ModuleInstallToolTip => _moduleCheckResult?.Availability == GameModuleAvailability.Available
        ? _moduleCheckResult.IsExactBuildMatch ? "可下载" : "下载后验证"
        : _moduleCheckResult?.IsExactBuildMatch == false ? "更新后验证" : "可更新";
    public bool CanRollbackGameModule => _moduleCatalogService.StorageErrors.Count == 0 && !IsDownloadActive && !_isModuleControlBlocked && IsModuleContextCurrent(_moduleCheckContext) &&
        _moduleCheckResult?.RollbackModule is not null;
    public string ModuleRollbackActionText => _moduleCheckResult?.RollbackModule is { } rollback
        ? $"回退到 v{rollback.Version}"
        : "无可回退版本";
    public bool ModuleRestartRequired => _moduleRestartRequired;
    public bool CanUninstallGameModule => _moduleCatalogService.StorageErrors.Count == 0 && !_isModuleControlBlocked &&
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
    public bool CanCheckApplicationUpdate => !IsDownloadActive && !_isApplicationUpdateBusy && !_applicationUpdateDownloaded && !_isApplicationUpdateCheckCooldown;
    public bool CanUseApplicationUpdate => !IsDownloadActive && !_isApplicationUpdateBusy && HasApplicationUpdateAvailable;
    public bool HasApplicationUpdateAvailable => _applicationUpdateResult?.IsUpdateAvailable == true && !_applicationUpdateDownloaded;
    public bool CanUseApplicationRollback => !IsDownloadActive && !_isApplicationUpdateBusy && !_applicationUpdateDownloaded &&
        _applicationUpdateResult?.RollbackTarget is not null;
    public string ApplicationRollbackActionText => _applicationUpdateResult?.RollbackTarget is { } rollback
        ? $"回退到 v{rollback.Version}"
        : "无可回退版本";
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
        var snapshot = _processService.GetProcesses();
        var group = _processService.ResolveLogicalGame(process, snapshot);
        var identity = new GameIdentityEvidenceService().Read(group, snapshot);
        var game = ResolveGameForProcess(group.DataProcess, new("", "", "", "", 0, "", "", "", "", Identity: identity), group: group);
        if (preferredGame is not null && !ReferenceEquals(game, preferredGame))
            throw new InvalidOperationException("所选进程的游戏名称与当前条目不同，请选择该游戏的实际运行进程。");
        AttachCore(process, group, game);
    }

    private void AttachCore(ProcessItem process, LogicalGameProcessGroup group, GameProfile? matchingGame)
    {
        var dataProcess = group.DataProcess;
        using var rootProcess = Process.GetProcessById(group.RootProcess.ProcessId);
        if (rootProcess.HasExited || rootProcess.StartTime.ToUniversalTime() != group.RootProcess.StartTimeUtc)
            throw new InvalidOperationException("游戏主进程已经退出或重新启动，请重新选择。");
        using var memory = new ProcessMemoryAccessor(dataProcess.ProcessId);
        memory.EnsureInstance(dataProcess.ProcessId, dataProcess.StartTimeUtc);
        CaptureActiveSession();
        StopLiveCandidateRefresh();
        if (_activeSession is { GameId: null } transient && !transient.ProcessGroup.IsSameInstance(group))
            DisconnectSession(transient, false);
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
            await ConnectToProcessAsync(process);
        }
        finally { ReleaseConnectionControlsAfter(cooldown); }
    }

    public Task AttachSelectedGameAsync() => AttachSelectedGameWithAssociationAsync(null);

    internal async Task AttachSelectedGameWithAssociationAsync(Func<GameAssociationRequest, ProcessItem?>? confirmAssociation)
    {
        var game = SelectedGame ?? throw new InvalidOperationException("请先选择游戏条目。");
        if (!CanConnectSelectedGame) throw new InvalidOperationException("所选游戏已经连接，或连接操作尚未完成。");
        var cooldown = BeginConnectionControlInteraction();
        try
        {
            var operation = CaptureGameOperation();
            var snapshot = _processService.GetProcesses();
            var candidates = _processService.FindGameCandidates(game, snapshot, _adapterRegistry.GetGameNames(game.ModuleId));
            var verified = new List<PreparedGameConnection>();
            foreach (var candidate in candidates)
            {
                PreparedGameConnection prepared;
                try { prepared = await PrepareGameConnectionAsync(operation, candidate, snapshot); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
                if (!ReferenceEquals(prepared.Game, game)) continue;
                verified.Add(prepared);
            }
            if (verified.Count == 1)
            {
                await CompleteGameConnectionAsync(verified[0], game);
                return;
            }
            RequireCurrentGameOperation(operation);
            if (confirmAssociation is null)
                throw new InvalidOperationException("没有找到可确认的游戏进程。请从游戏库连接并确认实际进程，或在顶部选择进程连接。");
            var selected = confirmAssociation(new(game.Name, snapshot, candidates));
            RequireCurrentGameOperation(operation);
            if (selected is null) return;
            if (!Games.Contains(game) || !snapshot.Any(item => item.ProcessId == selected.ProcessId && item.StartTimeUtc == selected.StartTimeUtc))
                throw new OperationCanceledException("游戏条目或候选进程已失效，请重新选择。");
            // Re-snapshot after the modal dialog: a PID/ancestor may have been replaced meanwhile.
            var refreshed = _processService.GetProcesses();
            var current = refreshed.FirstOrDefault(item => item.ProcessId == selected.ProcessId && item.StartTimeUtc == selected.StartTimeUtc)
                          ?? throw new InvalidOperationException("所选进程已经退出或重新启动，请重新选择。");
            var confirmed = await PrepareGameConnectionAsync(operation, current, refreshed);
            if (confirmed.Game is not null && !ReferenceEquals(confirmed.Game, game))
                throw new InvalidOperationException($"所选进程已确认属于游戏库中的“{confirmed.Game.Name}”，不会覆盖“{game.Name}”。");
            if (confirmed.Adapter is not null && !string.IsNullOrWhiteSpace(game.ModuleId) &&
                game.ModuleId != confirmed.Adapter.Id && !confirmed.Adapter.LegacyIds.Contains(game.ModuleId, StringComparer.Ordinal))
                throw new InvalidOperationException("所选进程的已验证模块身份与当前游戏不同，不能关联。");
            await CompleteGameConnectionAsync(confirmed, game);
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

    private async Task ConnectToProcessAsync(ProcessItem process, GameProfile? preferredGame = null)
    {
        var operation = CaptureGameOperation();
        var prepared = await PrepareGameConnectionAsync(operation, process, _processService.GetProcesses());
        if (preferredGame is not null && !ReferenceEquals(prepared.Game, preferredGame))
            throw new InvalidOperationException("候选进程的游戏名称与所选条目不同，请选择实际游戏进程。");
        await CompleteGameConnectionAsync(prepared, preferredGame ?? prepared.Game);
    }

    private sealed record PreparedGameConnection(ProcessItem Seed, LogicalGameProcessGroup Group,
        VersionFingerprint Fingerprint, IGameAdapter? Adapter, GameProfile? Game);

    private async Task<PreparedGameConnection> PrepareGameConnectionAsync(GameOperationContext operation, ProcessItem process,
        IReadOnlyList<ProcessItem> snapshot)
    {
        var group = _processService.ResolveLogicalGame(process, snapshot);
        var fingerprint = await AwaitGameOperationAsync(operation, _fingerprintService.CreateAsync(group.DataProcess.ExecutablePath));
        var identity = new GameIdentityEvidenceService().Read(group, snapshot);
        if (fingerprint.Identity?.PackageId != identity.PackageId)
            throw new IOException("游戏包在识别期间发生变化，请等待更新完成后重新连接。");
        fingerprint = fingerprint with { Identity = identity };
        if (fingerprint.PlatformAppId.Length == 0 && identity.PlatformAppId.Length > 0)
        {
            var platform = new GameVersionMetadataService().ReadPlatformMetadata(identity.InstallationExecutablePath);
            fingerprint = fingerprint with { PlatformName = platform.PlatformName, PlatformAppId = platform.AppId,
                PlatformBuildId = platform.BuildId, PlatformDisplayName = platform.DisplayName };
        }
        var game = ResolveGameForProcess(group.DataProcess, fingerprint, group: group);
        var adapter = await AwaitGameOperationAsync(operation, _adapterRegistry.ResolveAsync(group.DataProcess, fingerprint));
        RequireCurrentGameOperation(operation);
        return new(process, group, fingerprint, adapter, game);
    }

    private async Task CompleteGameConnectionAsync(PreparedGameConnection prepared, GameProfile? game)
    {
        var owner = game is null ? null : _sessions.Values.FirstOrDefault(session => session.GameId != game.Id &&
            session.ProcessGroup.IsSameInstance(prepared.Group) && Games.Any(profile => profile.Id == session.GameId));
        if (owner is not null)
            throw new InvalidOperationException("所选游戏实例已经关联到另一个游戏库条目，不能抢占已有连接。");
        var checkpoint = game is null ? null : new GameConnectionProfileCheckpoint(game);
        var durablySaved = false;
        try
        {
            AttachCore(prepared.Seed, prepared.Group, game);
            await MatchAttachedVersionCoreAsync(prepared.Fingerprint, game, prepared.Adapter, adapterPrepared: true,
                durableSaveCompleted: () => durablySaved = true);
        }
        catch
        {
            // A failed durable save must not leave newly learned identity active as if persisted.
            if (game is not null && !durablySaved)
            {
                checkpoint!.Restore();
                if (_sessions.TryGetValue(game.Id, out var session) && session.ProcessGroup.IsSameInstance(prepared.Group))
                    DisconnectSession(session, ReferenceEquals(_activeSession, session));
            }
            throw;
        }
    }

    public Task MatchAttachedVersionAsync() => MatchAttachedVersionCoreAsync();

    private async Task MatchAttachedVersionCoreAsync(VersionFingerprint? existingFingerprint = null, GameProfile? associatedGame = null,
        IGameAdapter? preparedAdapter = null, bool adapterPrepared = false, Action? durableSaveCompleted = null)
    {
        var operation = CaptureGameOperation();
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        var fingerprint = existingFingerprint ?? await AwaitGameOperationAsync(operation, _fingerprintService.CreateAsync(process.ExecutablePath));
        if (existingFingerprint is null && fingerprint.Identity is not null)
            fingerprint = fingerprint with { Identity = GameIdentityResolver.Merge(_activeSession?.Fingerprint?.Identity, fingerprint.Identity) };
        RequireCurrentGameOperation(operation);
        var adapter = adapterPrepared ? preparedAdapter : await AwaitGameOperationAsync(operation, _adapterRegistry.ResolveAsync(process, fingerprint));
        if (adapter is not null && !ReferenceEquals(_adapterRegistry.FindById(adapter.Id), adapter)) adapter = null;
        var metadata = await ReadGameDeclaredMetadataAsync(operation, process, adapter);
        RequireCurrentGameOperation(operation);
        if (!IsProcessRunning(process)) throw new InvalidOperationException("游戏进程已经退出或重启，请重新连接。");
        if (adapter is not null && !ReferenceEquals(_adapterRegistry.FindById(adapter.Id), adapter)) adapter = null;
        _attachedFingerprint = fingerprint;
        if (_activeSession is not null) _activeSession.Fingerprint = fingerprint;
        SetActiveAdapter(adapter);
        var game = associatedGame ?? ResolveGameForProcess(process, fingerprint, _activeAdapter);
        if (game is null)
        {
            SelectedVersion = null;
            ResetModuleCheckState();
            StatusText = _activeAdapter is null
                ? "这个游戏尚未进入游戏库；可使用通用扫描或点击“查新”"
                : $"已加载 {_activeAdapter.DisplayName}；下载模块后会自动保存入库";
            return;
        }

        BindActiveSessionToGame(game);
        _suppressGameActivation = true;
        try { SelectedGame = game; }
        finally { _suppressGameActivation = false; }
        _attachedGameId = game.Id;
        operation = CaptureGameOperation();
        var libraryChanged = await UpgradeLegacyVersionFingerprintsAsync(game);
        RequireCurrentGameOperation(operation);
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
        libraryChanged |= ApplyGameDeclaredMetadata(version, metadata);
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
        libraryChanged |= UpdateGameIdentity(game, fingerprint.Identity);
        game.LastUsedUtc = DateTime.UtcNow;
        game.NotifySummaryChanged();
        GamesView.Refresh();
        operation = CaptureGameOperation();
        if (libraryChanged)
        {
            RefreshLibraryModuleLoadStates();
            await _profileStore.SaveAsync(_document, durableSaveCompleted);
        }
        RequireCurrentGameOperation(operation);
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

    private async Task<GameProfile> AddCurrentProcessToLibraryCoreAsync(string userName, ProcessItem process,
        VersionFingerprint? existingFingerprint = null)
    {
        var operation = CaptureGameOperation();
        var fingerprint = existingFingerprint ?? await AwaitGameOperationAsync(operation, _fingerprintService.CreateAsync(process.ExecutablePath));
        if (existingFingerprint is null && fingerprint.Identity is not null)
            fingerprint = fingerprint with { Identity = GameIdentityResolver.Merge(_activeSession?.Fingerprint?.Identity, fingerprint.Identity) };
        RequireCurrentGameOperation(operation);
        var adapter = await AwaitGameOperationAsync(operation, _adapterRegistry.ResolveAsync(process, fingerprint));
        var metadata = await ReadGameDeclaredMetadataAsync(operation, process, adapter);
        RequireCurrentGameOperation(operation);
        if (!IsProcessRunning(process)) throw new InvalidOperationException("游戏进程已经退出或重启，请重新连接。");
        if (adapter is not null && !ReferenceEquals(_adapterRegistry.FindById(adapter.Id), adapter)) adapter = null;
        _attachedFingerprint = fingerprint;
        SetActiveAdapter(adapter);
        if (_activeSession is not null)
        {
            _activeSession.Fingerprint = fingerprint;
            _activeSession.Adapter = _activeAdapter;
        }

        var game = ResolveGameForProcess(process, fingerprint, _activeAdapter);
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
        UpdateGameIdentity(game, fingerprint.Identity);
        if (_activeAdapter is not null)
        {
            game.ModuleId = _activeAdapter.Id;
            game.IsModuleInstalled = _moduleCatalogService.FindInstalled(_activeAdapter.Id) is not null;
        }
        _attachedGameId = game.Id;
        game.IsConnected = true;
        _gameIconService.Save(game, process.Icon);
        game.IconSource ??= DefaultGameIcon;
        BindActiveSessionToGame(game);

        var version = FindMatchingVersion(game, fingerprint);
        if (version is null)
        {
            version = CreateVersionProfile(fingerprint);
            game.Versions.Add(version);
        }
        else ApplyFingerprint(version, fingerprint);
        ApplyGameDeclaredMetadata(version, metadata);
        if (_activeAdapter is not null) MigrateAdapterFields(game, version, _activeAdapter.Id);

        UpdateCurrentVersionMarkers(game, fingerprint.BuildSha256);

        SelectedGame = game;
        SelectedVersion = version;
        if (_activeSession is not null) _activeSession.VersionId = version.Id;
        OnPropertyChanged(nameof(ActiveGameIcon));
        game.NotifySummaryChanged();
        version.NotifyChoiceChanged();
        GamesView.Refresh();
        var savedOperation = CaptureGameOperation();
        await SaveLibraryAsync();
        if (IsCurrentGameOperation(savedOperation))
        {
            RestartLockMaintenance();
            StatusText = $"已保存入库：{game.Name} · {version.DisplayName}";
        }
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
        if (!string.IsNullOrWhiteSpace(game.ModuleId) && _moduleCatalogService.StorageErrors.Count > 0)
            throw new InvalidOperationException("模块资料无法安全读取或安装恢复未完成，暂不能移出关联游戏。请先打开“诊断”检查并恢复资料。");
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
                    game.IsModuleInstalled = false;
                }
                finally
                {
                    _isModuleControlBlocked = false;
                    NotifyModuleControls();
                }
            }
            var keptConnection = _sessions.TryGetValue(game.Id, out var session) &&
                                 ReferenceEquals(_activeSession, session) && ReferenceEquals(SelectedGame, game);
            if (keptConnection && session is not null)
            {
                CaptureActiveSession();
                StopSessionLockMaintenance(session);
                _sessions.Remove(game.Id);
                session.GameId = null;
                session.VersionId = null;
                game.IsConnected = false;
                _attachedGameId = null;
                _suppressGameActivation = true;
                try { SelectedGame = null; }
                finally { _suppressGameActivation = false; }
                RestoreSessionIntoView(session, null);
            }
            else if (session is not null)
            {
                if (session.IsSpeedOperationRunning)
                    throw new InvalidOperationException("待移出的游戏正在调整倍速，档案和连接已保留，请稍后重新移出。");
                DisconnectSession(session, false);
            }

            Games.Remove(game);
            if (ReferenceEquals(SelectedGame, game)) SelectedGame = Games.FirstOrDefault();
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
        var operation = CaptureGameOperation();
        var source = _scanCandidates;
        var comparison = SelectedComparison;
        var scanValue = ScanValue;
        if (!isNewScan && _scanCandidates is not { Count: > 0 })
            throw new InvalidOperationException("请先执行首次扫描。");

        StopLiveCandidateRefresh();
        _scanCancellation?.Cancel();
        _scanCancellation = new CancellationTokenSource();
        var cancellation = _scanCancellation;
        IsBusy = true;
        ProgressPercentage = 0;
        StatusText = isNewScan ? "正在扫描可读内存区域…" : "正在过滤候选地址…";
        var progress = new Progress<ScanProgress>(item =>
        {
            if (!IsCurrentGameOperation(operation) || !IsBusy || !ReferenceEquals(_scanCancellation, cancellation)) return;
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
                    cancellation.Token);
            }
            else
            {
                Func<ScanCandidatePartition, byte[]?> exactTargetFactory = partition =>
                {
                    if (comparison != ScanComparison.Exact) return null;
                    return MemoryValueCodec.TryParseEncoded(scanValue, partition.ValueType, partition.ScaleMultiplier, out var bytes)
                        ? bytes
                        : null;
                };
                if (comparison == ScanComparison.Exact &&
                    source!.Partitions.Any(partition => exactTargetFactory(partition) is null))
                    throw new InvalidOperationException("输入值无法转换为当前扫描会话中的某些类型或套路。");
                result = await _scanService.NextScanAsync(
                    process.ProcessId,
                    source!,
                    comparison,
                    exactTargetFactory,
                    progress,
                    cancellation.Token);
            }

            if (!IsCurrentGameOperation(operation) || cancellation.IsCancellationRequested)
            {
                result.Candidates.Dispose();
                if (IsCurrentGameOperation(operation)) StatusText = "扫描已取消";
                return;
            }
            if (result.Candidates.ProcessId != process.ProcessId || result.Candidates.ProcessStartTimeUtc != process.StartTimeUtc)
            {
                result.Candidates.Dispose();
                throw new InvalidOperationException("扫描期间目标进程实例已变化，请重新连接。");
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
            if (IsCurrentGameOperation(operation)) StatusText = "扫描已取消";
        }
        catch (Exception) when (!IsCurrentGameOperation(operation)) { }
        finally
        {
            if (ReferenceEquals(_scanCancellation, cancellation))
            {
                _scanCancellation = null;
                IsBusy = false;
                ProgressPercentage = 0;
                if (!_isShuttingDown) StartLiveCandidateRefresh();
            }
            cancellation.Dispose();
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
        InvalidateGameOperations();
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
        EnsureScanOwnership([candidate], process);
        EnsureSelectedVersionMatchesAttached();
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("字段备注名称必须由玩家填写。");

        using var memory = new ProcessMemoryAccessor(process.ProcessId);
        memory.EnsureInstance(process.ProcessId, process.StartTimeUtc);
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
        var operation = CaptureGameOperation();
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        var version = SelectedVersion ?? throw new InvalidOperationException("请先选择游戏版本。");
        EnsureSelectedVersionMatchesAttached();
        using var memory = new ProcessMemoryAccessor(process.ProcessId);
        memory.EnsureInstance(process.ProcessId, process.StartTimeUtc);
        var superseded = false;
        var refreshAdapter = _activeAdapter;

        foreach (var field in version.Fields.ToList())
        {
            if (field.LocatorKind == "GameAdapter" && !ReferenceEquals(_activeAdapter, refreshAdapter)) { superseded = true; continue; }
            var target = GetFieldOperationQueue(field, process, version, refreshAdapter);
            using var turn = target.EnterRead();
            await turn.Ready;
            RequireCurrentGameOperation(operation);
            if (!turn.IsCurrent || !version.Fields.Contains(field)) { superseded = true; continue; }
            bool CanApply() => turn.IsCurrent && version.Fields.Contains(field);
            if (field.LocatorKind == "GameAdapter")
            {
                var adapter = ResolveFieldAdapter(field, refreshAdapter);
                bool IsAdapterCurrent() => ReferenceEquals(_activeAdapter, adapter);
                if (!IsAdapterCurrent()) { superseded = true; continue; }
                try
                {
                    var current = await Task.Run(() => adapter.ReadField(process, field.AdapterFieldKey));
                    RequireCurrentGameOperation(operation);
                    if (!CanApply() || !IsAdapterCurrent()) { superseded = true; continue; }
                    field.CurrentValue = current.DisplayValue;
                    field.Status = field.IsValueLocked && IsUncoordinatedPageAdapter(adapter)
                        ? $"锁定已暂停：此版本模块页面未接入协调写入，请更新模块 · {current.Status}" : current.Status;
                    field.LastVerifiedUtc = DateTime.UtcNow;
                }
                catch (Exception exception)
                {
                    RequireCurrentGameOperation(operation);
                    if (!CanApply() || !IsAdapterCurrent()) { superseded = true; continue; }
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
            if (target.NativeAddress != address) { superseded = true; continue; }
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
        if (!superseded) StatusText = "已刷新保存字段";
    }

    public Task WriteSelectedCandidateAsync(string value)
    {
        var candidate = SelectedScanResult ?? throw new InvalidOperationException("请先选择一个扫描结果。");
        return WriteCandidatesAsync([candidate], value);
    }

    public async Task WriteCandidatesAsync(IReadOnlyList<ScanCandidate> candidates, string value)
    {
        var operation = CaptureGameOperation();
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        if (candidates.Count == 0) throw new InvalidOperationException("请至少选择一个扫描结果。");
        EnsureScanOwnership(candidates, process);
        var writes = candidates.Select(candidate =>
        {
            if (!MemoryValueCodec.TryParseEncoded(value, candidate.ValueType, candidate.ScaleMultiplier, out var bytes))
                throw new InvalidOperationException($"输入值无法按 {candidate.AddressDisplay} 的类型和搜索套路进行编码。");
            return (Candidate: candidate, Bytes: bytes);
        }).ToList();

        StopLiveCandidateRefresh();
        try
        {
            using var memory = MemoryWriteAccessFactory(process.ProcessId);
            memory.EnsureInstance(process.ProcessId, process.StartTimeUtc);
            var counts = new int[4];
            var warnings = new List<string>();
            foreach (var write in writes)
            {
                using var turn = GetFieldScope(process).Native(write.Candidate.Address, write.Bytes.Length).EnterWrite();
                await turn.Ready;
                RequireCurrentGameOperation(operation);
                EnsureScanOwnership([write.Candidate], process);
                memory.EnsureInstance(process.ProcessId, process.StartTimeUtc);
                var version = FindRunningVersion(process);
                var aliases = NativeAliases(version, memory, process, write.Candidate.Address, write.Bytes.Length);
                var result = MemoryWriteVerifier.Write(memory, write.Candidate.Address, write.Bytes);
                ApplyNativeAliases(version, aliases, process, write.Candidate.Address, write.Bytes.Length, result);
                if (aliases.Count != 0) { await SaveLibraryAsync(); RequireCurrentGameOperation(operation); }
                counts[(int)result.State]++;
                write.Candidate.CurrentBytes = result.CurrentBytes;
                if (result.State != MemoryWriteState.ReadbackConfirmed && warnings.Count < 3)
                    warnings.Add($"{write.Candidate.AddressDisplay}: {result.Description}");
            }

            StatusText = $"回读一致 {counts[(int)MemoryWriteState.ReadbackConfirmed]:N0}，回读失败 {counts[(int)MemoryWriteState.ReadbackFailed]:N0}，回读不一致 {counts[(int)MemoryWriteState.ReadbackMismatch]:N0}，写入失败 {counts[(int)MemoryWriteState.WriteFailed]:N0}；实际效果请在游戏内确认";
            if (warnings.Count > 0)
                throw new InvalidOperationException(StatusText + "\n" + string.Join("；", warnings));
        }
        finally
        {
            StartLiveCandidateRefresh();
        }
    }

    public async Task WriteSelectedFieldAsync(string value)
    {
        var operation = CaptureGameOperation();
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        var field = SelectedSavedField ?? throw new InvalidOperationException("请先选择字段。");
        var version = SelectedVersion ?? throw new InvalidOperationException("请先选择版本。");
        EnsureSelectedVersionMatchesAttached();
        var adapter = field.LocatorKind == "GameAdapter" ? ResolveFieldAdapter(field) : null;
        var target = GetFieldOperationQueue(field, process, version, adapter);
        using var turn = target.EnterWrite();
        await turn.Ready;
        RequireSavedFieldOperation(operation, version, field);
        RequireSavedFieldAdapter(field, adapter);
        await WriteSavedFieldCoreAsync(operation, process, version, field, adapter, value, target.NativeAddress);
    }

    private FieldOperationCoordinator.Scope GetFieldScope(ProcessItem process, GameVersionProfile? version = null) =>
        _fieldOperations.For(process.ProcessId, process.StartTimeUtc.Ticks,
            version is not null ? (string.IsNullOrEmpty(version.BuildFingerprint)
                ? VersionFingerprintService.CreateBuildFingerprint(version.ExecutableSha256, version.GameAssemblySha256, version.MetadataSha256) : version.BuildFingerprint)
                : _attachedFingerprint?.BuildSha256 ?? string.Empty);

    private FieldOperationCoordinator.Scope.Target GetFieldOperationQueue(SavedField field, ProcessItem? process = null,
        GameVersionProfile? version = null, IGameAdapter? adapter = null)
    {
        process ??= AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        version ??= SelectedVersion;
        var scope = GetFieldScope(process, version);
        if (field.LocatorKind == "GameAdapter")
            return scope.Module(ResolveFieldAdapter(field, adapter).Id, field.AdapterFieldKey);
        using var memory = MemoryWriteAccessFactory(process.ProcessId);
        memory.EnsureInstance(process.ProcessId, process.StartTimeUtc);
        if (!TryResolveAddress(memory, process, field, out var address))
            return scope.Module("unresolved-native", field.Id.ToString()); // No write is allowed until resolution succeeds.
        return scope.Native(address, field.ValueType.Size());
    }

    private sealed record FieldAlias(SavedField Field, bool Locked, string Target);
    private static List<FieldAlias> AdapterAliases(GameVersionProfile? version, IGameAdapter adapter, string key) =>
        version?.Fields.Where(field => field.LocatorKind == "GameAdapter" &&
            (field.AdapterId == adapter.Id || adapter.LegacyIds.Contains(field.AdapterId, StringComparer.Ordinal)) &&
            FieldOperationCoordinator.CanonicalKey(field.AdapterFieldKey) == FieldOperationCoordinator.CanonicalKey(key))
            .Select(field => new FieldAlias(field, field.IsValueLocked, field.LockedValue)).ToList() ?? [];

    private static void ApplyAdapterAliases(GameVersionProfile? version, IReadOnlyList<FieldAlias> aliases,
        ProcessItem process, AdapterFieldValue result, bool locksPaused = false)
    {
        foreach (var alias in aliases)
        {
            var field = alias.Field;
            if (version?.Fields.Contains(field) != true) continue;
            field.CurrentValue = result.DisplayValue;
            field.Status = locksPaused && field.IsValueLocked
                ? $"锁定已暂停：此版本模块页面未接入协调写入，请更新模块 · {result.Status}" : result.Status;
            field.LastVerifiedUtc = DateTime.UtcNow;
            field.ProcessStartTimeUtcTicks = process.StartTimeUtc.Ticks;
            if (alias.Locked && field.IsValueLocked && field.LockedValue == alias.Target) field.LockedValue = result.DisplayValue;
        }
    }

    private sealed record NativeAlias(FieldAlias Alias, ulong Address, int Size);
    private static List<NativeAlias> NativeAliases(GameVersionProfile? version, IMemoryWriteAccess memory,
        ProcessItem process, ulong address, int size) => version?.Fields
        .Where(field => field.LocatorKind != "GameAdapter")
        .Select(field => TryResolveAddress(memory, process, field, out var other)
            ? new NativeAlias(new(field, field.IsValueLocked, field.LockedValue), other, field.ValueType.Size()) : null)
        .OfType<NativeAlias>().Where(alias => Overlaps(address, size, alias.Address, alias.Size)).ToList() ?? [];

    private static bool Overlaps(ulong first, int firstSize, ulong second, int secondSize) =>
        first <= second ? second - first < (ulong)firstSize : first - second < (ulong)secondSize;

    private static void ApplyNativeAliases(GameVersionProfile? version, IReadOnlyList<NativeAlias> aliases,
        ProcessItem process, ulong address, int size, MemoryWriteResult result)
    {
        foreach (var alias in aliases)
        {
            var captured = alias.Alias;
            var field = captured.Field;
            if (version?.Fields.Contains(field) != true) continue;
            var sameRange = alias.Address == address && alias.Size == size;
            if (sameRange)
            {
                field.CurrentValue = result.HasReadback ? MemoryValueCodec.FormatDecoded(result.CurrentBytes, field.ValueType, field.ScaleMultiplier) : "—";
                field.Status = result.Description;
                field.LastAddress = address;
                field.ProcessStartTimeUtcTicks = process.StartTimeUtc.Ticks;
                if (result.HasReadback) field.LastVerifiedUtc = DateTime.UtcNow;
            }
            else if (result.State != MemoryWriteState.WriteFailed) field.Status = "重叠字段已修改，请刷新当前值";
            if (result.State == MemoryWriteState.WriteFailed) continue;
            if (!captured.Locked || !field.IsValueLocked || field.LockedValue != captured.Target) continue;
            if (sameRange && result.State == MemoryWriteState.ReadbackConfirmed) field.LockedValue = field.CurrentValue;
            else
            {
                field.IsValueLocked = false;
                field.LockedValue = string.Empty;
                field.Status += "；已暂停该字段锁定";
            }
        }
    }

    private List<SavedField> ConflictingLocks(ProcessItem process, GameVersionProfile version, SavedField field, IGameAdapter? adapter)
    {
        if (field.LocatorKind == "GameAdapter") return AdapterAliases(version, ResolveFieldAdapter(field, adapter), field.AdapterFieldKey)
            .Where(alias => alias.Locked && alias.Target.Trim() != field.LockedValue.Trim()).Select(alias => alias.Field).ToList();
        using var memory = MemoryWriteAccessFactory(process.ProcessId);
        memory.EnsureInstance(process.ProcessId, process.StartTimeUtc);
        if (!TryResolveAddress(memory, process, field, out var address) ||
            !MemoryValueCodec.TryParseEncoded(field.LockedValue, field.ValueType, field.ScaleMultiplier, out var expected)) return [];
        return NativeAliases(version, memory, process, address, expected.Length).Where(alias =>
        {
            if (!alias.Alias.Locked || ReferenceEquals(alias.Alias.Field, field)) return false;
            var other = alias.Alias.Field;
            if (!MemoryValueCodec.TryParseEncoded(alias.Alias.Target, other.ValueType, other.ScaleMultiplier, out var bytes)) return true;
            var start = Math.Max(address, alias.Address);
            var length = Math.Min(expected.Length - (int)(start - address), bytes.Length - (int)(start - alias.Address));
            return !expected.AsSpan((int)(start - address), length).SequenceEqual(bytes.AsSpan((int)(start - alias.Address), length));
        }).Select(alias => alias.Alias.Field).ToList();
    }

    // Pages bind to the running build, not whichever historical version is selected in the library.
    private GameVersionProfile? FindRunningVersion(ProcessItem process) =>
        ReferenceEquals(AttachedProcess, process) && _attachedFingerprint is { } build && _attachedGameId is Guid gameId
            ? Games.FirstOrDefault(game => game.Id == gameId)?.Versions.Where(v => VersionMatches(v, build))
                .OrderByDescending(v => v.Id == _activeSession?.VersionId).FirstOrDefault() : null;

    private async Task<AdapterFieldValue> WriteAdapterFieldCoordinatedAsync(ProcessItem process, IGameAdapter adapter,
        FieldOperationCoordinator.Scope scope, string key, string value, Action validate, Func<AdapterFieldValue>? write = null)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("模块字段键不能为空。");
        validate();
        using var turn = scope.Module(adapter.Id, key).EnterWrite();
        await turn.Ready;
        validate();
        var version = FindRunningVersion(process);
        var aliases = AdapterAliases(version, adapter, key);
        AdapterFieldValue result;
        try { result = await Task.Run(() => { validate(); return write is not null ? write() : adapter.WriteField(process, key, value); }); }
        catch { validate(); throw; }
        validate();
        ApplyAdapterAliases(version, aliases, process, result, IsUncoordinatedPageAdapter(adapter));
        if (aliases.Count != 0) { await SaveLibraryAsync(); validate(); }
        return result;
    }

    private void RequireCurrentAdapterOperation(GameOperationContext operation, IGameAdapter adapter)
    {
        RequireCurrentGameOperation(operation);
        if (!ReferenceEquals(_activeAdapter, adapter)) throw new OperationCanceledException("原专属模块已停用或替换。");
    }

    private void RequireSavedFieldOperation(GameOperationContext operation, GameVersionProfile version, SavedField field)
    {
        RequireCurrentGameOperation(operation);
        if (!version.Fields.Contains(field)) throw new OperationCanceledException("字段已从原游戏版本中移除，原操作已取消。");
    }

    private void RequireSavedFieldAdapter(SavedField field, IGameAdapter? adapter)
    {
        if (adapter is not null && (!ReferenceEquals(_activeAdapter, adapter) ||
            (field.AdapterId != adapter.Id && !adapter.LegacyIds.Contains(field.AdapterId, StringComparer.Ordinal))))
            throw new OperationCanceledException("字段原专属模块已变化，原操作已取消。");
    }

    private async Task WriteSavedFieldCoreAsync(GameOperationContext operation, ProcessItem process, GameVersionProfile version,
        SavedField field, IGameAdapter? adapter, string value, ulong? expectedAddress)
    {
        if (field.LocatorKind == "GameAdapter")
        {
            var aliases = AdapterAliases(version, adapter!, field.AdapterFieldKey);
            var updated = await AwaitGameOperationAsync(operation, Task.Run(() => adapter!.WriteField(process, field.AdapterFieldKey, value)));
            RequireSavedFieldOperation(operation, version, field);
            RequireSavedFieldAdapter(field, adapter);
            ApplyAdapterAliases(version, aliases, process, updated, IsUncoordinatedPageAdapter(adapter));
            await SaveLibraryAsync();
            RequireSavedFieldOperation(operation, version, field);
            RequireSavedFieldAdapter(field, adapter);
            StatusText = $"已实时修改并保存 {field.Name}";
            return;
        }

        if (!MemoryValueCodec.TryParseEncoded(value, field.ValueType, field.ScaleMultiplier, out var bytes))
            throw new InvalidOperationException("输入值无法按该字段保存的搜索套路进行编码。");

        using var memory = MemoryWriteAccessFactory(process.ProcessId);
        memory.EnsureInstance(process.ProcessId, process.StartTimeUtc);
        if (!TryResolveAddress(memory, process, field, out var address))
            throw new InvalidOperationException("该字段是动态地址，游戏重启后需要重新扫描并更新位置。");
        if (address != expectedAddress) throw new OperationCanceledException("字段实际地址已变化，请重新定位后修改。");
        var nativeAliases = NativeAliases(version, memory, process, address, bytes.Length);
        var result = MemoryWriteVerifier.Write(memory, address, bytes);
        ApplyNativeAliases(version, nativeAliases, process, address, bytes.Length, result);
        StatusText = $"{field.Name}：{field.Status}";
        if (result.State == MemoryWriteState.WriteFailed)
            throw new InvalidOperationException(StatusText);
        await SaveLibraryAsync();
        RequireSavedFieldOperation(operation, version, field);
        StatusText = $"{field.Name}：{field.Status}";
        if (result.State != MemoryWriteState.ReadbackConfirmed)
            throw new InvalidOperationException(StatusText);
    }

    public Task<SavedField> AddAdapterFieldAsync(string fieldKey, string displayName, string group) =>
        AddAdapterFieldAsync(fieldKey, displayName, group, static () => { });

    internal async Task<SavedField> AddAdapterFieldAsync(string fieldKey, string displayName, string group, Action validatePage)
    {
        validatePage();
        var operation = CaptureGameOperation();
        var adapter = _activeAdapter ?? throw new InvalidOperationException("当前游戏构建没有可用的专属适配器。");
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        var game = SelectedGame ?? throw new InvalidOperationException("请先把当前进程保存入库。");
        var version = SelectedVersion ?? throw new InvalidOperationException("请先保存并选择当前游戏版本。");
        EnsureSelectedVersionMatchesAttached();
        if (string.IsNullOrWhiteSpace(fieldKey)) throw new InvalidOperationException("请填写模块定义的稳定字段键；物品模块通常使用精确物品名。");
        if (string.IsNullOrWhiteSpace(displayName)) throw new InvalidOperationException("字段备注名称必须由玩家填写。");

        using var turn = GetFieldScope(process, version).Module(adapter.Id, fieldKey.Trim()).EnterRead();
        await turn.Ready;
        RequireCurrentGameOperation(operation);
        validatePage();
        if (!ReferenceEquals(_activeAdapter, adapter)) throw new OperationCanceledException("原专属模块已停用。");
        AdapterFieldValue current;
        try { current = await AwaitGameOperationAsync(operation, Task.Run(() => adapter.ReadField(process, fieldKey.Trim()))); }
        catch { RequireCurrentAdapterOperation(operation, adapter); validatePage(); throw; }
        RequireCurrentGameOperation(operation);
        validatePage();
        if (!ReferenceEquals(_activeAdapter, adapter) || !turn.IsCurrent) throw new OperationCanceledException("原专属模块或字段读取已失效。");
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
        RequireCurrentGameOperation(operation);
        validatePage();
        RequireSavedFieldAdapter(field, adapter);
        StatusText = $"已通过专属适配器保存字段 {field.Name}";
        return field;
    }

    public async Task RefreshAdapterInventoryAsync()
    {
        var operation = CaptureGameOperation();
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        if (_activeAdapter is not IInventoryGameAdapter adapter)
            throw new InvalidOperationException("当前游戏构建没有可用的背包专属修改器。");

        StatusText = "正在读取游戏背包…";
        await ReadAdapterSnapshotAsync(operation, process, adapter, () => adapter.ReadInventory(process), items =>
        {
            _adapterInventoryItems.Clear();
            foreach (var item in items) _adapterInventoryItems.Add(item);
            AdapterItemsView.Refresh();
            SelectedAdapterItem = _adapterInventoryItems.FirstOrDefault();
            StatusText = $"已读取 {_adapterInventoryItems.Count:N0} 种背包物品";
        });
    }

    private async Task ReadAdapterSnapshotAsync<T>(GameOperationContext operation, ProcessItem process,
        IGameAdapter adapter, Func<T> read, Action<T> apply)
    {
        RequireCurrentAdapterOperation(operation, adapter);
        var snapshot = GetFieldScope(process).CaptureModuleSnapshot(adapter.Id);
        try
        {
            if (!snapshot.IsCurrent) throw new GameEditorSnapshotChangedException();
            var result = await AwaitGameOperationAsync(operation, Task.Run(read));
            RequireCurrentAdapterOperation(operation, adapter);
            if (!snapshot.TryApply(() => { RequireCurrentAdapterOperation(operation, adapter); apply(result); }))
                throw new GameEditorSnapshotChangedException();
        }
        catch
        {
            RequireCurrentAdapterOperation(operation, adapter);
            if (!snapshot.IsCurrent)
            {
                var changed = new GameEditorSnapshotChangedException();
                StatusText = changed.Message;
                throw changed;
            }
            throw;
        }
    }

    public async Task WriteSelectedAdapterItemAsync(string value)
    {
        var item = SelectedAdapterItem ?? throw new InvalidOperationException("请先选择一个背包物品。");
        await WriteAdapterItemsAsync([item], value);
    }

    public async Task WriteAdapterItemsAsync(IReadOnlyList<AdapterInventoryItem> items, string value)
    {
        var operation = CaptureGameOperation();
        if (items.Count == 0) throw new InvalidOperationException("请至少选择一个背包物品。");
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        var adapter = _activeAdapter ?? throw new InvalidOperationException("当前游戏构建没有可用的专属适配器。");
        var updated = new List<AdapterFieldValue>();
        var scope = GetFieldScope(process);
        foreach (var item in items)
            updated.Add(await WriteAdapterFieldCoordinatedAsync(process, adapter, scope, item.FieldKey, value,
                () => RequireCurrentAdapterOperation(operation, adapter)));
        RequireCurrentAdapterOperation(operation, adapter);
        await RefreshAdapterInventoryAsync();
        RequireCurrentAdapterOperation(operation, adapter);
        SelectedAdapterItem = _adapterInventoryItems.FirstOrDefault(candidate =>
            string.Equals(candidate.FieldKey, items[0].FieldKey, StringComparison.Ordinal));
        StatusText = items.Count == 1
            ? $"已实时修改并保存 {items[0].DisplayName} = {updated[0].DisplayValue}"
            : $"已把 {items.Count:N0} 种物品的物品总数批量修改并保存为 {value.Trim()}";
    }

    public async Task RefreshAdapterCharactersAsync()
    {
        var operation = CaptureGameOperation();
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        if (_activeAdapter is not ICharacterAttributesGameAdapter adapter || !adapter.SupportsCharacterAttributes(process))
            throw new InvalidOperationException("当前游戏构建没有可用的人物属性编辑模块。");

        var selectedId = SelectedAdapterCharacter?.CharacterId;
        var selectedAttribute = SelectedCharacterAttribute?.Key;
        StatusText = "正在读取游戏人物与属性…";
        await ReadAdapterSnapshotAsync(operation, process, adapter, () => adapter.ReadCharacters(process), characters =>
        {
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
        });
    }

    public async Task WriteSelectedCharacterAttributeAsync(string value)
    {
        var operation = CaptureGameOperation();
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        if (_activeAdapter is not ICharacterAttributesGameAdapter adapter || !adapter.SupportsCharacterAttributes(process))
            throw new InvalidOperationException("当前游戏构建没有可用的人物属性编辑模块。");
        var character = SelectedAdapterCharacter ?? throw new InvalidOperationException("请先选择一个人物。");
        var attribute = SelectedCharacterAttribute ?? throw new InvalidOperationException("请先选择一个人物属性。");
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var target))
            throw new InvalidOperationException("人物属性必须是整数；具体允许范围由游戏专属模块校验。");

        StatusText = $"正在修改 {character.DisplayName} 的{attribute.DisplayName}…";
        var fieldKey = ModuleFieldKey.Create(ActiveCharacterEditorId, character.CharacterId, attribute.Key);
        await WriteAdapterFieldCoordinatedAsync(process, adapter, GetFieldScope(process), fieldKey, value,
            () => RequireCurrentAdapterOperation(operation, adapter), () =>
            {
                var updated = adapter.WriteCharacterAttribute(process, character.CharacterId, attribute.Key, target);
                return new(fieldKey, updated.Attributes.Single(a => a.Key == attribute.Key).RawValueDisplay, "已实时修改人物属性");
            });
        RequireCurrentAdapterOperation(operation, adapter);
        await RefreshAdapterCharactersAsync();
        RequireCurrentAdapterOperation(operation, adapter);
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
        var operation = CaptureGameOperation();
        var process = AttachedProcess ?? throw new InvalidOperationException("游戏进程未连接。");
        if (_activeAdapter is not IEntityEditorsGameAdapter adapter ||
            !adapter.SupportsEntityEditor(process, editor.Descriptor.Id))
            throw new InvalidOperationException("当前游戏构建没有可用的该项专属修改器。");

        var selectedEntityId = editor.SelectedEntity?.EntityId;
        var selectedFieldKey = editor.SelectedField?.Key;
        StatusText = $"正在读取{editor.Descriptor.DisplayName}…";
        await ReadAdapterSnapshotAsync(operation, process, adapter, () => adapter.ReadEditorEntities(process, editor.Descriptor.Id), entities =>
        {
            editor.ReplaceEntities(entities, selectedEntityId, selectedFieldKey);
            StatusText = entities.Count == 0
                ? $"当前没有可显示的{editor.Descriptor.DisplayName}数据"
                : $"已读取 {entities.Count:N0} 项{editor.Descriptor.DisplayName}数据";
        });
    }

    public async Task WriteEntityEditorFieldAsync(AdapterEntityEditorState editor, string value)
    {
        var operation = CaptureGameOperation();
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
        var key = ModuleFieldKey.Create(editor.Descriptor.Id, entity.EntityId, field.Key);
        await WriteAdapterFieldCoordinatedAsync(process, adapter, GetFieldScope(process), key, value,
            () => RequireCurrentAdapterOperation(operation, adapter), () =>
            {
                var updated = adapter.WriteEditorField(process, editor.Descriptor.Id, entity.EntityId, field.Key, target);
                return new(key, updated.Fields.Single(f => f.Key == field.Key).ValueDisplay, "已实时修改专属字段");
            });
        RequireCurrentAdapterOperation(operation, adapter);
        await RefreshEntityEditorAsync(editor);
        RequireCurrentAdapterOperation(operation, adapter);
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
        var operation = CaptureGameOperation();
        var speedService = _speedService;
        var process = AttachedProcess ?? throw new InvalidOperationException("请先连接游戏进程。");
        var multiplier = ParseSpeedMultiplier(SpeedMultiplier);
        var cooldown = BeginSpeedControlInteraction();
        _speedOperationsInFlight++;
        if (operation.Session is not null) operation.Session.IsSpeedOperationRunning = true;
        try
        {
            SpeedMultiplier = FormatSpeedMultiplier(multiplier);
            if (multiplier == 1d)
            {
                await AwaitGameOperationAsync(operation, Task.Run(speedService.Normalize), validateResult: false);
                if (operation.Session is not null) operation.Session.IsSpeedActive = false;
                RequireCurrentGameOperation(operation);
                IsSpeedActive = false;
                if (_activeSession is not null) _activeSession.IsSpeedActive = false;
                OnPropertyChanged(nameof(SpeedStatusText));
                StatusText = "游戏已切换为正常倍速";
                return;
            }
            var result = await AwaitGameOperationAsync(operation, Task.Run(() => speedService.Accelerate(process.ProcessId, multiplier)), validateResult: false);
            if (operation.Session is not null)
            {
                operation.Session.IsSpeedActive = true;
                operation.Session.SpeedMultiplierInput = FormatSpeedMultiplier(multiplier);
            }
            RequireCurrentGameOperation(operation);
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
            _speedOperationsInFlight--;
            if (operation.Session is not null) operation.Session.IsSpeedOperationRunning = false;
            ReleaseSpeedControlAfter(cooldown);
        }
    }

    public async Task RestoreGameSpeedAsync()
    {
        var operation = CaptureGameOperation();
        var speedService = _speedService;
        _ = AttachedProcess ?? throw new InvalidOperationException("请先连接游戏进程。");
        var cooldown = BeginSpeedControlInteraction();
        _speedOperationsInFlight++;
        if (operation.Session is not null) operation.Session.IsSpeedOperationRunning = true;
        try
        {
            if (!speedService.HasHooks || speedService.Multiplier == 1d)
            {
                IsSpeedActive = false;
                if (_activeSession is not null) _activeSession.IsSpeedActive = false;
                SpeedMultiplier = "1";
                StatusText = "当前已经是正常倍速";
                return;
            }
            await AwaitGameOperationAsync(operation, Task.Run(speedService.Normalize), validateResult: false);
            if (operation.Session is not null) operation.Session.IsSpeedActive = false;
            RequireCurrentGameOperation(operation);
            IsSpeedActive = false;
            if (_activeSession is not null) _activeSession.IsSpeedActive = false;
            SpeedMultiplier = "1";
            OnPropertyChanged(nameof(SpeedStatusText));
            StatusText = "游戏速度已回正";
        }
        finally
        {
            _speedOperationsInFlight--;
            if (operation.Session is not null) operation.Session.IsSpeedOperationRunning = false;
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
        {
            await WriteSelectedFieldAsync(value.Trim());
            return; // Keep the actual write/readback outcome, including module-specific feedback.
        }
        await SaveLibraryAsync();
        StatusText = $"已更新字段 {field.Name}";
    }

    public async Task ToggleSelectedFieldValueLockAsync()
    {
        var field = SelectedSavedField ?? throw new InvalidOperationException("请先选择字段。");
        var version = SelectedVersion ?? throw new InvalidOperationException("请先选择版本。");
        var unlocking = field.IsValueLocked;
        var matchingAdapter = _activeAdapter is { } active && (active.Id == field.AdapterId ||
            active.LegacyIds.Contains(field.AdapterId, StringComparer.Ordinal)) ? active : null;
        var aliases = new List<FieldAlias> { new(field, field.IsValueLocked, field.LockedValue) };
        if (field.LocatorKind == "GameAdapter")
            aliases = matchingAdapter is { } adapter ? AdapterAliases(version, adapter, field.AdapterFieldKey)
                : version.Fields.Where(f => f.LocatorKind == "GameAdapter" && f.AdapterId == field.AdapterId &&
                    FieldOperationCoordinator.CanonicalKey(f.AdapterFieldKey) == FieldOperationCoordinator.CanonicalKey(field.AdapterFieldKey))
                    .Select(f => new FieldAlias(f, f.IsValueLocked, f.LockedValue)).ToList();
        else if (unlocking)
            aliases = version.Fields.Where(f => f.LocatorKind != "GameAdapter" && f.ValueType.Size() == field.ValueType.Size() &&
                (field.LocatorKind == "ModuleOffset" ? f.LocatorKind == "ModuleOffset" &&
                    string.Equals(f.ModuleName, field.ModuleName, StringComparison.OrdinalIgnoreCase) && f.ModuleOffset == field.ModuleOffset
                    : f.LocatorKind != "ModuleOffset" && field.LastAddress != 0 && f.LastAddress == field.LastAddress &&
                        f.ProcessStartTimeUtcTicks == field.ProcessStartTimeUtcTicks))
                .Select(f => new FieldAlias(f, f.IsValueLocked, f.LockedValue)).ToList();
        if (!aliases.Any(a => ReferenceEquals(a.Field, field))) aliases.Add(new(field, field.IsValueLocked, field.LockedValue));
        byte[]? nativeTarget = null;
        if (field.LocatorKind != "GameAdapter" && AttachedProcess is { } process)
        {
            try
            {
                using var memory = MemoryWriteAccessFactory(process.ProcessId);
                memory.EnsureInstance(process.ProcessId, process.StartTimeUtc);
                if (TryResolveAddress(memory, process, field, out var address))
                    aliases = NativeAliases(version, memory, process, address, field.ValueType.Size())
                        .Where(a => a.Address == address && a.Size == field.ValueType.Size()).Select(a => a.Alias).ToList();
            }
            catch (Exception error) when (unlocking) { Debug.WriteLine(error); } // Cancellation of intent must work without the game.
        }
        if (!field.IsValueLocked)
        {
            if (field.LocatorKind == "GameAdapter" && IsUncoordinatedPageAdapter(_activeAdapter))
                throw new InvalidOperationException("此版本模块页面未接入协调写入，字段锁定已暂停；请更新模块。");
            if (!GetAdapterFieldPolicy(_activeAdapter, field).CanLock)
                throw new InvalidOperationException("该游戏专属字段仅本次运行有效，不支持锁定或在重启后自动重应用。");
            if (string.IsNullOrWhiteSpace(field.CurrentValue) || field.CurrentValue == "—")
                throw new InvalidOperationException("请先刷新或修改字段数值，再启用锁定。");
            if (AttachedProcess is not null) GetFieldOperationQueue(field).Invalidate();
            if (field.LocatorKind != "GameAdapter" && !MemoryValueCodec.TryParseEncoded(field.CurrentValue, field.ValueType, field.ScaleMultiplier, out nativeTarget))
                throw new InvalidOperationException("锁定目标无法编码。");
            foreach (var alias in aliases)
            {
                alias.Field.LockedValue = nativeTarget is null ? field.CurrentValue
                    : MemoryValueCodec.FormatDecoded(nativeTarget, alias.Field.ValueType, alias.Field.ScaleMultiplier);
                alias.Field.IsValueLocked = true;
            }
        }
        else
        {
            try { if (AttachedProcess is not null) GetFieldOperationQueue(field).Invalidate(); }
            catch (Exception error) { Debug.WriteLine(error); } // The stopped maintenance also observes IsValueLocked below.
            foreach (var alias in aliases) { alias.Field.IsValueLocked = false; alias.Field.LockedValue = string.Empty; }
        }
        try { await SaveLibraryAsync(); }
        finally { RestartLockMaintenance(); }
        StatusText = unlocking ? $"已解除 {field.Name} 的数值锁定" : $"已锁定 {field.Name} = {field.LockedValue}";
    }

    public async Task ChangeThemeAsync(ThemeChoice choice)
    {
        SelectedTheme = choice;
        await SaveLibraryAsync();
    }

    public async Task CheckGameModuleUpdatesAsync()
    {
        if (IsDownloadActive) return;
        var context = CaptureModuleContext();
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
            if (!IsModuleContextCurrent(context)) return;
            _moduleCheckContext = context;
            _moduleCheckResult = result;
            ModuleStatusText = result.StatusText;
            NotifyModuleControls();
        }
        catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            if (!IsModuleContextCurrent(context)) return;
            _moduleCheckResult = null;
            ModuleStatusText = "检查失败：服务器暂未发布专属模块清单。";
            NotifyModuleControls();
            throw new InvalidOperationException("服务器暂未发布专属模块清单，请稍后重试。", exception);
        }
        catch (Exception exception)
        {
            if (!IsModuleContextCurrent(context)) return;
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

    public async Task<bool> InstallAvailableGameModuleAsync()
    {
        var context = RequireModuleCheckContext();
        var module = _moduleCheckResult?.RemoteModule
                     ?? throw new InvalidOperationException("请先点击“查新”。");
        var game = SelectedGame;
        var version = SelectedVersion;
        var process = AttachedProcess;
        var originalSession = _activeSession;
        var installedBefore = _moduleCatalogService.FindInstalled(module.Id);
        var exactBuildMatch = _moduleCheckResult?.IsExactBuildMatch == true;
        if (_moduleCheckResult?.Availability is not (GameModuleAvailability.Available or GameModuleAvailability.UpdateAvailable))
            throw new InvalidOperationException("当前没有可下载或更新的专属模块。");
        if (_isModuleControlBlocked || IsDownloadActive) return false;
        _isModuleControlBlocked = true;
        NotifyModuleControls();
        var cooldown = Task.Delay(TimeSpan.FromSeconds(3));
        DownloadOperation? download = null;
        try
        {
            ModuleStatusText = $"正在下载并校验 {module.DisplayName} v{module.Version}…";
            download = BeginDownload(snapshot =>
            {
                var message = $"{module.DisplayName} v{module.Version} · {snapshot.DisplayText}";
                if (IsModuleContextCurrent(context)) ModuleStatusText = message;
                StatusText = message;
            });
            await _moduleCatalogService.InstallAsync(module, download, download.Token);
            var replacementRequiresRestart = installedBefore is not null || _adapterRegistry.RequiresRestart(module.Id);
            _moduleRestartRequired |= replacementRequiresRestart;
            if (replacementRequiresRestart)
            {
                DeactivateModuleUntilRestart(module.Id);
            }
            else
            {
                _adapterRegistry.LoadInstalledModule(module.Id);
                await ReloadAdaptersForSessionsAsync();
            }
            if (game is null)
            {
                if (process is null || !IsModuleContextCurrent(context))
                {
                    ReconcileInstalledModulesWithLibrary();
                    await SaveLibraryAsync();
                    ResetModuleCheckState();
                    StatusText = $"已安装 {module.DisplayName} v{module.Version}，请连接游戏后验证。";
                    return true;
                }
                game = await AddCurrentProcessToLibraryCoreAsync(
                    string.IsNullOrWhiteSpace(module.GameDisplayName) ? process.ProcessName : module.GameDisplayName,
                    process, context.Fingerprint);
                version = context.Fingerprint is { } originalFingerprint
                    ? FindMatchingVersion(game, originalFingerprint) : null;
                if (ReferenceEquals(SelectedGame, game) && ReferenceEquals(AttachedProcess, process))
                    context = CaptureModuleContext();
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
            if (!IsModuleContextCurrent(context))
            {
                ResetModuleCheckState();
                StatusText = $"已安装 {module.DisplayName} v{module.Version}。";
                return true;
            }
            _moduleCheckContext = CaptureModuleContext();
            _moduleCheckResult = new GameModuleCheckResult(GameModuleAvailability.Current, module,
                _moduleCatalogService.FindInstalled(module.Id), $"已安装最新专属模块：{module.DisplayName} v{module.Version}",
                exactBuildMatch, CatalogReferenceModule: module);
            ModuleStatusText = replacementRequiresRestart
                ? exactBuildMatch
                    ? $"已安装 {module.DisplayName} v{module.Version}；该模块已停用，请手动重启主程序后启用。"
                    : $"已安装 {module.DisplayName} v{module.Version}；该模块已停用，请手动重启主程序并连接游戏完成本地只读兼容验证。"
                : exactBuildMatch
                    ? installedAdapter is null
                        ? $"已安装 {module.DisplayName} v{module.Version}，连接兼容游戏版本后启用。"
                        : _moduleCheckResult.StatusText
                    : process is null
                        ? $"已安装 {module.DisplayName} v{module.Version}；连接游戏后将执行本地只读兼容验证。"
                        : installedAdapter is null
                            ? $"已安装 {module.DisplayName} v{module.Version}，但当前构建未通过本地只读兼容验证，模块未启用。"
                            : $"已安装 {module.DisplayName} v{module.Version}；当前构建已通过本地只读兼容验证并启用。";
            NotifyModuleControls();
        }
        catch (OperationCanceledException) when (download?.IsCanceled == true)
        {
            StatusText = "已取消下载，本地模块版本未改变。";
            if (IsModuleContextCurrent(context)) ModuleStatusText = StatusText;
            return false;
        }
        finally
        {
            if (download is not null) EndDownload(download);
            ReleaseModuleControlsAfter(cooldown);
        }
        return true;
    }

    public async Task<bool> RollbackCurrentGameModuleAsync()
    {
        var context = RequireModuleCheckContext();
        var rollback = _moduleCheckResult?.RollbackModule
                       ?? throw new InvalidOperationException("当前没有兼容的较低模块版本可回退。");
        if (_isModuleControlBlocked || IsDownloadActive) return false;
        _isModuleControlBlocked = true;
        NotifyModuleControls();
        var cooldown = Task.Delay(TimeSpan.FromSeconds(3));
        DownloadOperation? download = null;
        try
        {
            ModuleStatusText = $"正在下载并校验 {rollback.DisplayName} v{rollback.Version}…";
            download = BeginDownload(snapshot =>
            {
                var message = $"{rollback.DisplayName} v{rollback.Version} · {snapshot.DisplayText}";
                if (IsModuleContextCurrent(context)) ModuleStatusText = message;
                StatusText = message;
            });
            await _moduleCatalogService.InstallAsync(rollback, download, download.Token);
            _moduleRestartRequired = true;
            DeactivateModuleUntilRestart(rollback.Id);
            if (!IsModuleContextCurrent(context))
            {
                ResetModuleCheckState();
                StatusText = $"已手动回退 {rollback.DisplayName} 到 v{rollback.Version}；请重启主程序后启用。";
                return true;
            }
            _moduleCheckResult = new GameModuleCheckResult(GameModuleAvailability.Current, rollback,
                _moduleCatalogService.FindInstalled(rollback.Id),
                $"已手动回退到 {rollback.DisplayName} v{rollback.Version}；请重启主程序后启用。");
            ModuleStatusText = _moduleCheckResult.StatusText;
            StatusText = ModuleStatusText;
            NotifyModuleControls();
        }
        catch (OperationCanceledException) when (download?.IsCanceled == true)
        {
            StatusText = "已取消下载，本地模块版本未改变。";
            if (IsModuleContextCurrent(context)) ModuleStatusText = StatusText;
            return false;
        }
        finally
        {
            if (download is not null) EndDownload(download);
            ReleaseModuleControlsAfter(cooldown);
        }
        return true;
    }

    private void DeactivateModuleUntilRestart(string moduleId)
    {
        var sessions = _sessions.Values.Append(_activeSession).Where(item => item is not null)
            .Cast<GameConnectionSession>().Distinct().Where(session => SessionUsesModule(session, moduleId)).ToList();
        foreach (var session in sessions)
        {
            StopSessionLockMaintenance(session);
            DisposeSessionEditorPages(session);
            session.Adapter = null;
            session.AdapterItems = [];
            session.AdapterCharacters = [];
            session.SelectedAdapterFieldKey = null;
            session.SelectedCharacterId = null;
            session.SelectedCharacterAttributeKey = null;
        }
        if (AdapterUsesModule(_activeAdapter, moduleId)) SetActiveAdapter(null);
        _adapterRegistry.DeactivateUntilRestart(moduleId);
        RefreshLibraryModuleLoadStates();
        foreach (var session in sessions) RestartSessionLockMaintenance(session);
    }

    public async Task UninstallCurrentGameModuleAsync()
    {
        var context = CaptureModuleContext();
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
            var message = packageDeleted
                ? $"已卸载专属模块 v{record.Version}；游戏库和快捷入口已保留。"
                : $"已停用专属模块 v{record.Version}；残留文件将在下次启动自动清理。";
            var isCurrent = IsModuleContextCurrent(context);
            ResetModuleCheckState();
            if (isCurrent) ModuleStatusText = message;
            StatusText = $"{moduleId}：{message}";
            GamesView.Refresh();
            NotifyModuleControls();
        }
        finally { ReleaseModuleControlsAfter(cooldown); }
    }

    private async Task<bool> RemoveInstalledModuleCoreAsync(string moduleId)
    {
        var sessions = _sessions.Values.Append(_activeSession).Where(item => item is not null)
            .Cast<GameConnectionSession>().Distinct().Where(session => SessionUsesModule(session, moduleId)).ToList();
        var removed = _moduleCatalogService.Unregister(moduleId)
                      ?? throw new InvalidOperationException("模块安装记录不存在。");
        foreach (var session in sessions) StopSessionLockMaintenance(session);
        try
        {
            try
            {
                foreach (var session in sessions)
                {
                    DisposeSessionEditorPages(session);
                    session.Adapter = null;
                    session.AdapterItems = [];
                    session.AdapterCharacters = [];
                    session.SelectedAdapterFieldKey = null;
                    session.SelectedCharacterId = null;
                    session.SelectedCharacterAttributeKey = null;
                }
                if (AdapterUsesModule(_activeAdapter, moduleId))
                    SetActiveAdapter(null);
                if (_adapterRegistry.RequiresRestart(moduleId))
                {
                    _adapterRegistry.DeactivateUntilRestart(moduleId);
                    _moduleRestartRequired = true;
                }
                else _adapterRegistry.Deactivate(moduleId);
                await ReloadAdaptersForSessionsAsync();
            }
            catch
            {
                _moduleCatalogService.RestoreRegistration(removed);
                _adapterRegistry.LoadInstalledModule(moduleId);
                await ReloadAdaptersForSessionsAsync();
                throw;
            }
            return await _moduleCatalogService.DeletePackageAsync(moduleId);
        }
        finally
        {
            foreach (var session in sessions) RestartSessionLockMaintenance(session);
        }
    }

    private static bool SessionUsesModule(GameConnectionSession session, string moduleId) =>
        AdapterUsesModule(session.Adapter, moduleId);

    private static bool AdapterUsesModule(IGameAdapter? adapter, string moduleId) =>
        adapter is not null && (string.Equals(adapter.Id, moduleId, StringComparison.Ordinal) ||
            adapter.LegacyIds.Contains(moduleId, StringComparer.Ordinal));

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
            _adapterRegistry.LoadErrors,
            _moduleCatalogService.StorageErrors));
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
        if (IsDownloadActive || _isApplicationUpdateBusy || _isApplicationUpdateCheckCooldown || _applicationUpdateDownloaded) return;
        _isApplicationUpdateBusy = true;
        _isApplicationUpdateCheckCooldown = true;
        ReleaseApplicationUpdateCheckCooldownAfterDelay();
        NotifyApplicationUpdateState();
        try
        {
            ApplicationUpdateStatusText = "　检查中";
            _applicationUpdateResult = await _applicationUpdateService.CheckAsync();
            if (_applicationUpdateResult.UpdateTarget is { } updateTarget)
            {
                ApplicationUpdateStatusText = $"　v{updateTarget.Version}";
                StatusText = $"发现肝肾大圣新版本 v{updateTarget.Version}";
            }
            else
            {
                ApplicationUpdateStatusText = _applicationUpdateResult.RollbackTarget is { } rollback
                    ? $"　已最新 · 可回退 v{rollback.Version}"
                    : "　已最新";
                StatusText = "肝肾大圣当前已是最新版本";
            }
        }
        catch
        {
            _applicationUpdateResult = null;
            ApplicationUpdateStatusText = "　检查失败";
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
        var target = _applicationUpdateResult?.UpdateTarget
                     ?? throw new InvalidOperationException("请先检查更新。");
        return await DownloadApplicationReleaseAsync(target, ApplicationUpdateOperation.Update);
    }

    public IReadOnlyList<ApplicationRollbackBlock> GetApplicationRollbackBlocks()
    {
        var target = _applicationUpdateResult?.RollbackTarget
                     ?? throw new InvalidOperationException("当前没有可回退的主程序版本。");
        return _applicationUpdateService.FindRollbackBlocks(target, _moduleCatalogService.GetInstalledManifests());
    }

    public async Task<bool> DownloadApplicationRollbackAsync()
    {
        var target = _applicationUpdateResult?.RollbackTarget
                     ?? throw new InvalidOperationException("当前没有可回退的主程序版本。");
        var blocks = GetApplicationRollbackBlocks();
        if (blocks.Count > 0)
            throw new InvalidOperationException("当前安装的专属模块与目标主程序不兼容，请先手动回退列出的模块。\n" +
                                                string.Join("\n", blocks.Select(block =>
                                                    $"- {block.DisplayName} v{block.ModuleVersion}：{block.Reason}")));
        return await DownloadApplicationReleaseAsync(target, ApplicationUpdateOperation.Rollback);
    }

    private async Task<bool> DownloadApplicationReleaseAsync(
        ApplicationReleaseTarget target,
        ApplicationUpdateOperation operation)
    {
        if (_isApplicationUpdateBusy || _applicationUpdateDownloaded || IsDownloadActive) return false;
        _isApplicationUpdateBusy = true;
        NotifyApplicationUpdateState();
        DownloadOperation? download = null;
        try
        {
            ApplicationUpdateStatusText = $"　v{target.Version} 下载中";
            download = BeginDownload(snapshot =>
            {
                var progressText = snapshot.Phase switch
                {
                    DownloadPhase.Connecting => "连接中",
                    DownloadPhase.Waiting => "等待数据",
                    DownloadPhase.Verifying => "校验中",
                    DownloadPhase.Installing => "准备安装",
                    _ => snapshot.Percentage is { } percentage ? $"下载 {percentage}%" : "下载中"
                };
                ApplicationUpdateStatusText = $"　v{target.Version} {progressText.Trim()}";
                StatusText = $"肝肾大圣 v{target.Version} · {snapshot.DisplayText}";
            });
            await _applicationUpdateService.DownloadAsync(target, operation, download, download.Token);
            _applicationUpdateDownloaded = true;
            ApplicationUpdateStatusText = $"　v{target.Version} 已下载";
            StatusText = $"已下载并校验 v{target.Version}，可立即重启或下次启动时{(operation == ApplicationUpdateOperation.Update ? "更新" : "回退")}";
            return true;
        }
        catch (OperationCanceledException) when (download?.IsCanceled == true)
        {
            ApplicationUpdateStatusText = $"　v{target.Version} 已取消";
            StatusText = "已取消下载，本地主程序版本未改变。";
            return false;
        }
        catch
        {
            ApplicationUpdateStatusText = $"　v{target.Version} 下载失败";
            StatusText = $"下载肝肾大圣 v{target.Version} 失败，请稍后重试";
            throw;
        }
        finally
        {
            if (download is not null) EndDownload(download);
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
        _moduleViewGeneration++;
        _moduleCheckContext = null;
        _moduleCheckResult = null;
        var moduleId = ResolveActiveModuleId();
        var installed = string.IsNullOrWhiteSpace(moduleId) ? null : _moduleCatalogService.FindInstalled(moduleId);
        ModuleStatusText = _moduleCatalogService.StorageErrors.Count > 0
            ? "模块资料无法安全读取或安装恢复未完成，请打开“诊断”；原资料已保留"
            : _adapterRegistry.IsRestartRequired(moduleId)
            ? "该模块已停用，请手动重启主程序后启用"
            : _activeAdapter is not null
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

    private sealed record ModuleOperationContext(long Generation, GameProfile? Game, GameVersionProfile? Version,
        ProcessItem? Process, VersionFingerprint? Fingerprint, GameConnectionSession? Session);

    private ModuleOperationContext CaptureModuleContext() => new(_moduleViewGeneration, SelectedGame,
        SelectedVersion, AttachedProcess, _attachedFingerprint, _activeSession);

    private bool IsModuleContextCurrent(ModuleOperationContext? context) => context is not null &&
        context.Generation == _moduleViewGeneration && ReferenceEquals(context.Game, SelectedGame) &&
        ReferenceEquals(context.Version, SelectedVersion) && ReferenceEquals(context.Process, AttachedProcess) &&
        ReferenceEquals(context.Fingerprint, _attachedFingerprint) && ReferenceEquals(context.Session, _activeSession);

    private ModuleOperationContext RequireModuleCheckContext() => IsModuleContextCurrent(_moduleCheckContext)
        ? _moduleCheckContext!
        : throw new InvalidOperationException("游戏、版本或连接已变化，请重新点击“查新”。");

    private async Task ReloadAdaptersForSessionsAsync()
    {
        RefreshLibraryModuleLoadStates();
        var sessions = _sessions.Values
            .Append(_activeSession)
            .Where(session => session is not null)
            .Cast<GameConnectionSession>()
            .Distinct()
            .ToList();
        foreach (var session in sessions)
        {
            var previousAdapterId = session.Adapter?.Id;
            DisposeSessionEditorPages(session);
            session.Adapter = null;
            if (ReferenceEquals(_activeSession, session)) SetActiveAdapter(null);
            await RefreshSessionAdapterAsync(session);
            if (_isShuttingDown) return;
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
            if (_activeSession is not null) DisposeSessionEditorPages(_activeSession);
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
        var session = _activeSession;
        var selectedEditorId = SelectedAdapterEditorPage is { } selected && session?.EditorPages.Contains(selected) == true
            ? selected.Descriptor.Id : session?.SelectedEditorId;
        if (session is not null) session.SelectedEditorId = selectedEditorId;
        DisposeEditorPages();
        _characterEditorPage = null;
        if (_activeAdapter is null)
        {
            if (session is not null) DisposeSessionEditorPages(session);
            SelectedAdapterEditorPage = null;
            return;
        }
        if (session is null) throw new InvalidOperationException("模块页面需要游戏连接会话。");
        session.Adapter = _activeAdapter;
        if (ReferenceEquals(session.PageAdapter, _activeAdapter) &&
            SameProcessInstance(session.PageProcess, AttachedProcess) && session.PageBuild == _attachedFingerprint?.BuildSha256)
        {
            foreach (var page in session.EditorPages) AdapterEditorPages.Add(page);
            _characterEditorPage = AdapterEditorPages.OfType<AdapterCharacterEditorPageState>().FirstOrDefault();
            SelectEditorPage(selectedEditorId);
            return;
        }
        DisposeSessionEditorPages(session);
        session.PageAdapter = _activeAdapter;
        session.PageProcess = AttachedProcess;
        session.PageBuild = _attachedFingerprint?.BuildSha256;

        var descriptors = _activeAdapter.Editors.ToDictionary(editor => editor.Id, StringComparer.Ordinal);
        if (_activeAdapter is IGameEditorPageFactoryProvider factory)
        {
            var process = AttachedProcess ?? throw new InvalidOperationException("模块页面需要已连接的游戏进程。");
            var fingerprint = _attachedFingerprint ?? throw new InvalidOperationException("模块页面需要已识别的游戏构建。");
            var host = _editorHostServices ?? throw new InvalidOperationException("模块页面宿主服务尚未初始化。");
            var adapter = _activeAdapter;
            var scope = GetFieldScope(process);
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
                            new ScopedGameEditorHostServices(host, lifetime.Token,
                                () => !_isShuttingDown && ReferenceEquals(session.Adapter, adapter) &&
                                      SameProcessInstance(session.Process, process) && session.Fingerprint?.BuildSha256 == fingerprint.BuildSha256 &&
                                      (ReferenceEquals(_activeSession, session) || _sessions.Values.Contains(session)),
                                Dispatcher.CurrentDispatcher,
                                (key, value, validate) => WriteAdapterFieldCoordinatedAsync(process, adapter, scope, key, value, validate),
                                () => scope.CaptureModuleSnapshot(adapter.Id),
                                () => ReferenceEquals(_activeSession, session)),
                            lifetime.Token);
                        modulePage = factory.CreateEditorPage(descriptor.Id, context)
                                     ?? throw new InvalidOperationException($"模块页面工厂没有创建 {descriptor.Id}。");
                        var page = new AdapterModuleEditorPageState(descriptor, lifetime, modulePage);
                        session.EditorPages.Add(page);
                        AdapterEditorPages.Add(page);
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
                DisposeSessionEditorPages(session);
                DisposeEditorPages();
                throw;
            }
            SelectEditorPage(selectedEditorId);
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
            session.EditorPages.Add(page);
            AdapterEditorPages.Add(page);
            if (page is AdapterCharacterEditorPageState characterPage) _characterEditorPage = characterPage;
        }
        SelectEditorPage(selectedEditorId);
    }

    private void SelectEditorPage(string? selectedEditorId) =>
        SelectedAdapterEditorPage = AdapterEditorPages.FirstOrDefault(page =>
                                        string.Equals(page.Descriptor.Id, selectedEditorId, StringComparison.Ordinal))
                                    ?? AdapterEditorPages.FirstOrDefault();

    private void DisposeEditorPages()
    {
        // Only detach the currently displayed collection. The connection owns its pages.
        var owned = _sessions.Values.Append(_activeSession).Where(session => session is not null)
            .SelectMany(session => session!.EditorPages).ToHashSet();
        var orphaned = AdapterEditorPages.Where(page => !owned.Contains(page)).OfType<IDisposable>().ToArray();
        SelectedAdapterEditorPage = null;
        AdapterEditorPages.Clear();
        foreach (var page in orphaned)
        {
            try { page.Dispose(); }
            catch (Exception exception) { Debug.WriteLine(exception); }
        }
    }

    private static bool SameProcessInstance(ProcessItem? first, ProcessItem? second) =>
        first is not null && second is not null && first.ProcessId == second.ProcessId && first.StartTimeUtc == second.StartTimeUtc;

    private static void DisposeSessionEditorPages(GameConnectionSession session)
    {
        var disposablePages = session.EditorPages.OfType<IDisposable>().ToArray();
        session.EditorPages.Clear();
        session.PageAdapter = null;
        session.PageProcess = null;
        session.PageBuild = null;
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
        OnPropertyChanged(nameof(CanRemoveCurrentGame));
        OnPropertyChanged(nameof(CanCheckGameModules));
        OnPropertyChanged(nameof(CanInstallGameModule));
        OnPropertyChanged(nameof(ModuleInstallActionText));
        OnPropertyChanged(nameof(ModuleInstallToolTip));
        OnPropertyChanged(nameof(CanRollbackGameModule));
        OnPropertyChanged(nameof(ModuleRollbackActionText));
        OnPropertyChanged(nameof(ModuleRestartRequired));
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
        OnPropertyChanged(nameof(CanCheckApplicationUpdate));
        OnPropertyChanged(nameof(CanUseApplicationUpdate));
        OnPropertyChanged(nameof(HasApplicationUpdateAvailable));
        OnPropertyChanged(nameof(CanUseApplicationRollback));
        OnPropertyChanged(nameof(ApplicationRollbackActionText));
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

    private static bool TryResolveAddress(IMemoryWriteAccess memory, ProcessItem process, SavedField field, out ulong address)
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

    private async Task SaveLibraryAsync()
    {
        RefreshLibraryModuleLoadStates();
        await _profileStore.SaveAsync(_document);
    }

    private void RefreshLibraryModuleLoadStates()
    {
        foreach (var game in Games)
            game.IsModuleLoaded = !string.IsNullOrWhiteSpace(game.ModuleId) && _adapterRegistry.FindById(game.ModuleId) is not null;
    }

    private sealed record GameOperationContext(long Generation, GameProfile? Game, GameVersionProfile? Version,
        ProcessItem? Process, Guid? AttachedGameId, GameConnectionSession? Session);

    private GameOperationContext CaptureGameOperation() => new(_gameOperationGeneration, SelectedGame,
        SelectedVersion, AttachedProcess, _attachedGameId, _activeSession);

    private bool IsCurrentGameOperation(GameOperationContext context) => !_isShuttingDown &&
        context.Generation == _gameOperationGeneration && ReferenceEquals(context.Game, SelectedGame) &&
        ReferenceEquals(context.Version, SelectedVersion) && ReferenceEquals(context.Process, AttachedProcess) &&
        ReferenceEquals(context.Session, _activeSession) && context.AttachedGameId == _attachedGameId;

    private void RequireCurrentGameOperation(GameOperationContext context)
    {
        if (!IsCurrentGameOperation(context))
            throw new OperationCanceledException("游戏、版本或连接已变化，原操作已取消。");
    }

    private async Task<T> AwaitGameOperationAsync<T>(GameOperationContext context, Task<T> task, bool validateResult = true)
    {
        T result;
        try { result = await task; }
        catch (Exception exception) when (!IsCurrentGameOperation(context))
        { throw new OperationCanceledException("原游戏操作已失效，忽略迟到错误。", exception); }
        if (validateResult) RequireCurrentGameOperation(context);
        return result;
    }

    private async Task AwaitGameOperationAsync(GameOperationContext context, Task task, bool validateResult = true)
    {
        try { await task; }
        catch (Exception exception) when (!IsCurrentGameOperation(context))
        { throw new OperationCanceledException("原游戏操作已失效，忽略迟到错误。", exception); }
        if (validateResult) RequireCurrentGameOperation(context);
    }

    private void InvalidateGameOperations()
    {
        _gameOperationGeneration++;
        _scanCancellation?.Cancel();
    }

    private void EnsureScanOwnership(IReadOnlyList<ScanCandidate> candidates, ProcessItem process)
    {
        var store = _scanCandidates;
        if (store is null || store.ProcessId != process.ProcessId || store.ProcessStartTimeUtc != process.StartTimeUtc ||
            candidates.Any(candidate => candidate.ScanGenerationId != store.GenerationId))
            throw new InvalidOperationException("扫描结果不属于当前进程和扫描代，请重新扫描或选择当前结果。");
    }

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
        session.SelectedEditorId = SelectedAdapterEditorPage?.Descriptor.Id;
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
        DisposeSessionEditorPages(session);
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
        session.Adapter = null;
        _ = RefreshReboundSessionAdapterAsync(session);

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

    private async Task<bool> RefreshSessionAdapterAsync(GameConnectionSession session)
    {
        var generation = ++session.AdapterCheckGeneration;
        var process = session.Process;
        var fingerprint = session.Fingerprint;
        var adapter = fingerprint is null ? null : await _adapterRegistry.ResolveAsync(process, fingerprint);
        if (_isShuttingDown || generation != session.AdapterCheckGeneration ||
            !ReferenceEquals(process, session.Process) || !ReferenceEquals(fingerprint, session.Fingerprint) ||
            !IsProcessRunning(process) ||
            !ReferenceEquals(_activeSession, session) && !_sessions.Values.Contains(session)) return false;
        session.Adapter = adapter;
        return true;
    }

    private async Task RefreshReboundSessionAdapterAsync(GameConnectionSession session)
    {
        try
        {
            if (await RefreshSessionAdapterAsync(session) && ReferenceEquals(_activeSession, session))
                SetActiveAdapter(session.Adapter);
        }
        catch (Exception exception) { Debug.WriteLine(exception); }
    }

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
        DisposeSessionEditorPages(session);
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
        if (_moduleCatalogService.StorageErrors.Count > 0)
        {
            foreach (var game in Games) { game.IsModuleInstalled = false; game.IsModuleLoaded = false; }
            return false; // Do not erase semantic module identities because registration is unreadable.
        }
        var installedIds = installedManifests.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var game in Games) game.IsModuleInstalled = false;

        foreach (var manifest in installedManifests)
        {
            var game = Games.FirstOrDefault(item => string.Equals(item.ModuleId, manifest.Id, StringComparison.Ordinal))
                       ?? Games.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.ModuleId) &&
                           string.Equals(_adapterRegistry.FindById(item.ModuleId)?.Id, manifest.Id, StringComparison.Ordinal));
            if (game is null)
            {
                var names = new[] { manifest.GameDisplayName }.Concat(manifest.ProcessNames)
                    .Select(GameIdentityResolver.NormalizeName).Where(name => name.Length > 0).ToHashSet();
                var matches = Games.Where(item => string.IsNullOrWhiteSpace(item.ModuleId) &&
                    new[] { item.Name, item.ProcessName, item.Identity?.ProductName ?? "" }
                        .Select(GameIdentityResolver.NormalizeName).Any(names.Contains)).Take(2).ToArray();
                if (matches.Length == 1) game = matches[0];
            }
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
        RefreshLibraryModuleLoadStates();
        return changed;
    }

    private bool IsGameVisible(GameProfile game) =>
        string.IsNullOrWhiteSpace(SearchText) ||
        game.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ||
        game.ProcessName.Contains(SearchText, StringComparison.OrdinalIgnoreCase);

    private GameProfile? ResolveGameForProcess(ProcessItem process, VersionFingerprint? fingerprint = null, IGameAdapter? adapter = null,
        LogicalGameProcessGroup? group = null)
    {
        group ??= SameProcessInstance(_activeSession?.Process, process) ? _activeSession?.ProcessGroup : null;
        return GameIdentityResolver.Resolve(Games, process, fingerprint,
            moduleNames: _adapterRegistry.GetGameNames, preferredGame: SelectedGame,
            runningNames: group is null ? [] : [group.RootProcess.WindowTitle, group.RootProcess.ProcessName]);
    }

    private void BindActiveSessionToGame(GameProfile game)
    {
        var session = _activeSession;
        if (session is not null)
        {
            if (_sessions.TryGetValue(game.Id, out var previous) && !ReferenceEquals(previous, session))
                DisconnectSession(previous, false);
            if (session.GameId is Guid oldId && oldId != game.Id &&
                _sessions.TryGetValue(oldId, out var old) && ReferenceEquals(old, session))
            {
                _sessions.Remove(oldId);
                var oldGame = Games.FirstOrDefault(item => item.Id == oldId);
                if (oldGame is not null) oldGame.IsConnected = false;
            }
            session.GameId = game.Id;
            _sessions[game.Id] = session;
            game.IsConnected = true;
        }
        _attachedGameId = game.Id;
    }

    private async Task<bool> UpgradeLegacyVersionFingerprintsAsync(GameProfile game)
    {
        var legacyVersions = game.Versions.Where(version => string.IsNullOrWhiteSpace(version.BuildFingerprint)).ToList();
        if (legacyVersions.Count == 0 || !File.Exists(game.ExecutablePath)) return false;
        try
        {
            var savedBuild = await _fingerprintService.CreateAsync(game.ExecutablePath);
            var changed = false;
            foreach (var version in legacyVersions.Where(version => GameIdentityResolver.MatchesVersion(version, savedBuild)))
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
        => GameIdentityResolver.MatchesVersion(version, fingerprint);

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
        version.PackageSha256 = fingerprint.PackageSha256;
        version.PlatformName = fingerprint.PlatformName;
        version.PlatformAppId = fingerprint.PlatformAppId;
        version.PlatformBuildId = fingerprint.PlatformBuildId;
        version.PlatformDisplayName = fingerprint.PlatformDisplayName;
        version.FileSize = fingerprint.FileSize;
        version.Architecture = fingerprint.Architecture;
        version.LastVerifiedUtc = DateTime.UtcNow;
    }

    private static bool UpdateGameIdentity(GameProfile game, GameIdentityEvidence? evidence)
    {
        if (evidence is null) return false;
        var identity = GameIdentityResolver.Merge(game.Identity, evidence);
        if (Equals(game.Identity, identity)) return false;
        game.Identity = identity;
        return true;
    }

    private async Task<GameDeclaredVersionInfo?> ReadGameDeclaredMetadataAsync(GameOperationContext operation,
        ProcessItem process, IGameAdapter? adapter)
    {
        if (adapter is not IGameVersionMetadataProvider provider) return null;
        try
        {
            var metadata = await AwaitGameOperationAsync(operation, Task.Run(() => provider.ReadGameVersionMetadata(process.ToModuleContext())));
            return ReferenceEquals(_adapterRegistry.FindById(adapter.Id), adapter) ? metadata : null;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            RequireCurrentGameOperation(operation);
            // Optional metadata must never prevent connecting to a supported build.
            return null;
        }
    }

    private static bool ApplyGameDeclaredMetadata(GameVersionProfile version, GameDeclaredVersionInfo? metadata)
    {
        if (metadata is null) return false;
        var changed = !string.Equals(version.GameDeclaredVersion, metadata.Version, StringComparison.Ordinal) ||
                      !string.Equals(version.GameDeclaredProductName, metadata.ProductName, StringComparison.Ordinal) ||
                      !string.Equals(version.GameDeclaredBuildGuid, metadata.BuildGuid, StringComparison.Ordinal);
        version.GameDeclaredVersion = metadata.Version;
        version.GameDeclaredProductName = metadata.ProductName;
        version.GameDeclaredBuildGuid = metadata.BuildGuid;
        version.NotifyChoiceChanged();
        return changed;
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
                memory.EnsureInstance(process.ProcessId, process.StartTimeUtc);
                while (!cancellation.IsCancellationRequested)
                {
                    foreach (var candidate in candidates)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        if (!memory.TryRead(candidate.Address, candidate.ValueType.Size(), out var bytes)) bytes = [];
                        await RunOnUiAsync(() =>
                        {
                            if (!cancellation.IsCancellationRequested) candidate.CurrentBytes = bytes;
                        });
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
        if (_isShuttingDown) return;
        if (AttachedProcess is null || SelectedVersion?.Fields.Any(field => field.IsValueLocked) != true) return;
        if (SelectedGame is null || _attachedGameId != SelectedGame.Id ||
            _attachedFingerprint is null ||
            !VersionMatches(SelectedVersion, _attachedFingerprint)) return;
        StartSessionLockMaintenance(session, AttachedProcess, SelectedVersion);
    }

    private void RestartSessionLockMaintenance(GameConnectionSession session)
    {
        if (ReferenceEquals(session, _activeSession)) { RestartLockMaintenance(); return; }
        StopSessionLockMaintenance(session);
        if (_isShuttingDown || session.GameId is not Guid gameId ||
            !_sessions.TryGetValue(gameId, out var registered) || !ReferenceEquals(registered, session)) return;
        var game = Games.FirstOrDefault(item => item.Id == gameId);
        var version = game?.Versions.FirstOrDefault(item => item.Id == session.VersionId);
        if (version?.Fields.Any(field => field.IsValueLocked) != true || session.Fingerprint is null ||
            !VersionMatches(version, session.Fingerprint)) return;
        StartSessionLockMaintenance(session, session.Process, version);
    }

    private void StartSessionLockMaintenance(GameConnectionSession session, ProcessItem process, GameVersionProfile version)
    {
        var cancellation = new CancellationTokenSource();
        session.LockMaintenanceCancellation = cancellation;
        // Capture before scheduling: a later stop may dispose the source or replace the adapter.
        var token = cancellation.Token;
        var adapter = session.Adapter;
        _ = Task.Run(() => MaintainLockedValuesAsync(process, version, adapter, token));
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
                await RunOnUiAsync(() =>
                {
                    foreach (var field in version.Fields.Where(f => f.IsValueLocked && f.LocatorKind == "GameAdapter" && IsUncoordinatedPageAdapter(sessionAdapter)))
                        field.Status = "锁定已暂停：此版本模块页面未接入协调写入，请更新模块";
                    lockedFields = version.Fields.Where(field => field.IsValueLocked && GetAdapterFieldPolicy(sessionAdapter, field).CanLock &&
                        (field.LocatorKind != "GameAdapter" || sessionAdapter is not null)).ToList();
                });
                if (lockedFields.Count == 0) return;

                foreach (var field in lockedFields)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FieldOperationCoordinator.Scope.Turn? turn;
                    FieldOperationCoordinator.Scope.Target target;
                    try
                    {
                        target = GetFieldOperationQueue(field, process, version, sessionAdapter);
                        if (!target.TryEnterMaintenance(out turn)) continue;
                    }
                    catch (Exception error)
                    {
                        await RunOnUiAsync(() => { if (!cancellationToken.IsCancellationRequested && version.Fields.Contains(field)) field.Status = $"锁定失败：{error.Message}"; });
                        continue;
                    }
                    using var fieldTurn = turn!;
                    var lockedValue = field.LockedValue;
                    List<FieldAlias> lockSnapshots = [];
                    bool IsCurrentTarget() => !cancellationToken.IsCancellationRequested && field.IsValueLocked &&
                        field.LockedValue == lockedValue && version.Fields.Contains(field) && fieldTurn.IsCurrent &&
                        lockSnapshots.All(alias => !version.Fields.Contains(alias.Field) ||
                            alias.Field.IsValueLocked == alias.Locked && alias.Field.LockedValue == alias.Target);
                    try
                    {
                        if (!IsCurrentTarget()) continue;
                        var conflicts = new List<SavedField>();
                        await RunOnUiAsync(() =>
                        {
                            if (!IsCurrentTarget()) return;
                            conflicts = ConflictingLocks(process, version, field, sessionAdapter);
                            if (field.LocatorKind == "GameAdapter")
                                lockSnapshots = AdapterAliases(version, ResolveFieldAdapter(field, sessionAdapter), field.AdapterFieldKey);
                            else
                            {
                                using var snapshotMemory = MemoryWriteAccessFactory(process.ProcessId);
                                snapshotMemory.EnsureInstance(process.ProcessId, process.StartTimeUtc);
                                if (TryResolveAddress(snapshotMemory, process, field, out var snapshotAddress))
                                    lockSnapshots = NativeAliases(version, snapshotMemory, process, snapshotAddress, field.ValueType.Size()).Select(a => a.Alias).ToList();
                            }
                            if (conflicts.Count == 0) return;
                            foreach (var conflict in conflicts.Append(field)) conflict.Status = "锁定已暂停：同一字段存在冲突目标，请手动修改或解除锁定";
                        });
                        if (conflicts.Count != 0 || !IsCurrentTarget()) continue;
                        if (field.LocatorKind == "GameAdapter")
                        {
                            var adapter = ResolveFieldAdapter(field, sessionAdapter);
                            var current = adapter.ReadField(process, field.AdapterFieldKey);
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!IsCurrentTarget()) continue;
                            var final = current;
                            if (!string.Equals(current.DisplayValue, lockedValue, StringComparison.Ordinal))
                            {
                                using var mutation = target.BeginMutation();
                                if (!IsCurrentTarget()) continue;
                                final = adapter.WriteField(process, field.AdapterFieldKey, lockedValue);
                            }
                            await RunOnUiAsync(() =>
                            {
                                if (!IsCurrentTarget()) return;
                                field.CurrentValue = final.DisplayValue;
                                field.Status = $"锁定中 · {final.Status}";
                                field.LastVerifiedUtc = DateTime.UtcNow;
                            });
                            continue;
                        }

                        if (!MemoryValueCodec.TryParseEncoded(lockedValue, field.ValueType, field.ScaleMultiplier, out var expected))
                            throw new InvalidOperationException("锁定目标值无效。");
                        using var memory = MemoryWriteAccessFactory(process.ProcessId);
                        memory.EnsureInstance(process.ProcessId, process.StartTimeUtc);
                        if (!TryResolveAddress(memory, process, field, out var address))
                            throw new InvalidOperationException("动态地址需要重新定位。");
                        if (target.NativeAddress != address) throw new InvalidOperationException("字段实际地址已变化，请重新定位。");
                        if (!memory.TryRead(address, expected.Length, out var currentBytes))
                            throw new InvalidOperationException("读取失败。");
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!IsCurrentTarget()) continue;
                        var needsWrite = !currentBytes.AsSpan().SequenceEqual(expected);
                        var result = !needsWrite
                            ? new MemoryWriteResult(MemoryWriteState.ReadbackConfirmed, currentBytes)
                            : MemoryWriteVerifier.Write(memory, address, expected);
                        await RunOnUiAsync(() =>
                        {
                            if (!IsCurrentTarget()) return;
                            field.CurrentValue = result.HasReadback
                                ? MemoryValueCodec.FormatDecoded(result.CurrentBytes, field.ValueType, field.ScaleMultiplier) : "—";
                            field.Status = $"锁定中 · {(needsWrite ? result.Description : "当前值与锁定目标一致")}";
                            if (result.HasReadback) field.LastVerifiedUtc = DateTime.UtcNow;
                        });
                    }
                    catch (Exception exception)
                    {
                        await RunOnUiAsync(() =>
                        {
                            if (!IsCurrentTarget()) return;
                            field.CurrentValue = "—";
                            field.Status = $"锁定失败：{exception.Message}";
                        });
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
        if (field.LocatorKind == "GameAdapter" && IsUncoordinatedPageAdapter(adapter)) return new(false, false);
        if (!string.Equals(field.LocatorKind, "GameAdapter", StringComparison.Ordinal) ||
            !ModuleFieldKey.TryParse(field.AdapterFieldKey, out var editorId, out var entityId, out var fieldId))
            return new(false, true);
        return GetAdapterFieldPolicy(adapter, editorId, entityId, fieldId);
    }

    private static bool IsUncoordinatedPageAdapter(IGameAdapter? adapter) =>
        adapter is IGameEditorPageFactoryProvider and not ICoordinatedGameEditorPageProvider;

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

    internal bool IsShutdownCommitted => _isShuttingDown;

    public void Shutdown()
    {
        if (_isShuttingDown) return;
        if (_speedOperationsInFlight > 0)
            throw new InvalidOperationException("正在调整游戏倍速，请等待本次操作结束后再关闭。");
        CaptureActiveSession();
        var sessions = _sessions.Values
            .Append(_activeSession)
            .Where(session => session is not null)
            .Cast<GameConnectionSession>()
            .Distinct()
            .ToList();
        foreach (var session in sessions)
        {
            // This is still preparation: a normalization failure must leave operations,
            // pages, scans, downloads and the connection monitor usable for a retry.
            try { session.SpeedService.DetachSafely(); }
            finally
            {
                if (!session.SpeedService.HasHooks || session.SpeedService.Multiplier == 1d)
                {
                    session.IsSpeedActive = false;
                    if (ReferenceEquals(_activeSession, session)) IsSpeedActive = false;
                }
                if (ReferenceEquals(_activeSession, session)) OnPropertyChanged(nameof(SpeedStatusText));
            }
        }
        _isShuttingDown = true;
        _gameOperationGeneration++;
        var errors = new List<Exception>();
        void Cleanup(Action action)
        {
            try { action(); }
            catch (Exception exception) { errors.Add(exception); Debug.WriteLine(exception); }
        }
        Cleanup(() => _downloadOperation?.Cancel());
        Cleanup(() => _scanCancellation?.Cancel());
        Cleanup(StopLiveCandidateRefresh);
        foreach (var session in sessions)
        {
            Cleanup(() => StopSessionLockMaintenance(session));
            Cleanup(session.SpeedService.Dispose);
            Cleanup(() => DisposeSessionScanState(session));
            Cleanup(() => DisposeSessionEditorPages(session));
        }
        _sessions.Clear();
        _activeAdapter = null;
        Cleanup(DisposeEditorPages);
        Cleanup(_adapterRegistry.Dispose);
        Cleanup(_idleSpeedService.Dispose);
        if (errors.Count > 0)
            throw new AggregateException("退出已开始，但部分临时资源清理失败。窗口将继续关闭，不会停留在半退出状态。", errors);
    }

    private static bool PathsEqual(string left, string right)
        => GameIdentityResolver.PathsEqual(left, right);

}

public sealed record Choice<T>(T Value, string Display);
