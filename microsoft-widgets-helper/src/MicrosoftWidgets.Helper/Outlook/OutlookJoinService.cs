using PlannerEdge.Helper.Auth;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace PlannerEdge.Helper.Outlook;

public interface IOutlookMeetingLauncher
{
    Task OpenAsync(Uri uri, CancellationToken ct);
}

public sealed class OutlookMeetingLauncher : IOutlookMeetingLauncher
{
    public Task OpenAsync(Uri uri, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}

public sealed class OutlookJoinService(EventDetailsService details, MicrosoftAccountState state, IOutlookMeetingLauncher launcher, TimeProvider clock)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> launched = [];

    public async Task JoinAsync(EventRequest request, CancellationToken ct)
    {
        var lease = await state.GetAsync(ct);
        await gate.WaitAsync(ct);
        try
        {
            var key = lease.Key + ":" + lease.Generation + ":" + request.CalendarKey + ":" + request.Reference;
            foreach (var old in launched.Where(p => clock.GetUtcNow() - p.Value >= TimeSpan.FromSeconds(3)).Select(p => p.Key).ToArray()) launched.Remove(old);
            if (launched.ContainsKey(key)) throw new OutlookException("join_debounced", "The meeting was just opened. Wait before trying again.", 429);
            var item = await details.GetAsync(request, ct);
            if (!IsSafeUrl(item.JoinUrl)) throw new OutlookException("invalid_meeting_url", "This event has no supported HTTPS meeting link.", 400);
            state.RequireCurrent(lease);
            try { await state.LaunchAsync(lease, () => launcher.OpenAsync(new Uri(item.JoinUrl!), ct), ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException)
            { throw new OutlookException("launch_failed", "Windows could not open the meeting link."); }
            launched[key] = clock.GetUtcNow();
        }
        finally { gate.Release(); }
    }

    public static bool IsSafeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(char.IsControl) || value.Contains('\\')) return false;
        var decoded = Uri.UnescapeDataString(value);
        if (decoded.Any(char.IsControl) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.HostNameType == UriHostNameType.Unknown) return false;
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host == "localhost" || host.EndsWith(".localhost") || host.EndsWith(".local") || host.EndsWith(".internal") || !host.Contains('.') && uri.HostNameType != UriHostNameType.IPv6) return false;
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] is not (0 or 10 or 127) && b[0] < 224 && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31) && !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] is >= 64 and <= 127);
        return !address.Equals(IPAddress.IPv6Any) && !address.IsIPv6LinkLocal && !address.IsIPv6Multicast && !address.IsIPv6SiteLocal && (b[0] & 0xFE) != 0xFC && b[0] is >= 0x20 and <= 0x3F;
    }
}
