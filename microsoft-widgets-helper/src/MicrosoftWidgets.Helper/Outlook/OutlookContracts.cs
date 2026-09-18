namespace PlannerEdge.Helper.Outlook;

public sealed record OutlookError(string Code, string Message);
public sealed class OutlookException(string code, string message, int statusCode = 503) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public OutlookError Error => new(Code, Message);
}
public sealed record CalendarDescriptor(string Key, string Name, string Owner, string Kind, string Color, bool CanViewPrivateItems, bool IsLocalReference = false);
public sealed record WorkingHours(string[] DaysOfWeek, string StartTime, string EndTime, string TimeZone);
public sealed record OutlookPreferences(WorkingHours? WorkingHours, string PcTimeZone, string[] TimeZones);
public sealed record ViewRequest(string[] CalendarKeys, string Start, string End);
public sealed record EventRequest(string CalendarKey, string Reference);
public sealed record SourceRequest(string OwnerEmail);
public sealed record EventSummary(string Reference, string CalendarKey, string Title, string Start, string End, bool IsAllDay, bool IsPrivate, bool IsCancelled);
public sealed record SourceStatus(string CalendarKey, DateTimeOffset? FetchedAt, bool Stale, OutlookError? Error);
public sealed record CalendarViewResponse(IReadOnlyList<EventSummary> Events, IReadOnlyList<SourceStatus> Sources);
public sealed record EventDetails(string Reference, string CalendarKey, string Title, string Start, string End, bool IsAllDay, bool IsPrivate,
    string Location, string Organizer, string[] Attendees, string Description, string? JoinUrl);
public sealed record PairingRequest(string InstanceId, string RequestSecret);
public sealed record PairingPollRequest(string RequestSecret);
public sealed record PairingCreated(string Id, string Code, DateTimeOffset ExpiresAt);
public sealed record PendingPairing(string Id, string Code, string InstanceId, DateTimeOffset ExpiresAt);
public sealed record PairingResult(string Status, string? Credential = null);
public sealed record PairedInstance(string CredentialId, string InstanceId);
public sealed record RevokePairingRequest(string CredentialId);
public sealed record OutlookStatus(bool Configured, bool SignedIn, bool Ready, OutlookError? Error = null);

internal sealed record CalendarSource(CalendarDescriptor Descriptor, string Route, string? OwnerEmail = null)
{
    public string ViewRoute => Descriptor.Kind == "group" ? Route[..^"/calendar".Length] + "/calendarView" : Route + "/calendarView";
    public string EventRoute(string id) => Route + "/events/" + Uri.EscapeDataString(id);
}
