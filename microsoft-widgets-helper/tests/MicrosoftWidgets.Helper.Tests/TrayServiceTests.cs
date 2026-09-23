using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PlannerEdge.Helper.Hosting;
using PlannerEdge.Helper.Security;
using PlannerEdge.Helper.Updates;

namespace PlannerEdge.Helper.Tests;

public sealed class TrayServiceTests
{
    [Fact]
    public async Task TrayCanStartAndStopRepeatedlyWithoutLeavingItsThreadRunning()
    {
        for (var i = 0; i < 2; i++)
        {
            using var lifetime = new Lifetime();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var service = new TrayService(new UpdateService(new Source(), new Installer(), "0.3.2"), new LocalAccessService(TimeProvider.System), lifetime, NullLogger<TrayService>.Instance);
            await service.StartAsync(timeout.Token);
            await service.StopAsync(timeout.Token);
            Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
        }
    }

    [Fact]
    public async Task ShutdownDuringStartup_DoesNotStrandTheMessageLoop()
    {
        using var lifetime = new Lifetime();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var service = new TrayService(new UpdateService(new Source(), new Installer(), "0.3.2"), new LocalAccessService(TimeProvider.System), lifetime, NullLogger<TrayService>.Instance);
        var starting = service.StartAsync(timeout.Token);
        lifetime.StopApplication();
        await starting;
        await service.StopAsync(timeout.Token);
    }

    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => stopping.Cancel();
        public void Dispose() => stopping.Dispose();
    }
    private sealed class Source : IReleaseClient
    {
        public Task<UpdateRelease?> CheckAsync(string version, CancellationToken ct) => Task.FromResult<UpdateRelease?>(null);
        public Task DownloadAsync(UpdateRelease release, string path, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Installer : IUpdateInstaller
    {
        public bool CanInstall => false;
        public string CreateDownloadPath() => throw new NotSupportedException();
        public void Launch(string path, UpdateRelease release) => throw new NotSupportedException();
    }
}
