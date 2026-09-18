namespace PlannerEdge.Helper.Outlook;

public static class OutlookScopes
{
    public static readonly IReadOnlyList<string> All = Array.AsReadOnly(new[]
    {
        "User.Read", "Calendars.Read.Shared", "MailboxSettings.Read", "Group.Read.All"
    });
}
