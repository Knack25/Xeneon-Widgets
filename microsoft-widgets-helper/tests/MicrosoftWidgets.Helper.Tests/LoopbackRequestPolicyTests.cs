using System.Net;
using Microsoft.AspNetCore.Http;
using PlannerEdge.Helper.Security;

namespace PlannerEdge.Helper.Tests;

public sealed class LoopbackRequestPolicyTests
{
    [Theory]
    [InlineData("localhost:8787", true)]
    [InlineData("LOCALHOST:8787", true)]
    [InlineData("127.0.0.1:8787", true)]
    [InlineData("127.0.0.2:8787", false)]
    [InlineData("[::1]:8787", true)]
    [InlineData("attacker.invalid:8787", false)]
    [InlineData("localhost:9999", false)]
    public void Destination_must_be_expected_loopback_host(string host, bool expected)
    {
        var context = CreateContext(host, localPort: 8787);

        Assert.Equal(expected, LoopbackRequestPolicy.IsAllowed(context, 8787));
    }

    [Fact]
    public void Forwarded_host_cannot_substitute_for_the_request_destination()
    {
        var context = CreateContext("attacker.invalid:8787", localPort: 8787);
        context.Request.Headers["X-Forwarded-Host"] = "localhost:8787";

        Assert.False(LoopbackRequestPolicy.IsAllowed(context, 8787));
    }

    [Fact]
    public void Non_loopback_listener_address_is_rejected()
    {
        var context = CreateContext("localhost:8787", localPort: 8787);
        context.Connection.LocalIpAddress = IPAddress.Parse("192.168.1.10");

        Assert.False(LoopbackRequestPolicy.IsAllowed(context, 8787));
    }

    [Fact]
    public void Missing_host_is_rejected()
    {
        var context = CreateContext(null, localPort: 8787);

        Assert.False(LoopbackRequestPolicy.IsAllowed(context, 8787));
    }

    private static DefaultHttpContext CreateContext(string? host, int localPort)
    {
        var context = new DefaultHttpContext();
        if (host is not null) context.Request.Host = new HostString(host);
        context.Connection.LocalIpAddress = IPAddress.Loopback;
        context.Connection.LocalPort = localPort;
        return context;
    }
}
