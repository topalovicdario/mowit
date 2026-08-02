namespace MowIT.Infrastructure.Wifi;

public sealed class WifiRobotOptions
{
    public const int ServerPort = 5080;

    public string RobotId { get; init; } = "demo-robot-01";

    public string BearerToken { get; init; } = "dev-token-please-replace-in-prod";

    public string DisplayName { get; init; } = "Cloud Robot (WiFi)";

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public int MaxConsecutiveFailures { get; init; } = 6;

    public IReadOnlyList<string> CandidateBaseUrls { get; init; } = DefaultCandidates();

    public string BaseUrl => CandidateBaseUrls[0];

    private static IReadOnlyList<string> DefaultCandidates()
    {
#if ANDROID
        return new[]
        {
            $"http://10.0.2.2:{ServerPort}",
            $"http://192.168.56.1:{ServerPort}",
            $"http://172.20.10.6:{ServerPort}",
        };
#else
        return new[] { $"http://localhost:{ServerPort}" };
#endif
    }
}
