using StringifyDesktop.Services;
using StringifyDesktop.ViewModels;

namespace StringifyDesktop.Infrastructure;

public sealed class AppContext : IAsyncDisposable
{
    private bool disposed;

    public AppConfigurationLoader ConfigurationLoader { get; }
    public AppPaths Paths { get; }
    public UiDispatcher UiDispatcher { get; }
    public SingleInstanceService SingleInstanceService { get; }
    public MainWindowCoordinator MainWindowCoordinator { get; }
    public FilePickerService FilePickerService { get; }
    public MainViewModel MainViewModel { get; }

    private AppContext(
        AppConfigurationLoader configurationLoader,
        AppPaths paths,
        UiDispatcher uiDispatcher,
        SingleInstanceService singleInstanceService,
        MainWindowCoordinator mainWindowCoordinator,
        FilePickerService filePickerService,
        MainViewModel mainViewModel)
    {
        ConfigurationLoader = configurationLoader;
        Paths = paths;
        UiDispatcher = uiDispatcher;
        SingleInstanceService = singleInstanceService;
        MainWindowCoordinator = mainWindowCoordinator;
        FilePickerService = filePickerService;
        MainViewModel = mainViewModel;
    }

    public static AppContext Create(SingleInstanceService singleInstanceService)
    {
        var clock = new SystemClock();
        var paths = new AppPaths();
        var configurationLoader = new AppConfigurationLoader();
        var configuration = configurationLoader.Load(paths);
        var uiDispatcher = new UiDispatcher();
        var protocolRegistrationService = new ProtocolRegistrationService(configuration);
        var trayService = new TrayService(paths);
        var mainWindowCoordinator = new MainWindowCoordinator(trayService, uiDispatcher);
        var protectedFileStore = ProtectedFileStoreFactory.Create(paths);
        var viewModel = MainViewModel.Create(
            configuration,
            paths,
            uiDispatcher,
            protocolRegistrationService,
            singleInstanceService,
            clock,
            protectedFileStore,
            out var filePickerService);

        viewModel.BringToFrontRequested += (_, _) => mainWindowCoordinator.ShowWindow();

        return new AppContext(
            configurationLoader,
            paths,
            uiDispatcher,
            singleInstanceService,
            mainWindowCoordinator,
            filePickerService,
            viewModel);
    }

    public async Task InitializeAsync()
    {
        await MainViewModel.InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await MainViewModel.DisposeAsync();
        MainWindowCoordinator.Dispose();
        await SingleInstanceService.DisposeAsync();
    }
}
