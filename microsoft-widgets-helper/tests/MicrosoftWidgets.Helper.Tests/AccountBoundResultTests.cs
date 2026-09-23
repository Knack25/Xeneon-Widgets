using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Outlook;
using PlannerEdge.Helper.Planner;
using PlannerEdge.Helper.Security;
using AccountStateTests = MicrosoftWidgets.Helper.Tests.MicrosoftAccountStateTests;
using MemoryStore = MicrosoftWidgets.Helper.Tests.OutlookMemoryStore;

namespace PlannerEdge.Helper.Tests;

public sealed class AccountBoundResultTests
{
    [Fact]
    public async Task Outlook_response_backpressure_does_not_hold_the_account_transition_gate()
    {
        var state = CreateAccountState();
        var lease = await state.GetAsync(default);
        var client = new BlockingWriteStream();
        var context = CreateContext(client);
        var response = new AccountBoundResult(new BodyResult("outlook"), state, lease);

        var execution = response.ExecuteAsync(context);
        await client.Started.WaitAsync(TimeSpan.FromSeconds(2));
        var transition = state.PurgeDataAsync(default);

        try
        {
            await transition.WaitAsync(TimeSpan.FromMilliseconds(300));
        }
        finally
        {
            client.Release();
            await Task.WhenAll(execution, transition).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task Planner_response_backpressure_does_not_hold_the_lifecycle_purge_gate()
    {
        var state = CreateAccountState();
        var lease = await state.GetAsync(default);
        var accessGate = new PlannerDataAccessGate();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var lifecycle = new PlannerDataLifecycle(cache, accessGate);
        var client = new BlockingWriteStream();
        var context = CreateContext(client);
        var planner = new PlannerDataBoundResult(
            new BodyResult("planner"), lifecycle, lifecycle.CaptureTicket());
        var response = new AccountBoundResult(planner, state, lease);

        var execution = response.ExecuteAsync(context);
        await client.Started.WaitAsync(TimeSpan.FromSeconds(2));
        var purge = lifecycle.PurgeAsync(default);

        try
        {
            await purge.WaitAsync(TimeSpan.FromMilliseconds(300));
        }
        finally
        {
            client.Release();
            await Task.WhenAll(execution, purge).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task Ordinary_account_bound_results_are_capped_before_client_publication()
    {
        var state = CreateAccountState();
        var lease = await state.GetAsync(default);
        await using var body = new MemoryStream();
        var context = CreateContext(body);
        var response = new AccountBoundResult(
            new BodyResult(new byte[4 * 1024 * 1024 + 1]), state, lease);

        await response.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Contains("response_too_large", System.Text.Encoding.UTF8.GetString(body.ToArray()));
    }

    private static MicrosoftAccountState CreateAccountState() => new(
        new AccountStateTests.IdentityProvider(
            new MicrosoftAccountIdentity("home", "tenant", "client", "person@example.test")),
        new MemoryStore());

    private static DefaultHttpContext CreateContext(Stream body)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddRouting().BuildServiceProvider()
        };
        context.Response.Body = body;
        return context;
    }

    private sealed class BodyResult : IResult
    {
        private readonly byte[] body;

        public BodyResult(string body) : this(System.Text.Encoding.UTF8.GetBytes(body)) { }
        public BodyResult(byte[] body) => this.body = body;

        public Task ExecuteAsync(HttpContext http) =>
            http.Response.Body.WriteAsync(body, http.RequestAborted).AsTask();
    }

    private sealed class BlockingWriteStream : Stream
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => started.Task;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public void Release() => release.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
