using System.IO.Pipes;
using Microsoft.Extensions.Logging.Abstractions;
using PlannerEdge.Helper.Hosting;
using PlannerEdge.Helper.Security;

namespace PlannerEdge.Helper.Tests;

public sealed class HelperControlPipeTests
{
    [Fact]
    public async Task Open_setup_request_runs_in_the_owner_process_with_a_fresh_fragment_bootstrap()
    {
        var access = new LocalAccessService(TimeProvider.System);
        var opened = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeName = "MicrosoftWidgets.Helper.Tests." + Guid.NewGuid().ToString("N");
        var pipe = new HelperControlPipe(access, NullLogger<HelperControlPipe>.Instance, pipeName,
            owner => opened.TrySetResult(HelperHost.CreateSetupUrl(owner)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await pipe.StartAsync(timeout.Token);
        try
        {
            Assert.True(await HelperControlPipe.SendCommandAsync(pipeName, HelperControlPipe.OpenSetupCommand, timeout.Token));
            var url = await opened.Task.WaitAsync(timeout.Token);

            Assert.StartsWith("http://localhost:8787/#access=", url, StringComparison.Ordinal);
            Assert.DoesNotContain("?", url, StringComparison.Ordinal);
            Assert.DoesNotContain("access=", new Uri(url).Query, StringComparison.Ordinal);
        }
        finally
        {
            await pipe.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Unknown_commands_cannot_invoke_the_owner_callback()
    {
        var invoked = false;
        var pipeName = "MicrosoftWidgets.Helper.Tests." + Guid.NewGuid().ToString("N");
        var pipe = new HelperControlPipe(new LocalAccessService(TimeProvider.System), NullLogger<HelperControlPipe>.Instance,
            pipeName, _ => invoked = true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await pipe.StartAsync(timeout.Token);
        try
        {
            Assert.False(await HelperControlPipe.SendCommandAsync(pipeName, "unknown", timeout.Token));
            Assert.False(invoked);
        }
        finally
        {
            await pipe.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Same_user_stop_request_stops_the_owner_process()
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeName = "MicrosoftWidgets.Helper.Tests." + Guid.NewGuid().ToString("N");
        var pipe = new HelperControlPipe(new LocalAccessService(TimeProvider.System), NullLogger<HelperControlPipe>.Instance,
            pipeName, _ => { }, () => stopped.TrySetResult());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await pipe.StartAsync(timeout.Token);
        try
        {
            Assert.True(await HelperControlPipe.SendCommandAsync(pipeName, HelperControlPipe.StopCommand, timeout.Token));
            await stopped.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            await pipe.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Random_data_cannot_stop_the_owner_process()
    {
        var stopped = false;
        var pipeName = "MicrosoftWidgets.Helper.Tests." + Guid.NewGuid().ToString("N");
        var pipe = new HelperControlPipe(new LocalAccessService(TimeProvider.System), NullLogger<HelperControlPipe>.Instance,
            pipeName, _ => { }, () => stopped = true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await pipe.StartAsync(timeout.Token);
        try
        {
            Assert.False(await HelperControlPipe.SendCommandAsync(pipeName, "stop-now", timeout.Token));
            Assert.False(stopped);
        }
        finally
        {
            await pipe.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Stop_request_without_a_listener_respects_its_timeout()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var started = TimeProvider.System.GetTimestamp();

        Assert.False(await HelperControlPipe.RequestStopAsync(TimeSpan.FromMilliseconds(100), timeout.Token));

        Assert.True(TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Stalled_messages_cannot_block_later_owner_commands()
    {
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeName = "MicrosoftWidgets.Helper.Tests." + Guid.NewGuid().ToString("N");
        var pipe = new HelperControlPipe(new LocalAccessService(TimeProvider.System), NullLogger<HelperControlPipe>.Instance,
            pipeName, _ => invoked.TrySetResult());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));

        await pipe.StartAsync(timeout.Token);
        try
        {
            await using var stalled = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await stalled.ConnectAsync(timeout.Token);
            await stalled.WriteAsync(new byte[] { 10 }, timeout.Token);
            await stalled.FlushAsync(timeout.Token);
            await Task.Delay(HelperControlPipe.MessageTimeout + TimeSpan.FromMilliseconds(250), timeout.Token);

            Assert.True(await HelperControlPipe.SendCommandAsync(pipeName, HelperControlPipe.OpenSetupCommand, timeout.Token));
            await invoked.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            await pipe.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Control_server_requires_current_user_and_asynchronous_pipe_access()
    {
        Assert.True(HelperControlPipe.ServerOptions.HasFlag(PipeOptions.CurrentUserOnly));
        Assert.True(HelperControlPipe.ServerOptions.HasFlag(PipeOptions.Asynchronous));
    }

    [Fact]
    public void Parameterless_unbootstrapped_setup_open_is_not_available()
    {
        Assert.Null(typeof(HelperHost).GetMethod(nameof(HelperHost.OpenSetup), Type.EmptyTypes));
    }
}
