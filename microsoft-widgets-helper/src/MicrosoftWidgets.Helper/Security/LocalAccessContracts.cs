using System.Text.Json.Serialization;

namespace PlannerEdge.Helper.Security;

[JsonConverter(typeof(JsonStringEnumConverter<WidgetScope>))]
public enum WidgetScope
{
    [JsonStringEnumMemberName("planner")] Planner,
    [JsonStringEnumMemberName("outlook")] Outlook
}

public sealed record ScopedPairingRequest([property: JsonRequired] WidgetScope Scope, string InstanceId, string RequestSecret);
public sealed record WidgetPendingPairing(string Id, string Code, string InstanceId, DateTimeOffset ExpiresAt, WidgetScope Scope);
public sealed record WidgetPairedInstance(string CredentialId, string InstanceId, WidgetScope Scope);
public sealed record StoredWidgetCredential(string CredentialId, WidgetScope Scope, string InstanceId,
    string AccountKey, string Hash, DateTimeOffset CreatedAt);
public sealed record WidgetCredentialStore(int Version, StoredWidgetCredential[] Credentials);

public sealed record OwnerBootstrap(string Token, DateTimeOffset ExpiresAt);

public sealed record OwnerSessionResponse(string Token);

public class LocalAccessException(string message) : Exception(message);

internal sealed class LocalAccessThrottleException() :
    LocalAccessException("Local access bootstrap attempts are temporarily limited.");

public static class LocalAccessHeaders
{
    public const string Bootstrap = "X-Microsoft-Widgets-Bootstrap";
    public const string Owner = "X-Microsoft-Widgets-Owner";
    public const string OwnerReplacement = "X-Microsoft-Widgets-Owner-Replacement";
    public const string Credential = "X-Microsoft-Widgets-Credential";
}
