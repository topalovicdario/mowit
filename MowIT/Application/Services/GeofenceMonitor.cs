using System.Reactive.Linq;
using CommunityToolkit.Mvvm.Messaging;
using MowIT.Application.Messages;
using MowIT.Domain.Entities;
using MowIT.Domain.Interfaces;

namespace MowIT.Application.Services;

public class GeofenceMonitor : IDisposable
{
    private readonly IRobotSensors       _sensors;
    private readonly IBoundaryRepository _repo;
    private IDisposable? _sub;
    private BoundaryZone? _zone;

    public GeofenceMonitor(IRobotSensors sensors, IBoundaryRepository repo)
    {
        _sensors = sensors;
        _repo    = repo;
    }

    public async Task StartAsync()
    {
        var zones = await _repo.GetAllAsync();
        _zone = zones.FirstOrDefault();

        _sub = _sensors.SensorStream
            .Sample(TimeSpan.FromSeconds(1))
            .Subscribe(CheckGeofence);
    }

    public void Stop() => Dispose();

    private void CheckGeofence(SensorSnapshot s)
    {
        if (_zone is null || _zone.Contains(s.Gps)) return;
        WeakReferenceMessenger.Default.Send(
            new GeofenceBreachMessage(s.Gps, _zone.Name));
    }

    public void Dispose()
    {
        _sub?.Dispose();
        _sub = null;
    }
}
