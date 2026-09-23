using Microsoft.Extensions.Caching.Memory;
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Tests;

public sealed class SelectedPlanOperationRaceTests
{
    private const string MemberId = "11111111-1111-1111-1111-111111111111";

    public static TheoryData<string> OperationCategories => new()
    {
        "details", "completion", "assignment", "due-date", "move", "notes", "checklist",
        "checklist-completion", "chat", "metadata", "creation"
    };

    [Theory]
    [MemberData(nameof(OperationCategories))]
    public async Task BoardSwitchAfterValidationPreventsNextGraphBoundary(string category)
    {
        var fixture = new Fixture(category == "creation" ? 1 : 2);
        var operation = fixture.StartAsync(category);
        await fixture.Selection.BoundaryReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.Selection.SwitchBoard();
        fixture.Selection.ReleaseBoundary();

        await Assert.ThrowsAsync<ArgumentException>(() => operation);
        Assert.Equal(category == "creation" ? 0 : 1, fixture.Graph.Calls);
    }

    private sealed class Fixture
    {
        private readonly MutableSettingsStore settings = new();
        private readonly MemoryCache cache = new(new MemoryCacheOptions());
        public Fixture(int pauseAtBoundary)
        {
            Selection = new PausingSelectionCoordinator(pauseAtBoundary);
            Selected = new SelectedPlanTaskService(Graph, settings, Selection);
        }

        public RaceGraph Graph { get; } = new();
        public PausingSelectionCoordinator Selection { get; }
        public SelectedPlanTaskService Selected { get; }

        public Task StartAsync(string category)
        {
            var lifecycle = new PlannerDataLifecycle(cache);
            var details = new TaskDetailsService(Graph, Selected, lifecycle);
            return category switch
            {
                "details" => details.GetAsync("task", default),
                "completion" => new TaskCompletionService(Graph, Selected).CompleteAsync("task", default),
                "assignment" => new TaskAssignmentService(Graph, Selected,
                    new BoardMemberService(Graph, settings), cache).SetAsync("task", [MemberId], default),
                "due-date" => new DueDateService(Graph, Selected, cache).SetAsync("task", "2026-09-30", default),
                "move" => new TaskMoveService(Graph, Selected, cache).MoveAsync("task", "other-bucket", default),
                "notes" => new TaskNotesService(Graph, Selected, details).UpdateAsync("task", "notes", default),
                "checklist" => new ChecklistService(Graph, Selected, cache).AddAsync("task", "item", default),
                "checklist-completion" => new ChecklistCompletionService(Graph, Selected, cache)
                    .CompleteAsync("task", "item", default),
                "chat" => new TaskChatService(Graph, Selected, details, lifecycle).PostAsync("task", "message", default),
                "metadata" => new TaskMetadataService(Graph, Selected, cache).SetTitleAsync("task", "title", default),
                "creation" => new TaskCreationService(Graph, settings, new BoardMemberService(Graph, settings), Selection)
                    .CreateAsync("title", "bucket", null, [], default),
                _ => throw new ArgumentOutOfRangeException(nameof(category))
            };
        }
    }

    private sealed class PausingSelectionCoordinator(int pauseAtBoundary) : IBoardSelectionCoordinator
    {
        private long revision = 1;
        private int boundaryCount;
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BoundaryReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<BoardSelectionTicket> CaptureAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new BoardSelectionTicket("plan", revision));

        public Task<SettingsDto> ChangeAsync(Func<CancellationToken, Task<SettingsDto>> change,
            CancellationToken cancellationToken) => change(cancellationToken);

        public async Task RunAsync(BoardSelectionTicket ticket, Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken)
        {
            await RunAsync<object?>(ticket, async ct => { await operation(ct); return null; }, cancellationToken);
        }

        public async Task<T> RunAsync<T>(BoardSelectionTicket ticket, Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref boundaryCount) == pauseAtBoundary)
            {
                BoundaryReached.SetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            if (ticket.Revision != revision) throw new ArgumentException("The selected board changed. Try again.");
            return await operation(cancellationToken);
        }

        public void SwitchBoard() => Interlocked.Increment(ref revision);
        public void ReleaseBoundary() => release.SetResult();
    }

    private sealed class MutableSettingsStore : IPlannerSettingsStore
    {
        public Task<SettingsDto> LoadSettingsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SettingsDto("plan", "Plan", true));
        public Task SaveSettingsAsync(SettingsDto value, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<BoardDisplay?> LoadCachedDisplayAsync(CancellationToken cancellationToken) =>
            Task.FromResult<BoardDisplay?>(null);
        public Task SaveCachedDisplayAsync(BoardDisplay display, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RaceGraph : IPlannerGraphClient
    {
        public int Calls { get; private set; }
        private T Called<T>(T value) { Calls++; return value; }
        private Task Called() { Calls++; return Task.CompletedTask; }

        public Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken) =>
            Task.FromResult<GraphTask?>(Called(new GraphTask(taskId, "Task", "plan", "bucket", null, 5, 0,
                "etag", [], "thread")));
        public Task<GraphTaskDetails> GetTaskDetailsAsync(string taskId, CancellationToken cancellationToken) =>
            Task.FromResult(Called(new GraphTaskDetails("details-etag", [new("item", "Item", false, "a")], "notes")));
        public Task<IReadOnlyList<GraphPlan>> GetMyPlansAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GraphPlan>>(Called<IReadOnlyList<GraphPlan>>([new("plan", "Plan", "group", null)]));
        public Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GraphBucket>>(Called<IReadOnlyList<GraphBucket>>([new("bucket", "Bucket", planId), new("other-bucket", "Other", planId)]));
        public Task<IReadOnlyList<GraphMember>> GetGroupMembersAsync(string groupId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GraphMember>>(Called<IReadOnlyList<GraphMember>>([new(MemberId, "Member")]));
        public Task CompleteTaskAsync(string taskId, string etag, CancellationToken cancellationToken) => Called();
        public Task SetAssignmentsAsync(string taskId, IReadOnlyList<string> add, IReadOnlyList<string> remove,
            string etag, CancellationToken cancellationToken) => Called();
        public Task SetDueDateAsync(string taskId, DateTimeOffset? dueDate, string etag,
            CancellationToken cancellationToken) => Called();
        public Task MoveTaskAsync(string taskId, string bucketId, string etag,
            CancellationToken cancellationToken) => Called();
        public Task UpdateTaskDescriptionAsync(string taskId, string description, string etag,
            CancellationToken cancellationToken) => Called();
        public Task PatchChecklistAsync(string taskId, string itemId, GraphChecklistPatch? patch, string etag,
            CancellationToken cancellationToken) => Called();
        public Task CompleteChecklistItemAsync(string taskId, string itemId, string etag,
            CancellationToken cancellationToken) => Called();
        public Task ReplyToConversationAsync(string groupId, string threadId, string message,
            CancellationToken cancellationToken) => Called();
        Task IPlannerGraphClient.UpdateTaskAsync(string taskId, GraphTaskUpdate update, string etag,
            CancellationToken cancellationToken) => Called();
        public Task CreateTaskAsync(string planId, string bucketId, string title, DateTimeOffset? dueDate,
            IReadOnlyList<string> assigneeIds, CancellationToken cancellationToken) => Called();
        public Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> GetUserDisplayNameAsync(string userId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
