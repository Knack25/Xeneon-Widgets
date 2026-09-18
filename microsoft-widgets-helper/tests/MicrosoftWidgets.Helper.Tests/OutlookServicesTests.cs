using System.Net;
using PlannerEdge.Helper.Outlook;
using PlannerEdge.Helper.Storage;

namespace MicrosoftWidgets.Helper.Tests;

public sealed class OutlookServicesTests
{
    [Fact]
    public async Task GroupRoutesAndPersonalSecondaryCalendarsAreSourceAware()
    {
        var fixture = new OutlookFixture((uri, _) => uri.AbsolutePath switch
        {
            "/v1.0/me/calendars" => """{"value":[{"id":"primary","name":"Calendar","isDefaultCalendar":true,"owner":{"address":"me@example.com"}},{"id":"secondary","name":"Projects","owner":{"address":"ME@example.com"}}]}""",
            "/v1.0/me/memberOf" => """{"value":[{"id":"group1","displayName":"Team","groupTypes":["Unified"]},{"id":"security","groupTypes":[]}]}""",
            "/v1.0/groups/group1/calendar" => """{"id":"groupcal","name":"Calendar"}""",
            "/v1.0/groups/group1/calendarView" => """{"value":[]}""",
            _ => throw new InvalidOperationException(uri.AbsolutePath)
        });
        var calendars = await fixture.Catalog.GetAsync(default);
        Assert.Equal(2, calendars.Count(c => c.Kind == "personal"));
        var group = Assert.Single(calendars, c => c.Kind == "group");
        await fixture.Views.GetAsync(new([group.Key], "2026-09-01T00:00:00Z", "2026-09-08T00:00:00Z"), default);
        Assert.Contains(fixture.Handler.Requests, r => r.Uri.AbsolutePath == "/v1.0/groups/group1/calendarView");
    }

    [Fact]
    public async Task TransientGroupDiscoveryFailureRetainsKnownSources()
    {
        var offline = false;
        var fixture = new OutlookFixture((uri, _) => uri.AbsolutePath switch
        {
            "/v1.0/me/calendars" => """{"value":[]}""",
            "/v1.0/me/memberOf" when offline => throw new HttpRequestException(),
            "/v1.0/me/memberOf" => """{"value":[{"id":"group1","displayName":"Team","groupTypes":["Unified"]}]}""",
            "/v1.0/groups/group1/calendar" => """{"id":"groupcal","name":"Calendar"}""",
            _ => "{}"
        });
        var original = Assert.Single(await fixture.Catalog.GetAsync(default));
        offline = true;
        fixture.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(original, Assert.Single(await fixture.Catalog.GetAsync(default)));
        Assert.Single(fixture.Catalog.DiscoveryErrors);
    }

    [Fact]
    public async Task MoreThan32SelectedCalendarsRetainAllDisplayedReferences()
    {
        var fixture = new OutlookFixture((uri, _) => uri.AbsolutePath switch
        {
            "/v1.0/me/calendars" => "{\"value\":[" + string.Join(',', Enumerable.Range(0, 40).Select(i => "{\"id\":\"cal" + i + "\",\"name\":\"Calendar\"}")) + "]}",
            "/v1.0/me/memberOf" => """{"value":[]}""",
            _ when uri.AbsolutePath.EndsWith("/calendarView") => """{"value":[{"id":"event","subject":"Title","start":{"dateTime":"2026-09-02T10:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-09-02T11:00:00","timeZone":"UTC"}}]}""",
            _ => """{"id":"event","subject":"Title","start":{"dateTime":"2026-09-02T10:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-09-02T11:00:00","timeZone":"UTC"}}"""
        });
        var calendars = await fixture.Catalog.GetAsync(default);
        var view = await fixture.Views.GetAsync(new(calendars.Select(c => c.Key).ToArray(), "2026-09-01T00:00:00Z", "2026-09-08T00:00:00Z"), default);
        Assert.Equal(40, view.Events.Count);
        foreach (var item in view.Events) Assert.Equal("Title", (await fixture.Details.GetAsync(new(item.CalendarKey, item.Reference), default)).Title);
    }

    [Fact]
    public async Task PrivateSummariesMaskTitlesAndDetailsAreFetchedFresh()
    {
        var fixture = new OutlookFixture();
        var calendar = Assert.Single(await fixture.Catalog.GetAsync(default));
        var view = await fixture.Views.GetAsync(new([calendar.Key], "2026-09-01T00:00:00Z", "2026-09-08T00:00:00Z"), default);
        var summary = Assert.Single(view.Events);
        Assert.Equal("Private event", summary.Title);
        Assert.Equal("2026-09-03", summary.End);
        Assert.DoesNotContain(fixture.Handler.Requests, r => r.Uri.AbsolutePath.EndsWith("/events/event1"));
        var details = await fixture.Details.GetAsync(new(calendar.Key, summary.Reference), default);
        Assert.Equal("Secret title", details.Title);
        Assert.Equal("Authorized description", details.Description);
        Assert.All(fixture.Handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task AccessLossPurgesCachedSummaries(HttpStatusCode status)
    {
        var fixture = new OutlookFixture();
        var calendar = Assert.Single(await fixture.Catalog.GetAsync(default));
        var request = new ViewRequest([calendar.Key], "2026-09-01T00:00:00Z", "2026-09-08T00:00:00Z");
        Assert.Single((await fixture.Views.GetAsync(request, default)).Events);
        fixture.Handler.Status = status;
        var response = await fixture.Views.GetAsync(request, default);
        Assert.Empty(response.Events);
        fixture.Handler.Status = HttpStatusCode.ServiceUnavailable;
        Assert.Empty((await fixture.Views.GetAsync(request, default)).Events);
    }

    [Fact]
    public async Task OfflineCacheIsRangeBoundAndExpires()
    {
        var fixture = new OutlookFixture();
        var calendar = Assert.Single(await fixture.Catalog.GetAsync(default));
        var request = new ViewRequest([calendar.Key], "2026-09-01T00:00:00Z", "2026-09-08T00:00:00Z");
        await fixture.Views.GetAsync(request, default);
        fixture.Handler.Status = HttpStatusCode.BadGateway;
        var stale = await fixture.Views.GetAsync(request, default);
        Assert.Single(stale.Events);
        Assert.True(stale.Sources[0].Stale);
        Assert.Empty((await fixture.Views.GetAsync(request with { End = "2026-09-09T00:00:00Z" }, default)).Events);
        fixture.Clock.Advance(TimeSpan.FromHours(25));
        Assert.Empty((await fixture.Views.GetAsync(request, default)).Events);
    }

    [Theory]
    [InlineData("2026-09-01", "2026-09-08T00:00:00Z")]
    [InlineData("2026-09-01T00:00:00Z", "2027-09-08T00:00:00Z")]
    [InlineData("2026-09-08T00:00:00Z", "2026-09-01T00:00:00Z")]
    public async Task RejectsInvalidRanges(string start, string end)
    {
        var fixture = new OutlookFixture();
        await Assert.ThrowsAsync<OutlookException>(() => fixture.Views.GetAsync(new([], start, end), default));
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task AccountSwitchInvalidatesEventReferences()
    {
        var fixture = new OutlookFixture();
        var calendar = Assert.Single(await fixture.Catalog.GetAsync(default));
        var summary = Assert.Single((await fixture.Views.GetAsync(new([calendar.Key], "2026-09-01T00:00:00Z", "2026-09-08T00:00:00Z"), default)).Events);
        fixture.Tokens.Account = "account-b";
        await Assert.ThrowsAsync<OutlookException>(() => fixture.Details.GetAsync(new(calendar.Key, summary.Reference), default));
    }

    [Fact]
    public async Task SharedOwnerReferencesUseOwnerRoutesAndRemovalClearsDetails()
    {
        var fixture = new OutlookFixture((uri, _) => uri.AbsolutePath switch
        {
            "/v1.0/me/calendars" or "/v1.0/me/memberOf" => """{"value":[]}""",
            _ when uri.AbsolutePath.EndsWith("/calendar") => """{"id":"shared","name":"Resource","owner":{"address":"room@example.com"}}""",
            _ when uri.AbsolutePath.EndsWith("/calendarView") => """{"value":[{"id":"event","start":{"dateTime":"2026-09-01T10:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-09-01T11:00:00","timeZone":"UTC"}}]}""",
            _ => throw new InvalidOperationException()
        });
        var shared = await fixture.Catalog.AddAsync("room@example.com", default);
        Assert.True(shared.IsLocalReference);
        var view = await fixture.Views.GetAsync(new([shared.Key], "2026-09-01T00:00:00Z", "2026-09-08T00:00:00Z"), default);
        Assert.Contains(fixture.Handler.Requests, r => r.Uri.AbsolutePath == "/v1.0/users/room%40example.com/calendar/calendarView");
        await fixture.Catalog.RemoveAsync(shared.Key, default);
        Assert.Empty(await fixture.Catalog.GetAsync(default));
        await Assert.ThrowsAsync<OutlookException>(() => fixture.Details.GetAsync(new(shared.Key, Assert.Single(view.Events).Reference), default));
    }

    [Fact]
    public async Task OutlookPreferencesUseIanaZonesAndCacheMailboxReads()
    {
        var fixture = new OutlookFixture((_, _) => """{"timeZone":"Eastern Standard Time","workingHours":{"daysOfWeek":["monday"],"startTime":"08:00:00","endTime":"18:00:00","timeZone":{"name":"Eastern Standard Time"}}}""");
        var preferences = new OutlookPreferencesService(new OutlookGraphClient(new HttpClient(fixture.Handler), fixture.Tokens), fixture.State, fixture.Clock);
        var result = await preferences.GetAsync(default);
        Assert.Equal("America/New_York", result.WorkingHours!.TimeZone);
        await preferences.GetAsync(default);
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task LocalReferenceCanBeRemovedAfterGraphAccessIsRevoked()
    {
        var fixture = new OutlookFixture((uri, _) => uri.AbsolutePath.EndsWith("/calendar")
            ? """{"id":"shared","name":"Room"}""" : """{"value":[]}""");
        var calendar = await fixture.Catalog.AddAsync("room@example.com", default);
        await fixture.Catalog.GetAsync(default);
        fixture.Handler.Status = HttpStatusCode.Forbidden;
        fixture.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Empty(await fixture.Catalog.GetAsync(default));
        var count = fixture.Handler.Requests.Count;
        await fixture.Catalog.RemoveAsync(calendar.Key, default);
        Assert.Equal(count, fixture.Handler.Requests.Count);
        fixture.Handler.Status = HttpStatusCode.OK;
        Assert.Empty(await fixture.Catalog.GetAsync(default));
    }

    [Theory]
    [InlineData(503, true)]
    [InlineData(403, true)]
    [InlineData(401, false)]
    public async Task WorkingHoursFailureAllowsCalendarFallbackExceptUnauthorized(int statusCode, bool ready)
    {
        var fixture = new OutlookFixture((uri, _) => uri.AbsolutePath switch
        {
            "/v1.0/me/calendars" => """{"value":[{"id":"cal","name":"Calendar"}]}""",
            "/v1.0/me/memberOf" => """{"value":[]}""",
            _ => throw new OutlookException(statusCode == 401 ? "sign_in_required" : "preferences_unavailable", "Working hours unavailable.", statusCode)
        });
        var preferences = new OutlookPreferencesService(new OutlookGraphClient(new HttpClient(fixture.Handler), fixture.Tokens), fixture.State, fixture.Clock);
        var status = new OutlookStatusService(new OutlookFakeAuth { SignedIn = true }, fixture.State, fixture.Catalog, preferences, fixture.Clock);
        var response = await status.GetAsync(default);
        Assert.Equal(ready, response.Ready);
        if (ready)
        {
            Assert.Null(response.Error);
            Assert.Single(response.DiscoveryErrors);
            Assert.Single((await status.GetAsync(default)).DiscoveryErrors);
        }
        else Assert.Equal("sign_in_required", response.Error!.Code);
    }

    [Theory]
    [InlineData("2026-03-08T01:30:00", "Eastern Standard Time", "2026-03-08T06:30:00.0000000+00:00")]
    [InlineData("2026-03-08T03:30:00", "America/New_York", "2026-03-08T07:30:00.0000000+00:00")]
    [InlineData("2026-09-01T10:00:00", "Asia/Kathmandu", "2026-09-01T04:15:00.0000000+00:00")]
    public async Task TimedEventsNormalizeDstAndNonHourOffsets(string time, string zone, string expected)
    {
        var fixture = new OutlookFixture((uri, _) => uri.AbsolutePath switch
        {
            "/v1.0/me/calendars" => """{"value":[{"id":"cal","name":"Calendar"}]}""",
            "/v1.0/me/memberOf" => """{"value":[]}""",
            _ => System.Text.Json.JsonSerializer.Serialize(new { value = new[] { new { id = "exception", type = "exception", isCancelled = true, start = new { dateTime = time, timeZone = zone }, end = new { dateTime = time, timeZone = zone } } } })
        });
        var calendar = Assert.Single(await fixture.Catalog.GetAsync(default));
        var summary = Assert.Single((await fixture.Views.GetAsync(new([calendar.Key], "2026-09-01T00:00:00Z", "2026-09-08T00:00:00Z"), default)).Events);
        Assert.Equal(expected, summary.Start);
        Assert.True(summary.IsCancelled);
    }
}

internal sealed class OutlookClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan duration) => now += duration;
}

internal sealed class OutlookMemoryStore : ILocalJsonStore
{
    private readonly Dictionary<string, object> values = [];
    public Task<T?> ReadAsync<T>(string name, CancellationToken ct) => Task.FromResult(values.TryGetValue(name, out var value) ? (T)value : default);
    public Task WriteAsync<T>(string name, T value, CancellationToken ct) { values[name] = value!; return Task.CompletedTask; }
}

internal sealed class OutlookFixture
{
    public OutlookTokens Tokens { get; } = new();
    public OutlookClock Clock { get; } = new();
    public OutlookHandler Handler { get; }
    public OutlookAccountState State { get; }
    public CalendarCatalogService Catalog { get; }
    public CalendarViewService Views { get; }
    public EventDetailsService Details { get; }
    public OutlookFixture(Func<Uri, int, string>? response = null)
    {
        Handler = new(response ?? ((uri, _) => uri.AbsolutePath switch
        {
            "/v1.0/me/calendars" => """{"value":[{"id":"cal1","name":"Calendar","isDefaultCalendar":true,"owner":{"address":"me@example.com"},"hexColor":"#123456"}]}""",
            "/v1.0/me/memberOf" => """{"value":[]}""",
            _ when uri.AbsolutePath.EndsWith("/calendarView") => """{"value":[{"id":"event1","subject":"Secret title","sensitivity":"private","isAllDay":true,"start":{"dateTime":"2026-09-02T00:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-09-03T00:00:00","timeZone":"UTC"}}]}""",
            _ when uri.AbsolutePath.EndsWith("/events/event1") => """{"id":"event1","subject":"Secret title","sensitivity":"private","isAllDay":true,"start":{"dateTime":"2026-09-02T00:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-09-03T00:00:00","timeZone":"UTC"},"body":{"contentType":"text","content":"Authorized description"}}""",
            _ => "{}"
        }));
        State = new(Tokens, new OutlookMemoryStore());
        var graph = new OutlookGraphClient(new HttpClient(Handler), Tokens);
        Catalog = new(graph, State, new OutlookSettingsStore(new OutlookMemoryStore()), Clock);
        Views = new(graph, Catalog, State, Clock);
        Details = new(graph, Catalog, Views, State);
    }
}
