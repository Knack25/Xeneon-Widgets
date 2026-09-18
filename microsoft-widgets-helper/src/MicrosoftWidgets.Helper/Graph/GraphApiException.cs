using System.Net;

namespace PlannerEdge.Helper.Graph;

public sealed class GraphApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
