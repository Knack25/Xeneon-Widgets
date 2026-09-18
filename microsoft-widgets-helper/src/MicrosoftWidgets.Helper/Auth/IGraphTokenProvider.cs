namespace PlannerEdge.Helper.Graph;

public interface IGraphTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);

    Task<string> GetBasicUserTokenAsync(CancellationToken cancellationToken) => GetAccessTokenAsync(cancellationToken);
    Task<string> GetGroupMemberTokenAsync(CancellationToken cancellationToken) => GetAccessTokenAsync(cancellationToken);
}
