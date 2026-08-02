using MowIT.Domain.Entities;
using MowIT.Domain.Geometry;
using MowIT.Domain.Interfaces;

namespace MowIT.Domain.Strategies;

public class BoustrophedonStrategy : IMowingStrategy
{
    public string Name => "Boustrophedon (parallel rows)";

    public List<GpsPoint> GenerateRoute(BoundaryZone zone, float spacingMeters = 0.3f)
    {
        if (zone.Points.Count < 3) return [];
        if (spacingMeters <= 0) spacingMeters = 0.3f;

        var proj    = new LocalProjection(zone.Points[0]);
        var polygon = zone.Points.Select(proj.ToLocal).ToList();

        double minNorth = polygon.Min(p => p.North);
        double maxNorth = polygon.Max(p => p.North);

        var local = new List<(double East, double North)>();
        bool leftToRight = true;

        for (double north = minNorth + spacingMeters / 2; north <= maxNorth; north += spacingMeters)
        {
            var crossings = RowCrossings(polygon, north);
            if (crossings.Count < 2) continue;

            crossings.Sort();
            if (!leftToRight) crossings.Reverse();

            for (int i = 0; i + 1 < crossings.Count; i += 2)
            {
                double startEast = crossings[i];
                double endEast   = crossings[i + 1];
                int    steps     = Math.Max(1, (int)(Math.Abs(endEast - startEast) / spacingMeters));

                for (int s = 0; s <= steps; s++)
                {
                    double t    = (double)s / steps;
                    double east = startEast + t * (endEast - startEast);
                    local.Add((east, north));
                }
            }

            leftToRight = !leftToRight;
        }

        return local.Select(p => proj.ToGps(p.East, p.North)).ToList();
    }

    private static List<double> RowCrossings(IReadOnlyList<(double East, double North)> polygon, double north)
    {
        var crossings = new List<double>();
        int n = polygon.Count;

        for (int i = 0; i < n; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % n];

            if ((a.North <= north && b.North > north) ||
                (b.North <= north && a.North > north))
            {
                double t = (north - a.North) / (b.North - a.North);
                crossings.Add(a.East + t * (b.East - a.East));
            }
        }

        return crossings;
    }
}
