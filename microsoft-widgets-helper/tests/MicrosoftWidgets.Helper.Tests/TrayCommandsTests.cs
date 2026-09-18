using PlannerEdge.Helper.Hosting;

namespace PlannerEdge.Helper.Tests;

public sealed class TrayCommandsTests
{
    [Fact]
    public void OpenAndQuit_InvokeOnlyTheirOwnActions()
    {
        var opened = 0;
        var quit = 0;
        var checks = 0;
        var commands = new TrayCommands(() => opened++, () => throw new Exception("Unexpected updates page"),
            _ => { checks++; return Task.CompletedTask; }, () => quit++);
        commands.OpenSetup();
        Assert.Equal(1, opened);
        Assert.Equal(0, quit);
        commands.Quit();
        Assert.Equal(1, quit);
        Assert.Equal(0, checks);
    }

    [Fact]
    public async Task UpdateAction_OpensUpdatesAndChecksWithoutInstalling()
    {
        var actions = new List<string>();
        var commands = new TrayCommands(() => actions.Add("setup"), () => actions.Add("updates"),
            _ => { actions.Add("check"); return Task.CompletedTask; }, () => actions.Add("quit"));
        await commands.CheckForUpdatesAsync(default);
        Assert.Equal(new[] { "updates", "check" }, actions);
    }

    [Fact]
    public async Task RepeatedUpdateClicks_AreCoalesced_AndCanRetryAfterFailure()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = 0;
        var commands = new TrayCommands(() => { }, () => opened++, _ => waiting.Task, () => { });
        var first = commands.CheckForUpdatesAsync(default);
        await commands.CheckForUpdatesAsync(default);
        Assert.Equal(1, opened);
        waiting.SetException(new HttpRequestException("Offline"));
        await Assert.ThrowsAsync<HttpRequestException>(() => first);
        await Assert.ThrowsAsync<HttpRequestException>(() => commands.CheckForUpdatesAsync(default));
        Assert.Equal(2, opened);
    }
}
