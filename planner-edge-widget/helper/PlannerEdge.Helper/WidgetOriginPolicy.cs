namespace PlannerEdge.Helper;

public static class WidgetOriginPolicy
{
    public static bool IsAllowed(string origin)
    {
        if (origin is "null" or "file://")
        {
            return true;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttp
            && uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath == "/"
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment);
    }
}
