namespace PlannerEdge.Helper.Hosting;

public sealed class HelperAddress
{
    public const int DefaultPort = 8787;

    public HelperAddress(int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        Port = port;
        BaseUri = new Uri($"http://localhost:{port}/");
    }

    public int Port { get; }
    public Uri BaseUri { get; }
    public Uri HealthUri => new(BaseUri, "health");
    public string RecoveryMessage =>
        $"Unable to complete the action. Open {BaseUri.GetLeftPart(UriPartial.Authority)} in your browser.";
}
