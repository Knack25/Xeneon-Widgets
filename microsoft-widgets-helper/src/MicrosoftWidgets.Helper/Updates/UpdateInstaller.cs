using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using PlannerEdge.Helper.Storage;
using PlannerEdge.Helper.Hosting;

namespace PlannerEdge.Helper.Updates;

public interface IUpdateInstaller
{
    bool CanInstall { get; }
    string CreateDownloadPath();
    void Launch(string path, UpdateRelease release);
}

public sealed class UpdateInstaller : IUpdateInstaller
{
    public bool CanInstall => OperatingSystem.IsWindows() && File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));
    public static string ResultPath => Path.Combine(LocalPaths.AppDataRoot(), "update-result.json");

    public string CreateDownloadPath()
    {
        var directory = Path.Combine(LocalPaths.AppDataRoot(), "Updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "setup.exe");
    }

    public void Launch(string path, UpdateRelease release)
    {
        // Run outside the installation directory so Inno can replace the running helper.
        var runner = Path.Combine(Path.GetDirectoryName(path)!, "update-runner.exe");
        File.Copy(Environment.ProcessPath!, runner, false);
        var start = new ProcessStartInfo(runner) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "--apply-update", path, Environment.ProcessPath!, release.Sha256, release.Version }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start the updater.");
    }

    public static async Task ApplyAsync(string[] args)
    {
        if (args.Length != 5) return;
        var installer = args[1];
        var target = args[2];
        var result = "The update did not finish. Your existing helper has been restarted.";
        try
        {
            // Keep a read lock until setup exits to prevent replacing the verified executable.
            await using var file = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file));
            if (!hash.Equals(args[3], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update checksum changed.");
            var start = new ProcessStartInfo(installer) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "/HELPERUPDATE=1", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", $"/DIR={Path.GetDirectoryName(target)}" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Installer did not start.");
            await process.WaitForExitAsync();
            result = process.ExitCode == 0 ? $"Microsoft Widgets {args[4]} was installed. Import the bundled widget in iCUE if it has changed." : "Update cancelled or unsuccessful. Your existing helper has been restarted.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
        finally
        {
            try { await File.WriteAllTextAsync(ResultPath, JsonSerializer.Serialize(new { message = result, finishedAt = DateTimeOffset.UtcNow })); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (File.Exists(target))
            {
                // Setup may have exited before stopping the old helper. Restart it even on cancellation.
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                try { await client.PostAsync("http://localhost:8787/host/stop", null); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    await WaitForInstanceExitAsync(HelperHost.InstanceMutexName, timeout.Token);
                    using var process = Process.Start(new ProcessStartInfo(target, "--no-browser")
                    { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(target)! })
                        ?? throw new InvalidOperationException("Unable to restart the helper.");
                    var ready = false;
                    for (var attempt = 0; attempt < 30 && !process.HasExited; attempt++)
                    {
                        try { using var health = await client.GetAsync("http://localhost:8787/health"); ready = health.IsSuccessStatusCode; }
                        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
                        if (ready) break;
                        await Task.Delay(500);
                    }
                    if (!ready) throw new InvalidOperationException("The helper did not become ready.");
                    await HelperControlPipe.RequestOpenSetupAsync(timeout.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    try { await File.WriteAllTextAsync(ResultPath, JsonSerializer.Serialize(new { message = "The helper could not restart automatically. Open Microsoft Widgets Setup from the Start menu.", finishedAt = DateTimeOffset.UtcNow })); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
            }
        }
    }

    public static async Task WaitForInstanceExitAsync(string mutexName, CancellationToken ct)
    {
        while (Mutex.TryOpenExisting(mutexName, out var instance))
        {
            instance.Dispose();
            await Task.Delay(250, ct);
        }
    }
}
