namespace PlannerEdge.Helper.Hosting;

public sealed class TrayCommands(Action openSetup, Action openUpdates,
    Func<CancellationToken, Task> checkForUpdates, Action quit)
{
    private int checking;
    public void OpenSetup() => openSetup();
    public void Quit() => quit();

    public async Task CheckForUpdatesAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref checking, 1) != 0) return;
        try
        {
            openUpdates();
            await checkForUpdates(ct);
        }
        finally { Volatile.Write(ref checking, 0); }
    }
}
