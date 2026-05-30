namespace MowIT.Domain.Entities;

public readonly record struct GpsPoint(double Latitude, double Longitude)
{
    public double DistanceTo(GpsPoint other)
    {
        const double R = 6371000;
        double dLat = (other.Latitude  - Latitude)  * Math.PI / 180;
        double dLon = (other.Longitude - Longitude) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(Latitude * Math.PI / 180)
                 * Math.Cos(other.Latitude * Math.PI / 180)
                 * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    public double BearingTo(GpsPoint other)
    {
        double dLon = (other.Longitude - Longitude) * Math.PI / 180;
        double lat1 = Latitude  * Math.PI / 180;
        double lat2 = other.Latitude * Math.PI / 180;
        double y = Math.Sin(dLon) * Math.Cos(lat2);
        double x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    public override string ToString() => $"{Latitude:F6}°, {Longitude:F6}°";
}
