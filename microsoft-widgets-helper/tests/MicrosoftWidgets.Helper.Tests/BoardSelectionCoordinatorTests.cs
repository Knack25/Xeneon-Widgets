using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Planner;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Tests;

public sealed class BoardSelectionCoordinatorTests
{
    [Fact]
    public async Task OldTicketCannotRunGraphWorkAfterBoardSwitch()
    {
        var settings = new MutableSettingsStore("first-plan");
        var coordinator = new BoardSelectionCoordinator(settings);
        var ticket = await coordinator.CaptureAsync(default);
        await coordinator.ChangeAsync(_ => settings.SelectAsync("second-plan"), default);
        var called = false;

        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.RunAsync(ticket, _ =>
        {
            called = true;
            return Task.CompletedTask;
        }, default));

        Assert.False(called);
    }

    [Fact]
    public async Task BoardSwitchWaitsForGraphWorkAlreadyInsideBoundary()
    {
        var settings = new MutableSettingsStore("first-plan");
        var coordinator = new BoardSelectionCoordinator(settings);
        var ticket = await coordinator.CaptureAsync(default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var graph = coordinator.RunAsync(ticket, async _ =>
        {
            entered.SetResult();
            await release.Task;
        }, default);
        await entered.Task;

        var change = coordinator.ChangeAsync(_ => settings.SelectAsync("second-plan"), default);
        await Task.Yield();
        Assert.False(change.IsCompleted);

        release.SetResult();
        await graph;
        await change;
    }

    private sealed class MutableSettingsStore(string selectedPlanId) : IPlannerSettingsStore
    {
        private SettingsDto current = new(selectedPlanId, selectedPlanId, true);

        public Task<SettingsDto> SelectAsync(string planId)
        {
            current = current with { SelectedPlanId = planId, SelectedPlanTitle = planId };
            return Task.FromResult(current);
        }

        public Task<SettingsDto> LoadSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(current);
        public Task SaveSettingsAsync(SettingsDto settings, CancellationToken cancellationToken)
        {
            current = settings;
            return Task.CompletedTask;
        }
        public Task<BoardDisplay?> LoadCachedDisplayAsync(CancellationToken cancellationToken) =>
            Task.FromResult<BoardDisplay?>(null);
        public Task SaveCachedDisplayAsync(BoardDisplay display, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
