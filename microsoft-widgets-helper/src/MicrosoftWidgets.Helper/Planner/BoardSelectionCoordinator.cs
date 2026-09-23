using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Security;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Planner;

public readonly record struct BoardSelectionTicket(string PlanId, long Revision);

public interface IBoardSelectionCoordinator
{
    // Gate order is Microsoft account -> Planner lifecycle -> board selection. Code running in
    // RunAsync must not enter either outer gate; authorization-loss cleanup runs after it returns.
    Task<BoardSelectionTicket> CaptureAsync(CancellationToken cancellationToken);
    Task<SettingsDto> ChangeAsync(Func<CancellationToken, Task<SettingsDto>> change,
        CancellationToken cancellationToken);
    Task RunAsync(BoardSelectionTicket ticket, Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken);
    Task<T> RunAsync<T>(BoardSelectionTicket ticket, Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);
}

public sealed class BoardSelectionCoordinator(IPlannerSettingsStore settingsStore) : IBoardSelectionCoordinator
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? selectedPlanId;
    private long revision;
    private bool initialized;

    public async Task<BoardSelectionTicket> CaptureAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var settings = await settingsStore.LoadSettingsAsync(cancellationToken);
            Synchronize(settings.SelectedPlanId);
            if (string.IsNullOrWhiteSpace(selectedPlanId)) throw new ArgumentException("Choose a board first.");
            return new BoardSelectionTicket(selectedPlanId, revision);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SettingsDto> ChangeAsync(Func<CancellationToken, Task<SettingsDto>> change,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var updated = await change(cancellationToken);
            Synchronize(updated.SelectedPlanId);
            return updated;
        }
        finally
        {
            gate.Release();
        }
    }

    public Task RunAsync(BoardSelectionTicket ticket, Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken) => RunAsync<object?>(ticket, async ct =>
    {
        await operation(ct);
        return null;
    }, cancellationToken);

    public async Task<T> RunAsync<T>(BoardSelectionTicket ticket, Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            RequireCurrent(ticket);
            var result = await operation(cancellationToken);
            RequireCurrent(ticket);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private void Synchronize(string? planId)
    {
        if (!initialized || !string.Equals(selectedPlanId, planId, StringComparison.Ordinal))
        {
            selectedPlanId = planId;
            revision++;
            initialized = true;
        }
    }

    private void RequireCurrent(BoardSelectionTicket ticket)
    {
        if (!initialized || revision != ticket.Revision ||
            !string.Equals(selectedPlanId, ticket.PlanId, StringComparison.Ordinal))
            throw new ArgumentException("The selected board changed. Try again.");
    }
}

internal sealed class BoardSelectionBoundResult(IResult inner, IBoardSelectionCoordinator selection,
    BoardSelectionTicket ticket) : IResult, IBufferedHttpResult
{
    internal const long MaximumBufferedBytes = 4L * 1024 * 1024;

    // Serialization is the logical publication commit. Client I/O happens only after every gate is released.
    public Task<BufferedHttpResponse> PrepareAsync(HttpContext http) => selection.RunAsync(ticket,
        ct => BufferedHttpResponse.CreateAsync(inner, http, MaximumBufferedBytes, ct), http.RequestAborted);

    public async Task ExecuteAsync(HttpContext http) =>
        await (await PrepareAsync(http)).CopyToAsync(http);
}
