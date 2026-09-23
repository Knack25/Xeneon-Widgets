using PlannerEdge.Helper.Auth;
namespace PlannerEdge.Helper.Outlook;

public sealed class EventDetailsService(OutlookGraphClient graph, CalendarCatalogService catalog, CalendarViewService views, MicrosoftAccountState state)
{
    public async Task<EventDetails> GetAsync(EventRequest request, CancellationToken ct)
    {
        var lease = await state.GetAsync(ct);
        var source = await catalog.ResolveAsync(request.CalendarKey, ct);
        var id = views.ResolveReference(lease, request);
        try
        {
            var item = await graph.GetAsync(source.EventRoute(id) + "?$select=id,subject,start,end,isAllDay,sensitivity,location,organizer,attendees,body,onlineMeeting,onlineMeetingUrl", ct);
            state.RequireCurrent(lease);
            // Require the reference again: source removal during the network read must not return details.
            views.ResolveReference(lease, request);
            var allDay = item.Flag("isAllDay");
            var url = item.Child("onlineMeeting").Text("joinUrl");
            if (!OutlookJoinService.IsSafeUrl(url)) url = item.Text("onlineMeetingUrl");
            var body = item.Child("body");
            return new(request.Reference, request.CalendarKey, item.Text("subject", "Untitled event"),
                OutlookJson.EventTime(item.Child("start"), allDay), OutlookJson.EventTime(item.Child("end"), allDay), allDay,
                item.Text("sensitivity") is "private" or "confidential", item.Child("location").Text("displayName"),
                OutlookJson.Person(item.Child("organizer").Child("emailAddress")),
                item.Child("attendees").Items().Select(a => OutlookJson.Person(a.Child("emailAddress"))).ToArray(),
                body.Text("contentType").Equals("text", StringComparison.OrdinalIgnoreCase) ? body.Text("content") : "",
                OutlookJoinService.IsSafeUrl(url) ? url : null);
        }
        catch (OutlookException ex)
        {
            if (ex.StatusCode == 401 && state.IsCurrent(lease)) await state.PurgeDataAsync(CancellationToken.None);
            else if (ex.StatusCode is 403 or 404) state.PurgeSource(request.CalendarKey);
            throw;
        }
    }
}
