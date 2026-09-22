using PlannerEdge.Helper.Auth;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PlannerEdge.Helper.Outlook;

public sealed class CalendarCatalogService
{
    private readonly OutlookGraphClient graph;
    private readonly MicrosoftAccountState state;
    private readonly OutlookSettingsStore settings;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private Dictionary<string, CalendarSource> sources = [];
    private DateTimeOffset fetchedAt;
    private IReadOnlyList<OutlookError> discoveryErrors = [];
    public IReadOnlyList<OutlookError> DiscoveryErrors { get { lock (sync) return discoveryErrors; } }

    public CalendarCatalogService(OutlookGraphClient graph, MicrosoftAccountState state, OutlookSettingsStore settings, TimeProvider clock)
    {
        this.graph = graph; this.state = state; this.settings = settings; this.clock = clock;
        state.Invalidated += () => { lock (sync) { sources = []; fetchedAt = default; discoveryErrors = []; } };
    }

    public async Task<IReadOnlyList<CalendarDescriptor>> GetAsync(CancellationToken ct)
    {
        var lease = await state.GetAsync(ct);
        await gate.WaitAsync(ct);
        try
        {
            lock (sync)
            {
                state.RequireCurrent(lease);
                if (clock.GetUtcNow() - fetchedAt < TimeSpan.FromMinutes(15)) return sources.Values.Select(s => s.Descriptor).ToArray();
            }
            var found = new Dictionary<string, CalendarSource>();
            var errors = new List<OutlookError>();
            CalendarSource[] previous;
            lock (sync) previous = sources.Values.ToArray();
            void Preserve(Func<CalendarSource, bool> predicate)
            {
                foreach (var source in previous.Where(predicate)) found[source.Descriptor.Key] = source;
            }
            try
            {
                var recipient = await graph.GetCollectionAsync("me/calendars?$select=id,name,owner,color,hexColor,canViewPrivateItems,isDefaultCalendar", ct);
                var ownAddress = recipient.FirstOrDefault(c => c.Flag("isDefaultCalendar")).Child("owner").Text("address");
                foreach (var item in recipient.OrderByDescending(c => c.Flag("isDefaultCalendar")))
                {
                    var id = RequireId(item);
                    var isOwn = item.Flag("isDefaultCalendar") || ownAddress.Length > 0 && string.Equals(ownAddress, item.Child("owner").Text("address"), StringComparison.OrdinalIgnoreCase);
                    Add(found, lease, item, "me/calendars/" + Uri.EscapeDataString(id), isOwn ? "personal" : "shared", null);
                }
            }
            catch (OutlookException ex) when (ex.StatusCode != 401)
            {
                errors.Add(ex.Error);
                if (ex.StatusCode is not (403 or 404)) Preserve(s => s.Route.StartsWith("me/", StringComparison.Ordinal));
            }
            foreach (var owner in await settings.GetOwnersAsync(lease.Key, ct))
            {
                try
                {
                    var route = "users/" + Uri.EscapeDataString(owner) + "/calendar";
                    var item = await graph.GetAsync(route, ct);
                    Add(found, lease, item, route, "shared", owner);
                }
                catch (OutlookException ex) when (ex.StatusCode != 401)
                {
                    errors.Add(ex.Error);
                    if (ex.StatusCode is not (403 or 404)) Preserve(s => s.OwnerEmail == owner);
                }
            }
            try
            {
                var groups = await graph.GetCollectionAsync("me/memberOf?$select=id,displayName,groupTypes", ct);
                foreach (var group in groups.Where(g => g.Child("groupTypes").Items().Any(t => t.GetString() == "Unified")))
                {
                    var route = "groups/" + Uri.EscapeDataString(RequireId(group)) + "/calendar";
                    try
                    {
                        Add(found, lease, await graph.GetAsync(route, ct), route, "group", null, group.Text("displayName"));
                    }
                    catch (OutlookException ex) when (ex.StatusCode != 401)
                    {
                        errors.Add(ex.Error);
                        if (ex.StatusCode is not (403 or 404)) Preserve(s => s.Route == route);
                    }
                }
            }
            catch (OutlookException ex) when (ex.StatusCode != 401)
            {
                errors.Add(ex.Error);
                if (ex.StatusCode is not (403 or 404)) Preserve(s => s.Descriptor.Kind == "group");
            }
            state.RequireCurrent(lease);
            lock (sync)
            {
                state.RequireCurrent(lease);
                foreach (var key in sources.Keys.Except(found.Keys).ToArray()) state.PurgeSource(key);
                sources = found;
                fetchedAt = clock.GetUtcNow();
                discoveryErrors = errors;
                return sources.Values.Select(s => s.Descriptor).ToArray();
            }
        }
        catch (OutlookException ex)
        {
            if (ex.StatusCode == 401 && state.IsCurrent(lease)) await state.PurgeDataAsync(CancellationToken.None);
            else if (ex.StatusCode is 403 or 404) { lock (sync) { foreach (var key in sources.Keys) state.PurgeSource(key); sources = []; fetchedAt = default; } }
            throw;
        }
        finally { gate.Release(); }
    }

    internal async Task<CalendarSource> ResolveAsync(string key, CancellationToken ct)
    {
        await GetAsync(ct);
        lock (sync) return sources.TryGetValue(key ?? "", out var source) ? source : throw new OutlookException("source_not_found", "Select an available calendar.", 404);
    }

    public async Task<CalendarDescriptor> AddAsync(string ownerEmail, CancellationToken ct)
    {
        var owner = OutlookSettingsStore.NormalizeOwner(ownerEmail);
        var lease = await state.GetAsync(ct);
        await gate.WaitAsync(ct);
        try
        {
            var route = "users/" + Uri.EscapeDataString(owner) + "/calendar";
            var item = await graph.GetAsync(route, ct);
            state.RequireCurrent(lease);
            await settings.SetOwnerAsync(lease.Key, owner, true, ct);
            lock (sync)
            {
                state.RequireCurrent(lease);
                var source = Add(sources, lease, item, route, "shared", owner);
                fetchedAt = default;
                return source.Descriptor;
            }
        }
        finally { gate.Release(); }
    }

    public async Task RemoveAsync(string key, CancellationToken ct)
    {
        var lease = await state.GetAsync(ct);
        await gate.WaitAsync(ct);
        try
        {
            state.RequireCurrent(lease);
            var owner = (await settings.GetOwnersAsync(lease.Key, ct)).FirstOrDefault(value =>
                OutlookTokenProvider.Hash(lease.Key + "\nusers/" + Uri.EscapeDataString(value) + "/calendar") == key);
            if (owner is null) throw new OutlookException("source_not_found", "This local calendar reference does not exist.", 404);
            await settings.SetOwnerAsync(lease.Key, owner, false, ct);
            lock (sync) { state.RequireCurrent(lease); sources.Remove(key); fetchedAt = default; }
            state.PurgeSource(key);
        }
        finally { gate.Release(); }
    }

    private static CalendarSource Add(Dictionary<string, CalendarSource> target, AccountLease lease, JsonElement item, string route, string kind, string? owner, string? groupName = null)
    {
        RequireId(item);
        var key = OutlookTokenProvider.Hash(lease.Key + "\n" + route);
        var source = new CalendarSource(new(key, groupName ?? item.Text("name", "Calendar"), owner ?? OutlookJson.Person(item.Child("owner")), kind, Color(item, key), item.Flag("canViewPrivateItems"), owner is not null), route, owner);
        target[key] = source;
        return source;
    }

    private static string RequireId(JsonElement item) => item.Text("id") is { Length: > 0 } id ? id : throw new OutlookException("invalid_response", "Outlook returned an invalid calendar identifier.");
    private static string Color(JsonElement item, string key)
    {
        var hex = item.Text("hexColor");
        if (Regex.IsMatch(hex, "^#[0-9a-fA-F]{6}$")) return hex;
        var named = item.Text("color") switch
        {
            "lightBlue" => "#0078D4", "lightGreen" => "#498205", "lightOrange" => "#CA5010", "lightGray" => "#69797E",
            "lightYellow" => "#C19C00", "lightTeal" => "#038387", "lightPink" => "#C239B3", "lightBrown" => "#8E562E", "lightRed" => "#D13438", _ => null
        };
        string[] palette = ["#0078D4", "#498205", "#C239B3", "#038387", "#D13438", "#8764B8"];
        return named ?? palette[Convert.ToByte(key[..2], 16) % palette.Length];
    }
}
