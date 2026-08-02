namespace MowIT.Shared.Schedules;

public sealed record ScheduleDto
{
    public int       Id              { get; init; }
    public int[]     ActiveDays      { get; init; } = Array.Empty<int>();
    public long      StartTimeTicks  { get; init; }
    public int       DurationMinutes { get; init; }
    public bool      IsActive        { get; init; }
    public string    ZoneName        { get; init; } = string.Empty;
    public int?      ZoneId          { get; init; }
    public DateTime  LastExecutedUtc { get; init; }
}
