namespace MowIT.Shared.Telemetry;

public sealed record RobotCommandDto
{
    public Guid   Id         { get; init; } = Guid.NewGuid();
    public string Kind       { get; init; } = CommandKinds.Action;
    public string? ActionName { get; init; }
    public int    ActionCode { get; init; }
    public int    Param      { get; init; }
    public float  LinearVel  { get; init; }
    public float  AngularVel { get; init; }
    public BoundaryUploadDto? Boundary { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
}

public static class CommandKinds
{
    public const string Action   = "Action";
    public const string Motor    = "Motor";
    public const string Boundary = "Boundary";
}

public sealed record CommandListResponse
{
    public RobotCommandDto[] Commands { get; init; } = Array.Empty<RobotCommandDto>();
}

public sealed record BoundaryUploadDto
{
    public string         Name   { get; init; } = string.Empty;
    public GpsPointDto[]  Points { get; init; } = Array.Empty<GpsPointDto>();
}

public sealed record GpsPointDto
{
    public double Lat { get; init; }
    public double Lon { get; init; }
}
