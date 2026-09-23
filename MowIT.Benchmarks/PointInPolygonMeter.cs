using System.Diagnostics;
using MowIT.Domain.Entities;
using MowIT.Domain.Geometry;

namespace MowIT.Benchmarks;

public sealed record PipAccuracyResult(
    string PolygonId, int Samples, int Agreements, int Disagreements,
    double AgreementPct, double MaxDisagreementDistMm);

public sealed record PipEdgeCaseResult(
    string CaseId, string Description, string PolygonId, bool Expected, bool Actual, bool Pass);

public sealed record PipComplexityResult(int Vertices, double MeanNsPerCall);

public sealed record PipNoiseResult(
    double SigmaM, double DistanceFromEdgeM, int Trials, int Misclassified, double MisclassificationPct);

public static class PointInPolygonMeter
{
    public const int Seed = 20260101;

    public static PipAccuracyResult Accuracy(TestPolygon polygon, int samples = 100_000)
    {
        var zone  = polygon.Zone;
        var proj  = new LocalProjection(zone.Points[0]);
        var local = zone.Points.Select(proj.ToLocal).ToList();

        double minE = local.Min(p => p.East),  maxE = local.Max(p => p.East);
        double minN = local.Min(p => p.North), maxN = local.Max(p => p.North);

        var rng = new Random(Seed);
        int agreements = 0, disagreements = 0;
        double maxDistM = 0;

        for (int i = 0; i < samples; i++)
        {
            double x = minE + rng.NextDouble() * (maxE - minE);
            double y = minN + rng.NextDouble() * (maxN - minN);

            bool evenOdd = zone.Contains(proj.ToGps(x, y));
            bool winding = ContainsWinding(local, x, y);

            if (evenOdd == winding) { agreements++; continue; }

            disagreements++;
            double d = DistanceToPolygonEdges(local, x, y);
            if (d > maxDistM) maxDistM = d;
        }

        return new PipAccuracyResult(
            polygon.Id, samples, agreements, disagreements,
            100.0 * agreements / samples, maxDistM * 1000.0);
    }

    private static bool ContainsWinding(IReadOnlyList<(double East, double North)> poly, double px, double py)
    {
        int wn = 0;
        int n = poly.Count;

        for (int i = 0; i < n; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % n];

            if (a.North <= py)
            {
                if (b.North > py && IsLeft(a, b, px, py) > 0) wn++;
            }
            else if (b.North <= py && IsLeft(a, b, px, py) < 0)
            {
                wn--;
            }
        }
        return wn != 0;
    }

    private static double IsLeft((double East, double North) a, (double East, double North) b,
                                 double px, double py) =>
        (b.East - a.East) * (py - a.North) - (px - a.East) * (b.North - a.North);

    public static IReadOnlyList<PipEdgeCaseResult> EdgeCases(
        IReadOnlyDictionary<string, TestPolygon> byId)
    {
        var p1    = byId["P1"];
        var proj1 = new LocalProjection(p1.Zone.Points[0]);
        var loc1  = p1.Zone.Points.Select(proj1.ToLocal).ToList();

        double cx = loc1.Average(p => p.East), cy = loc1.Average(p => p.North);
        var mid   = ((loc1[0].East + loc1[1].East) / 2, (loc1[0].North + loc1[1].North) / 2);
        double minE = loc1.Min(p => p.East);

        var cases = new List<(string Id, string Desc, TestPolygon Poly, GpsPoint Point, bool Expected)>
        {
            ("R1", "tocka tocno na vrhu poligona", p1, p1.Zone.Points[0], true),
            ("R2", "tocka tocno na sredini brida", p1, proj1.ToGps(mid.Item1, mid.Item2), true),
            ("R3", "vodoravna zraka prolazi tocno kroz vrh", p1, proj1.ToGps(minE - 5, loc1[0].North), false),
            ("R4", "tocka 1 mm unutar ruba", p1, proj1.ToGps(cx, loc1[0].North + 0.001), true),
            ("R5", "tocka 1 mm izvan ruba", p1, proj1.ToGps(cx, loc1[0].North - 0.001), false),
            ("R6", "tocka u sredistu poligona", p1, proj1.ToGps(cx, cy), true),
            ("R7", "tocka daleko izvan poligona", p1, proj1.ToGps(cx + 1000, cy + 1000), false)
        };

        var p5    = byId["P5"];
        var proj5 = new LocalProjection(p5.Zone.Points[0]);
        var loc5  = p5.Zone.Points.Select(proj5.ToLocal).ToList();
        double notchE = (loc5[3].East + loc5[2].East) / 2;
        double notchN = (loc5[3].North + loc5[4].North) / 2;
        cases.Add(("R8", "tocka u rupi L-oblika", p5, proj5.ToGps(notchE, notchN), false));

        return cases
            .Select(c =>
            {
                bool actual = c.Poly.Zone.Contains(c.Point);
                return new PipEdgeCaseResult(c.Id, c.Desc, c.Poly.Id, c.Expected, actual, actual == c.Expected);
            })
            .ToList();
    }

    public static PipComplexityResult Complexity(int vertices, int calls = 100_000, double radiusM = 20.0)
    {
        var zone = TestPolygons.InscribedRegularZone(vertices, radiusM);
        var proj = new LocalProjection(zone.Points[0]);

        var rng = new Random(Seed);
        var queries = new GpsPoint[calls];
        for (int i = 0; i < calls; i++)
        {
            double x = (rng.NextDouble() * 2 - 1) * radiusM;
            double y = (rng.NextDouble() * 2 - 1) * radiusM;
            queries[i] = proj.ToGps(x, y);
        }

        int warmup = Math.Min(1000, calls);
        bool sink = false;
        for (int i = 0; i < warmup; i++) sink ^= zone.Contains(queries[i]);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < calls; i++) sink ^= zone.Contains(queries[i]);
        sw.Stop();

        GC.KeepAlive(sink);
        return new PipComplexityResult(vertices, sw.Elapsed.TotalMilliseconds * 1_000_000.0 / calls);
    }

    public static PipNoiseResult Noise(TestPolygon polygon, double sigmaM, double distanceM, int trials = 10_000)
    {
        var zone  = polygon.Zone;
        var proj  = new LocalProjection(zone.Points[0]);
        var local = zone.Points.Select(proj.ToLocal).ToList();

        var a = local[0];
        var b = local[1];
        double mx = (a.East + b.East) / 2, my = (a.North + b.North) / 2;

        double ex = b.East - a.East, ey = b.North - a.North;
        double len = Math.Sqrt(ex * ex + ey * ey);
        double nx = -ey / len, ny = ex / len;

        double cx = local.Average(p => p.East), cy = local.Average(p => p.North);
        if ((cx - mx) * nx + (cy - my) * ny < 0) { nx = -nx; ny = -ny; }

        double px = mx + nx * distanceM, py = my + ny * distanceM;

        var rng = new Random(Seed + (int)Math.Round(sigmaM * 1e6) + (int)Math.Round(distanceM * 1e3) * 7919);

        int misclassified = 0;
        for (int i = 0; i < trials; i++)
        {
            var (gx, gy) = NextGaussianPair(rng);
            if (!zone.Contains(proj.ToGps(px + gx * sigmaM, py + gy * sigmaM))) misclassified++;
        }

        return new PipNoiseResult(sigmaM, distanceM, trials, misclassified, 100.0 * misclassified / trials);
    }

    private static (double, double) NextGaussianPair(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        double r  = Math.Sqrt(-2.0 * Math.Log(u1));
        return (r * Math.Cos(2 * Math.PI * u2), r * Math.Sin(2 * Math.PI * u2));
    }

    private static double DistanceToPolygonEdges(
        IReadOnlyList<(double East, double North)> poly, double px, double py)
    {
        double best = double.MaxValue;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];

            double dx = b.East - a.East, dy = b.North - a.North;
            double len2 = dx * dx + dy * dy;
            double t = len2 <= 0 ? 0 : Math.Clamp(((px - a.East) * dx + (py - a.North) * dy) / len2, 0, 1);

            double qx = a.East + t * dx - px, qy = a.North + t * dy - py;
            double d = Math.Sqrt(qx * qx + qy * qy);
            if (d < best) best = d;
        }
        return best;
    }
}
