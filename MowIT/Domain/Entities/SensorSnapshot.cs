using MowIT.Domain.Enums;

namespace MowIT.Domain.Entities;

public record SensorSnapshot
{
    public GpsPoint   Gps           { get; init; }
    public float      GpsAccuracyMm { get; init; }
    public GpsFixType GpsFixType    { get; init; }

public float AccX  { get; init; }
    public float AccY  { get; init; }
    public float AccZ  { get; init; }
    public float GyroX { get; init; }
    public float GyroY { get; init; }
    public float GyroZ { get; init; }

public float PosX        { get; init; }
    public float PosY        { get; init; }
    public float HeadingRad  { get; init; }
    public float LinearSpeed { get; init; }

    public bool IsManualMode  { get; init; }
    public bool IsMotorMoving { get; init; }

    public DateTime Timestamp { get; init; }
}
