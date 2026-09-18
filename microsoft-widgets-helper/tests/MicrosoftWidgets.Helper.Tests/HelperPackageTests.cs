using PlannerEdge.Helper.Hosting;

namespace PlannerEdge.Helper.Tests;

public sealed class HelperPackageTests
{
    [Fact]
    public void OutlookAndPlannerPackagesHaveDistinctStablePaths()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "widgets", "OutlookEdgeWidget.icuewidget"), HelperHost.OutlookPackagePath);
        Assert.NotEqual(HelperHost.PlannerPackagePath, HelperHost.OutlookPackagePath);
    }
}
