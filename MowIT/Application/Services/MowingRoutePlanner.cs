using MowIT.Domain.Entities;
using MowIT.Domain.Geometry;
using MowIT.Domain.Interfaces;

namespace MowIT.Application.Services;

public sealed class MowingRoutePlanner
{
    public const float DefaultSwathWidthM = 0.25f;

    public const float DefaultOverlapRatio = 0f;

    public const int MaxRouteWaypoints = 2500;

    private readonly IReadOnlyList<IMowingStrategy> _strategies;

    public MowingRoutePlanner(IEnumerable<IMowingStrategy> strategies)
        => _strategies = strategies.ToList();

    public IReadOnlyList<IMowingStrategy> Strategies => _strategies;

    public int SelectedIndex { get; set; }

    public float SwathWidthM { get; set; } = DefaultSwathWidthM;
    public float OverlapRatio { get; set; } = DefaultOverlapRatio;

    public string StrategyName(int index) =>
        _strategies.Count > 0 ? _strategies[index % _strategies.Count].Name : "-";

    public List<GpsPoint> Plan(BoundaryZone zone) => Plan(zone, SelectedIndex);

    public List<GpsPoint> Plan(BoundaryZone zone, int strategyIndex)
    {
        if (_strategies.Count == 0 || zone.Points.Count < 3)
            return new List<GpsPoint>();

        var strategy = _strategies[strategyIndex % _strategies.Count];
        var route    = strategy.GenerateRoute(zone, RowSpacing(SwathWidthM, OverlapRatio));

        return RemoveCollinear(route, toleranceMeters: 0.01);
    }

    public static float RowSpacing(float swathWidthM, float overlapRatio)
    {
        if (swathWidthM <= 0) swathWidthM = DefaultSwathWidthM;
        overlapRatio = Math.Clamp(overlapRatio, 0f, 0.5f);
        return swathWidthM * (1f - overlapRatio);
    }

    public static List<GpsPoint> RemoveCollinearForTest(List<GpsPoint> route, double toleranceMeters) =>
        RemoveCollinear(route, toleranceMeters);

    private static List<GpsPoint> RemoveCollinear(List<GpsPoint> route, double toleranceMeters)
    {
        if (route.Count < 3) return route;

        var proj  = new LocalProjection(route[0]);
        var local = route.Select(proj.ToLocal).ToList();

        var keep = new List<int> { 0 };
        for (int i = 1; i < local.Count - 1; i++)
        {
            var a = local[keep[^1]];
            var b = local[i];
            var c = local[i + 1];

            double dx = c.East - a.East, dy = c.North - a.North;
            double len = Math.Sqrt(dx * dx + dy * dy);
            double dist = len < 1e-9
                ? Math.Sqrt(Math.Pow(b.East - a.East, 2) + Math.Pow(b.North - a.North, 2))
                : Math.Abs(dy * b.East - dx * b.North + c.East * a.North - c.North * a.East) / len;

            if (dist > toleranceMeters) keep.Add(i);
        }
        keep.Add(local.Count - 1);

        return keep.Select(i => route[i]).ToList();
    }
}
