using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using MowIT.Benchmarks;
using MowIT.Domain.Entities;
using MowIT.Domain.Interfaces;
using MowIT.Domain.Strategies;
using MowIT.Shared.Telemetry;

namespace MowIT.RobotSimulator;

internal static class HeadlessRunner
{
    private const double SwathWidthM   = 0.25;
    private const double DefaultDt     = 0.05;
    private const double DefaultMaxSec = 20000;
    private const int    DefaultSeed   = 20260101;

    private static readonly string[] BatchPolygons = ["P1", "P4", "P5"];
    private static readonly double[] BatchSpacings = [0.25, 0.50];

    private static readonly double[] BatchSigmas = [0.0];

    private static readonly int[] BatchSeeds = [20260101];

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private sealed record TracePoint(
        double T, double TrueLat, double TrueLon, double ReportedLat, double ReportedLon,
        int RouteIdx, int State);

    private sealed record SimResult(
        List<TracePoint> Trace, double MowTimeS, int RouteCount, int RouteIndex);

    private sealed record CoverageRow(
        string PolygonId, string Strategy, double SpacingM, double NoiseSigmaM,
        CoverageResult Coverage, double MowTimeS, double PathLengthM, double RouteLengthM,
        int UnreachedPoints);

    public static Task RunAsync(IConfiguration config)
    {
        string resultsDir = Path.Combine(AppContext.BaseDirectory, "results");
        string tracesDir  = Path.Combine(resultsDir, "traces");
        Directory.CreateDirectory(tracesDir);

        double dt      = Num(config["Headless:Dt"],         DefaultDt);
        double maxSec  = Num(config["Headless:MaxSeconds"], DefaultMaxSec);

        if (string.Equals(config["Headless:Batch"], "true", StringComparison.OrdinalIgnoreCase))
            RunBatch(resultsDir, tracesDir, dt, maxSec);
        else
            RunSingle(config, resultsDir, tracesDir, dt, maxSec);

        return Task.CompletedTask;
    }

    private static void RunBatch(string resultsDir, string tracesDir, double dt, double maxSec)
    {
        var total = Stopwatch.StartNew();
        var byId  = TestPolygons.All.ToDictionary(p => p.Id);

        int count = BatchPolygons.Length * 2 * BatchSpacings.Length * BatchSigmas.Length * BatchSeeds.Length;
        Console.WriteLine($"MowIT.RobotSimulator - dio B (headless, {count} pokusa, dt = {F(dt)} s)");
        Console.WriteLine($"Izlaz: {resultsDir}");
        Console.WriteLine();

        string coveragePath = Path.Combine(resultsDir, "sim_coverage.csv");
        using var writer = OpenCoverageWriter(coveragePath, append: false);

        int index = 0;
        foreach (string polygonId in BatchPolygons)
        {
            var polygon = byId[polygonId];

            foreach (IMowingStrategy strategy in new IMowingStrategy[]
                     { new BoustrophedonStrategy(), new SpiralInwardStrategy() })
            {
                string name = strategy is BoustrophedonStrategy ? "Boustrophedon" : "SpiralInward";

                foreach (double spacing in BatchSpacings)
                {
                    var route       = strategy.GenerateRoute(polygon.Zone, (float)spacing);
                    double routeLen = CoverageMeter.RouteLength(polygon.Zone, route);

                    foreach (double sigma in BatchSigmas)
                    {
                        foreach (int seed in BatchSeeds)
                        {
                            index++;
                            var row = RunExperiment(
                                polygon.Zone, polygonId, name, route, routeLen,
                                spacing, sigma, seed, dt, maxSec, tracesDir);

                            WriteCoverageRow(writer, row);
                            Console.WriteLine(
                                $"[{index}/{count}] {polygonId} {name} s={F(spacing, "0.00")} " +
                                $"sigma={F(sigma, "0.000")} seed={seed} -> " +
                                $"coverage={F(row.Coverage.CoveragePct, "0.0")}%");
                        }
                    }
                }
            }
        }

        total.Stop();
        Console.WriteLine();
        Console.WriteLine($"Gotovo za {total.Elapsed.TotalSeconds:F1} s. " +
                          $"{count} redaka u sim_coverage.csv, {count} tragova u {tracesDir}");

        RunInfo.Write(resultsDir, "headless --Headless:Batch true", new Dictionary<string, string>
        {
            ["dt_s"]              = F(dt),
            ["max_seconds"]       = F(maxSec),
            ["swath_m"]           = F(SwathWidthM),
            ["polygons"]          = string.Join(",", BatchPolygons),
            ["strategies"]        = "Boustrophedon,SpiralInward",
            ["spacings_m"]        = string.Join(",", BatchSpacings.Select(s => F(s))),
            ["noise_sigma_m"]     = string.Join(",", BatchSigmas.Select(s => F(s, "0.000"))),
            ["seeds"]             = string.Join(",", BatchSeeds),
            ["experiment_count"]  = count.ToString(CultureInfo.InvariantCulture),
            ["elapsed_s"]         = F(total.Elapsed.TotalSeconds, "0.0"),
        });
    }

    private static CoverageRow RunExperiment(
        BoundaryZone zone, string polygonId, string strategyName,
        IReadOnlyList<GpsPoint> route, double routeLengthM,
        double spacing, double sigma, int seed, double dt, double maxSec, string tracesDir)
    {
        var robot = new VirtualRobot { NoiseSigmaM = sigma };
        robot.SetSeed(seed);

        var sim = Simulate(robot, route.Select(p => (p.Latitude, p.Longitude)), dt, maxSec);

        var truePoints  = sim.Trace.Select(t => new GpsPoint(t.TrueLat, t.TrueLon)).ToList();
        var coverage    = CoverageMeter.Measure(zone, truePoints, SwathWidthM);
        double pathLen  = CoverageMeter.RouteLength(zone, truePoints);

        string tracePath = Path.Combine(
            tracesDir,
            $"trace_{polygonId}_{strategyName}_{F(spacing, "0.00")}_{F(sigma, "0.000")}_{seed}.csv");
        WriteTrace(tracePath, sim.Trace);

        return new CoverageRow(
            polygonId, strategyName, spacing, sigma, coverage,
            sim.MowTimeS, pathLen, routeLengthM,
            Math.Max(0, sim.RouteCount - sim.RouteIndex));
    }

    private static void RunSingle(
        IConfiguration config, string resultsDir, string tracesDir, double dt, double maxSec)
    {
        string? routeFile = config["Headless:RouteFile"];
        if (string.IsNullOrWhiteSpace(routeFile) || !File.Exists(routeFile))
        {
            Console.WriteLine($"headless: nedostaje --Headless:RouteFile (ili datoteka ne postoji: {routeFile})");
            return;
        }

        double sigma = Num(config["Headless:NoiseSigmaM"], 0);
        int    seed  = (int)Num(config["Headless:Seed"], DefaultSeed);

        var route = ReadRouteCsv(routeFile);
        if (route.Count == 0)
        {
            Console.WriteLine($"headless: ruta u {routeFile} je prazna");
            return;
        }

        var robot = new VirtualRobot { NoiseSigmaM = sigma };
        robot.SetSeed(seed);

        var sw  = Stopwatch.StartNew();
        var sim = Simulate(robot, route, dt, maxSec);
        sw.Stop();

        string outFile = config["Headless:OutFile"] ?? Path.Combine(tracesDir, "trace.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        WriteTrace(outFile, sim.Trace);

        Console.WriteLine($"headless: {route.Count} tocaka rute, {sim.Trace.Count} tocaka traga, " +
                          $"simulirano {F(sim.MowTimeS, "0.##")} s za {sw.Elapsed.TotalSeconds:F2} s stvarnog vremena");
        Console.WriteLine($"headless: trag zapisan u {outFile}");

        string? polygonId = config["Headless:PolygonId"];
        if (string.IsNullOrWhiteSpace(polygonId)) return;

        var polygon = TestPolygons.All.FirstOrDefault(p => p.Id == polygonId);
        if (polygon is null)
        {
            Console.WriteLine($"headless: nepoznat poligon '{polygonId}', pokrivenost se ne racuna");
            return;
        }

        var truePoints    = sim.Trace.Select(t => new GpsPoint(t.TrueLat, t.TrueLon)).ToList();
        var plannedPoints = route.Select(p => new GpsPoint(p.Lat, p.Lon)).ToList();

        var row = new CoverageRow(
            polygon.Id, "Manual", 0, sigma,
            CoverageMeter.Measure(polygon.Zone, truePoints, SwathWidthM),
            sim.MowTimeS,
            CoverageMeter.RouteLength(polygon.Zone, truePoints),
            CoverageMeter.RouteLength(polygon.Zone, plannedPoints),
            Math.Max(0, sim.RouteCount - sim.RouteIndex));

        using var writer = OpenCoverageWriter(Path.Combine(resultsDir, "sim_coverage.csv"));
        WriteCoverageRow(writer, row);

        Console.WriteLine($"headless: pokrivenost {F(row.Coverage.CoveragePct, "0.00")} %, " +
                          $"preklapanje {F(row.Coverage.OverlapPct, "0.00")} %, " +
                          $"prijedeno {F(row.PathLengthM, "0.##")} m naspram rute {F(row.RouteLengthM, "0.##")} m");
    }

    private static SimResult Simulate(
        VirtualRobot robot, IEnumerable<(double Lat, double Lon)> route, double dt, double maxSec)
    {
        robot.SetRoute(route);
        robot.ApplyCommand(new RobotCommandDto { Kind = CommandKinds.Action, ActionName = "StartMowing" });

        double t = 0;
        var trace = new List<TracePoint>();

        while (t < maxSec && robot.State == VirtualRobot.StateMowingValue)
        {
            robot.Tick(dt);
            trace.Add(new TracePoint(
                t, robot.TrueLat, robot.TrueLon, robot.ReportedLat, robot.ReportedLon,
                robot.RouteIndex, robot.State));
            t += dt;
        }

        return new SimResult(trace, t, robot.RouteCount, robot.RouteIndex);
    }

    private static List<(double Lat, double Lon)> ReadRouteCsv(string path)
    {
        var route = new List<(double Lat, double Lon)>();

        foreach (string line in File.ReadLines(path))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            string[] parts = trimmed.Split(',');
            if (parts.Length < 2) continue;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)) continue;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)) continue;

            route.Add((lat, lon));
        }

        return route;
    }

    private static void WriteTrace(string path, IEnumerable<TracePoint> trace)
    {
        using var writer = new StreamWriter(path, false, Utf8NoBom);
        writer.WriteLine("t_s,true_lat,true_lon,reported_lat,reported_lon,route_idx,state");

        foreach (var p in trace)
            writer.WriteLine(string.Join(',',
                F(p.T, "0.###"),
                F(p.TrueLat, "0.#########"), F(p.TrueLon, "0.#########"),
                F(p.ReportedLat, "0.#########"), F(p.ReportedLon, "0.#########"),
                p.RouteIdx.ToString(CultureInfo.InvariantCulture),
                p.State.ToString(CultureInfo.InvariantCulture)));
    }

    private static StreamWriter OpenCoverageWriter(string path, bool append = true)
    {
        bool isNew = !append || !File.Exists(path) || new FileInfo(path).Length == 0;
        var writer = new StreamWriter(path, append, Utf8NoBom);
        if (isNew)
            writer.WriteLine("polygon_id,strategy,spacing_m,noise_sigma_m,coverage_pct,overlap_pct," +
                             "outside_pct,mow_time_s,path_length_m,route_length_m,unreached_points");
        return writer;
    }

    private static void WriteCoverageRow(StreamWriter writer, CoverageRow r) =>
        writer.WriteLine(string.Join(',',
            r.PolygonId, r.Strategy, F(r.SpacingM, "0.###"), F(r.NoiseSigmaM, "0.####"),
            F(r.Coverage.CoveragePct, "0.####"), F(r.Coverage.OverlapPct, "0.####"),
            F(r.Coverage.OutsidePct, "0.####"),
            F(r.MowTimeS, "0.###"), F(r.PathLengthM, "0.###"), F(r.RouteLengthM, "0.###"),
            r.UnreachedPoints.ToString(CultureInfo.InvariantCulture)));

    private static double Num(string? value, double fallback) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : fallback;

    private static string F(double value, string format = "0.######") =>
        value.ToString(format, CultureInfo.InvariantCulture);
}
