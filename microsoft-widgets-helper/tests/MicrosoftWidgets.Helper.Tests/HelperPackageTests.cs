using PlannerEdge.Helper.Hosting;

namespace PlannerEdge.Helper.Tests;

public sealed class HelperPackageTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void OutlookAndPlannerPackagesHaveDistinctStablePaths()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "widgets", "OutlookEdgeWidget.icuewidget"), HelperHost.OutlookPackagePath);
        Assert.NotEqual(HelperHost.PlannerPackagePath, HelperHost.OutlookPackagePath);
    }

    [Fact]
    public void InstallerRefusesElevationAndUsesTheSameUserControlPipe()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "microsoft-widgets-helper", "installer", "MicrosoftWidgets.iss"));

        Assert.Contains("PrivilegesRequired=lowest", source, StringComparison.Ordinal);
        Assert.Contains("function InitializeSetup", source, StringComparison.Ordinal);
        Assert.Contains("function InitializeUninstall", source, StringComparison.Ordinal);
        Assert.Contains("IsAdmin", source, StringComparison.Ordinal);
        Assert.Contains("Run as administrator", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Knack25.MicrosoftWidgetsHelper.Control.v1", source, StringComparison.Ordinal);
        Assert.Contains("Result := RequestHelperStop;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("catch [IO.IOException] { exit 0 }", source, StringComparison.Ordinal);
        Assert.DoesNotContain("[UninstallRun]", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exec(HelperPath", source, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("microsoft-widgets-helper/scripts/publish.ps1")]
    [InlineData("planner-edge-widget/scripts/package.ps1")]
    [InlineData("scripts/package-outlook.ps1")]
    [InlineData("scripts/build-release.ps1")]
    public void ReleaseStagesUseTheExactInventoryVerifier(string relativePath)
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Contains("verify-release-inventory.ps1", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OutlookBuildRemovesItsOldDistBeforeWriting()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "outlook-edge-widget", "scripts", "build.mjs"));

        Assert.Contains("rm(dist", source, StringComparison.Ordinal);
        Assert.Contains("recursive: true", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".git")) || Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate the repository root.");
    }
}
