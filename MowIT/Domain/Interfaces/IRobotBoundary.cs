using MowIT.Domain.Entities;

namespace MowIT.Domain.Interfaces;

public interface IRobotBoundary
{
    Task SendBoundaryAsync(BoundaryZone zone, IProgress<int>? progress = null);
    Task SendRouteAsync(List<GpsPoint> route, IProgress<int>? progress = null);
    Task ClearBoundaryAsync();
}
