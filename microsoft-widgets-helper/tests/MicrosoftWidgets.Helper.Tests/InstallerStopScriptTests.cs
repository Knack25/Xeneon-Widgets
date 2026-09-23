using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PlannerEdge.Helper.Hosting;
using PlannerEdge.Helper.Security;

namespace PlannerEdge.Helper.Tests;

public sealed class InstallerStopScriptTests
{
    private static string ScriptPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "installer", "Stop-MicrosoftWidgetsHelper.ps1"));

    [Fact]
    public async Task Tested_installer_script_stops_the_real_control_pipe_and_waits_for_mutex_exit()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var pipeName = "MicrosoftWidgets.Helper.InstallerTests." + suffix;
        var mutexName = "Local\\MicrosoftWidgets.Helper.InstallerTests." + suffix;
        using var mutex = new Mutex(false, mutexName);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipe = new HelperControlPipe(new LocalAccessService(TimeProvider.System), NullLogger<HelperControlPipe>.Instance,
            pipeName, _ => { }, () => { mutex.Dispose(); stopped.TrySetResult(); });
        await pipe.StartAsync(CancellationToken.None);
        try
        {
            var result = await RunExactEncodedPayloadAsync(pipeName, mutexName, 3_000, 1_000, 1_000);
            Assert.Equal(0, result.ExitCode);
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await pipe.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Busy_or_missing_pipe_fails_closed_while_helper_mutex_exists()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var mutexName = "Local\\MicrosoftWidgets.Helper.InstallerTests." + suffix;
        using var mutex = new Mutex(false, mutexName);

        var result = await RunExactEncodedPayloadAsync("missing-" + suffix, mutexName, 1_500, 150, 150);

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Spoofed_ack_without_helper_exit_fails_closed()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var pipeName = "MicrosoftWidgets.Helper.InstallerTests." + suffix;
        var mutexName = "Local\\MicrosoftWidgets.Helper.InstallerTests." + suffix;
        using var mutex = new Mutex(false, mutexName);
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var serverTask = AckAsync(server);

        var result = await RunExactEncodedPayloadAsync(pipeName, mutexName, 2_000, 500, 250);

        await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Server_that_never_acknowledges_is_killed_by_the_parent_deadline()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var pipeName = "MicrosoftWidgets.Helper.InstallerTests." + suffix;
        var mutexName = "Local\\MicrosoftWidgets.Helper.InstallerTests." + suffix;
        using var mutex = new Mutex(false, mutexName);
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var hold = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var serverTask = HoldWithoutAckAsync(server, hold.Token);
        var started = Stopwatch.StartNew();

        var result = await RunExactEncodedPayloadAsync(pipeName, mutexName, 700, 500, 250);

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), result.Output);
        hold.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serverTask);
    }

    [Fact]
    public async Task Missing_helper_and_mutex_is_already_stopped()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var result = await RunExactEncodedPayloadAsync("missing-" + suffix, "Local\\missing-" + suffix, 1_500, 150, 150);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("0.1.1")]
    [InlineData("0.1.2")]
    [InlineData("0.1.3")]
    [InlineData("0.1.4")]
    public async Task Released_legacy_helper_stops_through_its_local_endpoint(string version)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var mutexName = "Local\\MicrosoftWidgets.Helper.InstallerTests." + suffix;
        using var mutex = new Mutex(false, mutexName);
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var baseUri = $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/";
        var requests = new List<string>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        var serverTask = ServeLegacyAsync(server, requests, () => mutex.Dispose(), version, deadline.Token);

        var result = await RunExactEncodedPayloadAsync("missing-" + suffix, mutexName, 6_000, 150, 2_000, baseUri);

        await serverTask;
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["GET /health", "POST /host/stop"], requests);
    }

    [Fact]
    public async Task Unknown_local_service_is_not_sent_a_legacy_stop()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var mutexName = "Local\\MicrosoftWidgets.Helper.InstallerTests." + suffix;
        using var mutex = new Mutex(false, mutexName);
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var baseUri = $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/";
        var requests = new List<string>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        var serverTask = ServeLegacyAsync(server, requests, () => { }, "0.1.5", deadline.Token);

        var result = await RunExactEncodedPayloadAsync("missing-" + suffix, mutexName, 4_000, 150, 500, baseUri);

        await serverTask;
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(["GET /health"], requests);
    }

    [Fact]
    public async Task Legacy_fallback_rejects_non_loopback_address()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var mutexName = "Local\\MicrosoftWidgets.Helper.InstallerTests." + suffix;
        using var mutex = new Mutex(false, mutexName);

        var result = await RunExactEncodedPayloadAsync("missing-" + suffix, mutexName,
            4_000, 150, 500, "http://192.0.2.1/");

        Assert.NotEqual(0, result.ExitCode);
    }

    private static async Task ServeLegacyAsync(TcpListener server, List<string> requests, Action stop,
        string version, CancellationToken cancellationToken)
    {
        for (var index = 0; index < (version == "0.1.5" ? 1 : 2); index++)
        {
            using var client = await server.AcceptTcpClientAsync(cancellationToken);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var line = await reader.ReadLineAsync(cancellationToken);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken))) { }
            requests.Add(line!.Split(' ', 3)[0] + " " + line.Split(' ', 3)[1]);
            var body = index == 0
                ? $"{{\"status\":\"ok\",\"version\":\"{version}\",\"service\":\"Microsoft Widgets Helper\"}}"
                : "";
            var status = index == 0 ? "200 OK" : "202 Accepted";
            var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.ASCII.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
            await stream.WriteAsync(response, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            if (index == 1) stop();
        }
    }

    private static async Task AckAsync(NamedPipeServerStream server)
    {
        await server.WaitForConnectionAsync();
        var length = server.ReadByte();
        Assert.True(length > 0);
        var command = new byte[length];
        await server.ReadExactlyAsync(command);
        await server.WriteAsync(new byte[] { 1 });
        await server.FlushAsync();
    }

    private static async Task HoldWithoutAckAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        await server.WaitForConnectionAsync(cancellationToken);
        var length = server.ReadByte();
        Assert.True(length > 0);
        var command = new byte[length];
        await server.ReadExactlyAsync(command, cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static async Task<(int ExitCode, string Output)> RunExactEncodedPayloadAsync(
        string pipeName, string mutexName, int overallTimeout, int connectTimeout, int mutexWait,
        string? legacyBaseUri = null)
    {
        var payload = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(
            await File.ReadAllTextAsync(ScriptPath)));
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(payload);
        startInfo.Environment["MICROSOFT_WIDGETS_STOP_PIPE"] = pipeName;
        startInfo.Environment["MICROSOFT_WIDGETS_STOP_MUTEX"] = mutexName;
        startInfo.Environment["MICROSOFT_WIDGETS_STOP_OVERALL_TIMEOUT_MS"] = overallTimeout.ToString();
        startInfo.Environment["MICROSOFT_WIDGETS_STOP_CONNECT_TIMEOUT_MS"] = connectTimeout.ToString();
        startInfo.Environment["MICROSOFT_WIDGETS_STOP_MUTEX_WAIT_MS"] = mutexWait.ToString();
        if (legacyBaseUri is not null)
            startInfo.Environment["MICROSOFT_WIDGETS_STOP_LEGACY_BASE_URI"] = legacyBaseUri;

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        return (process.ExitCode, (await stdout) + (await stderr));
    }
}
