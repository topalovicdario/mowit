namespace MowIT.Infrastructure.Ble;

public static class BleGattProfile
{
    public static readonly Guid ServiceUuid      = Guid.Parse("00001234-0000-1000-8000-00805f9b34fb");

public static readonly Guid GpsDataUuid      = Guid.Parse("00001235-0000-1000-8000-00805f9b34fb");
    public static readonly Guid ImuDataUuid      = Guid.Parse("00001236-0000-1000-8000-00805f9b34fb");
    public static readonly Guid OdometryUuid     = Guid.Parse("00001237-0000-1000-8000-00805f9b34fb");
    public static readonly Guid RobotStatusUuid  = Guid.Parse("00001238-0000-1000-8000-00805f9b34fb");

public static readonly Guid MotorCommandUuid = Guid.Parse("00001240-0000-1000-8000-00805f9b34fb");
    public static readonly Guid ActionCommandUuid = Guid.Parse("00001241-0000-1000-8000-00805f9b34fb");
    public static readonly Guid BoundaryChunkUuid = Guid.Parse("00001242-0000-1000-8000-00805f9b34fb");
    public static readonly Guid ScheduleDataUuid  = Guid.Parse("00001243-0000-1000-8000-00805f9b34fb");
}
