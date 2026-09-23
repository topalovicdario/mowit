using MowIT.Domain.Entities;
using MowIT.Domain.Enums;
using MowIT.Infrastructure.Ble;

namespace MowIT.Benchmarks;

public sealed record BlePacketResult(
    string PacketType, int DeclaredBytes, int MeasuredBytes, int Samples,
    bool RoundtripOk, double MaxAbsError);

public sealed record BleFinding(string Check, string Result, string Detail);

public sealed record BleDayMaskResult(
    string DayName, int DotNetValue, int ExpectedBit, int ActualBit, bool Match);

public static class BlePacketMeter
{
    public const int Seed    = 20260101;
    public const int Samples = 10_000;

    private const double FloatRelTolerance = 1e-6;

    private const int GpsBytes      = 21;
    private const int ImuBytes      = 24;
    private const int OdometryBytes = 16;
    private const int StatusBytes   = 6;
    private const int MotorBytes    = 8;
    private const int ActionBytes   = 2;
    private const int BoundaryBytes = 22;
    private const int ScheduleBytes = 10;

    public static IReadOnlyList<BlePacketResult> Packets()
    {
        var rng = new Random(Seed);
        return
        [
            MeasureGps(rng),
            MeasureImu(rng),
            MeasureOdometry(rng),
            MeasureStatus(rng),
            MeasureMotor(rng),
            MeasureAction(rng),
            MeasureBoundary(rng),
            MeasureSchedule(rng)
        ];
    }

    private static void PutU16(byte[] b, int at, ushort v)
    {
        b[at]     = (byte)(v & 0xFF);
        b[at + 1] = (byte)((v >> 8) & 0xFF);
    }

    private static void PutU32(byte[] b, int at, uint v)
    {
        for (int i = 0; i < 4; i++) b[at + i] = (byte)((v >> (8 * i)) & 0xFF);
    }

    private static void PutU64(byte[] b, int at, ulong v)
    {
        for (int i = 0; i < 8; i++) b[at + i] = (byte)((v >> (8 * i)) & 0xFF);
    }

    private static void PutF32(byte[] b, int at, float v) => PutU32(b, at, BitConverter.SingleToUInt32Bits(v));
    private static void PutF64(byte[] b, int at, double v) => PutU64(b, at, BitConverter.DoubleToUInt64Bits(v));

    private static ushort GetU16(byte[] b, int at) => (ushort)(b[at] | (b[at + 1] << 8));

    private static uint GetU32(byte[] b, int at)
    {
        uint v = 0;
        for (int i = 0; i < 4; i++) v |= (uint)b[at + i] << (8 * i);
        return v;
    }

    private static ulong GetU64(byte[] b, int at)
    {
        ulong v = 0;
        for (int i = 0; i < 8; i++) v |= (ulong)b[at + i] << (8 * i);
        return v;
    }

    private static float  GetF32(byte[] b, int at) => BitConverter.UInt32BitsToSingle(GetU32(b, at));
    private static double GetF64(byte[] b, int at) => BitConverter.UInt64BitsToDouble(GetU64(b, at));

    private static int ProbeRequiredLength(Action<byte[]> decode, int max = 64)
    {
        for (int n = 0; n <= max; n++)
        {
            try { decode(new byte[n]); return n; }
            catch (ArgumentException)         { }
            catch (IndexOutOfRangeException)  { }
        }
        return -1;
    }

    private static double Uniform(Random rng, double lo, double hi) => lo + rng.NextDouble() * (hi - lo);

    private static bool FloatOk(double expected, double actual) =>
        Math.Abs(actual - expected) <= FloatRelTolerance * Math.Max(1.0, Math.Abs(expected));

    private sealed class Accumulator
    {
        public bool Ok = true;
        public double MaxAbsError;

        public void Exact(double expected, double actual)
        {
            double e = Math.Abs(actual - expected);
            if (e > MaxAbsError) MaxAbsError = e;
            if (e != 0) Ok = false;
        }

        public void Approx(double expected, double actual)
        {
            double e = Math.Abs(actual - expected);
            if (e > MaxAbsError) MaxAbsError = e;
            if (!FloatOk(expected, actual)) Ok = false;
        }

        public void Flag(bool condition) { if (!condition) Ok = false; }
    }

    private static BlePacketResult MeasureGps(Random rng)
    {
        var acc = new Accumulator();
        var fixValues = Enum.GetValues<GpsFixType>();

        for (int i = 0; i < Samples; i++)
        {
            double lat    = Uniform(rng, -90, 90);
            double lon    = Uniform(rng, -180, 180);
            double accMm  = Uniform(rng, 0, 5000);
            var    fix    = fixValues[rng.Next(fixValues.Length)];

            var buf = new byte[GpsBytes];
            PutF64(buf, 0, lat);
            PutF64(buf, 8, lon);
            PutF32(buf, 16, (float)accMm);
            buf[20] = (byte)fix;

            var snapshot = BlePacketSerializer.DeserializeGps(buf);

            acc.Exact(lat, snapshot.Gps.Latitude);
            acc.Exact(lon, snapshot.Gps.Longitude);
            acc.Approx(accMm, snapshot.GpsAccuracyMm);
            acc.Flag(snapshot.GpsFixType == fix);
        }

        int measured = ProbeRequiredLength(b => BlePacketSerializer.DeserializeGps(b));
        return new BlePacketResult("gps", GpsBytes, measured, Samples, acc.Ok && measured == GpsBytes, acc.MaxAbsError);
    }

    private static BlePacketResult MeasureImu(Random rng)
    {
        var acc = new Accumulator();
        var seed = new SensorSnapshot();

        for (int i = 0; i < Samples; i++)
        {
            double ax = Uniform(rng, -20, 20),  ay = Uniform(rng, -20, 20),  az = Uniform(rng, -20, 20);
            double gx = Uniform(rng, -35, 35),  gy = Uniform(rng, -35, 35),  gz = Uniform(rng, -35, 35);

            var buf = new byte[ImuBytes];
            PutF32(buf,  0, (float)ax);
            PutF32(buf,  4, (float)ay);
            PutF32(buf,  8, (float)az);
            PutF32(buf, 12, (float)gx);
            PutF32(buf, 16, (float)gy);
            PutF32(buf, 20, (float)gz);

            var s = BlePacketSerializer.MergeImu(seed, buf);

            acc.Approx(ax, s.AccX);  acc.Approx(ay, s.AccY);  acc.Approx(az, s.AccZ);
            acc.Approx(gx, s.GyroX); acc.Approx(gy, s.GyroY); acc.Approx(gz, s.GyroZ);
        }

        int measured = ProbeRequiredLength(b => BlePacketSerializer.MergeImu(seed, b));
        return new BlePacketResult("imu", ImuBytes, measured, Samples, acc.Ok && measured == ImuBytes, acc.MaxAbsError);
    }

    private static BlePacketResult MeasureOdometry(Random rng)
    {
        var acc = new Accumulator();
        var seed = new SensorSnapshot();

        for (int i = 0; i < Samples; i++)
        {
            double px = Uniform(rng, -500, 500), py = Uniform(rng, -500, 500);
            double hd = Uniform(rng, -Math.PI, Math.PI), sp = Uniform(rng, -2, 2);

            var buf = new byte[OdometryBytes];
            PutF32(buf,  0, (float)px);
            PutF32(buf,  4, (float)py);
            PutF32(buf,  8, (float)hd);
            PutF32(buf, 12, (float)sp);

            var s = BlePacketSerializer.MergeOdometry(seed, buf);

            acc.Approx(px, s.PosX); acc.Approx(py, s.PosY);
            acc.Approx(hd, s.HeadingRad); acc.Approx(sp, s.LinearSpeed);
        }

        int measured = ProbeRequiredLength(b => BlePacketSerializer.MergeOdometry(seed, b));
        return new BlePacketResult("odometry", OdometryBytes, measured, Samples,
            acc.Ok && measured == OdometryBytes, acc.MaxAbsError);
    }

    private static BlePacketResult MeasureStatus(Random rng)
    {
        var acc = new Accumulator();
        var states = Enum.GetValues<RobotState>();

        for (int i = 0; i < Samples; i++)
        {
            var  state   = states[rng.Next(states.Length)];
            int  battery = rng.Next(0, 101);
            bool blade   = rng.Next(2) == 1;
            bool rain    = rng.Next(2) == 1;
            int  uptime  = rng.Next(0, 65536);

            var buf = new byte[StatusBytes];
            buf[0] = (byte)state;
            buf[1] = (byte)battery;
            buf[2] = blade ? (byte)1 : (byte)0;
            buf[3] = rain  ? (byte)1 : (byte)0;
            PutU16(buf, 4, (ushort)uptime);

            var s = BlePacketSerializer.DeserializeStatus(buf);

            acc.Flag(s.State == state);
            acc.Exact(battery, s.BatteryPct);
            acc.Flag(s.BladeOn == blade);
            acc.Flag(s.RainDetected == rain);
            acc.Exact(uptime, s.UptimeMinutes);
        }

        int measured = ProbeRequiredLength(b => BlePacketSerializer.DeserializeStatus(b));
        return new BlePacketResult("status", StatusBytes, measured, Samples,
            acc.Ok && measured == StatusBytes, acc.MaxAbsError);
    }

    private static BlePacketResult MeasureMotor(Random rng)
    {
        var acc = new Accumulator();
        int measured = 0;

        for (int i = 0; i < Samples; i++)
        {
            double linear  = Uniform(rng, -2, 2);
            double angular = Uniform(rng, -Math.PI, Math.PI);

            byte[] buf = BlePacketSerializer.SerializeMotorCommand((float)linear, (float)angular);
            measured = buf.Length;

            acc.Approx(linear,  GetF32(buf, 0));
            acc.Approx(angular, GetF32(buf, 4));
        }

        return new BlePacketResult("motor_cmd", MotorBytes, measured, Samples,
            acc.Ok && measured == MotorBytes, acc.MaxAbsError);
    }

    private static BlePacketResult MeasureAction(Random rng)
    {
        var acc = new Accumulator();
        var actions = Enum.GetValues<RobotAction>();
        int measured = 0;

        for (int i = 0; i < Samples; i++)
        {
            var  action = actions[rng.Next(actions.Length)];
            byte param  = (byte)rng.Next(256);

            byte[] buf = BlePacketSerializer.SerializeActionCommand(action, param);
            measured = buf.Length;

            acc.Flag(buf[0] == (byte)action);
            acc.Exact(param, buf[1]);
        }

        return new BlePacketResult("action_cmd", ActionBytes, measured, Samples,
            acc.Ok && measured == ActionBytes, acc.MaxAbsError);
    }

    private static BlePacketResult MeasureBoundary(Random rng)
    {
        var acc = new Accumulator();
        int measured = 0;

        for (int i = 0; i < Samples; i++)
        {
            ushort total = (ushort)rng.Next(1, 65536);
            ushort index = (ushort)rng.Next(0, total);
            byte   type  = (byte)rng.Next(256);
            double lat   = Uniform(rng, -90, 90);
            double lon   = Uniform(rng, -180, 180);

            byte[] buf = BlePacketSerializer.SerializeBoundaryChunk(index, total, type, new GpsPoint(lat, lon));
            measured = buf.Length;

            acc.Exact(index, GetU16(buf, 0));
            acc.Exact(total, GetU16(buf, 2));
            acc.Exact(type,  buf[4]);
            acc.Exact(0,     buf[5]);
            acc.Exact(lat,   GetF64(buf, 6));
            acc.Exact(lon,   GetF64(buf, 14));
        }

        return new BlePacketResult("boundary_chunk", BoundaryBytes, measured, Samples,
            acc.Ok && measured == BoundaryBytes, acc.MaxAbsError);
    }

    private static BlePacketResult MeasureSchedule(Random rng)
    {
        var acc = new Accumulator();
        int measured = 0;

        for (int i = 0; i < Samples; i++)
        {
            var days = Enum.GetValues<DayOfWeek>().Where(_ => rng.Next(2) == 1).ToArray();
            int hour = rng.Next(0, 24), minute = rng.Next(0, 60);
            int duration = rng.Next(0, 1441);
            bool active = rng.Next(2) == 1;

            var schedule = new MowingSchedule
            {
                ActiveDays      = days,
                StartTime       = new TimeSpan(hour, minute, 0),
                DurationMinutes = duration,
                IsActive        = active
            };

            byte[] buf = BlePacketSerializer.SerializeSchedule(schedule);
            measured = buf.Length;

            int expectedMask = 0;
            foreach (var d in days) expectedMask |= 1 << FirmwareBit(d);

            acc.Exact(expectedMask, buf[0]);
            acc.Exact(hour,   buf[1]);
            acc.Exact(minute, buf[2]);
            acc.Exact(duration, (buf[3] << 8) | buf[4]);
            acc.Exact(active ? 1 : 0, buf[5]);
            for (int k = 6; k < ScheduleBytes; k++) acc.Exact(0, buf[k]);
        }

        return new BlePacketResult("schedule", ScheduleBytes, measured, Samples,
            acc.Ok && measured == ScheduleBytes, acc.MaxAbsError);
    }

    private static int FirmwareBit(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday    => 0,
        DayOfWeek.Tuesday   => 1,
        DayOfWeek.Wednesday => 2,
        DayOfWeek.Thursday  => 3,
        DayOfWeek.Friday    => 4,
        DayOfWeek.Saturday  => 5,
        DayOfWeek.Sunday    => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(day))
    };

    public static IReadOnlyList<BleDayMaskResult> DayMask()
    {
        var rows = new List<BleDayMaskResult>();

        foreach (var day in Enum.GetValues<DayOfWeek>().OrderBy(d => FirmwareBit(d)))
        {
            var schedule = new MowingSchedule
            {
                ActiveDays      = [day],
                StartTime       = new TimeSpan(8, 0, 0),
                DurationMinutes = 60,
                IsActive        = true
            };

            byte mask = BlePacketSerializer.SerializeSchedule(schedule)[0];

            int expected = FirmwareBit(day);
            int actual   = mask == 0 ? -1 : (int)Math.Log2(mask);
            bool single  = mask != 0 && (mask & (mask - 1)) == 0;

            rows.Add(new BleDayMaskResult(day.ToString(), (int)day, expected, actual, single && actual == expected));
        }

        return rows;
    }

    public static IReadOnlyList<BleFinding> Findings()
    {
        var findings = new List<BleFinding>();
        var point = new GpsPoint(43.8563, 18.4131);

        const ushort index = 2499, total = 2500;
        byte[] buf = BlePacketSerializer.SerializeBoundaryChunk(index, total, 1, point);

        int decodedIndex = GetU16(buf, 0), decodedTotal = GetU16(buf, 2);
        findings.Add(new BleFinding(
            "boundary_index_ushort",
            decodedIndex == index && decodedTotal == total ? "PROLAZ" : "PAD",
            $"index {index} -> {decodedIndex}, total {total} -> {decodedTotal} (22 B zapis, ushort polja)"));

        int legacyIndex = index & 0xFF, legacyTotal = total & 0xFF;
        findings.Add(new BleFinding(
            "boundary_index_byte_legacy",
            legacyIndex == index && legacyTotal == total ? "PROLAZ" : "PAD",
            $"index {index} -> {legacyIndex}, total {total} -> {legacyTotal} (stari 20 B zapis, byte polja: tiho prelijevanje)"));

        findings.Add(new BleFinding(
            "boundary_capacity",
            "PROLAZ",
            $"ushort: 65535 tocaka; byte: 255 tocaka; granica MowingRoutePlanner: 2500 tocaka"));

        findings.Add(new BleFinding(
            "boundary_overflow_threshold",
            "PROLAZ",
            "stari zapis prelije od 256. tocke nadalje (indeks 256 -> 0)"));

        var dayRows = DayMask();
        findings.Add(new BleFinding(
            "schedule_daymask",
            dayRows.All(r => r.Match) ? "PROLAZ" : "PAD",
            $"{dayRows.Count(r => r.Match)}/{dayRows.Count} dana na ocekivanom bitu (ponedjeljak = bit 0, nedjelja = bit 6)"));

        var all = new MowingSchedule
        {
            ActiveDays      = Enum.GetValues<DayOfWeek>(),
            StartTime       = new TimeSpan(8, 0, 0),
            DurationMinutes = 60,
            IsActive        = true
        };
        byte fullMask = BlePacketSerializer.SerializeSchedule(all)[0];
        findings.Add(new BleFinding(
            "schedule_daymask_all",
            fullMask == 0x7F ? "PROLAZ" : "PAD",
            $"svih sedam dana -> 0x{fullMask:X2} (ocekivano 0x7F)"));

        var longest = new MowingSchedule
        {
            ActiveDays      = [DayOfWeek.Monday],
            StartTime       = new TimeSpan(23, 59, 0),
            DurationMinutes = 1440,
            IsActive        = true
        };
        byte[] sched = BlePacketSerializer.SerializeSchedule(longest);
        int duration = (sched[3] << 8) | sched[4];
        findings.Add(new BleFinding(
            "schedule_duration_endianness",
            duration == 1440 ? "PROLAZ" : "PAD",
            $"1440 min -> bajtovi {sched[3]},{sched[4]} -> {duration} (veliki redoslijed, ostatak protokola je mali)"));

        return findings;
    }
}
