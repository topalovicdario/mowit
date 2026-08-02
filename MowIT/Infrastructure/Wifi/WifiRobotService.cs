using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using MowIT.Application.Logging;
using MowIT.Application.Messages;
using MowIT.Domain.Entities;
using MowIT.Domain.Enums;
using MowIT.Infrastructure.Transport;
using MowIT.Shared.Telemetry;

namespace MowIT.Infrastructure.Wifi;

public sealed class WifiRobotService : IRobotTransport, IDisposable
{
    private const string Source = "WIFI";

    private readonly WifiRobotOptions _opts;
    private readonly ILogger<WifiRobotService> _logger;
    private readonly EventLogService _evt;
    private readonly HttpClient _http;

    private string _robotId;
    private string _base;

    private readonly Subject<MowerDevice>          _deviceSubject = new();
    private readonly Subject<RobotConnectionState> _stateSubject  = new();
    private readonly Subject<SensorSnapshot>       _sensorSubject = new();
    private readonly Subject<RobotStatus>          _statusSubject = new();

    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private long _eventCursor;

    public WifiRobotService(
        WifiRobotOptions opts,
        ILogger<WifiRobotService> logger,
        EventLogService evt)
    {
        _opts   = opts;
        _logger = logger;
        _evt    = evt;

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", opts.BearerToken);

        _robotId = opts.RobotId;
        _base    = opts.CandidateBaseUrls[0];
    }

    private string Enc => Uri.EscapeDataString(_robotId);
    private string TelemetryUrl => $"{_base}/robots/{Enc}/telemetry";
    private string CommandsUrl  => $"{_base}/robots/{Enc}/commands";
    private string EventsUrl     => $"{_base}/robots/{Enc}/events";

    public IObservable<MowerDevice> DiscoveredDevices => _deviceSubject;
    public bool IsScanning { get; private set; }

    public async Task StartScanAsync(CancellationToken ct = default)
    {
        IsScanning = true;
        _evt.State(Source, "scanning cloud for active robots...");

        string? reason = null;

        foreach (var candidate in _opts.CandidateBaseUrls)
        {
            HttpResponseMessage resp;
            try
            {
                resp = await _http.GetAsync($"{candidate}/robots/active", ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                IsScanning = false;
                return;
            }
            catch (Exception ex)
            {
                _evt.Warn(Source, $"{candidate} unreachable: {ex.Message}");
                reason = $"Can't reach the server. Tried: {string.Join(", ", _opts.CandidateBaseUrls)}. Is MowIT.ScheduleServer running and on the same network?";
                continue;
            }

            _base = candidate;

            if (resp.IsSuccessStatusCode)
            {
                var active = await resp.Content.ReadFromJsonAsync<ActiveRobotsResponse>(cancellationToken: ct);
                var robots = active?.Robots ?? Array.Empty<TelemetryEnvelope>();

                if (robots.Length == 0)
                {
                    reason = "No robots are online - start a robot (MowIT.RobotSimulator).";
                }
                else
                {
                    foreach (var r in robots)
                    {
                        _evt.Info(Source, $"online: {r.RobotId} (via {candidate})");
                        _deviceSubject.OnNext(new MowerDevice
                        {
                            Id   = DeviceIdFor(r.RobotId),
                            Name = r.RobotId,
                            Rssi = 0
                        });
                    }
                }
            }
            else if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                reason = "Server rejected the token - run MowIT.ScheduleServer in Development mode.";
            }
            else
            {
                reason = $"Server returned {(int)resp.StatusCode} at {candidate}.";
            }

            break;
        }

        if (reason is not null)
        {
            _evt.Warn(Source, reason);
            WeakReferenceMessenger.Default.Send(new RobotErrorMessage($"WIFI/{reason}"));
        }

        IsScanning = false;
    }

    public Task StopScanAsync()
    {
        IsScanning = false;
        return Task.CompletedTask;
    }

    public IObservable<RobotConnectionState> ConnectionState => _stateSubject;
    public RobotConnectionState CurrentState { get; private set; } = RobotConnectionState.Disconnected;
    public bool IsConnected => CurrentState == RobotConnectionState.Connected;

    public async Task<bool> ConnectAsync(MowerDevice device, CancellationToken ct = default)
    {
        if (IsConnected) return true;

        if (!string.IsNullOrWhiteSpace(device.Name))
            _robotId = device.Name;

        SetState(RobotConnectionState.Connecting);
        _evt.State(Source, $"connecting to {_robotId} via {_base}...");

        try
        {
            var envelope = await _http.GetFromJsonAsync<TelemetryEnvelope>(TelemetryUrl, ct);
            if (envelope?.IsOnline != true)
            {
                _evt.Warn(Source, "connect failed - robot offline");
                WeakReferenceMessenger.Default.Send(new RobotErrorMessage(
                    "WIFI/Robot went offline. Make sure MowIT.RobotSimulator is running."));
                SetState(RobotConnectionState.Disconnected);
                return false;
            }
        }
        catch (Exception ex)
        {
            _evt.Error(Source, $"connect failed: {ex.Message}");
            WeakReferenceMessenger.Default.Send(new RobotErrorMessage(
                $"WIFI/Lost the server at {_base}. Scan again."));
            SetState(RobotConnectionState.Disconnected);
            return false;
        }

        _eventCursor = 0;
        SetState(RobotConnectionState.Connected);
        WeakReferenceMessenger.Default.Send(new RobotConnectedMessage(_robotId));
        _evt.State(Source, $"connected to {_robotId}");

        _pollCts  = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoop(_pollCts.Token));
        return true;
    }

    public async Task DisconnectAsync()
    {
        _pollCts?.Cancel();
        if (_pollTask is not null)
        {
            try { await _pollTask; } catch {  }
        }
        _pollTask = null;

        if (CurrentState != RobotConnectionState.Disconnected)
        {
            SetState(RobotConnectionState.Disconnected);
            WeakReferenceMessenger.Default.Send(new RobotDisconnectedMessage("wifi"));
            _evt.State(Source, "disconnected");
        }
    }

    public IObservable<SensorSnapshot> SensorStream => _sensorSubject;
    public IObservable<RobotStatus>    StatusStream  => _statusSubject;
    public SensorSnapshot? LastSensor { get; private set; }
    public RobotStatus?    LastStatus  { get; private set; }

    private async Task PollLoop(CancellationToken ct)
    {
        int failures = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var envelope = await _http.GetFromJsonAsync<TelemetryEnvelope>(TelemetryUrl, ct);

                if (envelope is null || !envelope.IsOnline || envelope.Latest is null)
                {
                    if (++failures >= _opts.MaxConsecutiveFailures)
                    {
                        _evt.Warn(Source, "robot offline - dropping connection");
                        break;
                    }
                }
                else
                {
                    failures = 0;
                    PublishTelemetry(envelope.Latest);
                    await PollEvents(ct);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (++failures >= _opts.MaxConsecutiveFailures)
                {
                    _evt.Error(Source, $"poll failed - dropping connection: {ex.Message}");
                    break;
                }
            }

            try { await Task.Delay(_opts.PollInterval, ct); }
            catch (OperationCanceledException) { return; }
        }

        if (!ct.IsCancellationRequested)
            await DisconnectAsync();
    }

    private void PublishTelemetry(TelemetryDto t)
    {
        var snap = new SensorSnapshot
        {
            Gps           = new GpsPoint(t.Lat, t.Lon),
            GpsAccuracyMm = t.GpsAccuracyMm,
            GpsFixType    = (GpsFixType)t.GpsFixType,
            HeadingRad    = t.HeadingRad,
            PosX          = t.PosX,
            PosY          = t.PosY,
            LinearSpeed   = t.LinearSpeed,
            AccX          = t.AccX,
            AccY          = t.AccY,
            AccZ          = t.AccZ,
            GyroX         = t.GyroX,
            GyroY         = t.GyroY,
            GyroZ         = t.GyroZ,
            IsManualMode  = t.IsManualMode,
            IsMotorMoving = t.IsMotorMoving,
            Timestamp     = t.TimestampUtc,
        };
        LastSensor = snap;
        _sensorSubject.OnNext(snap);

        var status = new RobotStatus
        {
            State         = (RobotState)t.State,
            BatteryPct    = t.BatteryPct,
            BladeOn       = t.BladeOn,
            RainDetected  = t.RainDetected,
            UptimeMinutes = t.UptimeMinutes,
            Timestamp     = t.TimestampUtc,
        };
        LastStatus = status;
        _statusSubject.OnNext(status);
    }

    private async Task PollEvents(CancellationToken ct)
    {
        var resp = await _http.GetFromJsonAsync<EventListResponse>(
            $"{EventsUrl}?since={_eventCursor}", ct);
        if (resp is null) return;

        _eventCursor = resp.Cursor;
        foreach (var evt in resp.Events)
            DispatchEvent(evt);
    }

    private void DispatchEvent(RobotEventDto evt)
    {
        _evt.Rx(Source, $"event {evt.Type}{(evt.Reason is null ? "" : "/" + evt.Reason)}");

        switch (evt.Type)
        {
            case RobotEventTypes.BaseCaptured:
                WeakReferenceMessenger.Default.Send(new BaseCapturedMessage());
                break;
            case RobotEventTypes.BoundaryPointCaptured:
                WeakReferenceMessenger.Default.Send(new BoundaryPointCapturedMessage(evt.XCm, evt.YCm));
                break;
            case RobotEventTypes.OutlineCaptured:
                WeakReferenceMessenger.Default.Send(new OutlineCapturedMessage());
                break;
            case RobotEventTypes.ExitCaptured:
                WeakReferenceMessenger.Default.Send(new ExitCapturedMessage(evt.XCm, evt.YCm));
                break;
            case RobotEventTypes.CaptureEnd:
                WeakReferenceMessenger.Default.Send(new CaptureEndMessage(evt.Success, evt.Reason));
                break;
            case RobotEventTypes.BoundaryCleared:
                WeakReferenceMessenger.Default.Send(new BoundaryClearedMessage());
                break;
            case RobotEventTypes.Error:
                WeakReferenceMessenger.Default.Send(new RobotErrorMessage(evt.Reason ?? "UNKNOWN"));
                break;
        }
    }

    public async Task SendMotorCommandAsync(float linearVel, float angularVel)
    {
        if (!IsConnected) return;
        await PostCommand(new RobotCommandDto
        {
            Kind       = CommandKinds.Motor,
            LinearVel  = linearVel,
            AngularVel = angularVel
        });
    }

    public async Task SendActionAsync(RobotAction action, byte param = 0)
    {
        if (!IsConnected) return;
        _evt.Tx(Source, $"{action}");
        await PostCommand(new RobotCommandDto
        {
            Kind       = CommandKinds.Action,
            ActionName = action.ToString(),
            ActionCode = (byte)action,
            Param      = param
        });
    }

    public async Task SendBoundaryAsync(BoundaryZone zone, IProgress<int>? progress = null)
    {
        var dto = new BoundaryUploadDto
        {
            Name   = zone.Name,
            Points = zone.Points
                .Select(p => new GpsPointDto { Lat = p.Latitude, Lon = p.Longitude })
                .ToArray()
        };
        await PostCommand(new RobotCommandDto { Kind = CommandKinds.Boundary, Boundary = dto });
        progress?.Report(100);
    }

    public Task SendRouteAsync(List<GpsPoint> route, IProgress<int>? progress = null)
        => SendBoundaryAsync(new BoundaryZone { Points = route }, progress);

    public Task ClearBoundaryAsync()
        => SendActionAsync(RobotAction.BoundaryClear);

    private async Task PostCommand(RobotCommandDto command)
    {
        try
        {
            await _http.PostAsJsonAsync(CommandsUrl, command);
        }
        catch (Exception ex)
        {
            _evt.Error(Source, $"command {command.Kind} failed: {ex.Message}");
        }
    }

    private void SetState(RobotConnectionState state)
    {
        CurrentState = state;
        _stateSubject.OnNext(state);
    }

    private static Guid DeviceIdFor(string robotId)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(robotId));
        return new Guid(hash);
    }

    public void Dispose()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _http.Dispose();
        _deviceSubject.OnCompleted(); _deviceSubject.Dispose();
        _stateSubject.OnCompleted();  _stateSubject.Dispose();
        _sensorSubject.OnCompleted(); _sensorSubject.Dispose();
        _statusSubject.OnCompleted(); _statusSubject.Dispose();
    }
}
