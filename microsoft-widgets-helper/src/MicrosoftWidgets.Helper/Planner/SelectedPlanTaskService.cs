using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Planner;

public sealed class SelectedPlanTaskService
{
    private readonly IPlannerGraphClient graphClient;
    private readonly IBoardSelectionCoordinator selection;

    public SelectedPlanTaskService(IPlannerGraphClient graphClient, IPlannerSettingsStore settingsStore,
        IBoardSelectionCoordinator selection)
    {
        this.graphClient = graphClient;
        this.selection = selection;
    }

    public SelectedPlanTaskService(IPlannerGraphClient graphClient, IPlannerSettingsStore settingsStore)
        : this(graphClient, settingsStore, new BoardSelectionCoordinator(settingsStore)) { }

    public async Task<GraphTask> GetAsync(string taskId, CancellationToken cancellationToken)
        => (await GetBoundAsync(taskId, cancellationToken)).Task;

    public async Task<SelectedPlanTask> GetBoundAsync(string taskId, CancellationToken cancellationToken)
    {
        var ticket = await selection.CaptureAsync(cancellationToken);
        var task = await selection.RunAsync(ticket, ct => graphClient.GetTaskAsync(taskId, ct), cancellationToken)
            ?? throw new InvalidOperationException("Planner task was not found.");
        if (!string.Equals(task.PlanId, ticket.PlanId, StringComparison.Ordinal))
            throw new ArgumentException("This task is not on the selected board.");
        return new SelectedPlanTask(task, ticket);
    }

    public Task RunAsync(SelectedPlanTask task, Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken) => selection.RunAsync(task.Selection, operation, cancellationToken);

    public Task<T> RunAsync<T>(SelectedPlanTask task, Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) => selection.RunAsync(task.Selection, operation, cancellationToken);

    public async Task<SelectedPlanTask> RefreshAsync(SelectedPlanTask selected, CancellationToken cancellationToken)
    {
        var task = await RunAsync(selected,
            ct => graphClient.GetTaskAsync(selected.Task.Id, ct), cancellationToken)
            ?? throw new InvalidOperationException("Planner task was not found.");
        if (!string.Equals(task.PlanId, selected.Selection.PlanId, StringComparison.Ordinal))
            throw new ArgumentException("This task is not on the selected board.");
        return selected with { Task = task };
    }
}

public sealed record SelectedPlanTask(GraphTask Task, BoardSelectionTicket Selection);
