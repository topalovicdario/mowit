using System.Globalization;
using System.Text;
using MowIT.Domain.Entities;
using MowIT.Domain.Geometry;
using MowIT.Domain.Strategies;

namespace MowIT.Benchmarks;

public static class EqualCoverageMeter
{
    private const double SwathWidthM = 0.25;
    private const float  SpacingM    = 0.25f;
    private const double CellSizeM   = 0.05;
    private const double StepMeters  = 0.05;

    private const double TargetCoverage  = 0.95;
    private const double MaxLengthFactor = 20.0;
    private const int    BaseRecomputeSteps = 200;
    private const int    SeedBase = 20260101;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private sealed record RouteMetrics(double CoveragePct, double OverlapPct, double LengthM, double WorkEfficiencyPct);

    public static void RunAndWrite(string resultsDir)
    {
        Directory.CreateDirectory(resultsDir);
        Console.WriteLine();
        Console.WriteLine($"R6 - usporedba pri izjednacenoj ciljanoj pokrivenosti " +
                          $"(cilj {TargetCoverage * 100:F0} %, sigurnosna granica {MaxLengthFactor:F0}x duljine Boustrophedona)");

        var polygons = TestPolygons.All;
        var lines = new List<string>();

        foreach (var p in polygons)
        {
            var bouRoute = new BoustrophedonStrategy().GenerateRoute(p.Zone, SpacingM);
            var bou = Measure(p.Zone, bouRoute);

            var spiRoute = new SpiralInwardStrategy().GenerateRoute(p.Zone, SpacingM);
            var spi = Measure(p.Zone, spiRoute);

            double capLength = MaxLengthFactor * bou.LengthM;
            int seed = SeedBase + (int)char.GetNumericValue(p.Id[^1]) - 1;

            var walk = GrowRandomWalk(p.Zone, seed, capLength);
            bool hitSafetyLimit = walk.CoveragePct < TargetCoverage * 100.0;
            double lengthRatio  = walk.LengthM / bou.LengthM;

            lines.Add(Row(p.Id, "Boustrophedon", bou, lengthRatio: null, hitSafetyLimit: null));
            lines.Add(Row(p.Id, "SpiralInward", spi, lengthRatio: null, hitSafetyLimit: null));
            lines.Add(Row(p.Id, "RandomWalk", walk, lengthRatio, hitSafetyLimit));

            Console.WriteLine(
                $"  {p.Id} Bou ucinkovitost={bou.WorkEfficiencyPct,5:F1} % | " +
                $"Spi ucinkovitost={spi.WorkEfficiencyPct,5:F1} % | " +
                $"Rnd pokrivenost={walk.CoveragePct,5:F1} % ucinkovitost={walk.WorkEfficiencyPct,5:F1} % " +
                $"duljina={walk.LengthM,8:F0} m ({lengthRatio,4:F1}x sustavne)" +
                (hitSafetyLimit ? "  [DOSEGNUTA SIGURNOSNA GRANICA]" : ""));
        }

        Write(Path.Combine(resultsDir, "equal_coverage.csv"), lines);
    }

    private static RouteMetrics Measure(BoundaryZone zone, List<GpsPoint> route)
    {
        var cov = CoverageMeter.Measure(zone, route, SwathWidthM, CellSizeM);
        double length = CoverageMeter.RouteLength(zone, route);
        return new RouteMetrics(cov.CoveragePct, cov.OverlapPct, length, WorkEfficiency(cov.OverlapPct));
    }

    private static double WorkEfficiency(double overlapPct) => 100.0 / (1.0 + overlapPct / 100.0);

    private static RouteMetrics GrowRandomWalk(BoundaryZone zone, int seed, double capLength)
    {
        var proj = new LocalProjection(zone.Points[0]);
        var rng  = new Random(seed);

        var localPoly = zone.Points.Select(proj.ToLocal).ToList();
        double x = localPoly.Average(pt => pt.East);
        double y = localPoly.Average(pt => pt.North);
        double heading = rng.NextDouble() * 2 * Math.PI;

        var gpsTrace = new List<GpsPoint> { proj.ToGps(x, y) };
        double traveled = 0;
        long acceptedSteps = 0;

        long nextCheckStep = BaseRecomputeSteps;
        long checkInterval = BaseRecomputeSteps;

        long maxIterations = (long)(capLength / StepMeters) * 4;
        long iterations = 0;

        while (iterations++ < maxIterations)
        {
            double nx = x + Math.Cos(heading) * StepMeters;
            double ny = y + Math.Sin(heading) * StepMeters;
            var candidate = proj.ToGps(nx, ny);

            if (!zone.Contains(candidate))
            {
                heading += Math.PI + (rng.NextDouble() - 0.5) * Math.PI;
                continue;
            }

            x = nx; y = ny;
            gpsTrace.Add(candidate);
            traveled += StepMeters;
            acceptedSteps++;

            if (traveled >= capLength) break;

            if (acceptedSteps >= nextCheckStep)
            {
                double coveragePct = CoverageMeter.Measure(zone, gpsTrace, SwathWidthM, CellSizeM).CoveragePct;
                if (coveragePct >= TargetCoverage * 100.0) break;

                checkInterval *= 2;
                nextCheckStep += checkInterval;
            }
        }

        var finalCov = CoverageMeter.Measure(zone, gpsTrace, SwathWidthM, CellSizeM);
        double finalLength = CoverageMeter.RouteLength(zone, gpsTrace);
        return new RouteMetrics(finalCov.CoveragePct, finalCov.OverlapPct, finalLength, WorkEfficiency(finalCov.OverlapPct));
    }

    private static string Row(string polygonId, string strategy, RouteMetrics m, double? lengthRatio, bool? hitSafetyLimit) =>
        string.Join(',',
            polygonId,
            strategy,
            F(TargetCoverage * 100.0, "0.##"),
            F(m.CoveragePct, "0.####"),
            F(m.LengthM, "0.###"),
            "0",
            F(m.OverlapPct, "0.####"),
            F(m.WorkEfficiencyPct, "0.####"),
            lengthRatio.HasValue ? F(lengthRatio.Value, "0.###") : "",
            hitSafetyLimit.HasValue ? (hitSafetyLimit.Value ? "1" : "0") : "");

    private static string F(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    private static void Write(string path, IEnumerable<string> rows)
    {
        using var writer = new StreamWriter(path, false, Utf8NoBom);
        writer.WriteLine("polygon_id,strategy,target_coverage_pct,achieved_coverage_pct,route_length_m,mow_time_s,overlap_pct,work_efficiency_pct,length_ratio_vs_systematic,hit_safety_limit");
        foreach (string row in rows) writer.WriteLine(row);
    }
}
