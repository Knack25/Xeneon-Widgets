using System.Globalization;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Planner;

public sealed class TaskCreationService
{
    private readonly IPlannerGraphClient graphClient;
    private readonly BoardMemberService members;
    private readonly IBoardSelectionCoordinator selection;

    public TaskCreationService(IPlannerGraphClient graphClient, IPlannerSettingsStore settingsStore,
        BoardMemberService members, IBoardSelectionCoordinator selection)
    {
        this.graphClient = graphClient;
        this.members = members;
        this.selection = selection;
    }

    public TaskCreationService(IPlannerGraphClient graphClient, IPlannerSettingsStore settingsStore,
        BoardMemberService members) : this(graphClient, settingsStore, members,
        new BoardSelectionCoordinator(settingsStore)) { }

    public async Task CreateAsync(string title, string bucketId, string? date, IReadOnlyList<string> assigneeIds,
        CancellationToken cancellationToken, string? startDate = null, int? priority = null,
        IReadOnlyList<string>? labelIds = null)
    {
        title = title.Trim();
        if (title.Length is < 1 or > 255) throw new ArgumentException("Enter a task title (up to 255 characters).");
        if (assigneeIds.Count > 50 || assigneeIds.Any(id => !Guid.TryParse(id, out _)))
            throw new ArgumentException("Choose valid board members.");
        DateTimeOffset? due = null;
        if (date is not null)
        {
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                throw new ArgumentException("Choose a valid due date.");
            due = new DateTimeOffset(day.Year, day.Month, day.Day, 12, 0, 0, TimeSpan.Zero);
        }
        DateTimeOffset? start = null;
        if (startDate is not null)
        {
            if (!DateOnly.TryParseExact(startDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                throw new ArgumentException("Choose a valid start date.");
            start = new DateTimeOffset(day.Year, day.Month, day.Day, 12, 0, 0, TimeSpan.Zero);
        }
        if (start is not null && due is not null && start > due)
            throw new ArgumentException("Start date cannot be after the due date.");
        var resolvedPriority = priority ?? 5;
        if (resolvedPriority is not (1 or 3 or 5 or 9))
            throw new ArgumentException("Choose a valid priority.");
        var ticket = await selection.CaptureAsync(cancellationToken);
        var buckets = await selection.RunAsync(ticket,
            ct => graphClient.GetBucketsAsync(ticket.PlanId, ct), cancellationToken);
        if (!buckets.Any(bucket => bucket.Id == bucketId))
            throw new ArgumentException("Choose a bucket on the selected board.");
        var distinctAssignees = assigneeIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        await selection.RunAsync(ticket,
            ct => members.ValidateWithinSelectionAsync(distinctAssignees, ticket.PlanId, ct), cancellationToken);
        var distinctLabels = (labelIds ?? []).Distinct(StringComparer.Ordinal).ToArray();
        if (distinctLabels.Length > 0)
        {
            var validLabels = (await selection.RunAsync(ticket,
                    ct => graphClient.GetPlanLabelsAsync(ticket.PlanId, ct), cancellationToken))
                .Select(label => label.Id).ToHashSet(StringComparer.Ordinal);
            if (distinctLabels.Any(labelId => !validLabels.Contains(labelId)))
                throw new ArgumentException("Choose labels from the selected board.");
        }
        await selection.RunAsync(ticket,
            ct => graphClient.CreateTaskAsync(ticket.PlanId, bucketId, title, due, distinctAssignees,
                start, resolvedPriority, distinctLabels, ct), cancellationToken);
    }
}
