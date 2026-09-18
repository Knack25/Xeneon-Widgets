using System.Globalization;

namespace PlannerEdge.Helper.Outlook;

public sealed class CalendarViewService
{
    private sealed record Snapshot(EventSummary[] Events, Dictionary<string, string> References, DateTimeOffset FetchedAt, long Use);
    private sealed class Flight
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public Task<Snapshot> Task { get; set; } = null!;
        public int Waiters { get; set; }
    }
    private readonly record struct CacheKey(string Account, string Calendar, DateTimeOffset Start, DateTimeOffset End);
    private readonly OutlookGraphClient graph;
    private readonly CalendarCatalogService catalog;
    private readonly OutlookAccountState state;
    private readonly TimeProvider clock;
    private readonly object sync = new();
    private readonly Dictionary<CacheKey, Snapshot> cache = [];
    private readonly Dictionary<CacheKey, Flight> flights = [];
    private readonly Dictionary<string, long> sourceVersions = [];
    private long use;

    public CalendarViewService(OutlookGraphClient graph, CalendarCatalogService catalog, OutlookAccountState state, TimeProvider clock)
    {
        this.graph = graph; this.catalog = catalog; this.state = state; this.clock = clock;
        state.Invalidated += () => { lock (sync) { cache.Clear(); foreach (var flight in flights.Values) flight.Cancellation.Cancel(); flights.Clear(); sourceVersions.Clear(); } };
        state.SourceInvalidated += key =>
        {
            lock (sync)
            {
                sourceVersions[key] = sourceVersions.GetValueOrDefault(key) + 1;
                foreach (var entry in cache.Keys.Where(k => k.Calendar == key).ToArray()) cache.Remove(entry);
                foreach (var entry in flights.Where(p => p.Key.Calendar == key).ToArray()) { entry.Value.Cancellation.Cancel(); flights.Remove(entry.Key); }
            }
        };
    }

    public async Task<CalendarViewResponse> GetAsync(ViewRequest request, CancellationToken ct)
    {
        if (request.CalendarKeys is null || request.CalendarKeys.Length > 100 || request.CalendarKeys.Any(k => string.IsNullOrWhiteSpace(k) || k.Length > 128) ||
            !OutlookJson.HasOffset(request.Start) || !OutlookJson.HasOffset(request.End) ||
            !DateTimeOffset.TryParse(request.Start, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !DateTimeOffset.TryParse(request.End, CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) || end <= start || end - start > TimeSpan.FromDays(62))
            throw new OutlookException("invalid_range", "Select a range with explicit offsets of at most 62 days and known calendars.", 400);
        var lease = await state.GetAsync(ct);
        var results = await Task.WhenAll(request.CalendarKeys.Distinct().Select(key => ReadSourceAsync(lease, new(lease.Key, key, start, end), ct)));
        if (!state.IsCurrent(lease)) return new([], request.CalendarKeys.Select(key => new SourceStatus(key, null, false, new("sign_in_required", "Reconnect Outlook before loading calendar data."))).ToArray());
        return new(results.SelectMany(r => r.Events).ToArray(), results.Select(r => r.Status).ToArray());
    }

    private async Task<(EventSummary[] Events, SourceStatus Status)> ReadSourceAsync(OutlookAccountLease lease, CacheKey key, CancellationToken ct)
    {
        try
        {
            var source = await catalog.ResolveAsync(key.Calendar, ct);
            state.RequireCurrent(lease);
            Flight flight;
            lock (sync)
            {
                Expire();
                if (!flights.TryGetValue(key, out flight!))
                {
                    flight = new();
                    flights[key] = flight;
                    flight.Task = FetchAsync(lease, key, source, sourceVersions.GetValueOrDefault(key.Calendar), flight.Cancellation.Token);
                }
                flight.Waiters++;
            }
            Snapshot snapshot;
            try { snapshot = await flight.Task.WaitAsync(ct); }
            finally
            {
                lock (sync)
                {
                    if (--flight.Waiters == 0)
                    {
                        if (!flight.Task.IsCompleted) flight.Cancellation.Cancel();
                        if (flights.GetValueOrDefault(key) == flight) flights.Remove(key);
                    }
                }
            }
            state.RequireCurrent(lease);
            return (snapshot.Events, new(key.Calendar, snapshot.FetchedAt, false, null));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ([], new(key.Calendar, null, false, new("source_unavailable", "The account or calendar changed.")));
        }
        catch (OutlookException ex)
        {
            if (ex.StatusCode == 401 && state.IsCurrent(lease)) await state.PurgeDataAsync(CancellationToken.None);
            else if (ex.StatusCode is 403 or 404) state.PurgeSource(key.Calendar);
            lock (sync)
            {
                Expire();
                if (state.IsCurrent(lease) && ex.Code is "offline" or "throttled" && cache.TryGetValue(key, out var stale))
                {
                    cache[key] = stale with { Use = ++use };
                    return (stale.Events, new(key.Calendar, stale.FetchedAt, true, ex.Error));
                }
            }
            return ([], new(key.Calendar, null, false, ex.Error));
        }
    }

    private async Task<Snapshot> FetchAsync(OutlookAccountLease lease, CacheKey key, CalendarSource source, long version, CancellationToken ct)
    {
        var route = source.ViewRoute + "?startDateTime=" + Uri.EscapeDataString(key.Start.ToString("O")) + "&endDateTime=" + Uri.EscapeDataString(key.End.ToString("O")) +
            "&$select=id,subject,start,end,isAllDay,sensitivity,isCancelled&$top=250";
        var values = await graph.GetCollectionAsync(route, ct);
        var references = new Dictionary<string, string>();
        var events = values.Select(item =>
        {
            var id = item.Text("id");
            if (id.Length == 0) throw new OutlookException("invalid_response", "Outlook returned an invalid event identifier.");
            var reference = OutlookTokenProvider.Hash(lease.Key + "\n" + key.Calendar + "\n" + id);
            references[reference] = id;
            var isPrivate = item.Text("sensitivity") is "private" or "confidential";
            var allDay = item.Flag("isAllDay");
            return new EventSummary(reference, key.Calendar, isPrivate ? "Private event" : item.Text("subject", "Untitled event"),
                OutlookJson.EventTime(item.Child("start"), allDay), OutlookJson.EventTime(item.Child("end"), allDay), allDay, isPrivate, item.Flag("isCancelled"));
        }).DistinctBy(e => e.Reference).ToArray();
        state.RequireCurrent(lease);
        lock (sync)
        {
            state.RequireCurrent(lease);
            ct.ThrowIfCancellationRequested();
            if (version != sourceVersions.GetValueOrDefault(key.Calendar)) throw new OutlookException("source_not_found", "The calendar was removed.", 404);
            var snapshot = new Snapshot(events, references, clock.GetUtcNow(), ++use);
            cache[key] = snapshot;
            Expire();
            return snapshot;
        }
    }

    internal string ResolveReference(OutlookAccountLease lease, EventRequest request)
    {
        lock (sync)
        {
            state.RequireCurrent(lease);
            Expire();
            foreach (var entry in cache.Where(p => p.Key.Account == lease.Key && p.Key.Calendar == request.CalendarKey))
                if (entry.Value.References.TryGetValue(request.Reference ?? "", out var id)) return id;
            throw new OutlookException("event_not_found", "Refresh the calendar before opening this event.", 404);
        }
    }

    private void Expire()
    {
        foreach (var key in cache.Where(p => clock.GetUtcNow() - p.Value.FetchedAt >= TimeSpan.FromHours(24)).Select(p => p.Key).ToArray()) cache.Remove(key);
        // One range can contain up to 100 calendars. Evict complete ranges so a single
        // displayed multi-calendar view never loses its own detail references halfway through.
        var ranges = cache.GroupBy(p => (p.Key.Account, p.Key.Start, p.Key.End)).ToArray();
        foreach (var range in ranges.OrderBy(g => g.Max(p => p.Value.Use)).Take(Math.Max(0, ranges.Length - 32)))
            foreach (var entry in range) cache.Remove(entry.Key);
    }
}
