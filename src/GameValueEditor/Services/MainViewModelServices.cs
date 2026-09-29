using GameValueEditor.Services.Adapters;
using System.IO;

namespace GameValueEditor.Services;

public sealed class MainViewModelServices
{
    private MainViewModelServices(
        ProfileStore profileStore,
        ProcessService processService,
        VersionFingerprintService fingerprintService,
        MemoryScanService scanService,
        GameAdapterRegistry adapterRegistry,
        ThemeService themeService,
        ProcessSpeedService idleSpeedService,
        GameIconService gameIconService,
        GameModuleCatalogService moduleCatalogService,
        ApplicationUpdateService applicationUpdateService)
    {
        ProfileStore = profileStore;
        ProcessService = processService;
        FingerprintService = fingerprintService;
        ScanService = scanService;
        AdapterRegistry = adapterRegistry;
        ThemeService = themeService;
        IdleSpeedService = idleSpeedService;
        GameIconService = gameIconService;
        ModuleCatalogService = moduleCatalogService;
        ApplicationUpdateService = applicationUpdateService;
    }

    public ProfileStore ProfileStore { get; }
    public ProcessService ProcessService { get; }
    public VersionFingerprintService FingerprintService { get; }
    public MemoryScanService ScanService { get; }
    public GameAdapterRegistry AdapterRegistry { get; }
    public ThemeService ThemeService { get; }
    public ProcessSpeedService IdleSpeedService { get; }
    public GameIconService GameIconService { get; }
    public GameModuleCatalogService ModuleCatalogService { get; }
    public ApplicationUpdateService ApplicationUpdateService { get; }

    public static MainViewModelServices CreateDefault(ApplicationUpdateService? applicationUpdateService = null)
    {
        var profileStore = new ProfileStore();
        return new MainViewModelServices(
            profileStore,
            new ProcessService(),
            new VersionFingerprintService(),
            new MemoryScanService(Path.Combine(profileStore.RootDirectory, "scan-temp")),
            new GameAdapterRegistry(profileStore.ModulesDirectory),
            new ThemeService(),
            new ProcessSpeedService(),
            new GameIconService(profileStore.IconsDirectory),
            new GameModuleCatalogService(profileStore.ModulesDirectory),
            applicationUpdateService ?? new ApplicationUpdateService(profileStore.UpdatesDirectory));
    }
}
