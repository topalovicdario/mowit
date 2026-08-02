namespace MowIT.Shared.Telemetry;

public sealed record TelemetryEnvelope
{
    public string       RobotId    { get; init; } = string.Empty;
    public bool         IsOnline   { get; init; }
    public DateTime     LastSeenUtc { get; init; }
    public TelemetryDto? Latest    { get; init; }
}

public sealed record ActiveRobotsResponse
{
    public TelemetryEnvelope[] Robots { get; init; } = Array.Empty<TelemetryEnvelope>();
}
