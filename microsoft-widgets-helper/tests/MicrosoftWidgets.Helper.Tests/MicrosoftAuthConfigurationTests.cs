using Microsoft.Extensions.Options;
using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Tests;

public sealed class MicrosoftAuthConfigurationTests
{
    [Fact]
    public async Task SavingConfigurationAppliesWithoutRestartAndPersists()
    {
        var root = Path.Combine(Path.GetTempPath(), "PlannerEdgeTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalJsonStore(root);
            var options = Options.Create(new AzureAdOptions());
            var service = new MicrosoftAuthService(options, store, root);
            Assert.False((await service.GetStatusAsync(CancellationToken.None)).IsSignedIn);

            var clientId = Guid.NewGuid().ToString();
            await service.SaveConfigurationAsync(new AzureAdOptions { ClientId = clientId }, CancellationToken.None);
            Assert.Equal(clientId, (await service.GetConfigurationAsync(CancellationToken.None)).ClientId);
            Assert.Null((await service.GetStatusAsync(CancellationToken.None)).Error);

            var restarted = new MicrosoftAuthService(options, store, root);
            Assert.Equal(clientId, (await restarted.GetConfigurationAsync(CancellationToken.None)).ClientId);
        }
        finally
        {
            var absoluteRoot = Path.GetFullPath(root);
            var testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PlannerEdgeTests"));
            if (absoluteRoot.StartsWith(testRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(absoluteRoot))
                Directory.Delete(absoluteRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsInvalidApplicationId()
    {
        var service = new MicrosoftAuthService(Options.Create(new AzureAdOptions()),
            new LocalJsonStore(Path.Combine(Path.GetTempPath(), "PlannerEdgeTests", Guid.NewGuid().ToString("N"))));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveConfigurationAsync(
            new AzureAdOptions { ClientId = "not-an-id" }, CancellationToken.None));
    }

    [Fact]
    public async Task FailedInitializationCanRetryAfterCacheBecomesAvailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "PlannerEdgeTests", Guid.NewGuid().ToString("N"));
        var blockedCache = Path.Combine(root, "cache");
        Directory.CreateDirectory(root);
        try
        {
            var store = new LocalJsonStore(root);
            await store.WriteAsync("microsoft-auth", new AzureAdOptions { ClientId = Guid.NewGuid().ToString() }, CancellationToken.None);
            await File.WriteAllTextAsync(blockedCache, "occupied");
            var service = new MicrosoftAuthService(Options.Create(new AzureAdOptions()), store, blockedCache);

            await Assert.ThrowsAnyAsync<IOException>(() => service.GetConfigurationAsync(CancellationToken.None));
            File.Delete(blockedCache);

            Assert.Null((await service.GetStatusAsync(CancellationToken.None)).Error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
