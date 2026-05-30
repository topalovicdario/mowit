using MowIT.Domain.Entities;

namespace MowIT.Domain.Builders;

public class BoundaryZoneBuilder
{
    private string _name = "New Zone";
    private readonly List<GpsPoint> _points = new();

    public BoundaryZoneBuilder Named(string name)
    {
        _name = name;
        return this;
    }

    public BoundaryZoneBuilder AddPoint(double latitude, double longitude)
    {
        _points.Add(new GpsPoint { Latitude = latitude, Longitude = longitude });
        return this;
    }

    public BoundaryZoneBuilder AddPoint(GpsPoint point)
    {
        _points.Add(point);
        return this;
    }

    public BoundaryZoneBuilder AddPoints(IEnumerable<GpsPoint> points)
    {
        _points.AddRange(points);
        return this;
    }

public BoundaryZoneBuilder Close()
    {
        if (_points.Count > 0 && _points[0] != _points[^1])
            _points.Add(_points[0]);
        return this;
    }

    public BoundaryZone Build()
    {
        if (_points.Count < 3)
            throw new InvalidOperationException(
                $"A boundary zone requires at least 3 points; got {_points.Count}.");

        return new BoundaryZone { Name = _name, Points = [.._points] };
    }
}
