using System.Reflection;

namespace PlannerEdge.Helper.Updates;

public sealed record UpdateStatus(string State, string CurrentVersion, bool CanInstall,
    string? AvailableVersion = null, string? Notes = null, DateTimeOffset? LastChecked = null, string? Message = null);

public sealed class UpdateService(IReleaseClient source, IUpdateInstaller installer, string currentVersion)
{
    public static string ReleaseVersion => typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "ReleaseVersion").Value!;
    private readonly SemaphoreSlim gate = new(1, 1);
    private UpdateRelease? available;
    private volatile UpdateStatus status = new("idle", currentVersion, installer.CanInstall);
    public UpdateStatus Status => status;

    public async Task CheckAsync(CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct)) return;
        try
        {
            if (status.State == "installing") return;
            available = null;
            status = new("checking", currentVersion, installer.CanInstall, LastChecked: status.LastChecked);
            available = await source.CheckAsync(currentVersion, ct);
            status = new(available is null ? "current" : "available", currentVersion, installer.CanInstall,
                available?.Version, available?.Notes, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            status = status with { State = "error", Message = "Unable to check for updates. Check your connection and try again." };
        }
        finally { gate.Release(); }
    }

    public async Task InstallAsync(string approvedVersion, CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct)) return;
        try
        {
            if (status.State == "installing") return;
            if (!installer.CanInstall) throw new InvalidOperationException("Install Microsoft Widgets with the Windows installer to enable in-app updates.");
            if (available is null || approvedVersion != available.Version) throw new InvalidOperationException("Check for updates and approve the available version first.");
            status = status with { State = "downloading", Message = null };
            try
            {
                var path = installer.CreateDownloadPath();
                await source.DownloadAsync(available, path, ct);
                installer.Launch(path, available);
                status = status with { State = "installing", Message = "The installer is opening. The helper will restart when it finishes." };
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                status = status with { State = "error", Message = "The update could not be downloaded, verified, or started. Nothing was installed. Try again." };
            }
        }
        finally { gate.Release(); }
    }
}

public sealed class UpdateWorker(UpdateService updates) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await updates.CheckAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
        }
    }
}
