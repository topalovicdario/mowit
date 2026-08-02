using MowIT.Shared.Telemetry;

namespace MowIT.RobotSimulator;

public sealed class VirtualRobot
{
    private const double MetersPerDegree     = 111_319.444;
    private const float  GoodAccuracyMm       = 50f;
    private const float  StartAccuracyMm      = 800f;
    private const float  RtkAccuracyMm        = 12f;
    private const double AccuracyRampMmPerSec = 100;
    private const float  LinearAccelMs2       = 0.8f;
    private const float  AngularAccelRs2      = 3.0f;
    private const double ManualWatchdogSec    = 0.6;
    private const float  MowSpeedMs           = 0.3f;
    private const double WaypointReachM       = 0.30;

    private const int StateIdle              = 0;
    private const int StateMowing            = 1;
    private const int StateRecordingBoundary = 7;

    private const int FixStandard = 1;
    private const int FixRtkFixed = 3;

    private double _lat = 43.8563, _lon = 18.4131;
    private float  _heading = (float)(Math.PI / 2.0);
    private float  _accMm   = StartAccuracyMm;
    private int    _battery = 85;

    private double _baseLat, _baseLon;
    private bool   _hasDatum;

    private bool _capturing;
    private readonly List<(float X, float Y)>       _activePoints   = new();
    private readonly List<List<(float X, float Y)>> _closedPolygons = new();
    private (float X, float Y)? _exitPoint;
    private bool _pathSaved;

    private readonly List<(double Lat, double Lon)> _route = new();
    private int _routeIdx;

    private bool   _manualMode;
    private float  _targetLinear, _targetAngular;
    private float  _actualLinear, _actualAngular;
    private double _sinceLastMoveCmd;
    private int    _state = StateIdle;

    private readonly DateTime _bootedAt = DateTime.UtcNow;
    private double _elapsedSeconds;

    public void Tick(double dt)
    {
        _elapsedSeconds += dt;

        if (_state == StateMowing && _route.Count > 0)
        {
            FollowRoute(dt);
        }
        else
        {
            DriveManualOrArc(dt);
        }

        _accMm = (float)Math.Max(RtkAccuracyMm, StartAccuracyMm - AccuracyRampMmPerSec * _elapsedSeconds);
    }

    private void DriveManualOrArc(double dt)
    {
        if (_manualMode)
        {
            _sinceLastMoveCmd += dt;
            if (_sinceLastMoveCmd > ManualWatchdogSec)
            {
                _targetLinear  = 0;
                _targetAngular = 0;
            }
        }

        float targetLin, targetAng;
        if (_state == StateMowing)
        {
            targetLin = MowSpeedMs;
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

        _actualLinear  = Approach(_actualLinear,  targetLin, LinearAccelMs2  * (float)dt);
        _actualAngular = Approach(_actualAngular, targetAng, AngularAccelRs2 * (float)dt);

        _heading += _actualAngular * (float)dt;
        if (Math.Abs(_actualLinear) > 0.001f)
            StepGps(_actualLinear, _heading, dt);
    }

    private void FollowRoute(double dt)
    {
        if (_routeIdx >= _route.Count)
        {
            _state         = StateIdle;
            _actualLinear  = 0;
            _actualAngular = 0;
            return;
        }

        var (tLat, tLon) = _route[_routeIdx];
        double dNorth = (tLat - _lat) * MetersPerDegree;
        double dEast  = (tLon - _lon) * MetersPerDegree * Math.Cos(_lat * Math.PI / 180.0);
        double dist   = Math.Sqrt(dNorth * dNorth + dEast * dEast);

        if (dist < WaypointReachM)
        {
            _routeIdx++;
            return;
        }

        _heading       = (float)Math.Atan2(dEast, dNorth);
        _actualLinear  = MowSpeedMs;
        _actualAngular = 0;
        StepGps(MowSpeedMs, _heading, dt);
    }

    public TelemetryDto ToTelemetry()
    {
        float posXm = 0, posYm = 0;
        if (_hasDatum)
        {
            double latRad = _baseLat * Math.PI / 180.0;
            posXm = (float)((_lon - _baseLon) * MetersPerDegree * Math.Cos(latRad));
            posYm = (float)((_lat - _baseLat) * MetersPerDegree);
        }

        bool moving = Math.Abs(_actualLinear) > 0.01f || Math.Abs(_actualAngular) > 0.01f;

        return new TelemetryDto
        {
            Lat           = _lat,
            Lon           = _lon,
            GpsAccuracyMm = _accMm,
            GpsFixType    = _accMm < GoodAccuracyMm ? FixRtkFixed : FixStandard,
            HeadingRad    = _heading,
            PosX          = posXm,
            PosY          = posYm,
            LinearSpeed   = Math.Abs(_actualLinear),
            AccX          = (float)(Math.Sin(DateTime.Now.TimeOfDay.TotalSeconds) * 0.1),
            AccY          = (float)(Math.Cos(DateTime.Now.TimeOfDay.TotalSeconds) * 0.05),
            AccZ          = 9.81f,
            IsManualMode  = _manualMode,
            IsMotorMoving = moving,
            BatteryPct    = _battery,
            State         = _state,
            BladeOn       = _state == StateMowing,
            RainDetected  = false,
            UptimeMinutes = (int)(DateTime.UtcNow - _bootedAt).TotalMinutes,
            TimestampUtc  = DateTime.UtcNow,
        };
    }

    public IReadOnlyList<RobotEventDto> ApplyCommand(RobotCommandDto command)
    {
        if (command.Kind == CommandKinds.Motor)
        {
            ApplyMotor(command.LinearVel, command.AngularVel);
            return Array.Empty<RobotEventDto>();
        }

        if (command.Kind == CommandKinds.Boundary)
        {
            LoadRoute(command.Boundary);
            return Array.Empty<RobotEventDto>();
        }

        return ApplyAction(command.ActionName ?? string.Empty);
    }

    private void LoadRoute(BoundaryUploadDto? boundary)
    {
        if (boundary?.Points is not { Length: > 0 } points) return;

        _route.Clear();
        foreach (var p in points)
            _route.Add((p.Lat, p.Lon));
        _routeIdx  = 0;
        _pathSaved = true;
    }

    private void ApplyMotor(float linear, float angular)
    {
        if (!_manualMode && (Math.Abs(linear) > 0.001f || Math.Abs(angular) > 0.001f))
            _manualMode = true;

        if (!_manualMode) return;

        _targetLinear     = linear;
        _targetAngular    = angular;
        _sinceLastMoveCmd = 0;
    }

    private IReadOnlyList<RobotEventDto> ApplyAction(string action)
    {
        var events = new List<RobotEventDto>();
        switch (action)
        {
            case "ManualModeOn":
                _manualMode = true;
                break;

            case "ManualModeOff":
            case "Stop":
                _state            = StateIdle;
                _manualMode       = false;
                _targetLinear     = 0;
                _targetAngular    = 0;
                _sinceLastMoveCmd = 0;
                break;

            case "CaptureBase":
                if (_accMm > GoodAccuracyMm)
                    events.Add(Error("BASE_CAPTURE_FAIL/ACCURACY"));
                else
                {
                    _baseLat   = _lat;
                    _baseLon   = _lon;
                    _hasDatum  = true;
                    _pathSaved = false;
                    events.Add(new RobotEventDto { Type = RobotEventTypes.BaseCaptured });
                }
                break;

            case "StartMowing":
            case "StartRoute":
                if (_route.Count > 0)
                {
                    (_lat, _lon) = _route[0];
                    _routeIdx    = Math.Min(1, _route.Count - 1);
                    _state       = StateMowing;
                }
                else if (!_hasDatum)
                    events.Add(Error("MOWER_START_FAIL/NO_DATUM"));
                else if (!_pathSaved)
                    events.Add(Error("MOWER_START_FAIL/NO_PATH"));
                else
                    _state = StateMowing;
                break;

            case "BoundaryRecordStart":
            case "BoundaryClear":
                _activePoints.Clear();
                _closedPolygons.Clear();
                _route.Clear();
                _exitPoint = null;
                _pathSaved = false;
                _capturing = true;
                _state     = StateRecordingBoundary;
                events.Add(new RobotEventDto { Type = RobotEventTypes.BoundaryCleared });
                break;

            case "BoundaryCapturePoint":
                if (!_capturing || !_hasDatum)
                    events.Add(Error($"CAPTURE_POINT_FAIL/{(!_hasDatum ? "NO_DATUM" : "NOT_READY")}"));
                else
                {
                    var p = LocalCm();
                    _activePoints.Add(p);
                    events.Add(new RobotEventDto
                    {
                        Type = RobotEventTypes.BoundaryPointCaptured,
                        XCm  = (int)p.X,
                        YCm  = (int)p.Y
                    });
                }
                break;

            case "CaptureOutline":
                if (!_capturing)
                    events.Add(Error("CAPTURE_OUTLINE_FAIL"));
                else if (_activePoints.Count < 3)
                    events.Add(Error("CAPTURE_OUTLINE_FAIL/TOO_FEW_POINTS"));
                else
                {
                    _closedPolygons.Add(new List<(float, float)>(_activePoints));
                    _activePoints.Clear();
                    events.Add(new RobotEventDto { Type = RobotEventTypes.OutlineCaptured });
                }
                break;

            case "CaptureExit":
                if (!_capturing || !_hasDatum)
                    events.Add(Error($"CAPTURE_EXIT_FAIL/{(!_hasDatum ? "NO_DATUM" : "NOT_READY")}"));
                else
                {
                    var p = LocalCm();
                    _exitPoint = p;
                    events.Add(new RobotEventDto
                    {
                        Type = RobotEventTypes.ExitCaptured,
                        XCm  = (int)p.X,
                        YCm  = (int)p.Y
                    });
                }
                break;

            case "BoundaryRecordEnd":
                events.Add(EndRecording());
                break;
        }

        return events;
    }

    private RobotEventDto EndRecording()
    {
        bool wasCapturing = _capturing;
        _capturing = false;
        _state     = StateIdle;

        if (!wasCapturing)
            return Fail("NOT_RECORDING");
        if (_closedPolygons.Count == 0)
            return Fail("NO_POLYGON");
        if (_exitPoint is null)
            return Fail("NO_EXIT");

        _pathSaved = true;
        return new RobotEventDto { Type = RobotEventTypes.CaptureEnd, Success = true };
    }

    private static RobotEventDto Fail(string reason)
        => new() { Type = RobotEventTypes.CaptureEnd, Success = false, Reason = reason };

    private static RobotEventDto Error(string code)
        => new() { Type = RobotEventTypes.Error, Reason = code };

    private (float X, float Y) LocalCm()
    {
        double latRad = _baseLat * Math.PI / 180.0;
        float xCm = (float)((_lon - _baseLon) * MetersPerDegree * Math.Cos(latRad) * 100.0);
        float yCm = (float)((_lat - _baseLat) * MetersPerDegree * 100.0);
        return (xCm, yCm);
    }

    private void StepGps(double linearMs, double headingRad, double dt)
    {
        double dist = linearMs * dt;
        _lat += dist * Math.Cos(headingRad) / MetersPerDegree;
        _lon += dist * Math.Sin(headingRad) / MetersPerDegree / Math.Cos(_lat * Math.PI / 180.0);
    }

    private static float Approach(float current, float target, float step)
    {
        float diff = target - current;
        if (Math.Abs(diff) <= step) return target;
        return current + Math.Sign(diff) * step;
    }
}
