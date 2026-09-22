using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PlannerEdge.Helper.Auth;

namespace PlannerEdge.Helper.Security;

public sealed class LocalAccessService
{
    internal sealed class OwnerAuthorization(AccountLease? lease, DateTimeOffset expiresAt, TimeProvider timeProvider)
    {
        private int revoked;

        internal void Revoke() => Interlocked.Exchange(ref revoked, 1);

        internal void RequireCurrent(AccountLease current)
        {
            if (Volatile.Read(ref revoked) != 0 || expiresAt <= timeProvider.GetUtcNow() || lease is not null && lease != current)
                throw new OwnerAuthorizationException();
        }
    }

    public const int MaximumBootstrapCount = 256;
    public const int MaximumOwnerSessionCount = 256;

    private static readonly TimeSpan BootstrapLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OwnerSessionLifetime = TimeSpan.FromHours(8);
    private readonly object gate = new();
    private readonly Dictionary<long, ExpiringToken> bootstraps = [];
    private readonly Dictionary<long, ExpiringToken> ownerSessions = [];
    private readonly TimeProvider timeProvider;
    private readonly MicrosoftAccountState? accountState;
    private long nextTokenId;

    public LocalAccessService(TimeProvider timeProvider) : this(timeProvider, null) { }

    public LocalAccessService(TimeProvider timeProvider, MicrosoftAccountState? accountState)
    {
        this.timeProvider = timeProvider;
        this.accountState = accountState;
        if (accountState is not null) accountState.Invalidated += InvalidateAll;
    }

    public OwnerBootstrap CreateBootstrap()
    {
        var now = timeProvider.GetUtcNow();
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            var token = ToBase64Url(tokenBytes);
            var hash = SHA256.HashData(tokenBytes);
            var expiresAt = now.Add(BootstrapLifetime);
            lock (gate)
            {
                RemoveExpired(bootstraps, now);
                AddBounded(bootstraps, hash, expiresAt, MaximumBootstrapCount);
            }
            return new OwnerBootstrap(token, expiresAt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
        }
    }

    public string ExchangeBootstrap(string token)
        => ExchangeBootstrap(token, null);

    public async Task<string> ExchangeBootstrapAsync(string token, CancellationToken cancellationToken)
    {
        AccountLease? lease = accountState is null
            ? null
            : await accountState.GetIdentityAsync(requireAccount: false, cancellationToken);
        return ExchangeBootstrap(token, lease);
    }

    private string ExchangeBootstrap(string token, AccountLease? lease)
    {
        if (!TryGetHash(token, out var hash)) throw new LocalAccessException("The local access bootstrap is invalid.");
        try
        {
            var now = timeProvider.GetUtcNow();
            lock (gate)
            {
                RemoveExpired(bootstraps, now);
                var match = FindMatch(bootstraps, hash);
                if (match is null) throw new LocalAccessException("The local access bootstrap is invalid or expired.");

                bootstraps.Remove(match.Value);
                return IssueOwnerSession(now, lease);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    internal string IssueReplacementOwnerSession(AccountLease lease)
    {
        lock (gate)
        {
            var now = timeProvider.GetUtcNow();
            RemoveExpired(ownerSessions, now);
            return IssueOwnerSession(now, lease);
        }
    }

    private string IssueOwnerSession(DateTimeOffset now, AccountLease? lease)
    {
        var sessionBytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            var session = ToBase64Url(sessionBytes);
            var expiresAt = now.Add(OwnerSessionLifetime);
            AddBounded(ownerSessions, SHA256.HashData(sessionBytes), expiresAt,
                MaximumOwnerSessionCount, lease, new OwnerAuthorization(lease, expiresAt, timeProvider));
            return session;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionBytes);
        }
    }

    public bool ValidateOwnerSession(string token)
        => AuthorizeOwnerSession(token, null) is not null;

    public async Task<bool> ValidateOwnerSessionAsync(string token, CancellationToken cancellationToken)
        => await AuthorizeOwnerSessionAsync(token, cancellationToken) is not null;

    internal async Task<OwnerAuthorization?> AuthorizeOwnerSessionAsync(string token, CancellationToken cancellationToken)
    {
        AccountLease? lease = accountState is null
            ? null
            : await accountState.GetIdentityAsync(requireAccount: false, cancellationToken);
        return AuthorizeOwnerSession(token, lease);
    }

    private OwnerAuthorization? AuthorizeOwnerSession(string token, AccountLease? lease)
    {
        if (!TryGetHash(token, out var hash)) return null;
        try
        {
            lock (gate)
            {
                RemoveExpired(ownerSessions, timeProvider.GetUtcNow());
                var match = FindMatch(ownerSessions, hash);
                if (match is null) return null;
                var session = ownerSessions[match.Value];
                return session.Lease is null || session.Lease == lease ? session.Authorization : null;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    public void InvalidateOwnerSessions()
    {
        lock (gate)
        {
            Revoke(ownerSessions.Values);
            ownerSessions.Clear();
        }
    }

    private void InvalidateAll()
    {
        lock (gate)
        {
            Revoke(bootstraps.Values);
            Revoke(ownerSessions.Values);
            bootstraps.Clear();
            ownerSessions.Clear();
        }
    }

    private void AddBounded(Dictionary<long, ExpiringToken> tokens, byte[] hash, DateTimeOffset expiresAt, int maximumCount,
        AccountLease? lease = null, OwnerAuthorization? authorization = null)
    {
        while (tokens.Count >= maximumCount)
        {
            var id = tokens.Keys.Min();
            tokens[id].Authorization?.Revoke();
            tokens.Remove(id);
        }
        tokens[++nextTokenId] = new ExpiringToken(hash, expiresAt, lease, authorization);
    }

    private static long? FindMatch(Dictionary<long, ExpiringToken> tokens, byte[] hash)
    {
        long? match = null;
        foreach (var (id, stored) in tokens)
        {
            if (CryptographicOperations.FixedTimeEquals(stored.Hash, hash)) match = id;
        }
        return match;
    }

    private static void RemoveExpired(Dictionary<long, ExpiringToken> tokens, DateTimeOffset now)
    {
        foreach (var id in tokens.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
        {
            tokens[id].Authorization?.Revoke();
            tokens.Remove(id);
        }
    }

    private static void Revoke(IEnumerable<ExpiringToken> tokens)
    {
        foreach (var token in tokens) token.Authorization?.Revoke();
    }

    private static bool TryGetHash(string token, out byte[] hash)
    {
        hash = [];
        if (string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            var bytes = FromBase64Url(token);
            try
            {
                if (bytes.Length != 32) return false;
                hash = SHA256.HashData(bytes);
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string ToBase64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string token)
    {
        var padded = token.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }

    private sealed record ExpiringToken(byte[] Hash, DateTimeOffset ExpiresAt, AccountLease? Lease,
        OwnerAuthorization? Authorization);
}

internal sealed class OwnerAuthorizationException : Exception { }

public static class LocalAccessEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapLocalAccess(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/local-access/session", async (HttpContext context, LocalAccessService access, CancellationToken ct) =>
        {
            try
            {
                var session = await access.ExchangeBootstrapAsync(
                    context.Request.Headers[LocalAccessHeaders.Bootstrap].ToString(), ct);
                return Results.Ok(new OwnerSessionResponse(session));
            }
            catch (LocalAccessException)
            {
                return Results.Unauthorized();
            }
        });
        return endpoints;
    }
}
