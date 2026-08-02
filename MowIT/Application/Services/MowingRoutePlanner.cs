using MowIT.Domain.Entities;
using MowIT.Domain.Geometry;
using MowIT.Domain.Interfaces;

namespace MowIT.Application.Services;

public sealed class MowingRoutePlanner
{
    private const float RowSpacingMeters  = 0.5f;
    private const int   MaxRouteWaypoints = 2500;

    private readonly IReadOnlyList<IMowingStrategy> _strategies;

    public MowingRoutePlanner(IEnumerable<IMowingStrategy> strategies)
        => _strategies = strategies.ToList();

    public IReadOnlyList<IMowingStrategy> Strategies => _strategies;

    public int SelectedIndex { get; set; }

    public string StrategyName(int index) =>
        _strategies.Count > 0 ? _strategies[index % _strategies.Count].Name : "-";

    public List<GpsPoint> Plan(BoundaryZone zone) => Plan(zone, SelectedIndex);

    public List<GpsPoint> Plan(BoundaryZone zone, int strategyIndex)
    {
        if (_strategies.Count == 0 || zone.Points.Count < 3)
            return new List<GpsPoint>();

        var strategy = _strategies[strategyIndex % _strategies.Count];
        return strategy.GenerateRoute(zone, AdaptiveSpacing(zone.Points));
    }

    public static float AdaptiveSpacing(IReadOnlyList<GpsPoint> points)
    {
        var proj  = new LocalProjection(points[0]);
        var local = points.Select(proj.ToLocal).ToList();

        double widthM  = local.Max(p => p.East)  - local.Min(p => p.East);
        double heightM = local.Max(p => p.North) - local.Min(p => p.North);
        double areaM2  = Math.Max(1.0, widthM * heightM);

        double spacing = Math.Sqrt(areaM2 / MaxRouteWaypoints);
        return (float)Math.Max(RowSpacingMeters, spacing);
    }
}
