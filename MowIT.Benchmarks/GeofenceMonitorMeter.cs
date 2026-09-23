using System.Globalization;
using System.Text;
using MowIT.Application.Logging;
using MowIT.Application.Services;
using MowIT.Domain.Entities;
using MowIT.Domain.Enums;
using MowIT.Domain.Geometry;
using MowIT.Domain.Interfaces;

namespace MowIT.Benchmarks;

public static class GeofenceMonitorMeter
{
    private const double FixedAccuracyMm = 12.0;
    private const double Dt              = 0.05;

    private const int M1Repetitions = 10;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private sealed record LatencyRow(double SpeedMs, double SigmaM, int Run, double DistancePastM, double DetectDelayS);
    private sealed record BehaviourRow(string CaseId, string Description, string Expected, string Actual, bool Pass);
    private sealed record FalseAlarmRow(double DistanceInsideM, double SigmaM, double DurationS, int FalseAlarms);

    public static async Task RunAndWriteAsync(string resultsDir)
    {
        Console.WriteLine();
        Console.WriteLine("Dorada 3 - GeofenceMonitor (M1-M6, GPS tocnost fiksirana na 12 mm osim gdje je predmet mjerenja)");

        var latency = await RunM1Async();
        WriteLatency(Path.Combine(resultsDir, "geofence_latency.csv"), latency);
        Console.WriteLine($"  M1: {latency.Count} pokusa zapisano u geofence_latency.csv");

        var behaviour = new List<BehaviourRow>
        {
            await RunM2Async(),
            await RunM3Async(),
            await RunM4Async(),
            await RunM5Async(),
        };
        WriteBehaviour(Path.Combine(resultsDir, "geofence_behaviour.csv"), behaviour);
        foreach (var b in behaviour)
            Console.WriteLine($"  {b.CaseId}  {(b.Pass ? "PROLAZ" : "PAD")}  {b.Description}");

        var falseAlarms = await RunM6Async();
        WriteFalseAlarm(Path.Combine(resultsDir, "geofence_falsealarm.csv"), falseAlarms);
        Console.WriteLine($"  M6: {falseAlarms.Count} kombinacija zapisano u geofence_falsealarm.csv");
    }

    private static (BoundaryZone Zone, LocalProjection Proj) BuildZone()
    {
        var p1 = TestPolygons.All.First(p => p.Id == "P1");
        return (p1.Zone, new LocalProjection(p1.Zone.Points[0]));
    }

    private static BoundaryZone BuildSquareZone(
        int id, string name, LocalProjection outerProj,
        double centerEast, double centerNorth, double halfSize, DateTime createdAt, int vertexCount = 4)
    {
        var points = vertexCount == 2
            ? new List<GpsPoint>
              {
                  outerProj.ToGps(centerEast - halfSize, centerNorth),
                  outerProj.ToGps(centerEast + halfSize, centerNorth),
              }
            : new List<GpsPoint>
              {
                  outerProj.ToGps(centerEast - halfSize, centerNorth - halfSize),
                  outerProj.ToGps(centerEast + halfSize, centerNorth - halfSize),
                  outerProj.ToGps(centerEast + halfSize, centerNorth + halfSize),
                  outerProj.ToGps(centerEast - halfSize, centerNorth + halfSize),
              };

        return new BoundaryZone { Id = id, Name = name, CreatedAt = createdAt, Points = points };
    }

    private sealed class Harness : IAsyncDisposable
    {
        public BoundaryZone Zone { get; }
        public LocalProjection Proj { get; }
        public RecordingRobotControl Control { get; } = new();
        public EventLogService EventLog { get; } = new();
        public List<(double T, bool Inside, double DistPastM)> Log { get; } = new();

        private readonly RobotSensorsDouble _sensors = new();
        private readonly GeofenceMonitor _monitor;
        private readonly Random _rng;
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        private double _east, _north;

        public Harness(BoundaryZone zone, LocalProjection proj, int seed, IBoundaryRepository repo)
        {
            Zone = zone; Proj = proj;
            _rng = new Random(seed);
            _monitor = new GeofenceMonitor(_sensors, repo, new AlwaysConnectedConnection(), Control, EventLog);
        }

        public async Task ArmAsync() => await Task.Delay(200);

        public void SetPosition(double east, double north) { _east = east; _north = north; }

        public async Task DriveAsync(
            double veast, double vnorth, double sigmaM, double dt, double maxSeconds, Func<bool>? stopWhen = null)
        {
            double t = 0;
            while (t < maxSeconds)
            {
                _east  += veast  * dt;
                _north += vnorth * dt;
                PushSample(sigmaM);
                t += dt;
                await Task.Delay(TimeSpan.FromSeconds(dt));
                if (stopWhen is not null && stopWhen()) break;
            }
        }

        public async Task HoldAsync(double sigmaM, double dt, double seconds)
        {
            double t = 0;
            while (t < seconds)
            {
                PushSample(sigmaM);
                t += dt;
                await Task.Delay(TimeSpan.FromSeconds(dt));
            }
        }

        public bool HasLogContaining(string needle) =>
            EventLog.Entries.Any(e => e.Message.Contains(needle, StringComparison.OrdinalIgnoreCase));

        private void PushSample(double sigmaM)
        {
            double repEast = _east, repNorth = _north;
            if (sigmaM > 0)
            {
                repEast  += Gaussian(sigmaM);
                repNorth += Gaussian(sigmaM);
            }

            var reportedGps = Proj.ToGps(repEast, repNorth);
            _sensors.Push(new SensorSnapshot
            {
                Gps = reportedGps, GpsAccuracyMm = (float)FixedAccuracyMm, Timestamp = DateTime.UtcNow,
            });

            var trueGps = Proj.ToGps(_east, _north);
            bool inside = Zone.Contains(trueGps);
            double distPast = inside ? 0.0 : Zone.DistanceToBoundaryMeters(trueGps);
            Log.Add((_sw.Elapsed.TotalSeconds, inside, distPast));
        }

        private double Gaussian(double stdDev)
        {
            double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
            double z  = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            return z * stdDev;
        }

        public ValueTask DisposeAsync()
        {
            _monitor.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<List<LatencyRow>> RunM1Async()
    {
        var (zone, proj) = BuildZone();
        double[] speeds = [0.1, 0.3, 0.6];
        double[] sigmas = [0.0, 0.012, 0.084];

        var rows = new List<LatencyRow>();
        int seed = 5000;

        foreach (double speed in speeds)
        foreach (double sigma in sigmas)
        {
            for (int rep = 0; rep < M1Repetitions; rep++)
            {
                await using var h = new Harness(zone, proj, seed++, new FixedZoneRepository(zone));
                await h.ArmAsync();
                h.SetPosition(5, 0.6);

                await h.DriveAsync(veast: 0, vnorth: -speed, sigma, Dt, maxSeconds: 25,
                    stopWhen: () => h.Control.StopCount > 0);

                var firstOutside = h.Log.FirstOrDefault(s => !s.Inside);
                double lastT       = h.Log.Count > 0 ? h.Log[^1].T : 0;
                double distancePast = h.Log.Count > 0 ? h.Log[^1].DistPastM : 0;
                double delay = firstOutside != default ? lastT - firstOutside.T : double.NaN;

                rows.Add(new LatencyRow(speed, sigma, rep, distancePast, delay));
            }

            var thisCombo = rows.Where(r => Math.Abs(r.SpeedMs - speed) < 1e-9 && Math.Abs(r.SigmaM - sigma) < 1e-9).ToList();
            Console.WriteLine($"  M1 v={speed:F1} m/s sigma={sigma:F3} m: " +
                              $"srednje {thisCombo.Average(r => r.DistancePastM):F3} m preko granice, " +
                              $"srednje kasnjenje {thisCombo.Average(r => r.DetectDelayS):F2} s (n={thisCombo.Count})");
        }

        return rows;
    }

    private static async Task<BehaviourRow> RunM2Async()
    {
        var (zone, proj) = BuildZone();
        await using var h = new Harness(zone, proj, seed: 6001, new FixedZoneRepository(zone));
        await h.ArmAsync();
        h.SetPosition(5, 0.6);

        await h.DriveAsync(0, -0.3, sigmaM: 0, Dt, maxSeconds: 15, stopWhen: () => h.Control.StopCount > 0);
        int stopsAfterBreach = h.Control.StopCount;

        await h.HoldAsync(sigmaM: 0, Dt, seconds: 30);

        int warnCount = h.EventLog.Entries.Count(e => e.Message.Contains("left zone", StringComparison.OrdinalIgnoreCase));
        bool pass = stopsAfterBreach == 1 && h.Control.StopCount == 1 && warnCount == 1;

        return new BehaviourRow("M2",
            "Histereza: robot ostaje izvan zone 30 s - upozorenje i Stop tocno jednom",
            "1 upozorenje, 1 Stop naredba tijekom cijelog boravka izvan zone",
            $"upozorenja={warnCount}, Stop naredbi={h.Control.StopCount}", pass);
    }

    private static async Task<BehaviourRow> RunM3Async()
    {
        var (zone, proj) = BuildZone();
        await using var h = new Harness(zone, proj, seed: 6101, new FixedZoneRepository(zone));
        await h.ArmAsync();
        h.SetPosition(5, 0.6);

        await h.DriveAsync(0, -0.3, 0, Dt, 15, () => h.Control.StopCount > 0);
        int stopsAfterFirst = h.Control.StopCount;
        await h.HoldAsync(0, Dt, 1.0);

        await h.DriveAsync(0, +0.3, 0, Dt, 15, () => false);
        await h.HoldAsync(0, Dt, 1.0);
        bool sawBackInside    = h.HasLogContaining("back inside");
        int  stopsAfterReturn = h.Control.StopCount;

        await h.DriveAsync(0, -0.3, 0, Dt, 15, () => h.Control.StopCount > stopsAfterReturn);
        await h.HoldAsync(0, Dt, 1.0);

        bool pass = stopsAfterFirst == 1 && stopsAfterReturn == 1 && sawBackInside && h.Control.StopCount == 2;

        return new BehaviourRow("M3",
            "Povratak u zonu se prepoznaje, a sljedeci izlazak se ponovno prijavljuje",
            "dnevnik biljezi povratak, drugi izlazak daje drugu, zasebnu Stop naredbu (ukupno 2)",
            $"Stop nakon 1. izlaska={stopsAfterFirst}, povratak zabiljezen={sawBackInside}, " +
            $"Stop nakon povratka={stopsAfterReturn}, ukupno Stop={h.Control.StopCount}", pass);
    }

    private static async Task<BehaviourRow> RunM4Async()
    {
        var outerProj = new LocalProjection(TestPolygons.Origin);
        var zoneOld     = BuildSquareZone(201, "stara-valjana",  outerProj, centerEast: 0,  centerNorth: 0,  halfSize: 5, createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var zoneInvalid = BuildSquareZone(202, "nevaljana",      outerProj, centerEast: 50, centerNorth: 0,  halfSize: 5, createdAt: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), vertexCount: 2);
        var zoneNewest  = BuildSquareZone(203, "najnovija-valjana", outerProj, centerEast: 100, centerNorth: 0, halfSize: 5, createdAt: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        var repo = new FixedZoneRepository(new[] { zoneOld, zoneInvalid, zoneNewest });
        var proj = new LocalProjection(zoneNewest.Points[0]);

        await using var h = new Harness(zoneNewest, proj, seed: 6201, repo);
        await h.ArmAsync();

        bool armedOnNewest = h.HasLogContaining(zoneNewest.Name) || h.EventLog.Entries.Any(e => e.Message.Contains("armed", StringComparison.OrdinalIgnoreCase) && e.Message.Contains(zoneNewest.Name));

        h.SetPosition(5, 0.6);
        await h.DriveAsync(0, -0.3, 0, Dt, 15, () => h.Control.StopCount > 0);

        bool breachedOnNewest = h.Control.StopCount == 1;

        return new BehaviourRow("M4",
            "Odabir zone: od tri spremljene zone (jedna nevaljana) nadzire se najnovija valjana",
            "naoruzavanje javlja ime najnovije valjane zone, proboj njenog ruba se prepoznaje",
            $"naoruzan na '{zoneNewest.Name}'={armedOnNewest}, proboj njenog ruba prepoznat={breachedOnNewest}",
            armedOnNewest && breachedOnNewest);
    }

    private static async Task<BehaviourRow> RunM5Async()
    {
        var (zone, proj) = BuildZone();
        await using var h = new Harness(zone, proj, seed: 6301, new EmptyZoneRepository());
        await h.ArmAsync();

        bool idleLogged = h.HasLogContaining("idle");

        await h.DriveAsync(0.3, 0.3, 0, Dt, 3.0);

        bool pass = idleLogged && h.Control.StopCount == 0 && h.Control.Calls.Count == 0;

        return new BehaviourRow("M5",
            "Prazno spremiste zona: usluga ne baca iznimku i ne salje naredbe",
            "stanje 'idle' zabiljezeno, 0 poslanih naredbi",
            $"idle zabiljezen={idleLogged}, poslanih naredbi={h.Control.Calls.Count}", pass);
    }

    private static async Task<List<FalseAlarmRow>> RunM6Async()
    {
        double[] distances = [0.05, 0.10, 0.20, 0.50];
        double[] sigmas    = [0.012, 0.084];
        const double DurationS = 60.0;

        var (zone, proj) = BuildZone();
        var rows = new List<FalseAlarmRow>();
        int seed = 7000;

        foreach (double d in distances)
        foreach (double sigma in sigmas)
        {
            await using var h = new Harness(zone, proj, seed++, new FixedZoneRepository(zone));
            await h.ArmAsync();
            h.SetPosition(5, d);

            int stopsBefore = h.Control.StopCount;
            await h.HoldAsync(sigma, Dt, DurationS);
            int falseAlarms = h.Control.StopCount - stopsBefore;

            rows.Add(new FalseAlarmRow(d, sigma, DurationS, falseAlarms));
            Console.WriteLine($"  M6 d={d:F2} m sigma={sigma:F3} m: {falseAlarms} laznih uzbuna tijekom {DurationS:F0} s");
        }

        return rows;
    }

    private static void WriteLatency(string path, IEnumerable<LatencyRow> rows)
    {
        using var writer = new StreamWriter(path, false, Utf8NoBom);
        writer.WriteLine("speed_ms,sigma_m,run,distance_past_boundary_m,detect_delay_s");
        foreach (var r in rows)
            writer.WriteLine(string.Join(',',
                F(r.SpeedMs, "0.###"), F(r.SigmaM, "0.####"), r.Run.ToString(CultureInfo.InvariantCulture),
                F(r.DistancePastM, "0.####"),
                double.IsNaN(r.DetectDelayS) ? "" : F(r.DetectDelayS, "0.###")));
    }

    private static void WriteBehaviour(string path, IEnumerable<BehaviourRow> rows)
    {
        using var writer = new StreamWriter(path, false, Utf8NoBom);
        writer.WriteLine("case_id,description,expected,actual,pass");
        foreach (var r in rows)
            writer.WriteLine(string.Join(',', r.CaseId, Csv(r.Description), Csv(r.Expected), Csv(r.Actual), r.Pass ? "1" : "0"));
    }

    private static void WriteFalseAlarm(string path, IEnumerable<FalseAlarmRow> rows)
    {
        using var writer = new StreamWriter(path, false, Utf8NoBom);
        writer.WriteLine("distance_inside_m,sigma_m,duration_s,false_alarms");
        foreach (var r in rows)
            writer.WriteLine(string.Join(',',
                F(r.DistanceInsideM, "0.###"), F(r.SigmaM, "0.####"), F(r.DurationS, "0.#"),
                r.FalseAlarms.ToString(CultureInfo.InvariantCulture)));
    }

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private static string F(double value, string format = "0.######") =>
        value.ToString(format, CultureInfo.InvariantCulture);
}
