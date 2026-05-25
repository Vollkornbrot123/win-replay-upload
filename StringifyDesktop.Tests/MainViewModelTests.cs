using System.Net;
using StringifyDesktop.Models;
using StringifyDesktop.Services;
using StringifyDesktop.ViewModels;

namespace StringifyDesktop.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public async Task SyncDirectoryAsync_UploadsUntrackedFiles_AndSkipsUploadedOnes()
    {
        var tempRoot = CreateTempDirectory();
        var replayDirectory = Path.Combine(tempRoot, "Replays");
        Directory.CreateDirectory(replayDirectory);

        var existingReplay = Path.Combine(replayDirectory, "existing.replay");
        var newReplay = Path.Combine(replayDirectory, "new.replay");
        await File.WriteAllBytesAsync(existingReplay, ReplayFileTestData.CreateReplayBytes(headerMagic: ReplayFileTestData.NetworkDemoMagic));
        await File.WriteAllBytesAsync(newReplay, ReplayFileTestData.CreateReplayBytes(headerMagic: ReplayFileTestData.NetworkDemoMagic));

        try
        {
            var paths = new AppPaths(tempRoot);
            var configuration = new AppConfiguration(
                "https://backend.example.invalid",
                "https://issuer.example.invalid",
                "client-id",
                "profile email",
                "stringify-gg://auth/callback",
                replayDirectory);
            var clock = new SystemClock();
            var settingsStore = new SettingsStore(paths);
            var uploadLogStore = new UploadLogStore(paths);
            var protectedFileStore = new TestProtectedFileStore();

            await settingsStore.InitializeAsync();
            await uploadLogStore.InitializeAsync();
            await uploadLogStore.SetAsync("existing.replay", new UploadLogEntry(UploadStatus.Uploaded, DateTimeOffset.UtcNow.AddMinutes(-5)));
            await protectedFileStore.SaveSessionAsync(new OAuthSession(
                "token",
                null,
                null,
                "Bearer",
                "profile email",
                DateTimeOffset.UtcNow.AddHours(1),
                new DesktopAuthUser("user-1", "user@example.com", true, "User", "user", null)));

            var authService = new AuthService(configuration, protectedFileStore, clock);
            await authService.InitializeAsync();

            var requestedBatches = new List<string[]>();
            var uploadService = new UploadService(
                authService,
                new FakeUploadRoutingClient((files, _) =>
                {
                    requestedBatches.Add(files.Select(static file => file.FileName).ToArray());
                    return Task.FromResult<IReadOnlyList<UploadRouteResult>>(
                        files
                            .Select(static file => new UploadRouteResult.Ready(
                                file.FileName,
                                "https://upload.example.invalid/replay",
                                "PUT",
                                new Dictionary<string, string>()))
                            .ToArray());
                }),
                new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));

            var viewModel = new MainViewModel(
                configuration,
                new UiDispatcher(),
                new ProtocolRegistrationService(configuration),
                null!,
                clock,
                settingsStore,
                uploadLogStore,
                protectedFileStore,
                authService,
                null!,
                uploadService,
                new ReplayWatcherService(),
                new FilePickerService());

            await viewModel.SyncDirectoryAsync(replayDirectory);

            Assert.Single(requestedBatches);
            Assert.Equal(["new.replay"], requestedBatches[0]);

            Assert.Equal(2, uploadLogStore.GetSnapshot().Count);
            Assert.Equal(UploadStatus.Uploaded, uploadLogStore.GetStatus("existing.replay")?.Status);
            Assert.Equal(UploadStatus.Uploaded, uploadLogStore.GetStatus("new.replay")?.Status);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task SyncDirectoryAsync_DeletesReplayAfterSuccessfulSync_WhenEnabled()
    {
        var tempRoot = CreateTempDirectory();
        var replayDirectory = Path.Combine(tempRoot, "Replays");
        Directory.CreateDirectory(replayDirectory);

        var replayPath = Path.Combine(replayDirectory, "delete-me.replay");
        await File.WriteAllBytesAsync(replayPath, ReplayFileTestData.CreateReplayBytes(headerMagic: ReplayFileTestData.NetworkDemoMagic));

        try
        {
            var paths = new AppPaths(tempRoot);
            var configuration = new AppConfiguration(
                "https://backend.example.invalid",
                "https://issuer.example.invalid",
                "client-id",
                "profile email",
                "stringify-gg://auth/callback",
                replayDirectory);
            var clock = new SystemClock();
            var settingsStore = new SettingsStore(paths);
            var uploadLogStore = new UploadLogStore(paths);
            var protectedFileStore = new TestProtectedFileStore();

            await settingsStore.InitializeAsync();
            await settingsStore.UpdateAsync(deleteAfterUploadEnabled: true);
            await uploadLogStore.InitializeAsync();
            await protectedFileStore.SaveSessionAsync(new OAuthSession(
                "token",
                null,
                null,
                "Bearer",
                "profile email",
                DateTimeOffset.UtcNow.AddHours(1),
                new DesktopAuthUser("user-1", "user@example.com", true, "User", "user", null)));

            var authService = new AuthService(configuration, protectedFileStore, clock);
            await authService.InitializeAsync();

            var uploadService = new UploadService(
                authService,
                new FakeUploadRoutingClient((files, _) => Task.FromResult<IReadOnlyList<UploadRouteResult>>(
                    files
                        .Select(static file => new UploadRouteResult.Ready(
                            file.FileName,
                            "https://upload.example.invalid/replay",
                            "PUT",
                            new Dictionary<string, string>()))
                        .ToArray())),
                new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));

            var viewModel = new MainViewModel(
                configuration,
                new UiDispatcher(),
                new ProtocolRegistrationService(configuration),
                null!,
                clock,
                settingsStore,
                uploadLogStore,
                protectedFileStore,
                authService,
                null!,
                uploadService,
                new ReplayWatcherService(),
                new FilePickerService());

            await viewModel.SyncDirectoryAsync(replayDirectory);

            Assert.False(File.Exists(replayPath));
            Assert.Equal(UploadStatus.Uploaded, uploadLogStore.GetStatus("delete-me.replay")?.Status);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"StringifyDesktop.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeUploadRoutingClient : IUploadRoutingClient
    {
        private readonly Func<IReadOnlyList<UploadRouteRequest>, string, Task<IReadOnlyList<UploadRouteResult>>> handler;

        public FakeUploadRoutingClient(Func<IReadOnlyList<UploadRouteRequest>, string, Task<IReadOnlyList<UploadRouteResult>>> handler)
        {
            this.handler = handler;
        }

        public int MaxBatchSize => 5;

        public Task<IReadOnlyList<UploadRouteResult>> RequestUploadUrlsAsync(IReadOnlyList<UploadRouteRequest> files, string bearerToken, CancellationToken cancellationToken = default)
        {
            return handler(files, bearerToken);
        }
    }
}
