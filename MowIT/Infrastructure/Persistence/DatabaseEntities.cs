using SQLite;

namespace MowIT.Infrastructure.Persistence;

[Table("BoundaryZones")]
public class BoundaryZoneEntity
{
    [PrimaryKey, AutoIncrement]
    public int      Id        { get; set; }
    public string   Name      { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

[Table("BoundaryPoints")]
public class BoundaryPointEntity
{
    [PrimaryKey, AutoIncrement]
    public int    Id        { get; set; }
    public int    ZoneId    { get; set; }
    public int    Order     { get; set; }
    public double Latitude  { get; set; }
    public double Longitude { get; set; }
}

[Table("MowingSchedules")]
public class MowingScheduleEntity
{
    [PrimaryKey, AutoIncrement]
    public int    Id              { get; set; }
    public string ActiveDaysJson  { get; set; } = "[]";
    public long   StartTimeTicks  { get; set; }
    public int    DurationMinutes { get; set; }
    public bool   IsActive        { get; set; }
    public string ZoneName        { get; set; } = string.Empty;
    public DateTime LastExecuted  { get; set; }
    public int    ZoneId          { get; set; }
}
