using System.Net;
using Microsoft.AspNetCore.Http;

namespace PlannerEdge.Helper.Security;

public static class LoopbackRequestPolicy
{
    public static bool IsAllowed(HttpContext context, int expectedPort)
    {
        var host = context.Request.Host.Host.Trim('[', ']');
        var loopbackName = host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        var loopbackAddress = IPAddress.TryParse(host, out var parsed) &&
            (parsed.Equals(IPAddress.Loopback) || parsed.Equals(IPAddress.IPv6Loopback));

        return (loopbackName || loopbackAddress)
            && (context.Request.Host.Port ?? 80) == expectedPort
            && context.Connection.LocalPort == expectedPort
            && (context.Connection.LocalIpAddress is null || IPAddress.IsLoopback(context.Connection.LocalIpAddress));
    }
}
