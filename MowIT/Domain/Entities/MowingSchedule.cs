namespace MowIT.Domain.Entities;

public class MowingSchedule : ICloneable
{
    public int         Id              { get; set; }
    public DayOfWeek[] ActiveDays      { get; set; } = Array.Empty<DayOfWeek>();
    public TimeSpan    StartTime       { get; set; }
    public int         DurationMinutes { get; set; }
    public bool        IsActive        { get; set; }
    public DateTime    LastExecuted    { get; set; }
    public string      ZoneName        { get; set; } = string.Empty;
    public int?        ZoneId          { get; set; }

    public string DaysLabel  => ActiveDays.Length == 0
        ? "No days selected"
        : string.Join(", ", ActiveDays.Select(d => d.ToString()[..3]));

    public string ZoneLabel  => string.IsNullOrEmpty(ZoneName) ? "No zone linked" : ZoneName;

    public MowingSchedule Clone() => new()
    {
        ActiveDays      = (DayOfWeek[])ActiveDays.Clone(),
        StartTime       = StartTime,
        DurationMinutes = DurationMinutes,
        IsActive        = IsActive,
        ZoneName        = ZoneName,
        ZoneId          = ZoneId
    };

    object ICloneable.Clone() => Clone();
}
