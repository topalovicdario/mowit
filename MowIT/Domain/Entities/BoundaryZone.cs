using MowIT.Domain.Geometry;

namespace MowIT.Domain.Entities;

public class BoundaryZone
{
    public int            Id        { get; set; }
    public string         Name      { get; set; } = string.Empty;
    public List<GpsPoint> Points    { get; set; } = new();
    public DateTime       CreatedAt { get; set; }

    public bool IsValid => Points.Count >= 3;

    public bool Contains(GpsPoint point)
    {
        if (Points.Count < 3) return false;

        var proj    = new LocalProjection(Points[0]);
        var polygon = Points.Select(proj.ToLocal).ToList();
        var (px, py) = proj.ToLocal(point);

        int n = polygon.Count;
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var (xi, yi) = polygon[i];
            var (xj, yj) = polygon[j];
            bool intersects = ((yi > py) != (yj > py))
                           && (px < (xj - xi) * (py - yi) / (yj - yi) + xi);
            if (intersects) inside = !inside;
        }
        return inside;
    }

    public double DistanceToBoundaryMeters(GpsPoint point)
    {
        if (Points.Count < 2) return double.NaN;

        var proj     = new LocalProjection(Points[0]);
        var polygon  = Points.Select(proj.ToLocal).ToList();
        var (px, py) = proj.ToLocal(point);

        double best = double.MaxValue;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            double d = DistanceToSegment(
                px, py,
                polygon[j].East, polygon[j].North,
                polygon[i].East, polygon[i].North);
            if (d < best) best = d;
        }
        return best;
    }

    private static double DistanceToSegment(
        double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double len2 = dx * dx + dy * dy;

        double t = len2 <= 0 ? 0 : ((px - ax) * dx + (py - ay) * dy) / len2;
        t = Math.Clamp(t, 0, 1);

        double cx = ax + t * dx, cy = ay + t * dy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    public double AreaSquareMeters()
    {
        if (Points.Count < 3) return 0;

        var proj  = new LocalProjection(Points[0]);
        var local = Points.Select(proj.ToLocal).ToList();

        double area = 0;
        for (int i = 0; i < local.Count; i++)
        {
            var a = local[i];
            var b = local[(i + 1) % local.Count];
            area += a.East * b.North - b.East * a.North;
        }
        return Math.Abs(area) / 2;
    }

    public IEnumerable<GpsPoint[]> GetChunkedEnumerator(int chunkSize = 1)
    {
        for (int i = 0; i < Points.Count; i += chunkSize)
            yield return Points.Skip(i).Take(chunkSize).ToArray();
    }
}
