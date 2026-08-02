using MowIT.Application.Services;
using MowIT.Domain.Enums;
using MowIT.Domain.Interfaces;

namespace MowIT.Application.UseCases;

public class SendZoneToRobotUseCase
{
    private readonly IBoundaryRepository _repo;
    private readonly IRobotBoundary      _robotBoundary;
    private readonly IRobotControl       _control;
    private readonly MowingRoutePlanner  _planner;

    public SendZoneToRobotUseCase(
        IBoundaryRepository repo,
        IRobotBoundary robotBoundary,
        IRobotControl control,
        MowingRoutePlanner planner)
    {
        _repo          = repo;
        _robotBoundary = robotBoundary;
        _control       = control;
        _planner       = planner;
    }

    public async Task ExecuteAsync(int zoneId, bool startMowingAfter, IProgress<int>? progress = null)
    {
        var zone = await _repo.GetByIdAsync(zoneId)
            ?? throw new InvalidOperationException($"Zone {zoneId} not found");

        if (zone.Points.Count < 3)
            throw new InvalidOperationException("Zone must have at least 3 GPS points");

        var route = _planner.Plan(zone);

        if (route.Count > 0)
            await _robotBoundary.SendRouteAsync(route, progress);
        else
            await _robotBoundary.SendBoundaryAsync(zone, progress);

        if (startMowingAfter)
            await _control.SendActionAsync(RobotAction.StartMowing);
    }

    public Task StartMowingOnlyAsync()
        => _control.SendActionAsync(RobotAction.StartMowing);
}
