using System.Globalization;
using System.Text;
using MowIT.Domain.Entities;
using MowIT.Domain.Geometry;

namespace MowIT.Benchmarks;

public static class ProjectionMeter
{
    private const int Seed    = 20260101;
    private const int Samples = 10_000;

    private static readonly double[] SweepDistancesM = [1, 10, 100, 1000, 5000];

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private sealed record Row(double DistanceFromOriginM, int Samples, double MeanErrorMm, double MaxErrorMm);

    public static void RunAndWrite(string resultsDir)
    {
        Console.WriteLine();
        Console.WriteLine($"R4 - povratna pretvorba koordinata (LocalProjection), " +
                          $"{Samples} uzoraka po retku, sjeme {Seed}");

        var rows = new List<Row>();

        foreach (var polygon in TestPolygons.All)
        {
            var row = MeasurePolygon(polygon);
            rows.Add(row);
            Console.WriteLine($"  {polygon.Id} d={F(row.DistanceFromOriginM, "0.###")} m: " +
                              $"srednja pogreska={F(row.MeanErrorMm, "0.##########")} mm, " +
                              $"max={F(row.MaxErrorMm, "0.##########")} mm");
        }

        foreach (double d in SweepDistancesM)
        {
            var row = MeasureSweep(d);
            rows.Add(row);
            Console.WriteLine($"  udaljenost d={F(row.DistanceFromOriginM, "0.###")} m: " +
                              $"srednja pogreska={F(row.MeanErrorMm, "0.##########")} mm, " +
                              $"max={F(row.MaxErrorMm, "0.##########")} mm");
        }

        Write(Path.Combine(resultsDir, "projection_roundtrip.csv"), rows);
    }

    private static Row MeasurePolygon(TestPolygon polygon)
    {
        var zone  = polygon.Zone;
        var proj  = new LocalProjection(zone.Points[0]);
        var local = zone.Points.Select(proj.ToLocal).ToList();

        double minE = local.Min(p => p.East),  maxE = local.Max(p => p.East);
        double minN = local.Min(p => p.North), maxN = local.Max(p => p.North);
        double halfDiagonalM = Math.Sqrt(
            (maxE - minE) * (maxE - minE) + (maxN - minN) * (maxN - minN)) / 2.0;

        var rng = new Random(Seed);
        double sumMm = 0, maxMm = 0;

        for (int i = 0; i < Samples; i++)
        {
            double e = minE + rng.NextDouble() * (maxE - minE);
            double n = minN + rng.NextDouble() * (maxN - minN);

            double errorMm = RoundTripErrorMm(proj, e, n);
            sumMm += errorMm;
            if (errorMm > maxMm) maxMm = errorMm;
        }

        return new Row(halfDiagonalM, Samples, sumMm / Samples, maxMm);
    }

    private static Row MeasureSweep(double distanceM)
    {
        var proj = new LocalProjection(TestPolygons.Origin);

        var rng = new Random(Seed + (int)Math.Round(distanceM));
        double sumMm = 0, maxMm = 0;

        for (int i = 0; i < Samples; i++)
        {
            double angle = rng.NextDouble() * 2.0 * Math.PI;
            double e = distanceM * Math.Cos(angle);
            double n = distanceM * Math.Sin(angle);

            double errorMm = RoundTripErrorMm(proj, e, n);
            sumMm += errorMm;
            if (errorMm > maxMm) maxMm = errorMm;
        }

        return new Row(distanceM, Samples, sumMm / Samples, maxMm);
    }

    private static double RoundTripErrorMm(LocalProjection proj, double e, double n)
    {
        var gps   = proj.ToGps(e, n);
        var local = proj.ToLocal(gps);
        var back  = proj.ToGps(local.East, local.North);
        return gps.DistanceTo(back) * 1000.0;
    }

    private static string F(double value, string format = "0.######") =>
        value.ToString(format, CultureInfo.InvariantCulture);

    private static void Write(string path, IEnumerable<Row> rows)
    {
        using var writer = new StreamWriter(path, false, Utf8NoBom);
        writer.WriteLine("distance_from_origin_m,samples,mean_error_mm,max_error_mm");

        foreach (var r in rows)
            writer.WriteLine(string.Join(',',
                F(r.DistanceFromOriginM, "0.###"),
                r.Samples.ToString(CultureInfo.InvariantCulture),
                F(r.MeanErrorMm, "0.##########"),
                F(r.MaxErrorMm, "0.##########")));
    }
}
