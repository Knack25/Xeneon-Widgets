using System.IO.Pipes;
using System.Text;
using PlannerEdge.Helper.Security;

namespace PlannerEdge.Helper.Hosting;

public sealed class HelperControlPipe : BackgroundService
{
    public const string OpenSetupCommand = "open-setup";
    public const string StopCommand = "stop";
    internal const int MaximumMessageBytes = 64;
    internal static readonly PipeOptions ServerOptions = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
    internal static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(1);
    internal const string ControlPipeName = "Knack25.MicrosoftWidgetsHelper.Control.v1";
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(3);
    private readonly LocalAccessService access;
    private readonly ILogger<HelperControlPipe> logger;
    private readonly string pipeName;
    private readonly Action<LocalAccessService> openSetup;
    private readonly Action stop;

    public HelperControlPipe(LocalAccessService access, ILogger<HelperControlPipe> logger)
        : this(access, logger, ControlPipeName, HelperHost.OpenSetup, () => { })
    {
    }

    public HelperControlPipe(LocalAccessService access, ILogger<HelperControlPipe> logger, IHostApplicationLifetime lifetime)
        : this(access, logger, ControlPipeName, HelperHost.OpenSetup, lifetime.StopApplication)
    {
    }

    [ActivatorUtilitiesConstructor]
    public HelperControlPipe(LocalAccessService access, ILogger<HelperControlPipe> logger,
        IHostApplicationLifetime lifetime, HelperAddress address)
        : this(access, logger, ControlPipeName, owner => HelperHost.OpenSetup(owner, address),
            lifetime.StopApplication)
    {
    }

    internal HelperControlPipe(LocalAccessService access, ILogger<HelperControlPipe> logger, string pipeName,
        Action<LocalAccessService> openSetup)
        : this(access, logger, pipeName, openSetup, () => { })
    {
    }

    internal HelperControlPipe(LocalAccessService access, ILogger<HelperControlPipe> logger, string pipeName,
        Action<LocalAccessService> openSetup, Action stop)
    {
        this.access = access;
        this.logger = logger;
        this.pipeName = pipeName;
        this.openSetup = openSetup;
        this.stop = stop;
    }

    public static async Task<bool> RequestOpenSetupAsync(CancellationToken cancellationToken = default)
        => await RequestCommandAsync(ControlPipeName, OpenSetupCommand, ClientTimeout, cancellationToken);

    public static async Task<bool> RequestStopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => await RequestStopAsync(ControlPipeName, timeout, cancellationToken);

    internal static async Task<bool> RequestStopAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken = default)
        => await RequestCommandAsync(pipeName, StopCommand, timeout, cancellationToken);

    private static async Task<bool> RequestCommandAsync(string pipeName, string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return await SendCommandAsync(pipeName, command, deadline.Token);
        }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static async Task<bool> SendCommandAsync(string pipeName, string command, CancellationToken cancellationToken)
    {
        var message = Encoding.UTF8.GetBytes(command);
        if (message.Length is 0 or > MaximumMessageBytes) return false;

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(cancellationToken);
        await client.WriteAsync(new byte[] { (byte)message.Length }, cancellationToken);
        await client.WriteAsync(message, cancellationToken);
        await client.FlushAsync(cancellationToken);
        var response = new byte[1];
        return await client.ReadAsync(response, cancellationToken) == 1 && response[0] == 1;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, ServerOptions, MaximumMessageBytes + 1, 1);
                await server.WaitForConnectionAsync(stoppingToken);
                using var messageTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                messageTimeout.CancelAfter(MessageTimeout);
                await ProcessConnectionAsync(server, messageTimeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                logger.LogDebug("The helper control pipe request timed out");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(error, "The helper control pipe request failed");
            }
        }
    }

    private async Task ProcessConnectionAsync(Stream pipe, CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[1];
        var accepted = false;
        Action? afterAcknowledgement = null;
        if (await pipe.ReadAsync(lengthBuffer, cancellationToken) == 1 && lengthBuffer[0] is > 0 and <= MaximumMessageBytes)
        {
            var message = new byte[lengthBuffer[0]];
            try
            {
                await pipe.ReadExactlyAsync(message, cancellationToken);
                var command = Encoding.UTF8.GetString(message);
                if (command == OpenSetupCommand)
                {
                    openSetup(access);
                    accepted = true;
                }
                else if (command == StopCommand)
                {
                    accepted = true;
                    afterAcknowledgement = stop;
                }
            }
            catch (EndOfStreamException)
            {
                accepted = false;
            }
        }

        await pipe.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, cancellationToken);
        await pipe.FlushAsync(cancellationToken);
        afterAcknowledgement?.Invoke();
    }
}
