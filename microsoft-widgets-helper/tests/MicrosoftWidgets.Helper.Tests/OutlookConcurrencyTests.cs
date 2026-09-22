using PlannerEdge.Helper.Auth;
using System.Net;
using System.Text;
using PlannerEdge.Helper.Outlook;
using PlannerEdge.Helper.Security;
using PlannerEdge.Helper.Storage;

namespace MicrosoftWidgets.Helper.Tests;

public sealed class OutlookConcurrencyTests
{
    [Fact]
    public async Task GraphAllowsAtMostFourConcurrentRequestsAndCancelsQueuedReads()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var handler = new OutlookAsyncHandler(async (_, ct) =>
        {
            var count = Interlocked.Increment(ref active);
            Interlocked.Exchange(ref maximum, Math.Max(maximum, count));
            if (count == 4) entered.TrySetResult();
            try { await release.Task.WaitAsync(ct); return "{}"; }
            finally { Interlocked.Decrement(ref active); }
        });
        var graph = new OutlookGraphClient(new HttpClient(handler), new OutlookTokens());
        using var cancel = new CancellationTokenSource();
        var tasks = Enumerable.Range(0, 8).Select(_ => graph.GetAsync("me/mailboxSettings", cancel.Token)).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, maximum);
        cancel.Cancel();
        foreach (var task in tasks) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(4, handler.Count);
    }

    [Fact]
    public async Task SameRangeDeduplicatesAndOneCancelledWaiterDoesNotCancelAnother()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var handler = new OutlookAsyncHandler(async (uri, ct) =>
        {
            if (uri.AbsolutePath.EndsWith("/calendars")) return """{"value":[{"id":"cal","name":"Calendar"}]}""";
            if (uri.AbsolutePath.EndsWith("/memberOf")) return """{"value":[]}""";
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return """{"value":[]}""";
        });
        var tokens = new OutlookTokens();
        var state = new MicrosoftAccountState(tokens, new OutlookMemoryStore());
        var clock = new OutlookClock();
        var graph = new OutlookGraphClient(new HttpClient(handler), tokens);
        var catalog = new CalendarCatalogService(graph, state, new OutlookSettingsStore(new OutlookMemoryStore()), clock);
        var views = new CalendarViewService(graph, catalog, state, clock);
        var key = Assert.Single(await catalog.GetAsync(default)).Key;
        var request = new ViewRequest([key], "2026-09-01T00:00:00Z", "2026-09-08T00:00:00Z");
        using var cancel = new CancellationTokenSource();
        var first = views.GetAsync(request, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = views.GetAsync(request, default);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.TrySetResult();
        Assert.Null(Assert.Single((await second).Sources).Error);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ResetDuringGraphReadPreventsDataFromBeingPublished()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new OutlookAsyncHandler(async (uri, ct) =>
        {
            if (uri.AbsolutePath.EndsWith("/calendars")) return """{"value":[{"id":"cal","name":"Calendar"}]}""";
            if (uri.AbsolutePath.EndsWith("/memberOf")) return """{"value":[]}""";
            entered.TrySetResult();
            await release.Task;
            return """{"value":[{"id":"secret","subject":"Sensitive","start":{"dateTime":"2026-09-01T00:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-09-02T00:00:00","timeZone":"UTC"}}]}""";
        });
        var tokens = new OutlookTokens();
        var state = new MicrosoftAccountState(tokens, new OutlookMemoryStore());
        var graph = new OutlookGraphClient(new HttpClient(handler), tokens);
        var clock = new OutlookClock();
        var catalog = new CalendarCatalogService(graph, state, new OutlookSettingsStore(new OutlookMemoryStore()), clock);
        var views = new CalendarViewService(graph, catalog, state, clock);
        var key = Assert.Single(await catalog.GetAsync(default)).Key;
        var read = views.GetAsync(new([key], "2026-09-01T00:00:00Z", "2026-09-08T00:00:00Z"), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.InvalidateAsync(default);
        release.TrySetResult();
        Assert.Empty((await read).Events);
    }

    [Fact]
    public async Task Owner_source_removal_serializes_with_account_invalidation()
    {
        var tokens = new OutlookTokens();
        var store = new PausingOutlookStore();
        var state = new MicrosoftAccountState(tokens, store);
        var access = new LocalAccessService(TimeProvider.System, state);
        var lease = await state.GetAsync(default);
        var settings = new OutlookSettingsStore(store);
        const string owner = "room@example.com";
        await settings.SetOwnerAsync(lease.Key, owner, true, default);
        var key = OutlookTokenProvider.Hash(lease.Key + "\nusers/" + Uri.EscapeDataString(owner) + "/calendar");
        var catalog = new CalendarCatalogService(
            new OutlookGraphClient(new HttpClient(new OutlookAsyncHandler((_, _) => Task.FromResult("{}"))), tokens),
            state, settings, new OutlookClock());
        var session = await access.ExchangeBootstrapAsync(access.CreateBootstrap().Token, default);
        var authorization = await access.AuthorizeOwnerSessionAsync(session, default);
        Assert.NotNull(authorization);
        using var request = state.BindOwnerRequest(authorization);
        store.PauseNextRead();

        var removal = catalog.RemoveAsync(key, default);
        await store.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var invalidation = state.InvalidateAsync(default);
        Assert.False(invalidation.IsCompleted);
        store.ReleaseRead.TrySetResult();

        await removal;
        await invalidation;
        Assert.DoesNotContain(owner, await settings.GetOwnersAsync(lease.Key, default));
    }
}

internal sealed class OutlookAsyncHandler(Func<Uri, CancellationToken, Task<string>> respond) : HttpMessageHandler
{
    private int count;
    public int Count => count;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref count);
        return new(HttpStatusCode.OK) { Content = new StringContent(await respond(request.RequestUri!, ct), Encoding.UTF8, "application/json") };
    }
}

internal sealed class PausingOutlookStore : ILocalJsonStore
{
    private readonly OutlookMemoryStore inner = new();
    private int pauseRead;
    public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void PauseNextRead() => Interlocked.Exchange(ref pauseRead, 1);

    public async Task<T?> ReadAsync<T>(string name, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref pauseRead, 0) == 1)
        {
            ReadEntered.TrySetResult();
            await ReleaseRead.Task.WaitAsync(ct);
        }
        return await inner.ReadAsync<T>(name, ct);
    }

    public Task WriteAsync<T>(string name, T value, CancellationToken ct) => inner.WriteAsync(name, value, ct);
}
