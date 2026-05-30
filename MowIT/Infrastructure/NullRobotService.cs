using System.Reactive.Linq;
using MowIT.Domain.Entities;
using MowIT.Domain.Enums;
using MowIT.Domain.Interfaces;

namespace MowIT.Infrastructure;

public class NullRobotService
    : IRobotScanner, IRobotConnection, IRobotSensors, IRobotControl, IRobotBoundary
{
    public IObservable<MowerDevice>        DiscoveredDevices => Observable.Empty<MowerDevice>();
    public IObservable<RobotConnectionState> ConnectionState   => Observable.Return(RobotConnectionState.Disconnected);
    public IObservable<SensorSnapshot>     SensorStream      => Observable.Empty<SensorSnapshot>();
    public IObservable<RobotStatus>        StatusStream      => Observable.Empty<RobotStatus>();

    public RobotConnectionState CurrentState => RobotConnectionState.Disconnected;
    public bool IsConnected => false;
    public bool IsScanning  => false;

    public SensorSnapshot? LastSensor => null;
    public RobotStatus?    LastStatus  => null;

    public Task StartScanAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopScanAsync()                                 => Task.CompletedTask;

    public Task<bool> ConnectAsync(MowerDevice device, CancellationToken ct = default)
        => Task.FromResult(false);
    public Task DisconnectAsync() => Task.CompletedTask;

    public Task SendMotorCommandAsync(float linearVel, float angularVel) => Task.CompletedTask;
    public Task SendActionAsync(RobotAction action, byte param = 0)       => Task.CompletedTask;

    public Task SendBoundaryAsync(BoundaryZone zone, IProgress<int>? progress = null) => Task.CompletedTask;
    public Task SendRouteAsync(List<GpsPoint> route, IProgress<int>? progress = null)  => Task.CompletedTask;
    public Task ClearBoundaryAsync() => Task.CompletedTask;
}
