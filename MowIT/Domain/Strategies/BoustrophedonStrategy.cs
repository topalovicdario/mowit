using MowIT.Domain.Entities;
using MowIT.Domain.Interfaces;

namespace MowIT.Domain.Strategies;

public class BoustrophedonStrategy : IMowingStrategy
{
    public string Name => "Boustrophedon (parallel rows)";

    public List<GpsPoint> GenerateRoute(BoundaryZone zone, float spacingMeters = 0.3f)
    {
        var pts = zone.Points;
        if (pts.Count < 3) return [];

        double minLat = pts.Min(p => p.Latitude);
        double maxLat = pts.Max(p => p.Latitude);
        double minLon = pts.Min(p => p.Longitude);
        double maxLon = pts.Max(p => p.Longitude);

double spacingDeg = spacingMeters / 111_320.0;

        var route = new List<GpsPoint>();
        bool leftToRight = true;

        for (double lat = minLat + spacingDeg / 2; lat <= maxLat; lat += spacingDeg)
        {
            var intersections = GetRowIntersections(pts, lat);
            if (intersections.Count < 2) continue;

            intersections.Sort();

            if (!leftToRight) intersections.Reverse();

            for (int i = 0; i < intersections.Count - 1; i += 2)
            {
                double startLon = intersections[i];
                double endLon   = intersections[i + 1];
                int steps = Math.Max(1, (int)((endLon - startLon) / spacingDeg));

                for (int s = 0; s <= steps; s++)
                {
                    double lon = leftToRight
                        ? startLon + s * (endLon - startLon) / steps
                        : endLon   - s * (endLon - startLon) / steps;
                    route.Add(new GpsPoint { Latitude = lat, Longitude = lon });
                }
            }

            leftToRight = !leftToRight;
        }

        return route;
    }

    private static List<double> GetRowIntersections(IList<GpsPoint> polygon, double lat)
    {
        var intersections = new List<double>();
        int n = polygon.Count;

        for (int i = 0; i < n; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % n];

            if ((a.Latitude <= lat && b.Latitude > lat) ||
                (b.Latitude <= lat && a.Latitude > lat))
            {
                double t   = (lat - a.Latitude) / (b.Latitude - a.Latitude);
                double lon = a.Longitude + t * (b.Longitude - a.Longitude);
                intersections.Add(lon);
            }
        }

        return intersections;
    }
}
