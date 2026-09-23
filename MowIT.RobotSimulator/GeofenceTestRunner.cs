using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using MowIT.Application.Logging;
using MowIT.Application.Services;
using MowIT.Domain.Entities;
using MowIT.Domain.Enums;
using MowIT.Shared.Telemetry;

namespace MowIT.RobotSimulator;

internal static class GeofenceTestRunner
{
    private const double OriginLat = 43.8563, OriginLon = 18.4131;
    private const double MetersPerDegree = 111_319.444;

    private const double Dt              = 0.1;
    private const float  DriveSpeedMs    = 0.5f;
    private const double FullAccuracyWaitS = 8.0;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private sealed record Sample(
        string ScenarioId, double TS, double EastM, double NorthM,
        double DistancePastM, bool Inside, double GpsAccuracyMm, double ToleranceM, int StopCount);

    private sealed record ScenarioResult(string Id, string Description, string Expected, string Actual, bool Pass);

    public static async Task RunAsync(IConfiguration config)
    {
        string resultsDir = Path.Combine(AppContext.BaseDirectory, "results");
        Directory.CreateDirectory(resultsDir);

        Console.WriteLine("MowIT.RobotSimulator - dio G (geofencing, GeofenceMonitor bez izmjene)");
        Console.WriteLine($"Izlaz: {resultsDir}");
        Console.WriteLine();

        var allSamples = new List<Sample>();
        var scenarios  = new List<ScenarioResult>
        {
            await RunGs1(allSamples),
            await RunGs2(allSamples),
            await RunGs3(allSamples),
            await RunGs4(allSamples),
            await RunGs5(allSamples),
        };

        foreach (var s in scenarios)
            Console.WriteLine($"  {s.Id}  {(s.Pass ? "PROLAZ" : "PAD")}  {s.Description}");

        WriteSamples(Path.Combine(resultsDir, "geofence.csv"), allSamples);
        WriteScenarios(Path.Combine(resultsDir, "geofence_scenarios.csv"), scenarios);

        RunInfo.Write(resultsDir, "geofence-test", new Dictionary<string, string>
        {
            ["dt_s"]            = F(Dt),
            ["drive_speed_ms"]  = F(DriveSpeedMs),
            ["full_accuracy_wait_s"] = F(FullAccuracyWaitS),
            ["zone_size_m"]     = "10x10",
            ["scenario_count"]  = scenarios.Count.ToString(CultureInfo.InvariantCulture),
        });

        Console.WriteLine();
        Console.WriteLine($"geofence.csv ({allSamples.Count} redaka), geofence_scenarios.csv ({scenarios.Count} redaka), " +
                          $"{scenarios.Count(s => s.Pass)}/{scenarios.Count} scenarija PROLAZ");
    }

    private static async Task<ScenarioResult> RunGs1(List<Sample> sink)
    {
        await using var h = await ScenarioHarness.StartAsync("GS1", BuildZone(), At(5, 5));

        await h.DriveAsync(+DriveSpeedMs, 2.0);
        await h.DriveAsync(-DriveSpeedMs, 4.0);
        await h.DriveAsync(+DriveSpeedMs, 2.0);

        sink.AddRange(h.Samples);

        bool anyOutside = h.Samples.Any(s => !s.Inside);
        bool pass = !anyOutside && h.Control.StopCount == 0;

        return new ScenarioResult("GS1",
            "Robot ostaje unutar granice tijekom normalne voznje",
            "0 proboja, 0 Stop naredbi geofencinga",
            $"{(anyOutside ? "izasao izvan zone" : "ostao unutar zone")} u svih {h.Samples.Count} uzoraka, Stop poslan {h.Control.StopCount}x",
            pass);
    }

    private static async Task<ScenarioResult> RunGs2(List<Sample> sink)
    {
        await using var h = await ScenarioHarness.StartAsync("GS2", BuildZone(), At(8.5, 5));

        await h.HoldAsync(FullAccuracyWaitS);
        await h.DriveAsync(+DriveSpeedMs, 5.0);
        await h.HoldAsync(2.0);

        sink.AddRange(h.Samples);

        bool breachedOnce = h.Control.StopCount == 1;
        bool endsOutside  = !h.Samples[^1].Inside;
        bool pass = breachedOnce && endsOutside;

        return new ScenarioResult("GS2",
            "Izlazak jasno preko granice (> 0,5 m) pokrece proboj i Stop naredbu",
            "tocno 1 Stop naredba, robot ostaje izvan bez ponovnog okidanja",
            $"Stop poslan {h.Control.StopCount}x, zavrsna udaljenost {h.Samples[^1].DistancePastM:F2} m izvan zone",
            pass);
    }

    private static async Task<ScenarioResult> RunGs3(List<Sample> sink)
    {
        await using var h = await ScenarioHarness.StartAsync("GS3", BuildZone(), At(9.5, 5));

        await h.HoldAsync(FullAccuracyWaitS);
        await h.DriveAsync(+DriveSpeedMs, 1.4);
        await h.HoldAsync(3.0);

        sink.AddRange(h.Samples);

        bool pass = h.Control.StopCount == 0;

        return new ScenarioResult("GS3",
            "Malen izlazak unutar margine (< 0,5 m) se NE broji kao proboj " +
            "(ista margina koja postoji da red boustrophedona na rubu poligona ne bude lazni proboj)",
            "0 Stop naredbi",
            $"zavrsna udaljenost {h.Samples[^1].DistancePastM:F2} m izvan zone, Stop poslan {h.Control.StopCount}x",
            pass);
    }

    private static async Task<ScenarioResult> RunGs4(List<Sample> sink)
    {
        await using var h = await ScenarioHarness.StartAsync("GS4", BuildZone(), At(9.4, 5));

        await h.DriveAsync(+DriveSpeedMs, 2.4);
        await h.HoldAsync(9.0);

        sink.AddRange(h.Samples);

        double restDistanceM = h.Samples[^1].DistancePastM;

        double expectedFireT = restDistanceM > 0.5 ? (800 - restDistanceM * 1000) / 100.0 : double.PositiveInfinity;

        bool noEarlyBreach = h.Samples.Where(s => s.TS < expectedFireT - 0.3).All(s => s.StopCount == 0);
        var  breachSample  = h.Samples.FirstOrDefault(s => s.StopCount > 0);
        bool firedNearPrediction = breachSample is not null && Math.Abs(breachSample.TS - expectedFireT) < 1.5;

        bool pass = restDistanceM > 0.5 && noEarlyBreach && firedNearPrediction;

        return new ScenarioResult("GS4",
            "Losa GPS tocnost privremeno siri toleranciju proboja (tolerancija = max(0,5 m, tocnost))",
            $"bez proboja dok je tocnost losa, proboj cim tocnost padne ispod stvarne udaljenosti " +
            $"({restDistanceM:F2} m), ocekivano oko t={expectedFireT:F1} s",
            breachSample is not null
                ? $"proboj u t={breachSample.TS:F2} s (predvideno ~{expectedFireT:F1} s), bez ranog proboja: {noEarlyBreach}"
                : "proboj se nije dogodio unutar mjerenja",
            pass);
    }

    private static async Task<ScenarioResult> RunGs5(List<Sample> sink)
    {
        await using var h = await ScenarioHarness.StartAsync("GS5", BuildZone(), At(8.5, 5));

        await h.HoldAsync(FullAccuracyWaitS);
        await h.DriveAsync(+DriveSpeedMs, 6.0);
        await h.HoldAsync(1.5);
        int stopsAfterFirst = h.Control.StopCount;

        await h.DriveAsync(-DriveSpeedMs, 6.0);
        await h.HoldAsync(1.0);
        bool sawBackInside   = h.HasLogContaining("back inside");
        int  stopsAfterReturn = h.Control.StopCount;

        await h.DriveAsync(+DriveSpeedMs, 6.0);
        await h.HoldAsync(1.5);

        sink.AddRange(h.Samples);

        bool pass = stopsAfterFirst == 1 && stopsAfterReturn == 1 && sawBackInside && h.Control.StopCount == 2;

        return new ScenarioResult("GS5",
            "Histereza: proboj se ne ponavlja dok se robot ne vrati unutar zone; " +
            "novi izlazak nakon povratka izaziva DRUGI, zaseban proboj",
            "1. Stop nakon prvog izlaska (ukupno 1), dnevnik biljezi povratak unutra, " +
            "2. Stop nakon ponovnog izlaska (ukupno 2)",
            $"Stop nakon 1. izlaska={stopsAfterFirst}, povratak zabiljezen={sawBackInside}, " +
            $"Stop nakon povratka={stopsAfterReturn}, ukupno Stop={h.Control.StopCount}",
            pass);
    }

    private sealed class ScenarioHarness : IAsyncDisposable
    {
        public BoundaryZone Zone { get; }
        public VirtualRobot Robot { get; } = new();
        public RecordingRobotControl Control { get; } = new();
        public EventLogService EventLog { get; } = new();
        public List<Sample> Samples { get; } = new();

        private readonly RobotSensorsDouble _sensors = new();
        private readonly GeofenceMonitor _monitor;
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        private readonly string _scenarioId;

        private ScenarioHarness(string scenarioId, BoundaryZone zone)
        {
            _scenarioId = scenarioId;
            Zone = zone;
            _monitor = new GeofenceMonitor(
                _sensors, new FixedZoneRepository(zone), new AlwaysConnectedConnection(), Control, EventLog);
        }

        public static async Task<ScenarioHarness> StartAsync(string scenarioId, BoundaryZone zone, GpsPoint startPoint)
        {
            var h = new ScenarioHarness(scenarioId, zone);

            await Task.Delay(200);

            Teleport(h.Robot, startPoint);
            h.PushSample();
            return h;
        }

        private static void Teleport(VirtualRobot robot, GpsPoint target)
        {
            robot.SetRoute(new[] { (target.Latitude, target.Longitude) });
            robot.ApplyCommand(new RobotCommandDto { Kind = CommandKinds.Action, ActionName = "StartMowing" });
            robot.Tick(0.01);
            robot.Tick(0.01);
        }

        public async Task DriveAsync(float linearMs, double seconds)
        {
            int steps = Math.Max(1, (int)Math.Round(seconds / Dt));
            for (int i = 0; i < steps; i++)
            {
                Robot.ApplyCommand(new RobotCommandDto { Kind = CommandKinds.Motor, LinearVel = linearMs, AngularVel = 0f });
                Robot.Tick(Dt);
                PushSample();
                await Task.Delay(TimeSpan.FromSeconds(Dt));
            }
        }

        public async Task HoldAsync(double seconds)
        {
            Robot.ApplyCommand(new RobotCommandDto { Kind = CommandKinds.Action, ActionName = "Stop" });
            int steps = Math.Max(1, (int)Math.Round(seconds / Dt));
            for (int i = 0; i < steps; i++)
            {
                Robot.Tick(Dt);
                PushSample();
                await Task.Delay(TimeSpan.FromSeconds(Dt));
            }
        }

        public bool HasLogContaining(string needle) =>
            EventLog.Entries.Any(e => e.Message.Contains(needle, StringComparison.OrdinalIgnoreCase));

        private void PushSample()
        {
            var t   = Robot.ToTelemetry();
            var gps = new GpsPoint(t.Lat, t.Lon);
            _sensors.Push(new SensorSnapshot { Gps = gps, GpsAccuracyMm = t.GpsAccuracyMm, Timestamp = DateTime.UtcNow });

            bool   inside       = Zone.Contains(gps);
            double distancePast = inside ? 0.0 : Zone.DistanceToBoundaryMeters(gps);
            double tolerance    = Math.Max(0.5, t.GpsAccuracyMm / 1000.0);
            var    (east, north) = ToLocal(gps);

            Samples.Add(new Sample(
                _scenarioId, _sw.Elapsed.TotalSeconds, east, north,
                distancePast, inside, t.GpsAccuracyMm, tolerance, Control.StopCount));
        }

        public ValueTask DisposeAsync()
        {
            _monitor.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static BoundaryZone BuildZone() => new()
    {
        Id        = 900,
        Name      = "geofence-test-zone",
        CreatedAt = DateTime.UtcNow,
        Points    = new List<GpsPoint> { At(0, 0), At(10, 0), At(10, 10), At(0, 10) },
    };

    private static GpsPoint At(double eastM, double northM)
    {
        double lat = OriginLat + northM / MetersPerDegree;
        double lon = OriginLon + eastM / (MetersPerDegree * Math.Cos(OriginLat * Math.PI / 180.0));
        return new GpsPoint(lat, lon);
    }

    private static (double East, double North) ToLocal(GpsPoint p)
    {
        double north = (p.Latitude  - OriginLat) * MetersPerDegree;
        double east  = (p.Longitude - OriginLon) * MetersPerDegree * Math.Cos(OriginLat * Math.PI / 180.0);
        return (east, north);
    }

    private static void WriteSamples(string path, IEnumerable<Sample> rows)
    {
        using var writer = new StreamWriter(path, false, Utf8NoBom);
        writer.WriteLine("scenario_id,t_s,east_m,north_m,distance_past_m,inside,gps_accuracy_mm,tolerance_m,stop_count");
        foreach (var r in rows)
            writer.WriteLine(string.Join(',',
                r.ScenarioId, F(r.TS, "0.###"), F(r.EastM, "0.###"), F(r.NorthM, "0.###"),
                F(r.DistancePastM, "0.####"), r.Inside ? "1" : "0",
                F(r.GpsAccuracyMm, "0.##"), F(r.ToleranceM, "0.###"),
                r.StopCount.ToString(CultureInfo.InvariantCulture)));
    }

    private static void WriteScenarios(string path, IEnumerable<ScenarioResult> rows)
    {
        using var writer = new StreamWriter(path, false, Utf8NoBom);
        writer.WriteLine("scenario_id,description,expected,actual,pass");
        foreach (var r in rows)
            writer.WriteLine(string.Join(',', r.Id, Csv(r.Description), Csv(r.Expected), Csv(r.Actual), r.Pass ? "1" : "0"));
    }

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private static string F(double value, string format = "0.######") =>
        value.ToString(format, CultureInfo.InvariantCulture);
}
