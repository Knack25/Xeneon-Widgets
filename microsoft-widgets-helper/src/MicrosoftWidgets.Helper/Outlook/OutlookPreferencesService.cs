using PlannerEdge.Helper.Auth;
namespace PlannerEdge.Helper.Outlook;

public sealed class OutlookPreferencesService
{
    private readonly OutlookGraphClient graph;
    private readonly MicrosoftAccountState state;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private WorkingHours? hours;
    private DateTimeOffset fetchedAt;
    public OutlookPreferencesService(OutlookGraphClient graph, MicrosoftAccountState state, TimeProvider clock)
    {
        this.graph = graph; this.state = state; this.clock = clock;
        state.Invalidated += () => { lock (sync) { hours = null; fetchedAt = default; } };
    }

    public async Task<OutlookPreferences> GetAsync(CancellationToken ct)
    {
        var lease = await state.GetAsync(ct);
        await gate.WaitAsync(ct);
        try
        {
            if (clock.GetUtcNow() - fetchedAt >= TimeSpan.FromMinutes(15))
            {
                var json = await graph.GetAsync("me/mailboxSettings?$select=workingHours,timeZone", ct);
                var working = json.Child("workingHours");
                var next = working.ValueKind == System.Text.Json.JsonValueKind.Object
                    ? new WorkingHours(working.Child("daysOfWeek").Items().Select(d => d.GetString() ?? "").ToArray(), working.Text("startTime"), working.Text("endTime"), Iana(working.Child("timeZone").Text("name", json.Text("timeZone", "UTC")))) : null;
                lock (sync) { state.RequireCurrent(lease); hours = next; fetchedAt = clock.GetUtcNow(); }
            }
            state.RequireCurrent(lease);
            TimeZoneInfo.ClearCachedData();
            lock (sync)
            {
                state.RequireCurrent(lease);
                return new(hours, Iana(TimeZoneInfo.Local.Id), TimeZoneInfo.GetSystemTimeZones().Select(z => Iana(z.Id)).Append("UTC").Distinct().Order().ToArray());
            }
        }
        catch (OutlookException ex)
        {
            lock (sync) { hours = null; fetchedAt = default; }
            if (ex.StatusCode == 401 && state.IsCurrent(lease)) await state.PurgeDataAsync(CancellationToken.None);
            throw;
        }
        finally { gate.Release(); }
    }

    private static string Iana(string zone) => TimeZoneInfo.TryConvertWindowsIdToIanaId(zone, out var id) ? id : zone;
}
