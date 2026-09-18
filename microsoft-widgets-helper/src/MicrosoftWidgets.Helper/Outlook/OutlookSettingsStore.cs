using System.Net.Mail;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Outlook;

public sealed class OutlookSettingsStore(ILocalJsonStore store)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private static string Name(string account) => "outlook-sources-" + OutlookTokenProvider.Hash(account);
    public async Task<string[]> GetOwnersAsync(string account, CancellationToken ct) => await store.ReadAsync<string[]>(Name(account), ct) ?? [];

    public async Task SetOwnerAsync(string account, string owner, bool add, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var owners = (await GetOwnersAsync(account, ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (add)
            {
                if (owners.Count >= 100) throw new OutlookException("source_limit", "Remove a shared calendar before adding another.", 400);
                owners.Add(owner);
            }
            else owners.Remove(owner);
            await store.WriteAsync(Name(account), owners.Order().ToArray(), ct);
        }
        finally { gate.Release(); }
    }

    public static string NormalizeOwner(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 254 || value.Any(char.IsControl) || !MailAddress.TryCreate(value, out var mail) || mail.Address != value || !value.Contains('@'))
            throw new OutlookException("invalid_owner", "Enter a valid owner's email address.", 400);
        return value.ToLowerInvariant();
    }
}
