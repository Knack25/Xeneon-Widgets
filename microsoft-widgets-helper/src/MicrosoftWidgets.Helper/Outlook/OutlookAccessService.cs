using System.Net;
using System.Security.Cryptography;
using System.Text;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Outlook;

public sealed class OutlookAccessService
{
    private sealed record Session(string Hash, OutlookAccountLease Lease, DateTimeOffset ExpiresAt);
    private sealed record Pending(PendingPairing Info, string SecretHash, OutlookAccountLease Lease, bool Approved = false);
    private readonly OutlookAccountState state;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private readonly Dictionary<string, Pending> pending = [];
    private readonly List<Session> sessions = [];
    private readonly Queue<DateTimeOffset> bootstrap = new();
    private readonly Dictionary<string, DateTimeOffset> polls = [];

    public OutlookAccessService(OutlookAccountState state, ILocalJsonStore store, TimeProvider clock)
    {
        this.state = state; this.clock = clock;
        state.Invalidated += () => { lock (sync) { sessions.Clear(); pending.Clear(); polls.Clear(); } };
    }

    public static bool IsLocalHost(HttpRequest request)
    {
        var host = request.Host.Host;
        var local = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
        var port = request.Host.Port ?? (request.Scheme == "https" ? 443 : 80);
        return local && request.Scheme is "http" or "https" && (request.HttpContext.Connection.LocalPort == 0 || port == request.HttpContext.Connection.LocalPort);
    }

    public static bool IsSameOrigin(HttpRequest request)
    {
        if (!IsLocalHost(request)) return false;
        var origin = request.Headers.Origin.ToString();
        if (origin.Length != 0)
            return string.Equals(origin, request.Scheme + "://" + request.Host.Value, StringComparison.OrdinalIgnoreCase);
        return request.Headers["Sec-Fetch-Site"].ToString() == "same-origin";
    }

    public async Task<string> CreateSessionAsync(CancellationToken ct)
    {
        var lease = await state.GetIdentityAsync(false, ct);
        var token = RandomToken();
        lock (sync)
        {
            state.RequireCurrent(lease);
            sessions.RemoveAll(s => s.ExpiresAt <= clock.GetUtcNow());
            if (sessions.Count >= 64) sessions.RemoveAt(0);
            sessions.Add(new(Hash(token), lease, clock.GetUtcNow().AddHours(8)));
        }
        return token;
    }

    public async Task<bool> ValidateSessionAsync(string token, CancellationToken ct)
        => await AuthenticateSessionAsync(token, ct) is not null;

    internal async Task<OutlookAccountLease?> AuthenticateSessionAsync(string token, CancellationToken ct)
    {
        var lease = await state.GetIdentityAsync(false, ct);
        if (string.IsNullOrEmpty(token) || token.Length > 256) return null;
        lock (sync) return state.IsCurrent(lease) && sessions.Any(s => s.Lease == lease && s.ExpiresAt > clock.GetUtcNow() && Matches(s.Hash, token)) ? lease : null;
    }

    public async Task<PairingCreated> CreatePairingAsync(PairingRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.InstanceId) || request.InstanceId.Length > 200 || request.InstanceId.Any(char.IsControl) ||
            string.IsNullOrEmpty(request.RequestSecret) || request.RequestSecret.Length is < 32 or > 256 || request.RequestSecret.Any(char.IsControl))
            throw new OutlookException("invalid_pairing", "An instance identifier and an unguessable request secret are required.", 400);
        var lease = await state.GetAsync(ct);
        lock (sync)
        {
            Prune();
            if (pending.Count >= 32 || bootstrap.Count >= 10) throw new OutlookException("throttled", "Too many pairing requests. Try again in a minute.", 429);
            bootstrap.Enqueue(clock.GetUtcNow());
            var id = RandomToken();
            string code;
            do { code = RandomNumberGenerator.GetInt32(0, 1000000).ToString("D6"); } while (pending.Values.Any(p => p.Info.Code == code));
            var info = new PendingPairing(id, code, request.InstanceId, clock.GetUtcNow().AddMinutes(5));
            state.RequireCurrent(lease);
            pending.Add(id, new(info, Hash(request.RequestSecret), lease));
            return new(id, code, info.ExpiresAt);
        }
    }

    public async Task<PairingResult> PollAsync(string id, string secret, CancellationToken ct)
    {
        var lease = await state.GetAsync(ct);
        await gate.WaitAsync(ct);
        try
        {
            Pending request;
            lock (sync)
            {
                Prune();
                request = RequirePending(id, lease);
                if (string.IsNullOrEmpty(secret) || secret.Length > 256 || !Matches(request.SecretHash, secret)) throw Unauthorized();
                // Pending polling is limited; approval may be collected immediately after setup accepts it.
                if (!request.Approved && polls.TryGetValue(id, out var last) && clock.GetUtcNow() - last < TimeSpan.FromSeconds(1))
                    throw new OutlookException("throttled", "Wait before checking pairing again.", 429);
                polls[id] = clock.GetUtcNow();
                if (!request.Approved) return new("pending");
            }
            var credentials = await ReadCredentialsAsync(ct);
            if (credentials.Length >= 100) throw new OutlookException("pairing_limit", "Revoke an unused widget pairing before adding another.", 400);
            // The requester can recover a lost approval response without storing its secret
            // or a plaintext credential in the helper. Approval alone activates the hash.
            var credential = Hash("outlook-pairing\n" + id + "\n" + secret);
            if (credentials.Any(c => c.AccountKey == lease.Key && Matches(c.Hash, credential))) return new("approved", credential);
            var entry = new StoredOutlookCredential(RandomToken(), request.Info.InstanceId, lease.Key, Hash(credential));
            await state.SaveCredentialsAsync(lease, [.. credentials.Where(c => c.AccountKey == lease.Key && c.InstanceId != entry.InstanceId), entry], ct);
            lock (sync) { state.RequireCurrent(lease); polls.Remove(id); }
            return new("approved", credential);
        }
        finally { gate.Release(); }
    }

    public async Task ApproveAsync(string id, CancellationToken ct)
    {
        var lease = await state.GetAsync(ct);
        lock (sync) { Prune(); var request = RequirePending(id, lease); pending[id] = request with { Approved = true }; }
    }

    public async Task<IReadOnlyList<PendingPairing>> GetPendingAsync(CancellationToken ct)
    {
        var lease = await state.GetIdentityAsync(false, ct);
        lock (sync) { Prune(); return pending.Values.Where(p => p.Lease == lease && !p.Approved).Select(p => p.Info).ToArray(); }
    }

    public async Task<IReadOnlyList<PairedInstance>> GetPairedAsync(CancellationToken ct)
    {
        var lease = await state.GetIdentityAsync(false, ct);
        var credentials = await ReadCredentialsAsync(ct);
        state.RequireCurrent(lease);
        return credentials.Where(c => c.AccountKey == lease.Key).Select(c => new PairedInstance(c.CredentialId, c.InstanceId)).ToArray();
    }

    public async Task RevokeAsync(string id, CancellationToken ct)
    {
        var lease = await state.GetAsync(ct);
        await gate.WaitAsync(ct);
        try
        {
            var credentials = await ReadCredentialsAsync(ct);
            var removed = credentials.FirstOrDefault(c => c.CredentialId == id && c.AccountKey == lease.Key);
            lock (sync)
            {
                if (removed is not null)
                    foreach (var request in pending.Where(p => p.Value.Info.InstanceId == removed.InstanceId).Select(p => p.Key).ToArray()) { pending.Remove(request); polls.Remove(request); }
            }
            await state.SaveCredentialsAsync(lease, credentials.Where(c => c.CredentialId != id).ToArray(), ct);
        }
        finally { gate.Release(); }
    }

    public async Task<bool> ValidateCredentialAsync(string credential, CancellationToken ct)
        => await AuthenticateCredentialAsync(credential, ct) is not null;

    internal async Task<OutlookAccountLease?> AuthenticateCredentialAsync(string credential, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(credential) || credential.Length > 256) return null;
        OutlookAccountLease lease;
        try { lease = await state.GetAsync(ct); }
        catch (OutlookException) { return null; }
        var credentials = await ReadCredentialsAsync(ct);
        return state.IsCurrent(lease) && credentials.Any(c => c.AccountKey == lease.Key && Matches(c.Hash, credential)) ? lease : null;
    }

    private Task<StoredOutlookCredential[]> ReadCredentialsAsync(CancellationToken ct) => state.ReadCredentialsAsync(ct);
    private Pending RequirePending(string id, OutlookAccountLease lease) => pending.TryGetValue(id, out var value) && value.Lease == lease ? value : throw new OutlookException("pairing_expired", "This pairing request expired. Create a new request.", 404);
    private void Prune()
    {
        foreach (var id in pending.Where(p => p.Value.Info.ExpiresAt <= clock.GetUtcNow()).Select(p => p.Key).ToArray()) { pending.Remove(id); polls.Remove(id); }
        while (bootstrap.TryPeek(out var time) && clock.GetUtcNow() - time >= TimeSpan.FromMinutes(1)) bootstrap.Dequeue();
    }
    private static string RandomToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static string Hash(string value) => OutlookTokenProvider.Hash(value);
    private static bool Matches(string hash, string value) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(Hash(value)));
    private static OutlookException Unauthorized() => new("unauthorized", "The Outlook credential is not valid.", 401);
}
