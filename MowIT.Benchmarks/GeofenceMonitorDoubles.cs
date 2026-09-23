using System.Reactive.Linq;
using System.Reactive.Subjects;
using MowIT.Domain.Entities;
using MowIT.Domain.Enums;
using MowIT.Domain.Interfaces;

namespace MowIT.Benchmarks;

internal sealed class FixedZoneRepository : IBoundaryRepository
{
    private readonly List<BoundaryZone> _zones;
    public FixedZoneRepository(BoundaryZone zone) => _zones = new List<BoundaryZone> { zone };
    public FixedZoneRepository(IEnumerable<BoundaryZone> zones) => _zones = zones.ToList();

    public Task<List<BoundaryZone>> GetAllAsync() => Task.FromResult(new List<BoundaryZone>(_zones));
    public Task<BoundaryZone?> GetByIdAsync(int id) => Task.FromResult(_zones.FirstOrDefault(z => z.Id == id));
    public Task SaveAsync(BoundaryZone zone) => Task.CompletedTask;
    public Task DeleteAsync(int id) => Task.CompletedTask;
}

internal sealed class EmptyZoneRepository : IBoundaryRepository
{
    public Task<List<BoundaryZone>> GetAllAsync() => Task.FromResult(new List<BoundaryZone>());
    public Task<BoundaryZone?> GetByIdAsync(int id) => Task.FromResult<BoundaryZone?>(null);
    public Task SaveAsync(BoundaryZone zone) => Task.CompletedTask;
    public Task DeleteAsync(int id) => Task.CompletedTask;
}

internal sealed class AlwaysConnectedConnection : IRobotConnection
{
    public IObservable<RobotConnectionState> ConnectionState { get; } =
        Observable.Return(RobotConnectionState.Connected);

    public RobotConnectionState CurrentState => RobotConnectionState.Connected;
    public bool IsConnected => true;

    public Task<bool> ConnectAsync(MowerDevice device, CancellationToken ct = default) => Task.FromResult(true);
    public Task DisconnectAsync() => Task.CompletedTask;
}

internal sealed class RecordingRobotControl : IRobotControl
{
    public int StopCount { get; private set; }
    public List<(double ElapsedS, RobotAction Action)> Calls { get; } = new();
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

    public Task SendMotorCommandAsync(float linearVel, float angularVel) => Task.CompletedTask;

    public Task SendActionAsync(RobotAction action, byte param = 0)
    {
        Calls.Add((_sw.Elapsed.TotalSeconds, action));
        if (action == RobotAction.Stop) StopCount++;
        return Task.CompletedTask;
    }
}

internal sealed class RobotSensorsDouble : IRobotSensors
{
    private readonly Subject<SensorSnapshot> _subject = new();

    public IObservable<SensorSnapshot> SensorStream => _subject;
    public IObservable<RobotStatus>    StatusStream  { get; } = Observable.Never<RobotStatus>();
    public SensorSnapshot? LastSensor { get; private set; }
    public RobotStatus?    LastStatus => null;

    public void Push(SensorSnapshot s)
    {
        LastSensor = s;
        _subject.OnNext(s);
    }
}
