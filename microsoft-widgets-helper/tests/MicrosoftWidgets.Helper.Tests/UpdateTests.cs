using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using PlannerEdge.Helper.Hosting;
using PlannerEdge.Helper.Security;
using PlannerEdge.Helper.Updates;

namespace PlannerEdge.Helper.Tests;

public sealed class UpdateTests
{
    private static string Release(string tag = "v0.4.0", bool prerelease = false, string? digest = null) => JsonSerializer.Serialize(new
    {
        tag_name = tag, draft = false, prerelease, body = "Changes",
        assets = new[] { new { name = $"MicrosoftWidgetsSetup-{tag.TrimStart('v')}.exe", size = 3,
            browser_download_url = $"https://github.com/Knack25/Xeneon-Widgets/releases/download/{tag}/MicrosoftWidgetsSetup-{tag.TrimStart('v')}.exe",
            digest = digest ?? "sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad" } }
    });

    [Fact]
    public void StableRelease_UsesSuiteVersionAndExpectedAsset()
    {
        var release = ReleaseClient.Parse(Release(), "0.3.1");
        Assert.Equal("0.4.0", release!.Version);
        Assert.Null(ReleaseClient.Parse(Release(), "0.4.0"));
        Assert.Null(ReleaseClient.Parse(Release(), "0.5.0"));
        Assert.NotNull(ReleaseClient.Parse(Release("v0.10.0"), "0.9.0"));
    }

    [Theory]
    [InlineData("v0.4.0-beta")]
    [InlineData("../0.4.0")]
    public void MalformedVersions_AreRejected(string tag) => Assert.Throws<InvalidDataException>(() => ReleaseClient.Parse(Release(tag), "0.3.1"));

    [Fact]
    public void PreviewAndUnverifiedReleases_AreNotOffered()
    {
        Assert.Null(ReleaseClient.Parse(Release(prerelease: true), "0.3.1"));
        Assert.Throws<InvalidDataException>(() => ReleaseClient.Parse(Release(digest: ""), "0.3.1"));
        Assert.Throws<InvalidDataException>(() => ReleaseClient.Parse(Release().Replace("github.com/Knack25", "evil.example/Knack25"), "0.3.1"));
    }

    [Theory]
    [InlineData("abc", true)]
    [InlineData("bad", false)]
    public async Task Download_VerifiesDigestBeforeReturning(string content, bool valid)
    {
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) }));
        var client = new ReleaseClient(http);
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "setup.exe");
            if (valid) { await client.DownloadAsync(ReleaseClient.Parse(Release(), "0.3.1")!, path, default); Assert.Equal(content, await File.ReadAllTextAsync(path)); }
            else { await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(ReleaseClient.Parse(Release(), "0.3.1")!, path, default)); Assert.False(File.Exists(path)); }
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    [Fact]
    public async Task UntrustedDownloadRedirect_IsRejected()
    {
        using var http = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://evil.example/setup.exe");
            return response;
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ReleaseClient(http).DownloadAsync(
            ReleaseClient.Parse(Release(), "0.3.1")!, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), default));
    }

    [Fact]
    public async Task SimultaneousChecks_AreCoalesced()
    {
        var source = new WaitingSource();
        var service = new UpdateService(source, new Installer(), "0.3.1");
        var first = service.CheckAsync(default);
        await service.CheckAsync(default);
        Assert.Equal(1, source.Checks);
        Assert.Equal("checking", service.Status.State);
        source.Complete.SetResult(null);
        await first;
        Assert.Equal("current", service.Status.State);
    }

    private sealed class WaitingSource : IReleaseClient
    {
        public int Checks;
        public TaskCompletionSource<UpdateRelease?> Complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<UpdateRelease?> CheckAsync(string version, CancellationToken ct) { Checks++; return Complete.Task; }
        public Task DownloadAsync(UpdateRelease release, string path, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Checking_NeverInstalls_AndApprovalMustMatchVersion()
    {
        var source = new Source();
        var installer = new Installer();
        var service = new UpdateService(source, installer, "0.3.1");
        await service.CheckAsync(default);
        Assert.Equal("available", service.Status.State);
        Assert.Equal(0, installer.Starts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync("0.3.9", default));
        Assert.Equal(0, source.Downloads);
        await service.InstallAsync("0.4.0", default);
        Assert.Equal(1, installer.Starts);
        Assert.Equal("installing", service.Status.State);
        await service.InstallAsync("0.4.0", default);
        Assert.Equal(1, installer.Starts);
    }

    [Fact]
    public async Task FailedDownload_DoesNotLaunchInstaller_AndCanRetry()
    {
        var source = new Source { FailDownload = true };
        var installer = new Installer();
        var service = new UpdateService(source, installer, "0.3.1");
        await service.CheckAsync(default);
        await service.InstallAsync("0.4.0", default);
        Assert.Equal("error", service.Status.State);
        Assert.Equal(0, installer.Starts);
        source.FailDownload = false;
        await service.InstallAsync("0.4.0", default);
        Assert.Equal(1, installer.Starts);
    }

    [Fact]
    public async Task CancelledStream_ReturnsToRetryableError()
    {
        var service = new UpdateService(new CancelledSource(), new Installer(), "0.3.1");
        await service.CheckAsync(default);
        await service.InstallAsync("0.4.0", default);
        Assert.Equal("error", service.Status.State);
        Assert.Equal("0.4.0", service.Status.AvailableVersion);
    }

    private sealed class CancelledSource : IReleaseClient
    {
        public Task<UpdateRelease?> CheckAsync(string currentVersion, CancellationToken ct) => Task.FromResult(ReleaseClient.Parse(Release(), currentVersion));
        public Task DownloadAsync(UpdateRelease release, string path, CancellationToken ct) => throw new OperationCanceledException();
    }

    [Fact]
    public async Task RestartWaitsUntilOldInstanceReleasesItsHandle()
    {
        var name = "Local\\MicrosoftWidgetsTest-" + Guid.NewGuid();
        using var instance = new Mutex(false, name);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiting = UpdateInstaller.WaitForInstanceExitAsync(name, timeout.Token);
        Assert.False(waiting.IsCompleted);
        instance.Dispose();
        await waiting;
        Assert.True(waiting.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Update_recovery_stops_the_existing_helper_through_the_same_user_pipe()
    {
        var folder = Path.Combine(Path.GetTempPath(), "MicrosoftWidgets.Helper.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "not-an-executable.txt");
        var resultPath = Path.Combine(folder, "update-result.json");
        await File.WriteAllTextAsync(target, "fixture");
        var pipeName = "MicrosoftWidgets.Helper.Tests." + Guid.NewGuid().ToString("N");
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipe = new HelperControlPipe(new LocalAccessService(TimeProvider.System), NullLogger<HelperControlPipe>.Instance,
            pipeName, _ => { }, () => stopped.TrySetResult());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await pipe.StartAsync(timeout.Token);
        try
        {
            await UpdateInstaller.ApplyAsync(
                ["--apply-update", Path.Combine(folder, "missing-installer.exe"), target, "unused-hash", "0.4.0"],
                resultPath,
                "Local\\MicrosoftWidgetsTest-" + Guid.NewGuid(),
                pipeName);

            await stopped.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            await pipe.StopAsync(CancellationToken.None);
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task FailedCheck_ClearsStaleOffer_AndPortableCannotInstall()
    {
        var source = new Source();
        var service = new UpdateService(source, new Installer { CanInstall = false }, "0.3.1");
        await service.CheckAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync("0.4.0", default));
        source.FailCheck = true;
        await service.CheckAsync(default);
        Assert.Equal("error", service.Status.State);
        Assert.Null(service.Status.AvailableVersion);
    }

    private sealed class Source : IReleaseClient
    {
        public bool FailDownload, FailCheck;
        public int Downloads;
        public Task<UpdateRelease?> CheckAsync(string currentVersion, CancellationToken ct) => FailCheck
            ? throw new HttpRequestException("offline") : Task.FromResult(ReleaseClient.Parse(Release(), currentVersion));
        public Task DownloadAsync(UpdateRelease release, string path, CancellationToken ct)
        {
            Downloads++;
            if (FailDownload) throw new InvalidDataException("bad hash");
            return Task.CompletedTask;
        }
    }

    private sealed class Installer : IUpdateInstaller
    {
        public bool CanInstall { get; set; } = true;
        public int Starts;
        public string CreateDownloadPath() => "test-installer.exe";
        public void Launch(string path, UpdateRelease release) => Starts++;
    }
}
