using System.Reactive.Linq;
using CommunityToolkit.Mvvm.Messaging;
using MowIT.Application.Logging;
using MowIT.Application.Messages;
using MowIT.Domain.Entities;
using MowIT.Domain.Enums;
using MowIT.Domain.Interfaces;

namespace MowIT.Application.Services;

public sealed class GeofenceMonitor : IDisposable
{
    private const string Source = "GEOFENCE";

    private readonly IRobotSensors       _sensors;
    private readonly IBoundaryRepository _repo;
    private readonly IRobotConnection    _connection;
    private readonly IRobotControl       _control;
    private readonly EventLogService     _evt;

    private IDisposable? _connectionSub;
    private IDisposable? _sensorSub;
    private BoundaryZone? _zone;
    private bool _outside;

    public GeofenceMonitor(
        IRobotSensors sensors,
        IBoundaryRepository repo,
        IRobotConnection connection,
        IRobotControl control,
        EventLogService evt)
    {
        _sensors    = sensors;
        _repo       = repo;
        _connection = connection;
        _control    = control;
        _evt        = evt;

        _connectionSub = _connection.ConnectionState
            .Subscribe(state => _ = OnConnectionStateAsync(state));
    }

    private async Task OnConnectionStateAsync(RobotConnectionState state)
    {
        if (state == RobotConnectionState.Connected)
        {
            await ReloadAsync();

            _sensorSub?.Dispose();
            _outside   = false;
            _sensorSub = _sensors.SensorStream
                .Sample(TimeSpan.FromMilliseconds(500))
                .Subscribe(CheckGeofence);

            _evt.State(Source, _zone is null
                ? "idle - no boundary to watch"
                : $"armed on \"{_zone.Name}\" ({_zone.Points.Count} pts, {_zone.AreaSquareMeters():F1} m2)");
        }
        else
        {
            _sensorSub?.Dispose();
            _sensorSub = null;
            _outside   = false;
        }
    }

    public async Task ReloadAsync()
    {
        var zones = await _repo.GetAllAsync();
        _zone = zones
            .Where(z => z.IsValid)
            .OrderByDescending(z => z.CreatedAt)
            .FirstOrDefault();

        _evt.Info(Source, _zone is null
            ? "no saved boundary - geofence idle"
            : $"active zone \"{_zone.Name}\" ({_zone.Points.Count} pts)");
    }

    private void CheckGeofence(SensorSnapshot s)
    {
        if (_zone is null) return;
        if (s.Gps.Latitude == 0 && s.Gps.Longitude == 0) return;

        if (_zone.Contains(s.Gps))
        {
            if (_outside)
                _evt.Info(Source, $"robot back inside zone \"{_zone.Name}\"");
            _outside = false;
            return;
        }

        if (_outside) return;

        _outside = true;

        double past = _zone.DistanceToBoundaryMeters(s.Gps);
        _evt.Warn(Source,
            $"robot left zone \"{_zone.Name}\" at {s.Gps.Latitude:F7},{s.Gps.Longitude:F7} - " +
            $"{past:F2} m past the boundary, GPS +-{s.GpsAccuracyMm / 1000f:F2} m - stopping");

        WeakReferenceMessenger.Default.Send(new GeofenceBreachMessage(s.Gps, _zone.Name));
        _ = _control.SendActionAsync(RobotAction.Stop);
    }

    public void Dispose()
    {
        _sensorSub?.Dispose();
        _connectionSub?.Dispose();
        _sensorSub     = null;
        _connectionSub = null;
    }
}
