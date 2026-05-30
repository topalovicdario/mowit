using MowIT.Domain.Interfaces;

namespace MowIT.Application.UseCases;

public class SendBoundaryUseCase
{
    private readonly IRobotBoundary     _boundary;
    private readonly IBoundaryRepository _repo;

    public SendBoundaryUseCase(IRobotBoundary boundary, IBoundaryRepository repo)
    {
        _boundary = boundary;
        _repo     = repo;
    }

    public async Task ExecuteAsync(int zoneId, IProgress<int>? progress = null)
    {
        var zone = await _repo.GetByIdAsync(zoneId)
            ?? throw new InvalidOperationException($"Zone {zoneId} not found");

        if (zone.Points.Count < 3)
            throw new InvalidOperationException("Boundary must have at least 3 points");

        await _boundary.SendBoundaryAsync(zone, progress);
    }
}
