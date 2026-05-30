using MowIT.Domain.Entities;
using MowIT.Domain.Enums;

namespace MowIT.Infrastructure.Ble;

public static class BlePacketSerializer
{

public static SensorSnapshot DeserializeGps(byte[] data)
    {
        double lat = BitConverter.ToDouble(data, 0);
        double lon = BitConverter.ToDouble(data, 8);
        float  acc = BitConverter.ToSingle(data, 16);
        var    fix = (GpsFixType)data[20];
        return new SensorSnapshot
        {
            Gps           = new GpsPoint(lat, lon),
            GpsAccuracyMm = acc,
            GpsFixType    = fix,
            Timestamp     = DateTime.UtcNow
        };
    }

public static SensorSnapshot MergeImu(SensorSnapshot existing, byte[] data) => existing with
    {
        AccX  = BitConverter.ToSingle(data, 0),
        AccY  = BitConverter.ToSingle(data, 4),
        AccZ  = BitConverter.ToSingle(data, 8),
        GyroX = BitConverter.ToSingle(data, 12),
        GyroY = BitConverter.ToSingle(data, 16),
        GyroZ = BitConverter.ToSingle(data, 20)
    };

public static SensorSnapshot MergeOdometry(SensorSnapshot existing, byte[] data) => existing with
    {
        PosX        = BitConverter.ToSingle(data, 0),
        PosY        = BitConverter.ToSingle(data, 4),
        HeadingRad  = BitConverter.ToSingle(data, 8),
        LinearSpeed = BitConverter.ToSingle(data, 12)
    };

public static RobotStatus DeserializeStatus(byte[] data) => new()
    {
        State         = (Domain.Enums.RobotState)data[0],
        BatteryPct    = data[1],
        BladeOn       = data[2] != 0,
        RainDetected  = data[3] != 0,
        UptimeMinutes = BitConverter.ToUInt16(data, 4),
        Timestamp     = DateTime.UtcNow
    };

public static byte[] SerializeMotorCommand(float linearVel, float angularVel)
    {
        var buf = new byte[8];
        BitConverter.GetBytes(linearVel) .CopyTo(buf, 0);
        BitConverter.GetBytes(angularVel).CopyTo(buf, 4);
        return buf;
    }

public static byte[] SerializeActionCommand(Domain.Enums.RobotAction action, byte param = 0)
        => new[] { (byte)action, param };

public static byte[] SerializeBoundaryChunk(byte index, byte total, byte pointType, GpsPoint point)
    {
        var buf = new byte[20];
        buf[0] = index;
        buf[1] = total;
        buf[2] = pointType;
        buf[3] = 0;
        BitConverter.GetBytes(point.Latitude) .CopyTo(buf, 4);
        BitConverter.GetBytes(point.Longitude).CopyTo(buf, 12);
        return buf;
    }

public static byte[] SerializeSchedule(MowingSchedule s)
    {
        byte daysMask = 0;
        foreach (var d in s.ActiveDays)
            daysMask |= (byte)(1 << ((int)d + 6) % 7); 

        return new byte[]
        {
            daysMask,
            (byte)s.StartTime.Hours,
            (byte)s.StartTime.Minutes,
            (byte)(s.DurationMinutes >> 8),
            (byte)(s.DurationMinutes & 0xFF),
            s.IsActive ? (byte)1 : (byte)0,
            0, 0, 0, 0
        };
    }
}
