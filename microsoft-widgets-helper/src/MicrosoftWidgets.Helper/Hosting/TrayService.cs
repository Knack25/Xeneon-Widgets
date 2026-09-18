using System.Drawing;
using Microsoft.Win32;
using System.Windows.Forms;
using PlannerEdge.Helper.Updates;

namespace PlannerEdge.Helper.Hosting;

public sealed class TrayService(UpdateService updates, IHostApplicationLifetime lifetime, ILogger<TrayService> logger) : IHostedService
{
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Control? dispatcher;

    public Task StartAsync(CancellationToken ct)
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "Microsoft Widgets tray" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.WaitAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        RequestExit();
        await stopped.Task.WaitAsync(ct);
    }

    private void RequestExit()
    {
        try { dispatcher?.BeginInvoke((Action)Application.ExitThread); }
        catch (InvalidOperationException) { /* The message loop has already closed. */ }
    }

    private void Run()
    {
        try
        {
            using var control = new Control();
            _ = control.Handle;
            dispatcher = control;
            using var context = new ApplicationContext();
            using var menu = new ContextMenuStrip();
            using var stream = typeof(TrayService).Assembly.GetManifestResourceStream("MicrosoftWidgets.Helper.Icon.ico")
                ?? throw new InvalidOperationException("The helper icon is missing.");
            using var icon = new Icon(stream, SystemInformation.SmallIconSize);
            using var lightStream = typeof(TrayService).Assembly.GetManifestResourceStream("MicrosoftWidgets.Helper.IconLight.ico")
                ?? throw new InvalidOperationException("The light helper icon is missing.");
            using var lightIcon = new Icon(lightStream, SystemInformation.SmallIconSize);
            using var tray = new NotifyIcon
            {
                Icon = TrayIconTheme.IsLightForeground() ? lightIcon : icon,
                Text = $"Microsoft Widgets Helper {HelperHost.Version}",
                ContextMenuStrip = menu,
                Visible = true
            };
            var commands = new TrayCommands(HelperHost.OpenSetup, HelperHost.OpenUpdates, updates.CheckAsync, lifetime.StopApplication);
            void Report(Exception error)
            {
                logger.LogWarning(error, "Tray action failed");
                if (!lifetime.ApplicationStopping.IsCancellationRequested)
                    tray.ShowBalloonTip(5000, "Microsoft Widgets Helper", "Unable to complete the action. Open http://localhost:8787 in your browser.", ToolTipIcon.Warning);
            }
            void OpenSetup()
            {
                try { commands.OpenSetup(); }
                catch (Exception error) { Report(error); }
            }
            menu.Items.Add("Open setup", null, (_, _) => OpenSetup());
            var check = new ToolStripMenuItem("Check for updates");
            check.Click += async (_, _) =>
            {
                check.Enabled = false;
                check.Text = "Checking for updates...";
                try { await commands.CheckForUpdatesAsync(lifetime.ApplicationStopping); }
                catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested) { }
                catch (Exception error) { Report(error); }
                finally
                {
                    if (!check.IsDisposed)
                    {
                        check.Text = "Check for updates";
                        check.Enabled = true;
                    }
                }
            };
            menu.Items.Add(check);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit helper", null, (_, _) => commands.Quit());
            tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenSetup(); };
            using var registration = lifetime.ApplicationStopping.Register(RequestExit);
            void ThemeChanged(object sender, UserPreferenceChangedEventArgs e)
            {
                try
                {
                    control.BeginInvoke((Action)(() =>
                    {
                        if (!lifetime.ApplicationStopping.IsCancellationRequested)
                            tray.Icon = TrayIconTheme.IsLightForeground() ? lightIcon : icon;
                    }));
                }
                catch (InvalidOperationException) { /* Shutdown has already destroyed the dispatcher. */ }
            }
            SystemEvents.UserPreferenceChanged += ThemeChanged;
            try
            {
                ready.TrySetResult();
                // A dedicated STA message loop keeps Windows UI work off the web server threads.
                Application.Run(context);
            }
            finally
            {
                SystemEvents.UserPreferenceChanged -= ThemeChanged;
                tray.Visible = false;
            }
        }
        catch (Exception error)
        {
            logger.LogError(error, "The tray icon could not run");
            ready.TrySetException(error);
        }
        finally
        {
            dispatcher = null;
            lifetime.StopApplication();
            stopped.TrySetResult();
        }
    }
}
