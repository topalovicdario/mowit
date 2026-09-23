namespace MowIT.RobotSimulator;

internal static class LoopbackUrl
{
    public static string Normalize(string baseUrl, out bool rewritten)
    {
        rewritten = false;

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return baseUrl;
        if (!uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return baseUrl;

        rewritten = true;
        return new UriBuilder(uri) { Host = "127.0.0.1" }.Uri.ToString();
    }
}
