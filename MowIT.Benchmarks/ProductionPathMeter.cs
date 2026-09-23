using System.Diagnostics;
using System.Globalization;
using System.Text;
using MowIT.Application.Services;
using MowIT.Domain.Entities;
using MowIT.Domain.Interfaces;
using MowIT.Domain.Strategies;

namespace MowIT.Benchmarks;

public static class ProductionPathMeter
{
    private const int WarmupRuns   = 3;
    private const int Repetitions  = 20;
    private const double ToleranceM = 0.01;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private sealed record ProductionRow(
        string PolygonId, double RealAreaM2, string Strategy, double SpacingM, double SwathM,
        double OverlapRatio, int RoutePoints, double CoveragePct, double OverlapPct,
        double RouteLengthM, double MeanMs);

    private sealed record SimplificationRow(
        string PolygonId, string Strategy, int PointsFull, int PointsReduced,
        double ReductionPct, double CoverageFullPct, double CoverageReducedPct, double DeltaPp);

    public static void RunAndWrite(string resultsDir)
    {
        Console.WriteLine();
        Console.WriteLine("Dorada 2 - produkcijski put (planer.Plan) i kontrola uklanjanja kolinearnih tocaka");

        var planner = new MowingRoutePlanner(new IMowingStrategy[]
        {
            new BoustrophedonStrategy(),
            new SpiralInwardStrategy(),
        });

        string[] names = ["Boustrophedon", "SpiralInward"];
        float spacing = MowingRoutePlanner.RowSpacing(planner.SwathWidthM, planner.OverlapRatio);

        var productionRows     = new List<ProductionRow>();
        var simplificationRows = new List<SimplificationRow>();

        foreach (var p in TestPolygons.All)
        {
            double area = p.Zone.AreaSquareMeters();

            for (int si = 0; si < planner.Strategies.Count; si++)
            {
                string name = names[si];

                var route = planner.Plan(p.Zone, si);
                var cov   = CoverageMeter.Measure(p.Zone, route, planner.SwathWidthM);
                double routeLen = CoverageMeter.RouteLength(p.Zone, route);
                double meanMs   = MeasurePlanTiming(planner, p.Zone, si);

                productionRows.Add(new ProductionRow(
                    p.Id, area, name, spacing, planner.SwathWidthM, planner.OverlapRatio,
                    route.Count, cov.CoveragePct, cov.OverlapPct, routeLen, meanMs));

                Console.WriteLine($"  {p.Id} {name,-14} razmak={spacing:F3} m  tocaka={route.Count,5}  " +
                                  $"pokrivenost={cov.CoveragePct,6:F2} %  preklapanje={cov.OverlapPct,6:F2} %  " +
                                  $"{meanMs:F3} ms");

                var strategy = planner.Strategies[si];
                var full     = strategy.GenerateRoute(p.Zone, spacing);
                var reduced  = MowingRoutePlanner.RemoveCollinearForTest(full, ToleranceM);

                var covFull    = CoverageMeter.Measure(p.Zone, full,    planner.SwathWidthM);
                var covReduced = CoverageMeter.Measure(p.Zone, reduced, planner.SwathWidthM);
                double deltaPp = Math.Abs(covFull.CoveragePct - covReduced.CoveragePct);
                double reductionPct = full.Count == 0 ? 0 : 100.0 * (1.0 - (double)reduced.Count / full.Count);

                simplificationRows.Add(new SimplificationRow(
                    p.Id, name, full.Count, reduced.Count, reductionPct,
                    covFull.CoveragePct, covReduced.CoveragePct, deltaPp));

                string warn = deltaPp > 0.1 ? "  UPOZORENJE: delta > 0,1 postotnog boda" : "";
                Console.WriteLine($"    uklanjanje: {full.Count,5} -> {reduced.Count,5} tocaka " +
                                  $"({reductionPct,5:F1} %), delta pokrivenosti={deltaPp:F4} pb{warn}");
            }
        }

        WriteProduction(Path.Combine(resultsDir, "production_path.csv"), productionRows);
        WriteSimplification(Path.Combine(resultsDir, "route_simplification.csv"), simplificationRows);
    }

    private static double MeasurePlanTiming(MowingRoutePlanner planner, BoundaryZone zone, int strategyIndex)
    {
        for (int i = 0; i < WarmupRuns; i++) _ = planner.Plan(zone, strategyIndex);

        var samples = new double[Repetitions];
        for (int i = 0; i < Repetitions; i++)
        {
            var sw = Stopwatch.StartNew();
            _ = planner.Plan(zone, strategyIndex);
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }
        return samples.Average();
    }

    private static void WriteProduction(string path, IEnumerable<ProductionRow> rows)
    {
        using var writer = new StreamWriter(path, false, Utf8NoBom);
        writer.WriteLine("polygon_id,real_area_m2,strategy,spacing_m,swath_m,overlap_ratio," +
                         "route_points,coverage_pct,overlap_pct,route_length_m,mean_ms");
        foreach (var r in rows)
            writer.WriteLine(string.Join(',',
                r.PolygonId, F(r.RealAreaM2, "0.###"), r.Strategy, F(r.SpacingM, "0.###"),
                F(r.SwathM, "0.###"), F(r.OverlapRatio, "0.###"),
                r.RoutePoints.ToString(CultureInfo.InvariantCulture),
                F(r.CoveragePct, "0.####"), F(r.OverlapPct, "0.####"),
                F(r.RouteLengthM, "0.###"), F(r.MeanMs, "0.#####")));
    }

    private static void WriteSimplification(string path, IEnumerable<SimplificationRow> rows)
    {
        using var writer = new StreamWriter(path, false, Utf8NoBom);
        writer.WriteLine("polygon_id,strategy,points_full,points_reduced,reduction_pct," +
                         "coverage_full_pct,coverage_reduced_pct,delta_pp");
        foreach (var r in rows)
            writer.WriteLine(string.Join(',',
                r.PolygonId, r.Strategy,
                r.PointsFull.ToString(CultureInfo.InvariantCulture),
                r.PointsReduced.ToString(CultureInfo.InvariantCulture),
                F(r.ReductionPct, "0.##"), F(r.CoverageFullPct, "0.####"),
                F(r.CoverageReducedPct, "0.####"), F(r.DeltaPp, "0.####")));
    }

    private static string F(double value, string format = "0.######") =>
        value.ToString(format, CultureInfo.InvariantCulture);
}
