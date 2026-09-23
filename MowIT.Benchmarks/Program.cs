using System.Diagnostics;
using System.Globalization;
using System.Text;
using MowIT.Application.Services;
using MowIT.Domain.Entities;
using MowIT.Domain.Geometry;
using MowIT.Domain.Interfaces;
using MowIT.Domain.Strategies;

namespace MowIT.Benchmarks;

internal static class Program
{
    private const double SwathWidthM = 0.25;
    private const double CellSizeM   = 0.05;
    private const int    NoiseTrials = 10_000;
    private const int    PipSamples  = 100_000;
    private const int    PipCalls    = 100_000;

    private static readonly double[] Spacings   = [0.20, 0.25, 0.30, 0.40, 0.50, 0.75, 1.00];
    private static readonly double[] NoiseSigmas = [0.084, 0.012];
    private static readonly double[] NoiseDistances = [0, 0.02, 0.05, 0.084, 0.10, 0.15, 0.20, 0.30];
    private static readonly int[] ComplexityVertices = [4, 10, 50, 100, 500, 1000];

    private const double ExportSpacingM = 0.25;
    private static readonly string[] ExportPolygons = ["P1", "P2", "P3", "P4", "P5", "P6", "P7", "P8"];

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private sealed record CoverageRow(
        string PolygonId, string Description, double AreaM2, string Strategy,
        double SpacingM, double SwathM, CoverageResult Coverage,
        int RoutePoints, double RouteLengthM, int Turns);

    private sealed record TimingRow(
        string PolygonId, double AreaM2, string Strategy, double SpacingM, TimingResult Timing);

    private static int Main()
    {
        var total = Stopwatch.StartNew();
        string resultsDir = Path.Combine(AppContext.BaseDirectory, "results");
        Directory.CreateDirectory(resultsDir);

        Console.WriteLine("MowIT.Benchmarks - dio A (offline mjerenja algoritama)");
        Console.WriteLine($"Izlaz: {resultsDir}");
        Console.WriteLine();

        var polygons = TestPolygons.All;
        var byId     = polygons.ToDictionary(p => p.Id);

        CheckPolygons(polygons);

        var coverageRows = RunCoverage(polygons);
        var timingRows   = RunTiming(polygons);

        Console.WriteLine();
        Console.WriteLine("A4 - pripadnost tocke poligonu");

        var accuracy = polygons.Select(p =>
        {
            var r = PointInPolygonMeter.Accuracy(p, PipSamples);
            Console.WriteLine($"  A4.1 {p.Id}: slaganje {r.AgreementPct:F4} % " +
                              $"({r.Disagreements} neslaganja, max {r.MaxDisagreementDistMm:F4} mm od ruba)");
            return r;
        }).ToList();

        var edgeCases = PointInPolygonMeter.EdgeCases(byId);
        foreach (var c in edgeCases)
            Console.WriteLine($"  A4.2 {c.CaseId} [{c.PolygonId}] {c.Description}: " +
                              $"ocekivano={c.Expected}, dobiveno={c.Actual}, {(c.Pass ? "PROLAZ" : "PAD")}");

        var complexity = ComplexityVertices.Select(n =>
        {
            var r = PointInPolygonMeter.Complexity(n, PipCalls);
            Console.WriteLine($"  A4.3 n={n,4}: {r.MeanNsPerCall:F1} ns/poziv");
            return r;
        }).ToList();

        var noise = new List<PipNoiseResult>();
        foreach (double sigma in NoiseSigmas)
        {
            foreach (double d in NoiseDistances)
                noise.Add(PointInPolygonMeter.Noise(byId["P1"], sigma, d, NoiseTrials));

            var atZero = noise.First(r => r.SigmaM == sigma && r.DistanceFromEdgeM == 0);
            var atMax  = noise.Last(r => r.SigmaM == sigma);
            Console.WriteLine($"  A4.4 sigma={sigma:F3} m: pogreska {atZero.MisclassificationPct:F2} % pri d=0, " +
                              $"{atMax.MisclassificationPct:F2} % pri d={atMax.DistanceFromEdgeM:F2} m");
        }

        ExportPlotData(resultsDir, byId);
        RunBlePackets(resultsDir);
        RouteFidelityMeter.RunAndWrite(resultsDir);

        WriteCoverage(resultsDir, coverageRows);
        WriteTiming(resultsDir, timingRows);
        WriteAccuracy(resultsDir, accuracy);
        WriteEdgeCases(resultsDir, edgeCases);
        WriteComplexity(resultsDir, complexity);
        WriteNoise(resultsDir, noise);

        ProductionPathMeter.RunAndWrite(resultsDir);
        ProjectionMeter.RunAndWrite(resultsDir);
        EqualCoverageMeter.RunAndWrite(resultsDir);
        GeofenceMonitorMeter.RunAndWriteAsync(resultsDir).GetAwaiter().GetResult();

        WriteRunInfo(resultsDir, total.Elapsed);

        total.Stop();
        Console.WriteLine();
        Console.WriteLine($"Gotovo za {total.Elapsed.TotalSeconds:F1} s. Sve CSV datoteke, " +
                          $"routes\\, rasters\\ i run_info.txt u {resultsDir}");
        return 0;
    }

    private static void CheckPolygons(IReadOnlyList<TestPolygon> polygons)
    {
        Console.WriteLine("A1 - kontrola ispitnog skupa poligona");
        foreach (var p in polygons)
        {
            double measured = p.Zone.AreaSquareMeters();
            double relative = Math.Abs(measured - p.NominalAreaM2) / p.NominalAreaM2;

            Console.WriteLine($"  {p.Id} {p.Description,-38} vrhova={p.Zone.Points.Count} " +
                              $"nominalno={p.NominalAreaM2,8:F2} m2  izmjereno={measured,8:F2} m2  " +
                              $"odstupanje={relative * 100:F4} %");

            if (relative > 0.01)
                Console.WriteLine($"  UPOZORENJE: {p.Id} odstupa vise od 1 % od nominalne povrsine.");
        }
    }

    private static List<CoverageRow> RunCoverage(IReadOnlyList<TestPolygon> polygons)
    {
        Console.WriteLine();
        Console.WriteLine($"A2 + A2b - pokrivenost i preklapanje (w = {SwathWidthM} m, celija {CellSizeM} m)");

        var rows = new List<CoverageRow>();

        foreach (var p in polygons)
        {
            var sw = Stopwatch.StartNew();
            double area = p.Zone.AreaSquareMeters();

            foreach (double s in Spacings)
            {
                var boustrophedon = new BoustrophedonStrategy().GenerateRoute(p.Zone, (float)s);
                rows.Add(MakeRow(p, area, "Boustrophedon", s, boustrophedon));

                var spiral = new SpiralInwardStrategy().GenerateRoute(p.Zone, (float)s);
                rows.Add(MakeRow(p, area, "SpiralInward", s, spiral));

                rows.Add(MakeRow(p, area, "RandomWalk", s, MakeRandomWalk(p, boustrophedon)));
            }

            sw.Stop();
            var atQuarter = rows.Where(r => r.PolygonId == p.Id && Math.Abs(r.SpacingM - 0.25) < 1e-9)
                                .ToDictionary(r => r.Strategy);
            Console.WriteLine($"  {p.Id} ({area:F0} m2) gotovo za {sw.Elapsed.TotalSeconds:F1} s | pri s=0,25 m: " +
                              $"Bou {atQuarter["Boustrophedon"].Coverage.CoveragePct:F1} %, " +
                              $"Spi {atQuarter["SpiralInward"].Coverage.CoveragePct:F1} %, " +
                              $"Rnd {atQuarter["RandomWalk"].Coverage.CoveragePct:F1} %");
        }

        return rows;
    }

    private static List<GpsPoint> MakeRandomWalk(TestPolygon p, List<GpsPoint> boustrophedon) =>
        RandomWalkGenerator.Generate(
            p.Zone, CoverageMeter.RouteLength(p.Zone, boustrophedon), CellSizeM, PointInPolygonMeter.Seed);


    private static CoverageRow MakeRow(
        TestPolygon p, double area, string strategy, double spacing, List<GpsPoint> route)
    {
        var result = CoverageMeter.Measure(p.Zone, route, SwathWidthM, CellSizeM);
        return new CoverageRow(
            p.Id, p.Description, area, strategy, spacing, SwathWidthM, result,
            route.Count, CoverageMeter.RouteLength(p.Zone, route), CoverageMeter.CountTurns(p.Zone, route));
    }

    private static List<TimingRow> RunTiming(IReadOnlyList<TestPolygon> polygons)
    {
        Console.WriteLine();
        Console.WriteLine("A3 - vrijeme izvodenja generiranja rute (3 zagrijavanja + 20 ponavljanja)");

        var rows = new List<TimingRow>();
        IMowingStrategy[] strategies = [new BoustrophedonStrategy(), new SpiralInwardStrategy()];
        string[] names = ["Boustrophedon", "SpiralInward"];

        foreach (var p in polygons)
        {
            double area = p.Zone.AreaSquareMeters();
            for (int i = 0; i < strategies.Length; i++)
            {
                foreach (double s in Spacings)
                    rows.Add(new TimingRow(p.Id, area, names[i], s, TimingMeter.Measure(strategies[i], p.Zone, s)));

                var slowest = rows.Where(r => r.PolygonId == p.Id && r.Strategy == names[i])
                                  .MaxBy(r => r.Timing.MeanMs)!;
                Console.WriteLine($"  {p.Id} {names[i],-14} najsporije: {slowest.Timing.MeanMs:F3} ms " +
                                  $"pri s={slowest.SpacingM:F2} m ({slowest.Timing.RoutePoints} tocaka)");
            }
        }

        return rows;
    }

    private static void ExportPlotData(string dir, IReadOnlyDictionary<string, TestPolygon> byId)
    {
        Console.WriteLine();
        Console.WriteLine($"A7 - izvoz poligona, ruta i rasterskih karata " +
                          $"(s = {F(ExportSpacingM)} m, w = {F(SwathWidthM)} m, celija {F(CellSizeM)} m)");

        string routesDir  = Path.Combine(dir, "routes");
        string rastersDir = Path.Combine(dir, "rasters");
        Directory.CreateDirectory(routesDir);
        Directory.CreateDirectory(rastersDir);

        var polygonLines = new List<string>();

        foreach (string id in ExportPolygons)
        {
            var p    = byId[id];
            var proj = new LocalProjection(p.Zone.Points[0]);

            for (int i = 0; i < p.Zone.Points.Count; i++)
            {
                var g = p.Zone.Points[i];
                var (east, north) = proj.ToLocal(g);
                polygonLines.Add(string.Join(',',
                    id, i.ToString(CultureInfo.InvariantCulture),
                    FMetres(east), FMetres(north),
                    F(g.Latitude, "0.#########"), F(g.Longitude, "0.#########")));
            }

            var boustrophedon = new BoustrophedonStrategy().GenerateRoute(p.Zone, (float)ExportSpacingM);
            (string Name, List<GpsPoint> Route)[] routes =
            [
                ("Boustrophedon", boustrophedon),
                ("SpiralInward",  new SpiralInwardStrategy().GenerateRoute(p.Zone, (float)ExportSpacingM)),
                ("RandomWalk",    MakeRandomWalk(p, boustrophedon))
            ];

            foreach (var (name, route) in routes)
            {
                string suffix = $"{id}_{name}_{F(ExportSpacingM, "0.##")}";
                WriteRoute(Path.Combine(routesDir, $"route_{suffix}.csv"), proj, route);

                var raster = CoverageMeter.MeasureWithRaster(p.Zone, route, SwathWidthM, CellSizeM);
                WriteRaster(Path.Combine(rastersDir, $"raster_{suffix}.csv"), id, name, raster);

                var direct = CoverageMeter.Measure(p.Zone, route, SwathWidthM, CellSizeM);
                var fromCells = SummarizeRaster(raster);

                double diff = Math.Max(
                    Math.Abs(direct.CoveragePct - fromCells.CoveragePct),
                    Math.Max(Math.Abs(direct.OverlapPct - fromCells.OverlapPct),
                             Math.Abs(direct.OutsidePct - fromCells.OutsidePct)));

                Console.WriteLine($"  {id} {name,-14} tocaka={route.Count,6} mreza={raster.Nx}x{raster.Ny} | " +
                                  $"pokrivenost {direct.CoveragePct:F2} %, preklapanje {direct.OverlapPct:F2} %, " +
                                  $"izvan {direct.OutsidePct:F2} % | raster-Measure = {diff:E1}");

                if (diff > 1e-9)
                    Console.WriteLine($"  UPOZORENJE: raster i Measure se razilaze za {diff:E3} postotnih bodova.");
            }
        }

        Write(Path.Combine(dir, "polygons.csv"),
            "polygon_id,vertex_idx,east_m,north_m,lat,lon", polygonLines);
    }

    private static void WriteRoute(string path, LocalProjection proj, IReadOnlyList<GpsPoint> route)
    {
        using var writer = new StreamWriter(path, false, Utf8NoBom);
        writer.WriteLine("idx,east_m,north_m,lat,lon");

        for (int i = 0; i < route.Count; i++)
        {
            var (east, north) = proj.ToLocal(route[i]);
            writer.WriteLine(string.Join(',',
                i.ToString(CultureInfo.InvariantCulture),
                FMetres(east), FMetres(north),
                F(route[i].Latitude, "0.#########"), F(route[i].Longitude, "0.#########")));
        }
    }

    private static void WriteRaster(string path, string polygonId, string strategy, CoverageRaster raster)
    {
        using var writer = new StreamWriter(path, false, Utf8NoBom);

        writer.WriteLine($"# polygon_id={polygonId} strategy={strategy} spacing_m={F(ExportSpacingM)} " +
                         $"swath_m={F(SwathWidthM)} cell_m={F(raster.CellSizeM)}");
        writer.WriteLine($"# min_east_m={F(raster.MinEastM, "0.######")} min_north_m={F(raster.MinNorthM, "0.######")} " +
                         $"nx={raster.Nx} ny={raster.Ny}");

        var sb = new StringBuilder(raster.Nx * 4);
        for (int gy = 0; gy < raster.Ny; gy++)
        {
            sb.Clear();
            int rowBase = gy * raster.Nx;

            for (int gx = 0; gx < raster.Nx; gx++)
            {
                if (gx > 0) sb.Append(',');
                sb.Append(raster.Cells[rowBase + gx].ToString(CultureInfo.InvariantCulture));
            }
            writer.WriteLine(sb.ToString());
        }
    }

    private static CoverageResult SummarizeRaster(CoverageRaster raster)
    {
        int insideCells = 0, coveredInside = 0, coveredOutside = 0;
        long overlapSum = 0;

        foreach (int cell in raster.Cells)
        {
            if (cell == -2) { coveredOutside++; continue; }
            if (cell == -1) continue;

            insideCells++;
            if (cell < 1) continue;

            coveredInside++;
            overlapSum += cell;
        }

        double coveragePct = insideCells   == 0 ? 0 : 100.0 * coveredInside / insideCells;
        double overlapPct  = coveredInside == 0 ? 0 : 100.0 * (overlapSum - coveredInside) / coveredInside;
        double outsidePct  = coveredInside == 0 ? 0 : 100.0 * coveredOutside / coveredInside;

        return new CoverageResult(
            coveragePct, overlapPct, outsidePct, insideCells, coveredInside,
            raster.CellSizeM, raster.Result.SwathWidthM);
    }

    private static void RunBlePackets(string dir)
    {
        Console.WriteLine();
        Console.WriteLine($"E - protokolni sloj BLE ({BlePacketMeter.Samples} uzoraka po tipu paketa, " +
                          $"sjeme {BlePacketMeter.Seed})");

        var packets  = BlePacketMeter.Packets();
        var findings = BlePacketMeter.Findings();
        var dayMask  = BlePacketMeter.DayMask();

        foreach (var r in packets)
            Console.WriteLine($"  {r.PacketType,-14} deklarirano={r.DeclaredBytes,3} B  izmjereno={r.MeasuredBytes,3} B  " +
                              $"povratna vjernost={(r.RoundtripOk ? "PROLAZ" : "PAD")}  " +
                              $"najveca pogreska={r.MaxAbsError:E3}");

        foreach (var f in findings)
            Console.WriteLine($"  {f.Check,-28} {f.Result,-7} {f.Detail}");

        Console.WriteLine($"  maska dana: {dayMask.Count(d => d.Match)}/{dayMask.Count} dana na ocekivanom bitu");

        WriteBlePackets(dir, packets);
        WriteBleFindings(dir, findings);
        WriteBleDayMask(dir, dayMask);
    }

    private static string F(double value, string format = "0.######") =>
        value.ToString(format, CultureInfo.InvariantCulture);

    private static string FMetres(double value) => F(Math.Round(value, 4) + 0.0, "0.####");

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private static void Write(string path, string header, IEnumerable<string> lines)
    {
        using var writer = new StreamWriter(path, false, Utf8NoBom);
        writer.WriteLine(header);
        foreach (string line in lines) writer.WriteLine(line);
    }

    private static void WriteCoverage(string dir, IEnumerable<CoverageRow> rows) =>
        Write(Path.Combine(dir, "coverage.csv"),
            "polygon_id,polygon_desc,area_m2,strategy,spacing_m,swath_m,coverage_pct,overlap_pct,outside_pct,route_points,route_length_m,turns",
            rows.Select(r => string.Join(',',
                r.PolygonId, Csv(r.Description), F(r.AreaM2, "0.###"), r.Strategy,
                F(r.SpacingM, "0.###"), F(r.SwathM, "0.###"),
                F(r.Coverage.CoveragePct, "0.####"), F(r.Coverage.OverlapPct, "0.####"),
                F(r.Coverage.OutsidePct, "0.####"),
                r.RoutePoints.ToString(CultureInfo.InvariantCulture),
                F(r.RouteLengthM, "0.###"), r.Turns.ToString(CultureInfo.InvariantCulture))));

    private static void WriteTiming(string dir, IEnumerable<TimingRow> rows) =>
        Write(Path.Combine(dir, "timing.csv"),
            "polygon_id,area_m2,strategy,spacing_m,route_points,mean_ms,median_ms,stddev_ms,min_ms,max_ms",
            rows.Select(r => string.Join(',',
                r.PolygonId, F(r.AreaM2, "0.###"), r.Strategy, F(r.SpacingM, "0.###"),
                r.Timing.RoutePoints.ToString(CultureInfo.InvariantCulture),
                F(r.Timing.MeanMs, "0.#####"), F(r.Timing.MedianMs, "0.#####"),
                F(r.Timing.StdDevMs, "0.#####"), F(r.Timing.MinMs, "0.#####"), F(r.Timing.MaxMs, "0.#####"))));

    private static void WriteAccuracy(string dir, IEnumerable<PipAccuracyResult> rows) =>
        Write(Path.Combine(dir, "pip_accuracy.csv"),
            "polygon_id,samples,agreements,disagreements,agreement_pct,max_disagreement_dist_mm",
            rows.Select(r => string.Join(',',
                r.PolygonId, r.Samples.ToString(CultureInfo.InvariantCulture),
                r.Agreements.ToString(CultureInfo.InvariantCulture),
                r.Disagreements.ToString(CultureInfo.InvariantCulture),
                F(r.AgreementPct, "0.######"), F(r.MaxDisagreementDistMm, "0.######"))));

    private static void WriteEdgeCases(string dir, IEnumerable<PipEdgeCaseResult> rows) =>
        Write(Path.Combine(dir, "pip_edge_cases.csv"),
            "case_id,description,polygon_id,expected,actual,pass",
            rows.Select(r => string.Join(',',
                r.CaseId, Csv(r.Description), r.PolygonId,
                r.Expected ? "inside" : "outside", r.Actual ? "inside" : "outside",
                r.Pass ? "true" : "false")));

    private static void WriteComplexity(string dir, IEnumerable<PipComplexityResult> rows) =>
        Write(Path.Combine(dir, "pip_complexity.csv"),
            "vertices,mean_ns_per_call",
            rows.Select(r => string.Join(',',
                r.Vertices.ToString(CultureInfo.InvariantCulture), F(r.MeanNsPerCall, "0.###"))));

    private static void WriteNoise(string dir, IEnumerable<PipNoiseResult> rows) =>
        Write(Path.Combine(dir, "pip_noise.csv"),
            "sigma_m,distance_from_edge_m,trials,misclassified,misclassification_pct",
            rows.Select(r => string.Join(',',
                F(r.SigmaM, "0.###"), F(r.DistanceFromEdgeM, "0.###"),
                r.Trials.ToString(CultureInfo.InvariantCulture),
                r.Misclassified.ToString(CultureInfo.InvariantCulture),
                F(r.MisclassificationPct, "0.####"))));

    private static void WriteBlePackets(string dir, IEnumerable<BlePacketResult> rows) =>
        Write(Path.Combine(dir, "ble_packets.csv"),
            "packet_type,declared_bytes,measured_bytes,samples,roundtrip_ok,max_abs_error",
            rows.Select(r => string.Join(',',
                r.PacketType,
                r.DeclaredBytes.ToString(CultureInfo.InvariantCulture),
                r.MeasuredBytes.ToString(CultureInfo.InvariantCulture),
                r.Samples.ToString(CultureInfo.InvariantCulture),
                r.RoundtripOk ? "1" : "0",
                F(r.MaxAbsError, "0.############"))));

    private static void WriteBleFindings(string dir, IEnumerable<BleFinding> rows) =>
        Write(Path.Combine(dir, "ble_findings.csv"),
            "check,result,detail",
            rows.Select(r => string.Join(',', Csv(r.Check), Csv(r.Result), Csv(r.Detail))));

    private static void WriteBleDayMask(string dir, IEnumerable<BleDayMaskResult> rows) =>
        Write(Path.Combine(dir, "ble_daymask.csv"),
            "day_name,dotnet_value,expected_bit,actual_bit,match",
            rows.Select(r => string.Join(',',
                r.DayName,
                r.DotNetValue.ToString(CultureInfo.InvariantCulture),
                r.ExpectedBit.ToString(CultureInfo.InvariantCulture),
                r.ActualBit.ToString(CultureInfo.InvariantCulture),
                r.Match ? "1" : "0")));

    private static void WriteRunInfo(string dir, TimeSpan elapsed)
    {
        var sb = new StringBuilder();
        sb.AppendLine("MowIT.Benchmarks - dio A");
        sb.AppendLine($"utc                 = {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"dotnet_version      = {Environment.Version}");
        sb.AppendLine($"os                  = {Environment.OSVersion}");
        sb.AppendLine($"processors          = {Environment.ProcessorCount}");
        sb.AppendLine($"git_commit          = {GitCommit()}");
        sb.AppendLine($"elapsed_s           = {F(elapsed.TotalSeconds, "0.##")}");
        sb.AppendLine();
        sb.AppendLine("parametri mjerenja");
        sb.AppendLine($"origin              = {TestPolygons.Origin.Latitude.ToString(CultureInfo.InvariantCulture)}, " +
                      $"{TestPolygons.Origin.Longitude.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"swath_width_m       = {F(SwathWidthM)}");
        sb.AppendLine($"cell_size_m         = {F(CellSizeM)}");
        sb.AppendLine($"spacings_m          = {string.Join("; ", Spacings.Select(s => F(s)))}");
        sb.AppendLine($"timing_warmup       = 3");
        sb.AppendLine($"timing_repetitions  = 20");
        sb.AppendLine($"pip_samples         = {PipSamples}");
        sb.AppendLine($"pip_complexity_n    = {string.Join("; ", ComplexityVertices)}");
        sb.AppendLine($"pip_complexity_calls= {PipCalls}");
        sb.AppendLine($"noise_sigmas_m      = {string.Join("; ", NoiseSigmas.Select(s => F(s)))}");
        sb.AppendLine($"noise_distances_m   = {string.Join("; ", NoiseDistances.Select(s => F(s)))}");
        sb.AppendLine($"noise_trials        = {NoiseTrials}");
        sb.AppendLine($"random_seed         = {PointInPolygonMeter.Seed}");
        sb.AppendLine($"export_polygons     = {string.Join("; ", ExportPolygons)}");
        sb.AppendLine($"export_spacing_m    = {F(ExportSpacingM)}");
        sb.AppendLine($"ble_samples         = {BlePacketMeter.Samples}");
        sb.AppendLine($"ble_seed            = {BlePacketMeter.Seed}");

        File.WriteAllText(Path.Combine(dir, "run_info.txt"), sb.ToString(), Utf8NoBom);
    }

    private static string GitCommit()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse HEAD")
            {
                WorkingDirectory       = @"C:\mowit",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using var process = Process.Start(psi);
            if (process is null) return "unknown";

            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return process.ExitCode == 0 && output.Length > 0 ? output : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
