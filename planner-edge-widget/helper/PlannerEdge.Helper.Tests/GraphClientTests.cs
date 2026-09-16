using System.Net;
using System.Text;
using PlannerEdge.Helper.Graph;

namespace PlannerEdge.Helper.Tests;

public sealed class GraphClientTests
{
    [Fact]
    public async Task GetTasksAsync_MapsTaskAndFollowsNextPage()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/tasks")
            ? """{"value":[{"id":"one","title":"First","planId":"plan","percentComplete":0,"@odata.etag":"W/\"v1\""}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/next"}"""
            : """{"value":[{"id":"two","title":"Second","planId":"plan","percentComplete":0,"@odata.etag":"W/\"v2\""}]}""");
        var client = CreateClient(handler);

        var tasks = await client.GetTasksAsync("plan", CancellationToken.None);

        Assert.Equal(2, tasks.Count);
        Assert.Equal("W/\"v1\"", tasks[0].ETag);
        Assert.Equal("two", tasks[1].Id);
    }

    [Fact]
    public async Task CompleteTaskAsync_SendsLatestEtagAndCompletionBody()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Equal("W/\"latest\"", request.Headers.IfMatch.Single().ToString());
            Assert.Equal("{\"percentComplete\":100}", request.Content!.ReadAsStringAsync().Result);
            return "{}";
        });
        await CreateClient(handler).CompleteTaskAsync("task-1", "W/\"latest\"", CancellationToken.None);
    }

    private static PlannerGraphClient CreateClient(HttpMessageHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") },
        new StaticTokenProvider());

    private sealed class StaticTokenProvider : IGraphTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult("token");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string> responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody(request), Encoding.UTF8, "application/json")
            });
        }
    }
}
