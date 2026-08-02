namespace MowIT.Shared.Telemetry;

public sealed record TelemetryDto
{
    public double Lat { get; init; }
    public double Lon { get; init; }

    public float GpsAccuracyMm { get; init; }
    public int   GpsFixType    { get; init; }

    public float HeadingRad  { get; init; }
    public float PosX        { get; init; }
    public float PosY        { get; init; }
    public float LinearSpeed { get; init; }

    public float AccX  { get; init; }
    public float AccY  { get; init; }
    public float AccZ  { get; init; }
    public float GyroX { get; init; }
    public float GyroY { get; init; }
    public float GyroZ { get; init; }

    public bool IsManualMode  { get; init; }
    public bool IsMotorMoving { get; init; }

    public int  BatteryPct    { get; init; }
    public int  State         { get; init; }
    public bool BladeOn       { get; init; }
    public bool RainDetected  { get; init; }
    public int  UptimeMinutes { get; init; }

    public DateTime TimestampUtc { get; init; }
}
