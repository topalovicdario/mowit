using MowIT.Domain.Entities;

namespace MowIT.Domain.Geometry;

public readonly struct LocalProjection
{
    private const double A   = 6_378_137.0;
    private const double F   = 1.0 / 298.257223563;
    private const double E2  = F * (2.0 - F);
    private const double B   = A * (1.0 - F);
    private const double Ep2 = (A * A - B * B) / (B * B);
    private const double Deg = Math.PI / 180.0;

    private readonly double _x0, _y0, _z0;
    private readonly double _sinLat, _cosLat, _sinLon, _cosLon;

    public LocalProjection(GpsPoint origin)
    {
        double lat = origin.Latitude  * Deg;
        double lon = origin.Longitude * Deg;

        _sinLat = Math.Sin(lat); _cosLat = Math.Cos(lat);
        _sinLon = Math.Sin(lon); _cosLon = Math.Cos(lon);

        (_x0, _y0, _z0) = GeodeticToEcef(lat, lon);
    }

    public (double East, double North) ToLocal(GpsPoint p)
    {
        var (x, y, z) = GeodeticToEcef(p.Latitude * Deg, p.Longitude * Deg);

        double dx = x - _x0, dy = y - _y0, dz = z - _z0;

        double east  = -_sinLon * dx + _cosLon * dy;
        double north = -_sinLat * _cosLon * dx - _sinLat * _sinLon * dy + _cosLat * dz;
        return (east, north);
    }

    public GpsPoint ToGps(double east, double north)
    {
        double dx = -_sinLon * east - _sinLat * _cosLon * north;
        double dy =  _cosLon * east - _sinLat * _sinLon * north;
        double dz =                    _cosLat * north;

        var (lat, lon) = EcefToGeodetic(_x0 + dx, _y0 + dy, _z0 + dz);
        return new GpsPoint(lat / Deg, lon / Deg);
    }

    private static (double X, double Y, double Z) GeodeticToEcef(double lat, double lon)
    {
        double sinLat = Math.Sin(lat), cosLat = Math.Cos(lat);
        double n = A / Math.Sqrt(1.0 - E2 * sinLat * sinLat);

        double x = n * cosLat * Math.Cos(lon);
        double y = n * cosLat * Math.Sin(lon);
        double z = n * (1.0 - E2) * sinLat;
        return (x, y, z);
    }

    private static (double Lat, double Lon) EcefToGeodetic(double x, double y, double z)
    {
        double p     = Math.Sqrt(x * x + y * y);
        double theta = Math.Atan2(z * A, p * B);
        double sinT  = Math.Sin(theta), cosT = Math.Cos(theta);

        double lat = Math.Atan2(
            z + Ep2 * B * sinT * sinT * sinT,
            p - E2  * A * cosT * cosT * cosT);
        double lon = Math.Atan2(y, x);
        return (lat, lon);
    }
}
