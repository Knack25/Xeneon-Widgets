using PlannerEdge.Helper;

namespace PlannerEdge.Helper.Tests;

public sealed class WidgetOriginPolicyTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("file://")]
    [InlineData("http://localhost:8787")]
    public void AllowsWidgetOrigins(string origin)
    {
        Assert.True(WidgetOriginPolicy.IsAllowed(origin));
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("file:///some/path")]
    [InlineData("http://localhost.evil.test:8787")]
    public void RejectsUnrelatedOrigins(string origin)
    {
        Assert.False(WidgetOriginPolicy.IsAllowed(origin));
    }
}
