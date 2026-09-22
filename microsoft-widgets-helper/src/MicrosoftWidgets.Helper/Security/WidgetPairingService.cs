using PlannerEdge.Helper.Outlook;
using System.Security.Cryptography;
using System.Text;

namespace PlannerEdge.Helper.Security;

public sealed class WidgetPairingService
{
    private sealed record Pending(WidgetPendingPairing Info, string SecretHash, OutlookAccountLease Lease, bool Approved = false);
    private readonly OutlookAccountState state;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private readonly Dictionary<string, Pending> pending = [];
    private readonly Dictionary<string, DateTimeOffset> polls = [];
    private readonly Queue<DateTimeOffset> bootstrap = new();

    public WidgetPairingService(OutlookAccountState state, TimeProvider clock)
    {
        this.state = state;
        this.clock = clock;
        state.Invalidated += () => { lock (sync) { pending.Clear(); polls.Clear(); } };
    }

    public async Task<PairingCreated> CreateAsync(WidgetScope scope, PairingRequest request, OutlookAccountLease lease, CancellationToken ct)
    {
        if (!Enum.IsDefined(scope) || string.IsNullOrWhiteSpace(request.InstanceId) || request.InstanceId.Length > 200 || request.InstanceId.Any(char.IsControl) ||
            string.IsNullOrEmpty(request.RequestSecret) || request.RequestSecret.Length is < 32 or > 256 || request.RequestSecret.Any(char.IsControl))
            throw new OutlookException("invalid_pairing", "A widget scope, instance identifier and unguessable request secret are required.", 400);
        PairingCreated result = null!;
        await state.ExecuteAuthorizedAsync(lease, () =>
        {
            lock (sync)
            {
                Prune();
                if (pending.Count >= 32 || bootstrap.Count >= 10) throw new OutlookException("throttled", "Too many pairing requests. Try again in a minute.", 429);
                bootstrap.Enqueue(clock.GetUtcNow());
                var id = RandomToken();
                string code;
                do { code = RandomNumberGenerator.GetInt32(0, 1000000).ToString("D6"); } while (pending.Values.Any(p => p.Info.Code == code));
                var info = new WidgetPendingPairing(id, code, request.InstanceId, clock.GetUtcNow().AddMinutes(5), scope);
                pending.Add(id, new(info, Hash(request.RequestSecret), lease));
                result = new(id, code, info.ExpiresAt);
            }
            return Task.CompletedTask;
        }, ct);
        return result;
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
                state.RequireCurrent(lease);
                request = RequirePending(id, lease);
                if (string.IsNullOrEmpty(secret) || secret.Length > 256 || !Matches(request.SecretHash, secret))
                    throw new OutlookException("unauthorized", "The pairing secret is not valid.", 401);
                // Approval can be collected immediately, even after a recent pending poll.
                if (!request.Approved && polls.TryGetValue(id, out var last) && clock.GetUtcNow() - last < TimeSpan.FromSeconds(1))
                    throw new OutlookException("throttled", "Wait before checking pairing again.", 429);
                polls[id] = clock.GetUtcNow();
                if (!request.Approved) return new("pending");
            }
            var credentials = await state.ReadWidgetCredentialsAsync(lease, ct);
            var credential = Hash("widget-pairing-v1\n" + request.Info.Scope + "\n" + id + "\n" + secret);
            if (credentials.Any(c => c.AccountKey == lease.Key && c.Scope == request.Info.Scope && Matches(c.Hash, credential)))
                return new("approved", credential);
            var retained = credentials.Where(c => !(c.AccountKey == lease.Key && c.Scope == request.Info.Scope && c.InstanceId == request.Info.InstanceId)).ToArray();
            if (retained.Length >= 100) throw new OutlookException("pairing_limit", "Revoke an unused widget pairing before adding another.", 400);
            var entry = new StoredWidgetCredential(RandomToken(), request.Info.Scope, request.Info.InstanceId, lease.Key, Hash(credential), clock.GetUtcNow());
            await state.SaveWidgetCredentialsAsync(lease, [.. retained, entry], ct);
            lock (sync)
            {
                state.RequireCurrent(lease);
                // A replaced request must not restore its old credential by polling again.
                RemoveRequests(p => p.Info.Id != id && p.Lease == lease && p.Info.Scope == entry.Scope && p.Info.InstanceId == entry.InstanceId);
                polls.Remove(id);
            }
            return new("approved", credential);
        }
        finally { gate.Release(); }
    }

    public async Task ApproveAsync(string id, CancellationToken ct)
    {
        var lease = await state.GetAsync(ct);
        await state.ExecuteAuthorizedAsync(lease, () =>
        {
            lock (sync) { Prune(); var request = RequirePending(id, lease); pending[id] = request with { Approved = true }; }
            return Task.CompletedTask;
        }, ct);
    }

    public async Task<IReadOnlyList<WidgetPendingPairing>> GetPendingAsync(CancellationToken ct)
    {
        var lease = await state.GetIdentityAsync(false, ct);
        lock (sync) { state.RequireCurrent(lease); Prune(); return pending.Values.Where(p => p.Lease == lease && !p.Approved).Select(p => p.Info).ToArray(); }
    }

    public async Task<IReadOnlyList<WidgetPairedInstance>> GetPairedAsync(CancellationToken ct)
    {
        var lease = await state.GetIdentityAsync(false, ct);
        var credentials = await state.ReadWidgetCredentialsAsync(lease, ct);
        return credentials.Where(c => c.AccountKey == lease.Key).Select(c => new WidgetPairedInstance(c.CredentialId, c.InstanceId, c.Scope)).ToArray();
    }

    public async Task RevokeAsync(string id, CancellationToken ct)
    {
        var lease = await state.GetAsync(ct);
        await gate.WaitAsync(ct);
        try
        {
            var credentials = await state.ReadWidgetCredentialsAsync(lease, ct);
            var removed = credentials.FirstOrDefault(c => c.CredentialId == id && c.AccountKey == lease.Key);
            if (removed is null) return;
            await state.SaveWidgetCredentialsAsync(lease, credentials.Where(c => c != removed).ToArray(), ct);
            lock (sync)
            {
                state.RequireCurrent(lease);
                RemoveRequests(p => p.Lease == lease && p.Info.Scope == removed.Scope && p.Info.InstanceId == removed.InstanceId);
            }
        }
        finally { gate.Release(); }
    }

    public async Task<OutlookAccountLease?> AuthenticateAsync(WidgetScope scope, string credential, CancellationToken ct)
    {
        if (!Enum.IsDefined(scope) || string.IsNullOrEmpty(credential) || credential.Length > 256) return null;
        try
        {
            var lease = await state.GetAsync(ct);
            var credentials = await state.ReadWidgetCredentialsAsync(lease, ct);
            return state.IsCurrent(lease) && credentials.Any(c => c.AccountKey == lease.Key && c.Scope == scope && Matches(c.Hash, credential)) ? lease : null;
        }
        catch (OutlookException) { return null; }
    }

    private Pending RequirePending(string id, OutlookAccountLease lease) => pending.TryGetValue(id, out var value) && value.Lease == lease
        ? value : throw new OutlookException("pairing_expired", "This pairing request expired. Create a new request.", 404);
    private void RemoveRequests(Func<Pending, bool> predicate)
    {
        foreach (var id in pending.Where(p => predicate(p.Value)).Select(p => p.Key).ToArray()) { pending.Remove(id); polls.Remove(id); }
    }
    private void Prune()
    {
        RemoveRequests(p => p.Info.ExpiresAt <= clock.GetUtcNow());
        while (bootstrap.TryPeek(out var time) && clock.GetUtcNow() - time >= TimeSpan.FromMinutes(1)) bootstrap.Dequeue();
    }
    private static string RandomToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool Matches(string hash, string value) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(Hash(value)));
}
