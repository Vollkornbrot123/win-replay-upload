using System.Collections.ObjectModel;
using StringifyDesktop.Commands;
using StringifyDesktop.Models;
using StringifyDesktop.Services;

namespace StringifyDesktop.ViewModels;

public sealed class MainViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly AppConfiguration configuration;
    private readonly UiDispatcher uiDispatcher;
    private readonly ProtocolRegistrationService protocolRegistrationService;
    private readonly SingleInstanceService singleInstanceService;
    private readonly SystemClock clock;
    private readonly SettingsStore settingsStore;
    private readonly UploadLogStore uploadLogStore;
    private readonly IProtectedFileStore protectedFileStore;
    private readonly AuthService authService;
    private readonly BackendApiClient backendApiClient;
    private readonly UploadService uploadService;
    private readonly ReplayWatcherService replayWatcherService;
    private readonly FilePickerService filePickerService;
    private readonly HashSet<string> inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim syncGate = new(1, 1);
    private readonly SemaphoreSlim automaticCatchUpGate = new(1, 1);

    private AppSettings settings = AppSettings.Default;
    private bool initialized;
    private bool isBusy;
    private bool isOpeningBrowser;
    private bool isSettingsView;
    private string watchDirectoryInput = string.Empty;
    private bool watching;
    private string? activeWatchDirectory;
    private string authStatus = "Restoring secure browser sign-in...";
    private string? authError;

    public MainViewModel(
        AppConfiguration configuration,
        UiDispatcher uiDispatcher,
        ProtocolRegistrationService protocolRegistrationService,
        SingleInstanceService singleInstanceService,
        SystemClock clock,
        SettingsStore settingsStore,
        UploadLogStore uploadLogStore,
        IProtectedFileStore protectedFileStore,
        AuthService authService,
        BackendApiClient backendApiClient,
        UploadService uploadService,
        ReplayWatcherService replayWatcherService,
        FilePickerService filePickerService)
    {
        this.configuration = configuration;
        this.uiDispatcher = uiDispatcher;
        this.protocolRegistrationService = protocolRegistrationService;
        this.singleInstanceService = singleInstanceService;
        this.clock = clock;
        this.settingsStore = settingsStore;
        this.uploadLogStore = uploadLogStore;
        this.protectedFileStore = protectedFileStore;
        this.authService = authService;
        this.backendApiClient = backendApiClient;
        this.uploadService = uploadService;
        this.replayWatcherService = replayWatcherService;
        this.filePickerService = filePickerService;
        settings = settingsStore.Get();

        SignInCommand = new AsyncCommand(StartSignInAsync, () => !IsAuthBusy);
        SignOutCommand = new AsyncCommand(SignOutAsync, () => IsSignedIn);
        SaveWatchDirectoryCommand = new AsyncCommand(SaveWatchDirectoryAsync, () => IsSignedIn);
        ChooseFolderCommand = new AsyncCommand(ChooseFolderAsync, () => IsSignedIn);
        ToggleAutoSyncCommand = new AsyncCommand(ToggleAutoSyncAsync, () => IsSignedIn);
        ToggleDeleteAfterUploadCommand = new AsyncCommand(ToggleDeleteAfterUploadAsync, () => IsSignedIn);
        ManualSyncCommand = new AsyncCommand(ManualSyncAsync, () => IsSignedIn && !IsBusy);
        PickFilesCommand = new AsyncCommand(PickFilesAsync, () => IsSignedIn && !IsBusy);
        RetryFailedCommand = new AsyncCommand(RetryFailedAsync, () => IsSignedIn && !IsBusy && FailedCount > 0);
        ClearFailedCommand = new AsyncCommand(ClearFailedAsync, () => FailedCount > 0);
        ExportLogCommand = new AsyncCommand(ExportLogAsync);
        ShowDashboardCommand = new RelayCommand(() => IsSettingsView = false, () => IsSettingsView);
        ShowSettingsCommand = new RelayCommand(() => IsSettingsView = true, () => IsDashboardView);

        HistoryRows = new ObservableCollection<UploadHistoryRow>();
    }

    public event EventHandler? BringToFrontRequested;

    public ObservableCollection<UploadHistoryRow> HistoryRows { get; }

    public FilePickerService FilePickerService => filePickerService;

    public AsyncCommand SignInCommand { get; }

    public AsyncCommand SignOutCommand { get; }

    public AsyncCommand SaveWatchDirectoryCommand { get; }

    public AsyncCommand ChooseFolderCommand { get; }

    public AsyncCommand ToggleAutoSyncCommand { get; }

    public AsyncCommand ToggleDeleteAfterUploadCommand { get; }

    public AsyncCommand ManualSyncCommand { get; }

    public AsyncCommand PickFilesCommand { get; }

    public AsyncCommand RetryFailedCommand { get; }

    public AsyncCommand ClearFailedCommand { get; }

    public AsyncCommand ExportLogCommand { get; }

    public RelayCommand ShowDashboardCommand { get; }

    public RelayCommand ShowSettingsCommand { get; }

    public bool IsSignedIn => authService.Session is not null;

    public bool IsSignedOut => !IsSignedIn;

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                RefreshComputedState();
            }
        }
    }

    public bool IsAuthBusy => isOpeningBrowser || authService.IsProcessingCallback;

    public string SignInButtonText => isOpeningBrowser ? "Opening browser..." : "Sign in with Stringify";

    public string WatchDirectoryInput
    {
        get => watchDirectoryInput;
        set => SetProperty(ref watchDirectoryInput, value);
    }

    public bool AutoSyncEnabled => settings.AutoSyncEnabled;

    public bool DeleteAfterUploadEnabled => settings.DeleteAfterUploadEnabled;

    public bool IsSettingsView
    {
        get => isSettingsView;
        private set
        {
            if (SetProperty(ref isSettingsView, value))
            {
                OnPropertyChanged(nameof(IsDashboardView));
                RefreshComputedState();
            }
        }
    }

    public bool IsDashboardView => !IsSettingsView;

    public bool Watching
    {
        get => watching;
        private set
        {
            if (SetProperty(ref watching, value))
            {
                OnPropertyChanged(nameof(ShouldOfferTrayOnClose));
            }
        }
    }

    public string UserLabel =>
        authService.Session?.User.Email
        ?? authService.Session?.User.PreferredUsername
        ?? authService.Session?.User.Name
        ?? authService.Session?.User.Sub
        ?? string.Empty;

    public int UploadedCount { get; private set; }

    public int AlreadyUploadedCount { get; private set; }

    public int FailedCount { get; private set; }

    public int TotalCount => UploadedCount + AlreadyUploadedCount + FailedCount;

    public string AuthStatus
    {
        get => authStatus;
        private set => SetProperty(ref authStatus, value);
    }

    public string? AuthError
    {
        get => authError;
        private set
        {
            if (SetProperty(ref authError, value))
            {
                OnPropertyChanged(nameof(HasAuthError));
            }
        }
    }

    public bool HasAuthError => !string.IsNullOrWhiteSpace(AuthError);

    public bool HasAuthStatus => !string.IsNullOrWhiteSpace(AuthStatus);

    public bool ShouldOfferTrayOnClose => Watching;

    public string WatcherStatusText => Watching ? "active" : "idle";

    public string AutoSyncStatusText => AutoSyncEnabled ? "on" : "off";

    public async Task InitializeAsync()
    {
        protocolRegistrationService.EnsureRegistered();

        await settingsStore.InitializeAsync();
        await uploadLogStore.InitializeAsync();
        await authService.InitializeAsync();

        settings = settingsStore.Get();
        WatchDirectoryInput = settings.WatchDir ?? configuration.DefaultReplayFolder;
        LoadHistoryFromStore();
        SyncAuthState();

        authService.SessionChanged += OnAuthStateChanged;
        authService.CallbackStateChanged += OnCallbackStateChanged;
        singleInstanceService.ArgumentsReceived += OnSingleInstanceArgumentsReceived;
        replayWatcherService.ReplayReady += OnReplayReadyAsync;

        initialized = true;
        singleInstanceService.PublishStartupArguments();
        await ReconcileWatcherAsync();
    }

    public async ValueTask DisposeAsync()
    {
        singleInstanceService.ArgumentsReceived -= OnSingleInstanceArgumentsReceived;
        authService.SessionChanged -= OnAuthStateChanged;
        authService.CallbackStateChanged -= OnCallbackStateChanged;
        replayWatcherService.ReplayReady -= OnReplayReadyAsync;
        await replayWatcherService.DisposeAsync();
    }

    public static MainViewModel Create(
        AppConfiguration configuration,
        AppPaths paths,
        UiDispatcher uiDispatcher,
        ProtocolRegistrationService protocolRegistrationService,
        SingleInstanceService singleInstanceService,
        SystemClock clock,
        IProtectedFileStore protectedFileStore,
        out FilePickerService filePickerService)
    {
        var settingsStore = new SettingsStore(paths);
        var uploadLogStore = new UploadLogStore(paths);
        var authService = new AuthService(configuration, protectedFileStore, clock);
        var backendApiClient = new BackendApiClient(configuration);
        var uploadService = new UploadService(authService, backendApiClient);
        var watcherService = new ReplayWatcherService();
        filePickerService = new FilePickerService();

        return new MainViewModel(
            configuration,
            uiDispatcher,
            protocolRegistrationService,
            singleInstanceService,
            clock,
            settingsStore,
            uploadLogStore,
            protectedFileStore,
            authService,
            backendApiClient,
            uploadService,
            watcherService,
            filePickerService);
    }

    private async Task StartSignInAsync()
    {
        try
        {
            isOpeningBrowser = true;
            RefreshComputedState();
            AuthError = null;
            await authService.StartBrowserSignInAsync();
            SyncAuthState();
        }
        catch (Exception error)
        {
            AuthError = error.Message;
        }
        finally
        {
            isOpeningBrowser = false;
            RefreshComputedState();
        }
    }

    private async Task SignOutAsync()
    {
        await replayWatcherService.StopAsync();
        Watching = false;
        activeWatchDirectory = null;
        IsSettingsView = false;
        await authService.SignOutAsync();
        SyncAuthState();
    }

    private async Task SaveWatchDirectoryAsync()
    {
        var normalized = WatchDirectoryInput.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            WatchDirectoryInput = settings.WatchDir ?? configuration.DefaultReplayFolder;
            return;
        }

        settings = await settingsStore.UpdateAsync(watchDir: normalized);
        WatchDirectoryInput = normalized;
        await ReconcileWatcherAsync();
    }

    private async Task ChooseFolderAsync()
    {
        var picked = await filePickerService.PickFolderAsync(ResolvedWatchDirectory);
        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        WatchDirectoryInput = picked;
        await SaveWatchDirectoryAsync();
    }

    private async Task ToggleAutoSyncAsync()
    {
        settings = await settingsStore.UpdateAsync(autoSyncEnabled: !settings.AutoSyncEnabled);
        RefreshComputedState();
        await ReconcileWatcherAsync();
    }

    private async Task ToggleDeleteAfterUploadAsync()
    {
        settings = await settingsStore.UpdateAsync(deleteAfterUploadEnabled: !settings.DeleteAfterUploadEnabled);
        RefreshComputedState();
    }

    private async Task ManualSyncAsync()
    {
        if (!IsSignedIn)
        {
            return;
        }

        await RunBusyAsync(() => SyncDirectoryAsync(ResolvedWatchDirectory));
    }

    private async Task PickFilesAsync()
    {
        if (!IsSignedIn)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            var files = await filePickerService.PickReplayFilesAsync(ResolvedWatchDirectory);
            await ProcessReplaysAsync(files);
        });
    }

    private async Task RetryFailedAsync()
    {
        if (!IsSignedIn)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            var failed = uploadLogStore
                .GetSnapshot()
                .Where(static pair => pair.Value.Status == UploadStatus.Failed)
                .Select(static pair => pair.Key)
                .ToArray();
            var knownFiles = (await replayWatcherService.ScanAsync(ResolvedWatchDirectory))
                .ToDictionary(static path => Path.GetFileName(path), static path => path, StringComparer.OrdinalIgnoreCase);

            await ProcessReplaysAsync(
                failed
                    .Where(knownFiles.ContainsKey)
                    .Select(name => knownFiles[name])
                    .ToArray());
        });
    }

    private async Task ClearFailedAsync()
    {
        await uploadLogStore.ClearFailedAsync();
        LoadHistoryFromStore();
    }

    private async Task ExportLogAsync()
    {
        await uploadLogStore.ExportAsync();
    }

    private async Task OnReplayReadyAsync(string path)
    {
        await ProcessReplayAsync(path);
    }

    internal async Task SyncDirectoryAsync(string directory)
    {
        var files = await replayWatcherService.ScanAsync(directory);
        await ProcessReplaysAsync(files);
    }

    private async Task ProcessReplayAsync(string filePath)
    {
        await ProcessReplaysAsync([filePath]);
    }

    private async Task ProcessReplaysAsync(IEnumerable<string> filePaths)
    {
        var pending = filePaths
            .Select(static path => new PendingReplay(path, Path.GetFileName(path)))
            .Where(static replay => !string.IsNullOrWhiteSpace(replay.FileName))
            .DistinctBy(static replay => replay.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        var reserved = new List<PendingReplay>(pending.Length);
        await syncGate.WaitAsync();
        try
        {
            foreach (var replay in pending)
            {
                if (inFlight.Add(replay.FileName))
                {
                    reserved.Add(replay);
                }
            }
        }
        finally
        {
            syncGate.Release();
        }

        try
        {
            if (!IsSignedIn)
            {
                return;
            }

            var readyToUpload = reserved
                .Where(replay => !ShouldSkip(uploadLogStore.GetStatus(replay.FileName)))
                .ToArray();
            if (readyToUpload.Length == 0)
            {
                return;
            }

            var outcomes = await uploadService.UploadReplaysAsync(
                readyToUpload
                    .Select(static replay => (replay.FilePath, replay.FileName))
                    .ToArray());

            foreach (var replay in readyToUpload)
            {
                var outcome = outcomes.GetValueOrDefault(replay.FileName)
                    ?? new UploadOutcome.Failed("Upload service did not return an outcome.");
                var detail = TryDeleteUploadedReplay(replay.FilePath, outcome);

                UploadLogEntry entry = outcome switch
                {
                    UploadOutcome.Uploaded => new(UploadStatus.Uploaded, clock.UtcNow, Detail: detail),
                    UploadOutcome.AlreadyUploaded alreadyUploaded => new(UploadStatus.AlreadyUploaded, clock.UtcNow, HttpStatus: alreadyUploaded.HttpStatus, Detail: detail),
                    UploadOutcome.Failed failed => new(UploadStatus.Failed, clock.UtcNow, failed.Error, failed.HttpStatus),
                    _ => new(UploadStatus.Failed, clock.UtcNow, "Unknown upload result")
                };

                await uploadLogStore.SetAsync(replay.FileName, entry);
            }

            LoadHistoryFromStore();
        }
        finally
        {
            await syncGate.WaitAsync();
            try
            {
                foreach (var replay in reserved)
                {
                    inFlight.Remove(replay.FileName);
                }
            }
            finally
            {
                syncGate.Release();
            }
        }
    }

    private async void OnAuthStateChanged(object? sender, EventArgs e)
    {
        try
        {
            await uiDispatcher.InvokeAsync(() =>
            {
                SyncAuthState();
            });
            await ReconcileWatcherAsync();
        }
        catch (Exception error)
        {
            await uiDispatcher.InvokeAsync(() =>
            {
                AuthError = error.Message;
                AuthStatus = "The app could not refresh auth state.";
            });
        }
    }

    private async void OnCallbackStateChanged(object? sender, EventArgs e)
    {
        try
        {
            await uiDispatcher.InvokeAsync(() =>
            {
                SyncAuthState();
                BringToFrontRequested?.Invoke(this, EventArgs.Empty);
            });
            await ReconcileWatcherAsync();
        }
        catch (Exception error)
        {
            await uiDispatcher.InvokeAsync(() =>
            {
                AuthError = error.Message;
                AuthStatus = "The sign-in callback could not be applied.";
            });
        }
    }

    private async void OnSingleInstanceArgumentsReceived(object? sender, string[] args)
    {
        try
        {
            await authService.HandleLaunchArgumentsAsync(args);
            await uiDispatcher.InvokeAsync(() =>
            {
                BringToFrontRequested?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (Exception error)
        {
            await uiDispatcher.InvokeAsync(() =>
            {
                AuthError = error.Message;
                AuthStatus = "The app could not process the sign-in callback.";
                BringToFrontRequested?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    private void SyncAuthState()
    {
        if (!IsSignedIn && IsSettingsView)
        {
            IsSettingsView = false;
        }

        AuthStatus = authService.CallbackMessage
            ?? (IsSignedIn ? "Signed in and ready to sync replays." : "Sign in to start syncing replays.");
        AuthError = authService.CallbackError;

        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(IsSignedOut));
        OnPropertyChanged(nameof(UserLabel));
        OnPropertyChanged(nameof(IsAuthBusy));
        OnPropertyChanged(nameof(SignInButtonText));
        OnPropertyChanged(nameof(ShouldOfferTrayOnClose));
        OnPropertyChanged(nameof(HasAuthStatus));
        RefreshComputedState();
    }

    private async Task ReconcileWatcherAsync()
    {
        if (!initialized)
        {
            return;
        }

        var shouldWatch = IsSignedIn && settings.AutoSyncEnabled;
        var targetDirectory = ResolvedWatchDirectory;
        if (shouldWatch && !string.IsNullOrWhiteSpace(targetDirectory))
        {
            if (Watching && string.Equals(activeWatchDirectory, targetDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var result = await replayWatcherService.StartAsync(targetDirectory);
            await uiDispatcher.InvokeAsync(() =>
            {
                Watching = result.Watching;
                activeWatchDirectory = result.Directory;
                OnPropertyChanged(nameof(WatcherStatusText));
            });
            BeginAutomaticCatchUp(targetDirectory);
            return;
        }

        if (Watching || activeWatchDirectory is not null)
        {
            await replayWatcherService.StopAsync();
            await uiDispatcher.InvokeAsync(() =>
            {
                Watching = false;
                activeWatchDirectory = null;
                OnPropertyChanged(nameof(WatcherStatusText));
            });
        }
    }

    private void BeginAutomaticCatchUp(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        _ = RunAutomaticCatchUpAsync(directory);
    }

    private async Task RunAutomaticCatchUpAsync(string directory)
    {
        await automaticCatchUpGate.WaitAsync();
        try
        {
            await RunBusyAsync(() => SyncDirectoryAsync(directory));
        }
        catch
        {
            // Scan failures already collapse to an empty set, and upload failures are tracked per file.
        }
        finally
        {
            automaticCatchUpGate.Release();
        }
    }

    private void LoadHistoryFromStore()
    {
        var rows = uploadLogStore.GetSnapshot()
            .Select(static pair => new UploadHistoryRow(
                pair.Key,
                pair.Value.Status,
                pair.Value.At,
                pair.Value.Detail ?? pair.Value.Error ?? (pair.Value.HttpStatus is int status ? $"HTTP {status}" : null)))
            .OrderByDescending(static row => row.At)
            .ToArray();

        uiDispatcher.Post(() =>
        {
            HistoryRows.Clear();
            foreach (var row in rows)
            {
                HistoryRows.Add(row);
            }

            UploadedCount = rows.Count(static row => row.Status == UploadStatus.Uploaded);
            AlreadyUploadedCount = rows.Count(static row => row.Status == UploadStatus.AlreadyUploaded);
            FailedCount = rows.Count(static row => row.Status == UploadStatus.Failed);
            OnPropertyChanged(nameof(UploadedCount));
            OnPropertyChanged(nameof(AlreadyUploadedCount));
            OnPropertyChanged(nameof(FailedCount));
            OnPropertyChanged(nameof(TotalCount));
            RefreshComputedState();
        });
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string ResolvedWatchDirectory =>
        string.IsNullOrWhiteSpace(WatchDirectoryInput)
            ? settings.WatchDir ?? configuration.DefaultReplayFolder
            : WatchDirectoryInput.Trim();

    private string? TryDeleteUploadedReplay(string filePath, UploadOutcome outcome)
    {
        if (!settings.DeleteAfterUploadEnabled || outcome is not (UploadOutcome.Uploaded or UploadOutcome.AlreadyUploaded))
        {
            return null;
        }

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            return null;
        }
        catch (Exception error)
        {
            return $"Could not delete local replay: {error.Message}";
        }
    }

    private static bool ShouldSkip(UploadLogEntry? entry)
    {
        return entry?.Status is UploadStatus.Uploaded or UploadStatus.AlreadyUploaded;
    }

    private void RefreshComputedState()
    {
        SignInCommand.RaiseCanExecuteChanged();
        SignOutCommand.RaiseCanExecuteChanged();
        SaveWatchDirectoryCommand.RaiseCanExecuteChanged();
        ChooseFolderCommand.RaiseCanExecuteChanged();
        ToggleAutoSyncCommand.RaiseCanExecuteChanged();
        ToggleDeleteAfterUploadCommand.RaiseCanExecuteChanged();
        ManualSyncCommand.RaiseCanExecuteChanged();
        PickFilesCommand.RaiseCanExecuteChanged();
        RetryFailedCommand.RaiseCanExecuteChanged();
        ClearFailedCommand.RaiseCanExecuteChanged();
        ExportLogCommand.RaiseCanExecuteChanged();
        ShowDashboardCommand.RaiseCanExecuteChanged();
        ShowSettingsCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(IsAuthBusy));
        OnPropertyChanged(nameof(SignInButtonText));
        OnPropertyChanged(nameof(AutoSyncEnabled));
        OnPropertyChanged(nameof(AutoSyncStatusText));
        OnPropertyChanged(nameof(DeleteAfterUploadEnabled));
        OnPropertyChanged(nameof(ShouldOfferTrayOnClose));
    }

    private sealed record PendingReplay(
        string FilePath,
        string FileName);
}
