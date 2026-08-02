using MowIT.Domain.Entities;
using MowIT.Domain.Geometry;
using MowIT.Domain.Interfaces;

namespace MowIT.Domain.Strategies;

public class SpiralInwardStrategy : IMowingStrategy
{
    public string Name => "Spiral Inward";

    public List<GpsPoint> GenerateRoute(BoundaryZone zone, float spacingMeters = 0.3f)
    {
        if (zone.Points.Count < 3) return [];
        if (spacingMeters <= 0) spacingMeters = 0.3f;

        var proj  = new LocalProjection(zone.Points[0]);
        var shell = zone.Points.Select(proj.ToLocal).ToList();

        var route = new List<(double East, double North)>();

        while (shell.Count >= 3)
        {
            route.AddRange(shell);
            shell = Shrink(shell, spacingMeters);
        }

        return route.Select(p => proj.ToGps(p.East, p.North)).ToList();
    }

    private static List<(double East, double North)> Shrink(
        List<(double East, double North)> polygon, double amountMeters)
    {
        double centerEast  = polygon.Average(p => p.East);
        double centerNorth = polygon.Average(p => p.North);

        var shrunk = new List<(double East, double North)>();
        foreach (var pt in polygon)
        {
            double dEast  = pt.East  - centerEast;
            double dNorth = pt.North - centerNorth;
            double dist   = Math.Sqrt(dEast * dEast + dNorth * dNorth);

            if (dist <= amountMeters) return [];

            double scale = (dist - amountMeters) / dist;
            shrunk.Add((centerEast + dEast * scale, centerNorth + dNorth * scale));
        }

        return shrunk;
    }
}
