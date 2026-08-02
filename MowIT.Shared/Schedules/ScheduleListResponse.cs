namespace MowIT.Shared.Schedules;

public sealed record ScheduleListResponse
{
    public string         RobotId    { get; init; } = string.Empty;
    public long           Version    { get; init; }
    public DateTime       UpdatedUtc { get; init; }
    public ScheduleDto[]  Schedules  { get; init; } = Array.Empty<ScheduleDto>();
}

public sealed record ScheduleVersionResponse
{
    public string   RobotId    { get; init; } = string.Empty;
    public long     Version    { get; init; }
    public DateTime UpdatedUtc { get; init; }
}

public sealed record ScheduleUploadRequest
{
    public ScheduleDto[] Schedules { get; init; } = Array.Empty<ScheduleDto>();
}
