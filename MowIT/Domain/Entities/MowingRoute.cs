namespace MowIT.Domain.Entities;

public class MowingRoute
{
    public int            Id          { get; set; }
    public string         Name        { get; set; } = string.Empty;
    public List<GpsPoint> Waypoints   { get; set; } = new();
    public DateTime       GeneratedAt { get; set; }
}
