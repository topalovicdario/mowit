namespace MowIT.Shared.Telemetry;

public sealed record RobotEventDto
{
    public long     Seq        { get; init; }
    public string   Type       { get; init; } = string.Empty;
    public int      XCm        { get; init; }
    public int      YCm        { get; init; }
    public bool     Success    { get; init; }
    public string?  Reason     { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
}

public static class RobotEventTypes
{
    public const string Connected             = "Connected";
    public const string BaseCaptured          = "BaseCaptured";
    public const string BoundaryPointCaptured = "BoundaryPointCaptured";
    public const string OutlineCaptured       = "OutlineCaptured";
    public const string ExitCaptured          = "ExitCaptured";
    public const string CaptureEnd            = "CaptureEnd";
    public const string BoundaryCleared       = "BoundaryCleared";
    public const string Error                 = "Error";
}

public sealed record EventListResponse
{
    public RobotEventDto[] Events { get; init; } = Array.Empty<RobotEventDto>();
    public long            Cursor { get; init; }
}
