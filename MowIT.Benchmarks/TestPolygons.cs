using MowIT.Domain.Entities;
using MowIT.Domain.Geometry;

namespace MowIT.Benchmarks;

public sealed record TestPolygon(string Id, string Description, BoundaryZone Zone, double NominalAreaM2);

public static class TestPolygons
{
    public static readonly GpsPoint Origin = new(43.8563, 18.4131);

    private static readonly LocalProjection Proj = new(Origin);

    public static IReadOnlyList<TestPolygon> All { get; } = Build();

    private static IReadOnlyList<TestPolygon> Build()
    {
        var list = new List<TestPolygon>
        {
            Make("P1", "kvadrat 10x10 m", 100, Square(10)),
            Make("P2", "kvadrat 25x25 m", 625, Square(25)),
            Make("P3", "kvadrat 50x50 m", 2500, Square(50)),
            Make("P4", "pravokutnik 5x40 m", 200, Rectangle(5, 40)),
            Make("P5", "L-oblik 20x20 minus 10x10", 300, LShape(20, 10)),
            Make("P6", "konveksni sedmerokut", 280, RegularPolygonWithArea(7, 280)),
            Make("P7", "kvadrat 10x10 m zarotiran 30 stupnjeva", 100, Rotate(Square(10), 5, 5, 30)),
            Make("P8", "pravokutni trokut, katete 30 m", 450, Triangle(30))
        };
        return list;
    }

    private static TestPolygon Make(string id, string description, double nominalAreaM2,
                                    IReadOnlyList<(double East, double North)> local)
    {
        var zone = new BoundaryZone
        {
            Id        = int.Parse(id[1..]),
            Name      = id,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Points    = local.Select(p => Proj.ToGps(p.East, p.North)).ToList()
        };
        return new TestPolygon(id, description, zone, nominalAreaM2);
    }

    private static (double East, double North)[] Square(double side) => Rectangle(side, side);

    private static (double East, double North)[] Rectangle(double width, double height) =>
        [(0, 0), (width, 0), (width, height), (0, height)];

    private static (double East, double North)[] LShape(double outer, double notch) =>
        [(0, 0), (outer, 0), (outer, outer - notch), (outer - notch, outer - notch),
         (outer - notch, outer), (0, outer)];

    private static (double East, double North)[] Triangle(double leg) =>
        [(0, 0), (leg, 0), (0, leg)];

    private static (double East, double North)[] RegularPolygonWithArea(int n, double areaM2)
    {
        double radius = Math.Sqrt(2 * areaM2 / (n * Math.Sin(2 * Math.PI / n)));
        return RegularPolygon(n, radius);
    }

    public static (double East, double North)[] RegularPolygon(int n, double radiusM)
    {
        var pts = new (double East, double North)[n];
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            pts[i] = (radiusM * Math.Cos(a), radiusM * Math.Sin(a));
        }
        return pts;
    }

    private static (double East, double North)[] Rotate(
        IReadOnlyList<(double East, double North)> pts, double cx, double cy, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        double c = Math.Cos(rad), s = Math.Sin(rad);

        var result = new (double East, double North)[pts.Count];
        for (int i = 0; i < pts.Count; i++)
        {
            double dx = pts[i].East - cx, dy = pts[i].North - cy;
            result[i] = (cx + dx * c - dy * s, cy + dx * s + dy * c);
        }
        return result;
    }

    public static BoundaryZone InscribedRegularZone(int vertices, double radiusM = 20.0) =>
        new()
        {
            Id     = vertices,
            Name   = $"regular-{vertices}",
            Points = RegularPolygon(vertices, radiusM).Select(p => Proj.ToGps(p.East, p.North)).ToList()
        };
}
