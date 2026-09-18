using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PlannerEdge.Helper.Updates;

public sealed record UpdateRelease(string Version, string Url, long Size, string Sha256, string Notes);

public interface IReleaseClient
{
    Task<UpdateRelease?> CheckAsync(string currentVersion, CancellationToken ct);
    Task DownloadAsync(UpdateRelease release, string path, CancellationToken ct);
}

public sealed class ReleaseClient(HttpClient http) : IReleaseClient
{
    public const string Repository = "https://github.com/Knack25/Xeneon-Widgets";
    private const long MaxInstallerBytes = 200 * 1024 * 1024;

    public async Task<UpdateRelease?> CheckAsync(string currentVersion, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        ct = timeout.Token;
        using var response = await http.GetAsync("https://api.github.com/repos/Knack25/Xeneon-Widgets/releases/latest", HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(1024 * 1024, ct);
        return Parse(await response.Content.ReadAsStringAsync(ct), currentVersion);
    }

    public static UpdateRelease? Parse(string json, string currentVersion)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$") ||
            !Version.TryParse(tag[1..], out var latest)) throw new InvalidDataException("The release version is invalid.");
        if (latest <= Version.Parse(currentVersion)) return null;
        var name = $"MicrosoftWidgetsSetup-{tag[1..]}.exe";
        var matches = root.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("The release installer is not ready.");
        var asset = matches[0];
        var url = asset.GetProperty("browser_download_url").GetString();
        if (url != $"{Repository}/releases/download/{tag}/{name}") throw new InvalidDataException("The installer source is invalid.");
        var digest = asset.TryGetProperty("digest", out var value) ? value.GetString() ?? "" : "";
        if (!Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$")) throw new InvalidDataException("The release has no verified download checksum.");
        var size = asset.GetProperty("size").GetInt64();
        if (size <= 0 || size > MaxInstallerBytes) throw new InvalidDataException("The installer size is invalid.");
        return new(tag[1..], url, size, digest[7..], root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "");
    }

    public async Task DownloadAsync(UpdateRelease release, string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        ct = timeout.Token;
        try
        {
            var url = new Uri(release.Url);
            for (var redirects = 0; redirects < 5; redirects++)
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    var next = response.Headers.Location ?? throw new InvalidDataException("Missing download redirect.");
                    url = next.IsAbsoluteUri ? next : new Uri(url, next);
                    if (url.Scheme != "https" || !url.IsDefaultPort || !string.IsNullOrEmpty(url.UserInfo) ||
                        url.Host != "release-assets.githubusercontent.com") throw new InvalidDataException("Unexpected download destination.");
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long length && length != release.Size)
                    throw new InvalidDataException("The installer size does not match the release.");
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    long total = 0;
                    int count;
                    while ((count = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        total += count;
                        if (total > release.Size || total > MaxInstallerBytes) throw new InvalidDataException("The installer is larger than expected.");
                        hash.AppendData(buffer, 0, count);
                        await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    }
                    if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Installer verification failed. Nothing was installed.");
                }
                return;
            }
            throw new InvalidDataException("Too many download redirects.");
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }
}
