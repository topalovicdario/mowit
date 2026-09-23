using MowIT.Domain.Entities;
using MowIT.Domain.Geometry;

namespace MowIT.Benchmarks;

public static class RandomWalkGenerator
{
    public static List<GpsPoint> Generate(
        BoundaryZone zone,
        double targetLengthMeters,
        double stepMeters = 0.05,
        int seed = 20260101)
    {
        var proj = new LocalProjection(zone.Points[0]);
        var rng  = new Random(seed);

        var localPoly = zone.Points.Select(proj.ToLocal).ToList();
        double cx = localPoly.Average(p => p.East);
        double cy = localPoly.Average(p => p.North);

        double x = cx, y = cy;
        double heading = rng.NextDouble() * 2 * Math.PI;

        var trace = new List<(double East, double North)> { (x, y) };
        double traveled = 0;

        int maxSteps = (int)(targetLengthMeters / stepMeters) * 4;
        int steps = 0;

        while (traveled < targetLengthMeters && steps++ < maxSteps)
        {
            double nx = x + Math.Cos(heading) * stepMeters;
            double ny = y + Math.Sin(heading) * stepMeters;

            var candidate = proj.ToGps(nx, ny);

            if (!zone.Contains(candidate))
            {
                heading += Math.PI + (rng.NextDouble() - 0.5) * Math.PI;
                continue;
            }

            x = nx; y = ny;
            trace.Add((x, y));
            traveled += stepMeters;
        }

        return trace.Select(p => proj.ToGps(p.East, p.North)).ToList();
    }
}
