using MowIT.Domain.Enums;

namespace MowIT.Domain.Entities;

public record RobotStatus
{
    public RobotState State         { get; init; }
    public int        BatteryPct    { get; init; }
    public bool       BladeOn       { get; init; }
    public bool       RainDetected  { get; init; }
    public int        UptimeMinutes { get; init; }
    public DateTime   Timestamp     { get; init; }
}
