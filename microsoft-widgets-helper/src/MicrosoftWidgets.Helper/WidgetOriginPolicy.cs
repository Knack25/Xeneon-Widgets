namespace PlannerEdge.Helper;

public static class WidgetOriginPolicy
{
    public static bool IsAllowed(string origin)
    {
        return origin is "null" or "file://";
    }
}
