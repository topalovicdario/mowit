using System.Reactive.Linq;
using System.Reactive.Subjects;
using MowIT.Application.Logging;
using MowIT.Domain.Entities;
using MowIT.Domain.Enums;
using MowIT.Domain.Interfaces;

namespace MowIT.Infrastructure.Transport;

public sealed class RobotTransportRouter
    : IRobotScanner, IRobotConnection, IRobotSensors, IRobotControl, IRobotBoundary,
      IRobotTransportSwitch, IDisposable
{
    private const string Source = "ROUTE";

    private readonly IReadOnlyDictionary<TransportKind, IRobotTransport> _transports;
    private readonly EventLogService _evt;

    private readonly BehaviorSubject<IRobotTransport> _active;
    private readonly BehaviorSubject<TransportKind>   _kind;

    public RobotTransportRouter(
        IReadOnlyDictionary<TransportKind, IRobotTransport> transports,
        TransportKind defaultKind,
        EventLogService evt)
    {
        _transports = transports;
        _evt        = evt;
        _active     = new BehaviorSubject<IRobotTransport>(transports[defaultKind]);
        _kind       = new BehaviorSubject<TransportKind>(defaultKind);
    }

    private IRobotTransport Active => _active.Value;

    public TransportKind CurrentKind => _kind.Value;
    public IObservable<TransportKind> KindChanges => _kind.DistinctUntilChanged();

    public async Task SelectAsync(TransportKind kind)
    {
        if (kind == _kind.Value) return;
        if (!_transports.TryGetValue(kind, out var next)) return;

        try { await Active.DisconnectAsync(); } catch {  }

        _active.OnNext(next);
        _kind.OnNext(kind);
        _evt.State(Source, $"transport to {kind}");
    }

    public IObservable<MowerDevice> DiscoveredDevices =>
        _active.Select(t => t.DiscoveredDevices).Switch();
    public bool IsScanning => Active.IsScanning;
    public Task StartScanAsync(CancellationToken ct = default) => Active.StartScanAsync(ct);
    public Task StopScanAsync() => Active.StopScanAsync();

    public IObservable<RobotConnectionState> ConnectionState =>
        _active.Select(t => t.ConnectionState).Switch();
    public RobotConnectionState CurrentState => Active.CurrentState;
    public bool IsConnected => Active.IsConnected;
    public Task<bool> ConnectAsync(MowerDevice device, CancellationToken ct = default)
        => Active.ConnectAsync(device, ct);
    public Task DisconnectAsync() => Active.DisconnectAsync();

    public IObservable<SensorSnapshot> SensorStream =>
        _active.Select(t => t.SensorStream).Switch();
    public IObservable<RobotStatus> StatusStream =>
        _active.Select(t => t.StatusStream).Switch();
    public SensorSnapshot? LastSensor => Active.LastSensor;
    public RobotStatus?    LastStatus  => Active.LastStatus;

    public Task SendMotorCommandAsync(float linearVel, float angularVel)
        => Active.SendMotorCommandAsync(linearVel, angularVel);
    public Task SendActionAsync(RobotAction action, byte param = 0)
        => Active.SendActionAsync(action, param);

    public Task SendBoundaryAsync(BoundaryZone zone, IProgress<int>? progress = null)
        => Active.SendBoundaryAsync(zone, progress);
    public Task SendRouteAsync(List<GpsPoint> route, IProgress<int>? progress = null)
        => Active.SendRouteAsync(route, progress);
    public Task ClearBoundaryAsync() => Active.ClearBoundaryAsync();

    public void Dispose()
    {
        _active.Dispose();
        _kind.Dispose();
    }
}
