using MowIT.Domain.Entities;
using MowIT.Domain.Interfaces;

namespace MowIT.Domain.Strategies;

public class SpiralInwardStrategy : IMowingStrategy
{
    public string Name => "Spiral Inward";

    public List<GpsPoint> GenerateRoute(BoundaryZone zone, float spacingMeters = 0.3f)
    {
        var route  = new List<GpsPoint>();
        var shell  = zone.Points.ToList();

        double spacingDeg = spacingMeters / 111_320.0;

        while (shell.Count >= 3)
        {
            route.AddRange(shell);
            shell = ShrinkPolygon(shell, spacingDeg);
        }

        return route;
    }

    private static List<GpsPoint> ShrinkPolygon(List<GpsPoint> polygon, double amount)
    {
        
        double cLat = polygon.Average(p => p.Latitude);
        double cLon = polygon.Average(p => p.Longitude);

        var shrunk = new List<GpsPoint>();
        foreach (var pt in polygon)
        {
            double dLat = pt.Latitude  - cLat;
            double dLon = pt.Longitude - cLon;
            double dist = Math.Sqrt(dLat * dLat + dLon * dLon);

            if (dist <= amount) return [];

            double scale = (dist - amount) / dist;
            shrunk.Add(new GpsPoint
            {
                Latitude  = cLat + dLat * scale,
                Longitude = cLon + dLon * scale
            });
        }

        return shrunk;
    }
}
