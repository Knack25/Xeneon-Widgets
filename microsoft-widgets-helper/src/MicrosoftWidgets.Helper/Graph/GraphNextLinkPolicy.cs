namespace PlannerEdge.Helper.Graph;

public enum GraphCollectionKind
{
    Plans,
    Groups,
    Buckets,
    Tasks,
    Members,
    ConversationPosts
}

public static class GraphNextLinkPolicy
{
    public const int MaxPages = 100;
    public const int PlanRecordLimit = 1_000;
    public const int GroupRecordLimit = 5_000;
    public const int BucketRecordLimit = 1_000;
    public const int TaskRecordLimit = 10_000;
    public const int MemberRecordLimit = 10_000;
    public const int ConversationRecordLimit = 1_000;

    public static Uri RequireAllowed(Uri current, string candidate, ISet<string> visited)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("graph.microsoft.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !uri.AbsolutePath.StartsWith("/v1.0/", StringComparison.Ordinal)
            || !uri.AbsolutePath.Equals(current.AbsolutePath, StringComparison.Ordinal))
            throw new InvalidDataException("Microsoft Graph returned an invalid continuation link.");

        var canonical = Canonical(uri);
        if (!visited.Add(canonical))
            throw new InvalidDataException("Microsoft Graph returned a repeated continuation link.");
        return uri;
    }

    public static void RequirePageCount(int pages)
    {
        if (pages > MaxPages)
            throw new InvalidDataException("Microsoft Graph pagination exceeded the page limit.");
    }

    public static void RequireRecordCount(GraphCollectionKind kind, int records)
    {
        var limit = kind switch
        {
            GraphCollectionKind.Plans => PlanRecordLimit,
            GraphCollectionKind.Groups => GroupRecordLimit,
            GraphCollectionKind.Buckets => BucketRecordLimit,
            GraphCollectionKind.Tasks => TaskRecordLimit,
            GraphCollectionKind.Members => MemberRecordLimit,
            GraphCollectionKind.ConversationPosts => ConversationRecordLimit,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (records > limit)
            throw new InvalidDataException("Microsoft Graph pagination exceeded the record limit.");
    }

    public static string Canonical(Uri uri) =>
        $"https://graph.microsoft.com{uri.AbsolutePath}{uri.Query}";
}

public static class PlannerGraphHttpHandlerFactory
{
    public static HttpClientHandler Create() => new() { AllowAutoRedirect = false };
}
