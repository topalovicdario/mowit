using System.Net.Http.Headers;
using System.Net.Http.Json;
using MowIT.Domain.Entities;
using MowIT.Domain.Enums;
using MowIT.Domain.Interfaces;
using MowIT.Shared.Schedules;
using MowIT.Shared.Telemetry;

namespace MowIT.RobotSimulator;

internal sealed class ThinScheduleSyncClient : IScheduleSyncService
{
    private readonly HttpClient _http;
    private readonly string _schedulesUrl;

    public ThinScheduleSyncClient(string baseUrl, string robotId, string token)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _schedulesUrl = $"/robots/{Uri.EscapeDataString(robotId)}/schedules";
    }

    public bool IsEnabled => true;

    public async Task PushAsync(IReadOnlyList<MowingSchedule> schedules, CancellationToken ct = default)
    {
        var payload = new ScheduleUploadRequest { Schedules = schedules.Select(ToDto).ToArray() };
        try
        {
            await _http.PostAsJsonAsync(_schedulesUrl, payload, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ! push rasporeda nije uspio: {ex.Message}");
        }
    }

    public async Task DeleteAsync(int scheduleId, CancellationToken ct = default)
    {
        try { await _http.DeleteAsync($"{_schedulesUrl}/{scheduleId}", ct); }
        catch (Exception ex) { Console.WriteLine($"  ! brisanje rasporeda nije uspjelo: {ex.Message}"); }
    }

    private static ScheduleDto ToDto(MowingSchedule s) => new()
    {
        Id              = s.Id,
        ActiveDays      = s.ActiveDays.Select(d => (int)d).ToArray(),
        StartTimeTicks  = s.StartTime.Ticks,
        DurationMinutes = s.DurationMinutes,
        IsActive        = s.IsActive,
        ZoneName        = s.ZoneName,
        ZoneId          = s.ZoneId,
        LastExecutedUtc = s.LastExecuted.Kind == DateTimeKind.Utc
            ? s.LastExecuted
            : s.LastExecuted.ToUniversalTime()
    };
}

internal sealed class ThinRobotControlClient : IRobotControl, IRobotBoundary
{
    private readonly HttpClient _http;
    private readonly string _commandsUrl;

    public ThinRobotControlClient(string baseUrl, string robotId, string token)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _commandsUrl = $"/robots/{Uri.EscapeDataString(robotId)}/commands";
    }

    public Task SendMotorCommandAsync(float linearVel, float angularVel)
        => PostCommand(new RobotCommandDto
        {
            Kind       = CommandKinds.Motor,
            LinearVel  = linearVel,
            AngularVel = angularVel
        });

    public Task SendActionAsync(RobotAction action, byte param = 0)
        => PostCommand(new RobotCommandDto
        {
            Kind       = CommandKinds.Action,
            ActionName = action.ToString(),
            ActionCode = (byte)action,
            Param      = param
        });

    public Task SendBoundaryAsync(BoundaryZone zone, IProgress<int>? progress = null)
        => PostCommand(new RobotCommandDto
        {
            Kind     = CommandKinds.Boundary,
            Boundary = new BoundaryUploadDto
            {
                Name   = zone.Name,
                Points = zone.Points
                    .Select(p => new GpsPointDto { Lat = p.Latitude, Lon = p.Longitude })
                    .ToArray()
            }
        });

    public Task SendRouteAsync(List<GpsPoint> route, IProgress<int>? progress = null)
        => SendBoundaryAsync(new BoundaryZone { Points = route }, progress);

    public Task ClearBoundaryAsync() => SendActionAsync(RobotAction.BoundaryClear);

    public Task SendRawAsync(RobotCommandDto command) => PostCommand(command);

    private async Task PostCommand(RobotCommandDto command)
    {
        try
        {
            await _http.PostAsJsonAsync(_commandsUrl, command);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ! naredba {command.Kind} nije poslana: {ex.Message}");
        }
    }
}

internal sealed class InMemoryScheduleRepository : IScheduleRepository
{
    private readonly List<MowingSchedule> _items = new();
    private readonly object _gate = new();

    public int ExecutedSaveCount { get; private set; }

    public void ResetExecutedSaveCount()
    {
        lock (_gate) ExecutedSaveCount = 0;
    }

    public List<MowingSchedule> Snapshot()
    {
        lock (_gate) return new List<MowingSchedule>(_items);
    }

    public Task<List<MowingSchedule>> GetAllAsync() => Task.FromResult(Snapshot());

    public Task<MowingSchedule?> GetByIdAsync(int id)
    {
        lock (_gate) return Task.FromResult(_items.FirstOrDefault(s => s.Id == id));
    }

    public Task SaveAsync(MowingSchedule schedule)
    {
        lock (_gate)
        {
            if (!_items.Contains(schedule))
            {
                _items.RemoveAll(s => s.Id == schedule.Id);
                _items.Add(schedule);
            }
            if (schedule.LastExecuted != default) ExecutedSaveCount++;
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(int id)
    {
        lock (_gate) _items.RemoveAll(s => s.Id == id);
        return Task.CompletedTask;
    }
}

internal sealed class ToggleableConnection : IRobotConnection
{
    public IObservable<RobotConnectionState> ConnectionState { get; } = new NeverObservable<RobotConnectionState>();

    public RobotConnectionState CurrentState =>
        IsConnected ? RobotConnectionState.Connected : RobotConnectionState.Disconnected;

    public bool IsConnected { get; set; } = true;

    public Task<bool> ConnectAsync(MowerDevice device, CancellationToken ct = default)
    {
        IsConnected = true;
        return Task.FromResult(true);
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        return Task.CompletedTask;
    }
}

internal sealed class NeverObservable<T> : IObservable<T>
{
    public IDisposable Subscribe(IObserver<T> observer) => new Unsubscriber();

    private sealed class Unsubscriber : IDisposable
    {
        public void Dispose() { }
    }
}

internal sealed class EmptyBoundaryRepository : IBoundaryRepository
{
    public Task<List<BoundaryZone>> GetAllAsync() => throw new NotSupportedException();
    public Task<BoundaryZone?> GetByIdAsync(int id) => throw new NotSupportedException();
    public Task SaveAsync(BoundaryZone zone) => throw new NotSupportedException();
    public Task DeleteAsync(int id) => throw new NotSupportedException();
}
