using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Outlook;

namespace PlannerEdge.Helper.Security;

public sealed class AccountBoundResult(IResult inner, MicrosoftAccountState state, AccountLease lease) : IResult
{
    internal const int MaximumBufferedBytes = 4 * 1024 * 1024;

    public async Task ExecuteAsync(HttpContext http)
    {
        try
        {
            using var binding = state.BindRequest(lease);
            var response = await state.ExecuteAuthorizedAsync(lease,
                () => inner is IBufferedHttpResult buffered
                    ? buffered.PrepareAsync(http)
                    : BufferedHttpResponse.CreateAsync(inner, http, MaximumBufferedBytes, http.RequestAborted),
                http.RequestAborted);
            await response.CopyToAsync(http);
        }
        catch (OutlookException ex) when (!http.Response.HasStarted)
        {
            await Results.Json(new { error = ex.Error }, statusCode: ex.StatusCode).ExecuteAsync(http);
        }
    }
}
