using System.Net;
using System.Text;
using PlannerEdge.Helper.Outlook;

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
        var state = new OutlookAccountState(tokens, new OutlookMemoryStore());
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
        var state = new OutlookAccountState(tokens, new OutlookMemoryStore());
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
