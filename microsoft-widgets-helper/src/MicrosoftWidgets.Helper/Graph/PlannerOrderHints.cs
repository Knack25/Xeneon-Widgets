namespace PlannerEdge.Helper.Graph;

internal static class PlannerOrderHints
{
    public static List<GraphChecklistItem> InCanonicalOrder(IEnumerable<GraphChecklistItem> items) =>
        items.OrderBy(item => item.OrderHint is null)
            .ThenBy(item => item.OrderHint, StringComparer.Ordinal)
            .ToList();
}
