using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Outlook;

namespace PlannerEdge.Helper.Security;

public sealed class AccountBoundResult(IResult inner, MicrosoftAccountState state, AccountLease lease) : IResult
{
    public async Task ExecuteAsync(HttpContext http)
    {
        try
        {
            using var binding = state.BindRequest(lease);
            await state.ExecuteAuthorizedAsync(lease, () => inner.ExecuteAsync(http), http.RequestAborted);
        }
        catch (OutlookException ex) when (!http.Response.HasStarted)
        {
            await Results.Json(new { error = ex.Error }, statusCode: ex.StatusCode).ExecuteAsync(http);
        }
    }
}
