namespace PlannerEdge.Helper.Security;

public sealed record OwnerBootstrap(string Token, DateTimeOffset ExpiresAt);

public sealed record OwnerSessionResponse(string Token);

public sealed class LocalAccessException(string message) : Exception(message);

public static class LocalAccessHeaders
{
    public const string Bootstrap = "X-Microsoft-Widgets-Bootstrap";
    public const string Owner = "X-Microsoft-Widgets-Owner";
}
