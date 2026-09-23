using PlannerEdge.Helper.Outlook;
using PlannerEdge.Helper.Security;

namespace PlannerEdge.Helper.Planner;

public readonly record struct PlannerDataTicket(long Generation);

public sealed class PlannerDataAccessGate
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly AsyncLocal<PlannerDataTicket?> boundTicket = new();
    private long generation;

    public PlannerDataTicket CaptureTicket() =>
        boundTicket.Value ?? new PlannerDataTicket(Interlocked.Read(ref generation));

    public IDisposable BindOperation()
    {
        var previous = boundTicket.Value;
        boundTicket.Value ??= new PlannerDataTicket(Interlocked.Read(ref generation));
        return new OperationScope(() => boundTicket.Value = previous);
    }

    public void RequireCurrent(PlannerDataTicket ticket)
    {
        if (ticket.Generation != Interlocked.Read(ref generation))
            throw DataChanged();
    }

    public async Task ExecutePublicationAsync(PlannerDataTicket ticket, Func<Task> publication,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            RequireCurrent(ticket);
            await publication();
            RequireCurrent(ticket);
        }
        finally { gate.Release(); }
    }

    public async Task<T> ExecutePublicationAsync<T>(PlannerDataTicket ticket, Func<Task<T>> publication,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            RequireCurrent(ticket);
            var result = await publication();
            RequireCurrent(ticket);
            return result;
        }
        finally { gate.Release(); }
    }

    public async Task AdvanceAsync(Action invalidateMemory, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            Interlocked.Increment(ref generation);
            invalidateMemory();
        }
        finally { gate.Release(); }
    }

    private static OutlookException DataChanged() =>
        new("planner_data_changed", "Planner authorization changed. Refresh the widget.", 401);

    private sealed class OperationScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

public sealed class PlannerDataBoundResult(IResult inner, PlannerDataLifecycle lifecycle,
    PlannerDataTicket ticket) : IResult
{
    public Task ExecuteAsync(HttpContext http) => lifecycle.ExecutePublicationAsync(
        ticket, () => inner.ExecuteAsync(http), http.RequestAborted);
}

internal sealed class PlannerDataBufferedBoundResult(IBufferedHttpResult inner,
    PlannerDataLifecycle lifecycle, PlannerDataTicket ticket) : IResult, IBufferedHttpResult
{
    public Task<BufferedHttpResponse> PrepareAsync(HttpContext http) => lifecycle.ExecutePublicationAsync(
        ticket, () => inner.PrepareAsync(http), http.RequestAborted);

    public async Task ExecuteAsync(HttpContext http) =>
        await (await PrepareAsync(http)).CopyToAsync(http);
}
