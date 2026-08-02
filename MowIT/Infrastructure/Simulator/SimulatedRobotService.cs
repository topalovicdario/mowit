using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using MowIT.Application.Logging;
using MowIT.Application.Messages;
using MowIT.Domain.Entities;
using MowIT.Domain.Enums;
using MowIT.Domain.Geometry;
using MowIT.Domain.Interfaces;
using MowIT.Infrastructure.Transport;

namespace MowIT.Infrastructure.Simulator;


public sealed class SimulatedRobotService
    : IRobotTransport, IDisposable
{
    private const double MetersPerDegree = 111_319.444;
    private const double TickSeconds     = 0.2;
    private const float  GoodAccuracyMm  = 50f;     
    private const float  StartAccuracyMm = 800f;
    private const float  RtkAccuracyMm   = 12f;
    private const double AccuracyRampMmPerSec = 100;

   
    private const float  LinearAccelMs2  = 0.8f;    
    private const float  AngularAccelRs2 = 3.0f;    
    private const int    ManualWatchdogMs = 600;    

    private readonly Subject<MowerDevice>          _deviceSubject = new();
    private readonly Subject<RobotConnectionState> _stateSubject  = new();
    private readonly Subject<SensorSnapshot>       _sensorSubject = new();
    private readonly Subject<RobotStatus>          _statusSubject = new();

    private IDisposable? _sensorTimer;

    private double   _lat = 43.8563, _lon = 18.4131;     
 
    private float    _heading = (float)(Math.PI / 2.0);
    private float    _accMm = StartAccuracyMm;
    private DateTime _connectedAt;
    private int      _battery = 85;

    
    private double _baseLat, _baseLon;
    private bool   _hasDatum;

   
    private bool                                  _capturing;
    private readonly List<LocalPoint>             _activePoints   = new();
    private readonly List<List<LocalPoint>>       _closedPolygons = new();
    private LocalPoint?                           _exitPoint;

    private bool                                  _pathSaved;

    private readonly List<GpsPoint>               _route = new();
    private int                                   _routeIdx;
    private const double                          WaypointReachM = 0.30;

    
    private bool       _manualMode;
    private float      _targetLinear, _targetAngular;   
    private float      _actualLinear, _actualAngular;   
    private DateTime?  _lastMoveCmdAt;                  
    private RobotState _state = RobotState.Idle;

    private readonly ILogger<SimulatedRobotService> _logger;
    private readonly EventLogService _evt;
    private const string Source = "SIM";

    public SimulatedRobotService(ILogger<SimulatedRobotService> logger, EventLogService evt)
    {
        _logger = logger;
        _evt    = evt;
        _evt.Info(Source, "SimulatedRobotService constructed - emulating GreenTitan over fake SPP");
        StartSensorSimulation();
    }

  
    private void LogTx(string command)  => _evt.Tx   (Source, command);
    private void LogRx(string response) => _evt.Rx   (Source, response);
    private void LogState(string what)  => _evt.State(Source, what);


    public IObservable<MowerDevice> DiscoveredDevices => _deviceSubject;
    public bool IsScanning { get; private set; }

    public async Task StartScanAsync(CancellationToken ct = default)
    {
        IsScanning = true;
        LogState("Scan started");
        await Task.Delay(500, ct);
        var dev1 = new MowerDevice { Id = Guid.NewGuid(), Name = "GreenTitan-SIM",  Rssi = -55 };
        var dev2 = new MowerDevice { Id = Guid.NewGuid(), Name = "GreenTitan-SIM2", Rssi = -72 };
        _evt.Info(Source, $"discovered {dev1.Name} rssi={dev1.Rssi}");
        _evt.Info(Source, $"discovered {dev2.Name} rssi={dev2.Rssi}");
        _deviceSubject.OnNext(dev1);
        _deviceSubject.OnNext(dev2);
        IsScanning = false;
        LogState("Scan finished");
    }

    public Task StopScanAsync()
    {
        IsScanning = false;
        LogState("Scan cancelled");
        return Task.CompletedTask;
    }



    public IObservable<RobotConnectionState> ConnectionState => _stateSubject;
    public RobotConnectionState CurrentState { get; private set; } = RobotConnectionState.Disconnected;
    public bool IsConnected => CurrentState == RobotConnectionState.Connected;

    public async Task<bool> ConnectAsync(MowerDevice device, CancellationToken ct = default)
    {
        CurrentState = RobotConnectionState.Connecting;
        _stateSubject.OnNext(RobotConnectionState.Connecting);
        LogState($"Connecting to {device.Name}...");
        await Task.Delay(800, ct);
        CurrentState  = RobotConnectionState.Connected;
        _connectedAt  = DateTime.UtcNow;
        _accMm        = StartAccuracyMm;
        _stateSubject.OnNext(RobotConnectionState.Connected);
        WeakReferenceMessenger.Default.Send(new RobotConnectedMessage(device.Name));
        LogState($"Connected to {device.Name} - GPS accuracy ramp starting at {StartAccuracyMm:F0} mm");
        return true;
    }

    public Task DisconnectAsync()
    {
        CurrentState = RobotConnectionState.Disconnected;
        _stateSubject.OnNext(RobotConnectionState.Disconnected);
        WeakReferenceMessenger.Default.Send(new RobotDisconnectedMessage("simulator"));
        LogState("Disconnected");
        return Task.CompletedTask;
    }



    public IObservable<SensorSnapshot> SensorStream => _sensorSubject;
    public IObservable<RobotStatus>    StatusStream  => _statusSubject;
    public SensorSnapshot? LastSensor { get; private set; }
    public RobotStatus?    LastStatus  { get; private set; }

    private void StartSensorSimulation()
    {
        _sensorTimer = Observable
            .Interval(TimeSpan.FromMilliseconds(200))
            .Subscribe(_ => EmitSensorData());
    }

    private void EmitSensorData()
    {
        if (CurrentState != RobotConnectionState.Connected) return;


        if (_manualMode && _lastMoveCmdAt is DateTime t
            && (DateTime.UtcNow - t).TotalMilliseconds > ManualWatchdogMs)
        {
            _targetLinear = 0;
            _targetAngular = 0;
        }

       
        if (_state == RobotState.Mowing && _route.Count > 0)
        {
            FollowRouteStep();
        }
        else
        {
            float targetLin, targetAng;
            if (_state == RobotState.Mowing)
            {
                targetLin = 0.3f;
                targetAng = 0.075f;
            }
            else if (_manualMode)
            {
                targetLin = _targetLinear;
                targetAng = _targetAngular;
            }
            else
            {
                targetLin = 0;
                targetAng = 0;
            }

            _actualLinear  = ApproachLinear(_actualLinear,  targetLin, LinearAccelMs2  * (float)TickSeconds);
            _actualAngular = ApproachLinear(_actualAngular, targetAng, AngularAccelRs2 * (float)TickSeconds);

            _heading += _actualAngular * (float)TickSeconds;
            if (Math.Abs(_actualLinear) > 0.001f)
                StepGps(_actualLinear, _heading);
        }

    
        var elapsed = (DateTime.UtcNow - _connectedAt).TotalSeconds;
        _accMm = (float)Math.Max(RtkAccuracyMm, StartAccuracyMm - AccuracyRampMmPerSec * elapsed);

       
        float posXm = 0, posYm = 0;
        if (_hasDatum)
        {
            var (east, north) = new LocalProjection(new GpsPoint(_baseLat, _baseLon))
                .ToLocal(new GpsPoint(_lat, _lon));
            posXm = (float)east;
            posYm = (float)north;
        }

        bool moving = Math.Abs(_actualLinear) > 0.01f || Math.Abs(_actualAngular) > 0.01f;

        var snap = new SensorSnapshot
        {
            Gps           = new GpsPoint(_lat, _lon),
            GpsAccuracyMm = _accMm,
            GpsFixType    = _accMm < GoodAccuracyMm ? GpsFixType.RtkFixed : GpsFixType.Standard,
            HeadingRad    = _heading,
            PosX          = posXm,
            PosY          = posYm,
            LinearSpeed   = Math.Abs(_actualLinear),
            AccX          = (float)(Math.Sin(DateTime.Now.TimeOfDay.TotalSeconds) * 0.1),
            AccY          = (float)(Math.Cos(DateTime.Now.TimeOfDay.TotalSeconds) * 0.05),
            AccZ          = 9.81f,
            IsManualMode  = _manualMode,
            IsMotorMoving = moving,
            Timestamp     = DateTime.UtcNow,
        };
        LastSensor = snap;
        _sensorSubject.OnNext(snap);

    
        if (DateTime.UtcNow.Millisecond < 200) EmitStatus();
    }

 
    private static float ApproachLinear(float current, float target, float step)
    {
        float diff = target - current;
        if (Math.Abs(diff) <= step) return target;
        return current + Math.Sign(diff) * step;
    }

    private void StepGps(double linearMs, double headingRad)
    {
        double dist = linearMs * TickSeconds;
        _lat += dist * Math.Cos(headingRad) / MetersPerDegree;
        _lon += dist * Math.Sin(headingRad) / MetersPerDegree / Math.Cos(_lat * Math.PI / 180.0);
    }

    private void FollowRouteStep()
    {
        if (_routeIdx >= _route.Count)
        {
            _state         = RobotState.Idle;
            _actualLinear  = 0;
            _actualAngular = 0;
            EmitStatus();
            LogState("Route complete - mowing finished");
            return;
        }

        var target = _route[_routeIdx];
        var current = new GpsPoint(_lat, _lon);
        if (current.DistanceTo(target) < WaypointReachM)
        {
            _routeIdx++;
            return;
        }

        _heading       = (float)(current.BearingTo(target) * Math.PI / 180.0);
        _actualLinear  = 0.3f;
        _actualAngular = 0;
        StepGps(_actualLinear, _heading);
    }

    private void EmitStatus()
    {
        var status = new RobotStatus
        {
            State         = _state,
            BatteryPct    = _battery,
            BladeOn       = _state == RobotState.Mowing,
            RainDetected  = false,
            UptimeMinutes = (int)(DateTime.UtcNow - _connectedAt).TotalMinutes,
            Timestamp     = DateTime.UtcNow,
        };
        LastStatus = status;
        _statusSubject.OnNext(status);
    }

    public async Task SendMotorCommandAsync(float linearVel, float angularVel)
    {
        if (CurrentState != RobotConnectionState.Connected)
        {
            _evt.Warn(Source, "motor cmd dropped - not connected");
            return;
        }

       
        if (!_manualMode && (Math.Abs(linearVel) > 0.001f || Math.Abs(angularVel) > 0.001f))
        {
            LogTx("MOWER/MANUAL/ON   (auto)");
            _manualMode = true;
            EmitSensorData();        
            LogRx("MOWER/MANUAL/ON/OK");
            await Task.Delay(50);      
        }

        if (!_manualMode) return;

        LogTx(string.Format(CultureInfo.InvariantCulture, "MOWER/MOVE/{0:F3},{1:F3}", linearVel, angularVel));
        _targetLinear  = linearVel;
        _targetAngular = angularVel;
        _lastMoveCmdAt = DateTime.UtcNow;
        LogRx("MOWER/MOVE/OK");
    }

    public async Task SendActionAsync(RobotAction action, byte param = 0)
    {
        if (CurrentState != RobotConnectionState.Connected)
        {
            _evt.Warn(Source, $"action {action} dropped - not connected");
            return;
        }

        string cmd = action switch
        {
            RobotAction.CaptureBase           => "GPS/CAPTURE/BASE",
            RobotAction.StartMowing           => "MOWER/START",
            RobotAction.StartRoute            => "MOWER/START",
            RobotAction.Stop                  => "MOWER/MANUAL/OFF",
            RobotAction.ManualModeOn          => "MOWER/MANUAL/ON",
            RobotAction.ManualModeOff         => "MOWER/MANUAL/OFF",
            RobotAction.BoundaryRecordStart   => "MOWER/CAPTURE/START",
            RobotAction.BoundaryClear         => "MOWER/CAPTURE/START",
            RobotAction.BoundaryCapturePoint  => "MOWER/CAPTURE/POINT",
            RobotAction.CaptureOutline        => "MOWER/CAPTURE/OUTLINE",
            RobotAction.CaptureExit           => "MOWER/CAPTURE/EXIT",
            RobotAction.BoundaryRecordEnd     => "MOWER/CAPTURE/END",
            _                                 => $"<unmapped:{action}>"
        };
        LogTx(cmd);

     
        await Task.Delay(120);

        switch (action)
        {
            case RobotAction.CaptureBase:
                HandleCaptureBase();
                break;

            case RobotAction.ManualModeOn:
                _manualMode = true;
                EmitSensorData();
                LogRx("MOWER/MANUAL/ON/OK");
                break;

            case RobotAction.ManualModeOff:
                _manualMode    = false;
                _targetLinear  = 0;
                _targetAngular = 0;
                _lastMoveCmdAt = null;
                EmitSensorData();
                LogRx("MOWER/MANUAL/OFF/OK");
                break;

            case RobotAction.StartMowing:
            case RobotAction.StartRoute:
                HandleStartMowing();
                break;

            case RobotAction.Stop:
                _state         = RobotState.Idle;
                _manualMode    = false;
                _targetLinear  = 0;
                _targetAngular = 0;
                _lastMoveCmdAt = null;
                EmitStatus();
                LogRx("MOWER/MANUAL/OFF/OK");
                break;

            case RobotAction.BoundaryRecordStart:
            case RobotAction.BoundaryClear:
                HandleRecordStart();
                break;

            case RobotAction.BoundaryCapturePoint:
                HandleCapturePoint();
                break;

            case RobotAction.CaptureOutline:
                HandleCaptureOutline();
                break;

            case RobotAction.CaptureExit:
                HandleCaptureExit();
                break;

            case RobotAction.BoundaryRecordEnd:
                HandleRecordEnd();
                break;

            default:
                _evt.Warn(Source, $"action {action} not handled by simulator");
                break;
        }
    }

    private void HandleStartMowing()
    {
        if (_route.Count > 0)
        {
            _lat      = _route[0].Latitude;
            _lon      = _route[0].Longitude;
            _routeIdx = Math.Min(1, _route.Count - 1);
            _state    = RobotState.Mowing;
            EmitStatus();
            LogRx("MOWER/START/OK");
            LogState($"Mowing started - following uploaded route ({_route.Count} waypoints)");
            return;
        }

        if (!_hasDatum)
        {
            LogRx("MOWER/START/FAIL/NO_DATUM");
            WeakReferenceMessenger.Default.Send(new RobotErrorMessage("MOWER_START_FAIL/NO_DATUM"));
            return;
        }
        if (!_pathSaved)
        {
            LogRx("MOWER/START/FAIL/NO_PATH");
            WeakReferenceMessenger.Default.Send(new RobotErrorMessage("MOWER_START_FAIL/NO_PATH"));
            return;
        }
        _state = RobotState.Mowing;
        EmitStatus();
        LogRx("MOWER/START/OK");
        LogState($"Mowing started - driving from exit ({_exitPoint?.XCm/100f:F2}, {_exitPoint?.YCm/100f:F2}) m");
    }

    private void HandleCaptureBase()
    {
        if (_accMm > GoodAccuracyMm)
        {
            LogRx($"GPS/CAPTURE/BASE/FAIL/ACCURACY  (have {_accMm:F0} mm, need < {GoodAccuracyMm:F0} mm)");
            WeakReferenceMessenger.Default.Send(new RobotErrorMessage("BASE_CAPTURE_FAIL/ACCURACY"));
            return;
        }
        _baseLat   = _lat;
        _baseLon   = _lon;
        _hasDatum  = true;
        
        _pathSaved = false;
        LogRx($"GPS/CAPTURE/BASE/OK  lat={_baseLat:F7} lon={_baseLon:F7}");
        WeakReferenceMessenger.Default.Send(new BaseCapturedMessage());
        LogState("Base datum set - local NEU frame anchored at this GPS");
    }

    private void HandleRecordStart()
    {
    
        _activePoints.Clear();
        _closedPolygons.Clear();
        _route.Clear();
        _exitPoint = null;
        _pathSaved = false;
        _capturing = true;
        _state     = RobotState.RecordingBoundary;
        EmitStatus();
        LogRx("MOWER/CAPTURE/START/OK");
        LogState("Capture session started - clearing any prior polygons + exit + saved-path flag");
        WeakReferenceMessenger.Default.Send(new BoundaryClearedMessage());
    }

    private void HandleCapturePoint()
    {
        if (!_capturing || !_hasDatum)
        {
            string reason = !_hasDatum ? "NO_DATUM" : "NOT_READY";
            LogRx($"MOWER/CAPTURE/POINT/FAIL/{reason}");
            WeakReferenceMessenger.Default.Send(new RobotErrorMessage($"CAPTURE_POINT_FAIL/{reason}"));
            return;
        }
        var p = LocalCm();
        _activePoints.Add(p);
        LogRx($"MOWER/CAPTURE/POINT/OK/{(int)p.XCm},{(int)p.YCm}");
        _evt.Info(Source, $"Point added - active polygon now has {_activePoints.Count} pts");
        WeakReferenceMessenger.Default.Send(new BoundaryPointCapturedMessage((int)p.XCm, (int)p.YCm));
    }

    private void HandleCaptureOutline()
    {
        if (!_capturing)
        {
            LogRx("MOWER/CAPTURE/OUTLINE/FAIL/NOT_RECORDING");
            WeakReferenceMessenger.Default.Send(new RobotErrorMessage("CAPTURE_OUTLINE_FAIL"));
            return;
        }
        if (_activePoints.Count < 3)
        {
            LogRx($"MOWER/CAPTURE/OUTLINE/FAIL/TOO_FEW_POINTS  (have {_activePoints.Count}, need ≥ 3)");
            WeakReferenceMessenger.Default.Send(new RobotErrorMessage("CAPTURE_OUTLINE_FAIL/TOO_FEW_POINTS"));
            return;
        }
        _closedPolygons.Add(new List<LocalPoint>(_activePoints));
        _evt.Info(Source, $"Polygon #{_closedPolygons.Count} closed with {_activePoints.Count} pts");
        _activePoints.Clear();
        LogRx("MOWER/CAPTURE/OUTLINE/OK");
        WeakReferenceMessenger.Default.Send(new OutlineCapturedMessage());
    }

    private void HandleCaptureExit()
    {
        if (!_capturing || !_hasDatum)
        {
            string reason = !_hasDatum ? "NO_DATUM" : "NOT_READY";
            LogRx($"MOWER/CAPTURE/EXIT/FAIL/{reason}");
            WeakReferenceMessenger.Default.Send(new RobotErrorMessage($"CAPTURE_EXIT_FAIL/{reason}"));
            return;
        }
        var p = LocalCm();
        _exitPoint = p;
        LogRx($"MOWER/CAPTURE/EXIT/OK/{(int)p.XCm},{(int)p.YCm}");
        WeakReferenceMessenger.Default.Send(new ExitCapturedMessage((int)p.XCm, (int)p.YCm));
    }

    private void HandleRecordEnd()
    {
        bool wasCapturing = _capturing;
        _capturing = false;
        _state     = RobotState.Idle;
        EmitStatus();

        if (!wasCapturing)
        {
            LogRx("MOWER/CAPTURE/END/FAIL/NOT_RECORDING");
            WeakReferenceMessenger.Default.Send(new CaptureEndMessage(false, "NOT_RECORDING"));
            return;
        }

        if (_closedPolygons.Count == 0)
        {
            LogRx("MOWER/CAPTURE/END/FAIL/NO_POLYGON");
            WeakReferenceMessenger.Default.Send(new CaptureEndMessage(false, "NO_POLYGON"));
            return;
        }

        if (_exitPoint is null)
        {
            LogRx("MOWER/CAPTURE/END/FAIL/NO_EXIT");
            WeakReferenceMessenger.Default.Send(new CaptureEndMessage(false, "NO_EXIT"));
            return;
        }

        _pathSaved = true;
        LogRx($"MOWER/CAPTURE/END/OK  polys={_closedPolygons.Count} exit=({_exitPoint?.XCm/100f:F2}, {_exitPoint?.YCm/100f:F2}) m");
        LogState("Path saved - Start Mowing is now allowed");
        WeakReferenceMessenger.Default.Send(new CaptureEndMessage(true));
    }

    private LocalPoint LocalCm()
    {
        var (east, north) = new LocalProjection(new GpsPoint(_baseLat, _baseLon))
            .ToLocal(new GpsPoint(_lat, _lon));
        return new LocalPoint((float)(east * 100.0), (float)(north * 100.0));
    }



    public async Task SendBoundaryAsync(BoundaryZone zone, IProgress<int>? progress = null)
    {
        for (int i = 0; i < zone.Points.Count; i++)
        {
            await Task.Delay(40);
            progress?.Report((i + 1) * 100 / Math.Max(1, zone.Points.Count));
        }

        _route.Clear();
        _route.AddRange(zone.Points);
        _routeIdx  = 0;
        _pathSaved = true;
        LogState($"Route uploaded - {_route.Count} waypoints, ready to mow");
    }

    public Task SendRouteAsync(List<GpsPoint> route, IProgress<int>? progress = null)
        => SendBoundaryAsync(new BoundaryZone { Points = route }, progress);

    public Task ClearBoundaryAsync()
    {
        _activePoints.Clear();
        _closedPolygons.Clear();
        _exitPoint = null;
        _capturing = false;
        return Task.CompletedTask;
    }

  

    public void Dispose()
    {
        _sensorTimer?.Dispose();
        _deviceSubject.OnCompleted();
        _stateSubject .OnCompleted();
        _sensorSubject.OnCompleted();
        _statusSubject.OnCompleted();
        _deviceSubject.Dispose();
        _stateSubject .Dispose();
        _sensorSubject.Dispose();
        _statusSubject.Dispose();
    }
}
