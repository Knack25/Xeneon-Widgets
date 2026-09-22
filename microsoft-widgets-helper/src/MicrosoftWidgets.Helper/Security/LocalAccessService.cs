using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace PlannerEdge.Helper.Security;

public sealed class LocalAccessService(TimeProvider timeProvider)
{
    public const int MaximumBootstrapCount = 256;
    public const int MaximumOwnerSessionCount = 256;

    private static readonly TimeSpan BootstrapLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OwnerSessionLifetime = TimeSpan.FromHours(8);
    private readonly object gate = new();
    private readonly Dictionary<long, ExpiringToken> bootstraps = [];
    private readonly Dictionary<long, ExpiringToken> ownerSessions = [];
    private long nextTokenId;

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
                var sessionBytes = RandomNumberGenerator.GetBytes(32);
                try
                {
                    var session = ToBase64Url(sessionBytes);
                    AddBounded(ownerSessions, SHA256.HashData(sessionBytes), now.Add(OwnerSessionLifetime), MaximumOwnerSessionCount);
                    return session;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(sessionBytes);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    public bool ValidateOwnerSession(string token)
    {
        if (!TryGetHash(token, out var hash)) return false;
        try
        {
            lock (gate)
            {
                RemoveExpired(ownerSessions, timeProvider.GetUtcNow());
                return FindMatch(ownerSessions, hash) is not null;
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
            ownerSessions.Clear();
        }
    }

    private void AddBounded(Dictionary<long, ExpiringToken> tokens, byte[] hash, DateTimeOffset expiresAt, int maximumCount)
    {
        while (tokens.Count >= maximumCount) tokens.Remove(tokens.Keys.Min());
        tokens[++nextTokenId] = new ExpiringToken(hash, expiresAt);
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
            tokens.Remove(id);
        }
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

    private sealed record ExpiringToken(byte[] Hash, DateTimeOffset ExpiresAt);
}

public static class LocalAccessEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapLocalAccess(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/local-access/session", (HttpContext context, LocalAccessService access) =>
        {
            try
            {
                var session = access.ExchangeBootstrap(context.Request.Headers[LocalAccessHeaders.Bootstrap].ToString());
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
