namespace PlannerEdge.Helper.Storage;

public static class LocalPaths
{
    public static string AppDataRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "PlannerEdgeWidget");
    }
}
