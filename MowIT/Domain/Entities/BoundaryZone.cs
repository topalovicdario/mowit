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
        int n = Points.Count;
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = Points[i].Longitude, yi = Points[i].Latitude;
            double xj = Points[j].Longitude, yj = Points[j].Latitude;
            bool intersects = ((yi > point.Latitude) != (yj > point.Latitude))
                           && (point.Longitude < (xj - xi) * (point.Latitude - yi) / (yj - yi) + xi);
            if (intersects) inside = !inside;
        }
        return inside;
    }

public double AreaSquareMeters()
    {
        if (Points.Count < 3) return 0;
        var origin = Points[0];
        double area = 0;
        for (int i = 0; i < Points.Count; i++)
        {
            var a = ToLocal(Points[i], origin);
            var b = ToLocal(Points[(i + 1) % Points.Count], origin);
            area += a.x * b.y - b.x * a.y;
        }
        return Math.Abs(area) / 2;
    }

public IEnumerable<GpsPoint[]> GetChunkedEnumerator(int chunkSize = 1)
    {
        for (int i = 0; i < Points.Count; i += chunkSize)
            yield return Points.Skip(i).Take(chunkSize).ToArray();
    }

    private static (double x, double y) ToLocal(GpsPoint p, GpsPoint origin)
    {
        const double R = 6371000;
        double x = R * Math.Cos(origin.Latitude * Math.PI / 180)
                     * (p.Longitude - origin.Longitude) * Math.PI / 180;
        double y = R * (p.Latitude - origin.Latitude) * Math.PI / 180;
        return (x, y);
    }
}
