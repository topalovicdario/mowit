using MowIT.Domain.Entities;

namespace MowIT.Domain.Interfaces;

public interface IMowingStrategy
{
    string Name { get; }

List<GpsPoint> GenerateRoute(BoundaryZone zone, float spacingMeters = 0.3f);
}
