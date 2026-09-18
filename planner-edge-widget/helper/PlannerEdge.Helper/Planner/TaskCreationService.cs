using System.Globalization;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Planner;

public sealed class TaskCreationService(IPlannerGraphClient graphClient, IPlannerSettingsStore settingsStore, BoardMemberService members)
{
    public async Task CreateAsync(string title, string bucketId, string? date, IReadOnlyList<string> assigneeIds,
        CancellationToken cancellationToken)
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
        var settings = await settingsStore.LoadSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.SelectedPlanId)) throw new ArgumentException("Choose a board first.");
        var buckets = await graphClient.GetBucketsAsync(settings.SelectedPlanId, cancellationToken);
        if (!buckets.Any(bucket => bucket.Id == bucketId))
            throw new ArgumentException("Choose a bucket on the selected board.");
        var distinctAssignees = assigneeIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        await members.ValidateAsync(distinctAssignees, cancellationToken);
        await graphClient.CreateTaskAsync(settings.SelectedPlanId, bucketId, title, due, distinctAssignees, cancellationToken);
    }
}
