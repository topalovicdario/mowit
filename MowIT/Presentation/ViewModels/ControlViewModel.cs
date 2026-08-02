using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MowIT.Application.Logging;
using MowIT.Application.Messages;
using MowIT.Application.Services;
using MowIT.Domain.Entities;
using MowIT.Domain.Enums;
using MowIT.Domain.Geometry;
using MowIT.Domain.Interfaces;
using MowIT.Presentation.ViewModels.Base;

namespace MowIT.Presentation.ViewModels;

public partial class ControlViewModel : BaseViewModel
{
    private readonly IRobotControl   _control;
    private readonly IRobotSensors   _sensors;
    private readonly IRobotBoundary      _boundary;
    private readonly IBoundaryRepository _repo;
    private readonly MowingRoutePlanner  _planner;
    private readonly GeofenceMonitor  _geofence;
    private readonly EventLogService _evt;
    private const string Source = "VM";
    private const int    JoystickKeepAliveMs = 250;
    private IDisposable? _joystickSub;
    private IDisposable? _sensorSub;
    private IDisposable? _statusSub;
    private readonly Subject<(float lin, float ang)> _joystickSubject = new();

    public EventLogService EventLog => _evt;

[ObservableProperty] private float _linearVelocity;
    [ObservableProperty] private float _angularVelocity;

[ObservableProperty] private GpsPoint  _currentPosition;
    [ObservableProperty] private string    _currentGpsText  = "No GPS fix";
    [ObservableProperty] private float     _gpsAccuracyMm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpsFixLabel))]
    [NotifyPropertyChangedFor(nameof(GpsAccuracyText))]
    private GpsFixType _gpsFixType = GpsFixType.NoFix;

[ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SetupStatusText))]
    [NotifyPropertyChangedFor(nameof(CanStartRecording))]
    [NotifyCanExecuteChangedFor(nameof(StartBoundaryRecordingCommand))]
    private bool _hasDatum;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SetupStatusText))]
    private bool _hasPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotRecording))]
    [NotifyPropertyChangedFor(nameof(CanStartRecording))]
    [NotifyPropertyChangedFor(nameof(RecordingStatusText))]
    [NotifyPropertyChangedFor(nameof(CanCapturePoint))]
    [NotifyPropertyChangedFor(nameof(CanCaptureOutline))]
    [NotifyPropertyChangedFor(nameof(CanSaveBoundary))]
    [NotifyCanExecuteChangedFor(nameof(StartBoundaryRecordingCommand))]
    [NotifyCanExecuteChangedFor(nameof(CapturePointCommand))]
    [NotifyCanExecuteChangedFor(nameof(CaptureOutlineCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveBoundaryCommand))]
    private bool _isRecordingBoundary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordingStatusText))]
    [NotifyPropertyChangedFor(nameof(CanSaveBoundary))]
    [NotifyPropertyChangedFor(nameof(CanCaptureOutline))]
    [NotifyCanExecuteChangedFor(nameof(CaptureOutlineCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveBoundaryCommand))]
    private int _boundaryPointCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordingStatusText))]
    [NotifyPropertyChangedFor(nameof(CanSaveBoundary))]
    [NotifyCanExecuteChangedFor(nameof(SaveBoundaryCommand))]
    private int _polygonCount;

    [ObservableProperty] private string _lastEvent = "Waiting...";

    
    public ObservableCollection<List<LocalPoint>> ClosedPolygons { get; } = new();
    public ObservableCollection<LocalPoint>       ActivePolygon  { get; } = new();
    public List<List<GpsPoint>>                   ClosedGpsPolygons { get; } = new();
    public List<GpsPoint>                         ActiveGpsPolygon  { get; } = new();
    [ObservableProperty] private LocalPoint? _mowerLocal;
    [ObservableProperty] private float _mowerHeadingRad;


    private GpsPoint? _baseGps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualStateText))]
    private bool _isManualMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualStateText))]
    private bool _isMotorMoving;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MowingColor))]
    [NotifyPropertyChangedFor(nameof(MowingLabel))]
    [NotifyPropertyChangedFor(nameof(MowingHeadline))]
    [NotifyPropertyChangedFor(nameof(MowingSubtext))]
    private bool _isMowing;

    public Color  MowingColor    => IsMowing ? Color.FromArgb("#4CAF50") : Color.FromArgb("#E53935");
    public string MowingLabel    => IsMowing ? "Mowing" : "Not mowing";
    public string MowingHeadline => IsMowing ? "MOWING" : "NOT MOWING";
    public string MowingSubtext  => IsMowing ? "Blades running - cutting in progress" : "Mower is idle";

public ObservableCollection<GpsPoint> RobotTrail { get; } = new();

public bool HasGpsFix      => GpsFixType != GpsFixType.NoFix || CurrentPosition.Latitude != 0 || CurrentPosition.Longitude != 0;
    public bool IsNotRecording => !IsRecordingBoundary;
    public bool CanStartRecording => HasDatum && !IsRecordingBoundary;


    public bool CanCapturePoint    => IsRecordingBoundary;
    public bool CanCaptureOutline  => IsRecordingBoundary && BoundaryPointCount >= 3;
  
    public bool CanSaveBoundary    => IsRecordingBoundary
                                      && (BoundaryPointCount >= 3
                                          || (PolygonCount >= 1 && BoundaryPointCount == 0));

    public string GpsFixLabel => GpsFixType switch
    {
        GpsFixType.RtkFixed => "RTK fix",
        GpsFixType.RtkFloat => "RTK float",
        GpsFixType.Standard => "GNSS",
        _                   => "No fix"
    };

    public string GpsAccuracyText => GpsAccuracyMm <= 0
        ? "-"
        : GpsAccuracyMm < 1000
            ? $"{GpsFixLabel} ±{GpsAccuracyMm:F0}mm"
            : $"{GpsFixLabel} ±{GpsAccuracyMm / 1000:F2}m";

    public string SetupStatusText => (HasDatum, HasPath) switch
    {
        (false, _)    => "Set GPS base before recording",
        (true, false) => "No boundary recorded yet",
        _             => "Ready to mow"
    };

    public string RecordingStatusText => IsRecordingBoundary
        ? $"Recording - {PolygonCount} zone{(PolygonCount == 1 ? "" : "s")}, {BoundaryPointCount} point{(BoundaryPointCount == 1 ? "" : "s")}"
        : (HasPath ? "Boundary saved" : "Not recording");

    public string ManualStateText => IsManualMode
        ? (IsMotorMoving ? "Moving" : "Stopped")
        : string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDriveMode))]
    private bool _isPlanMode;

    public bool IsDriveMode => !IsPlanMode;

    public List<GpsPoint>   PlanPoints      { get; } = new();
    public List<LocalPoint> PlanPointsLocal { get; } = new();
    public List<LocalPoint> PlanRoute       { get; } = new();

    [ObservableProperty] private int _planRevision;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlanStrategy0))]
    [NotifyPropertyChangedFor(nameof(IsPlanStrategy1))]
    private int _planStrategyIndex;

    public bool IsPlanStrategy0 => PlanStrategyIndex == 0;
    public bool IsPlanStrategy1 => PlanStrategyIndex == 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlanAct))]
    private bool _hasPlanRoute;

    public int    PlanPointCount      => PlanPoints.Count;
    public bool   CanPickPlanStrategy => PlanPoints.Count >= 3;
    public bool   CanPlanAct          => HasPlanRoute;
    public string PlanHint => PlanPoints.Count switch
    {
        0 => "Drive to a corner, then capture",
        _ => $"{PlanPoints.Count} point{(PlanPoints.Count == 1 ? "" : "s")} - drive to the next corner"
    };

    public ControlViewModel(
        IRobotControl control,
        IRobotSensors sensors,
        IRobotBoundary boundary,
        IBoundaryRepository repo,
        MowingRoutePlanner planner,
        GeofenceMonitor geofence,
        EventLogService evt)
    {
        _control  = control;
        _sensors  = sensors;
        _boundary = boundary;
        _repo     = repo;
        _planner  = planner;
        _geofence = geofence;
        _evt      = evt;
        Title     = "Robot Setup & Control";

        _joystickSub = _joystickSubject
            .Sample(TimeSpan.FromMilliseconds(100))
            .Select(v => IsIdleVector(v)
                ? Observable.Return(v)
                : Observable.Return(v).Concat(
                      Observable.Interval(TimeSpan.FromMilliseconds(JoystickKeepAliveMs))
                                .Select(_ => v)))
            .Switch()
            .SelectMany(v => Observable.FromAsync(
                () => _control.SendMotorCommandAsync(v.lin, v.ang)))
            .Subscribe();

        _sensorSub = _sensors.SensorStream
            .Sample(TimeSpan.FromMilliseconds(100))
            .Subscribe(s => MainThread.BeginInvokeOnMainThread(() =>
            {
                CurrentPosition = s.Gps;
                GpsAccuracyMm   = s.GpsAccuracyMm;
                GpsFixType      = s.GpsFixType;
                CurrentGpsText  = HasGpsFix
                    ? $"{s.Gps.Latitude:F6}°   {s.Gps.Longitude:F6}°"
                    : "No GPS fix";
                OnPropertyChanged(nameof(GpsAccuracyText));
                OnPropertyChanged(nameof(HasGpsFix));

                IsManualMode  = s.IsManualMode;
                IsMotorMoving = s.IsMotorMoving;

                if (RobotTrail.Count == 0 || RobotTrail[^1].DistanceTo(s.Gps) > 0.5)
                    RobotTrail.Add(s.Gps);

                MowerLocal       = ProjectToLocal(s.Gps);
                MowerHeadingRad  = s.HeadingRad;
            }));

        _statusSub = _sensors.StatusStream
            .Subscribe(s => MainThread.BeginInvokeOnMainThread(() =>
            {
                bool wasRecording = IsRecordingBoundary;
                IsRecordingBoundary = s.State == RobotState.RecordingBoundary;
                IsMowing = s.State == RobotState.Mowing;
                if (!wasRecording && IsRecordingBoundary)
                {
                    BoundaryPointCount = 0;
                    PolygonCount       = 0;
                    _evt.State(Source, $"IsRecordingBoundary=true  (firmware state={s.State})");
                }
                else if (wasRecording && !IsRecordingBoundary)
                {
                    BoundaryPointCount = 0;
                    _evt.State(Source, $"IsRecordingBoundary=false  (firmware state={s.State})");
                }
            }));

        WeakReferenceMessenger.Default.Register<BaseCapturedMessage>(this, (_, _) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                HasDatum = true;
                if (HasGpsFix && (CurrentPosition.Latitude != 0 || CurrentPosition.Longitude != 0))
                    _baseGps = CurrentPosition;
                MowerLocal = ProjectToLocal(CurrentPosition);
                _evt.State(Source, $"HasDatum=true  baseGps=({_baseGps?.Latitude:F7}, {_baseGps?.Longitude:F7})");
                LogAndToast("Base captured");
            }));

        WeakReferenceMessenger.Default.Register<BoundaryPointCapturedMessage>(this, (_, m) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                BoundaryPointCount++;
                ActivePolygon.Add(new LocalPoint(m.XCm, m.YCm));
                LogAndToast($"Point {BoundaryPointCount} marked  ({m.XCm/100f:F2}, {m.YCm/100f:F2}) m");
            }));

        WeakReferenceMessenger.Default.Register<BoundaryGpsPointCapturedMessage>(this, (_, m) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _baseGps ??= m.Point;

                BoundaryPointCount++;
                ActiveGpsPolygon.Add(m.Point);

                if (ProjectToLocal(m.Point) is LocalPoint lp)
                    ActivePolygon.Add(lp);

                LogAndToast($"Point {BoundaryPointCount} marked  {m.Point.Latitude:F7}, {m.Point.Longitude:F7}");
            }));

        WeakReferenceMessenger.Default.Register<OutlineCapturedMessage>(this, (_, _) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                PolygonCount++;
                if (ActivePolygon.Count > 0)
                {
                    ClosedPolygons.Add(new List<LocalPoint>(ActivePolygon));
                    ActivePolygon.Clear();
                }
                if (ActiveGpsPolygon.Count > 0)
                {
                    ClosedGpsPolygons.Add(new List<GpsPoint>(ActiveGpsPolygon));
                    ActiveGpsPolygon.Clear();
                }
                BoundaryPointCount = 0;
                LogAndToast($"+ Outline {PolygonCount} closed - mark next zone or press Save");
            }));

        WeakReferenceMessenger.Default.Register<CaptureEndMessage>(this, (_, m) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (m.Success)
                {
                    var gpsZone = BuildWalkedGpsZone();

                    HasPath = true;
                    IsRecordingBoundary = false;
                    BoundaryPointCount  = 0;
                    PolygonCount        = 0;
                    ActivePolygon.Clear();
                    ActiveGpsPolygon.Clear();
                    ClosedGpsPolygons.Clear();
                    _evt.State(Source, "HasPath=true, IsRecordingBoundary=false (path saved)");
                    LogAndToast("Boundary saved to mower");

                    if (gpsZone is not null)
                        _ = PersistWalkedZoneAsync(gpsZone);
                }
                else
                {
                    string reason = string.IsNullOrWhiteSpace(m.FailReason) ? "unknown" : m.FailReason;
                    AbortRecording($"END/{reason}");
                }
            }));

        WeakReferenceMessenger.Default.Register<RobotErrorMessage>(this, (_, m) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (m.Code.StartsWith("CAPTURE_", StringComparison.OrdinalIgnoreCase)
                    || m.Code.StartsWith("BASE_CAPTURE_", StringComparison.OrdinalIgnoreCase))
                {
                    AbortRecording(m.Code);
                }
                else
                {
                    LogAndToast($"{m.Code}");
                }
            }));

        WeakReferenceMessenger.Default.Register<BoundaryClearedMessage>(this, (_, _) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                HasPath = false;
                _evt.State(Source, "HasPath=false (firmware cleared CONFIG_PATH on START)");
            }));
    }

    private void AbortRecording(string reason)
    {
       
        IsRecordingBoundary = false;
        BoundaryPointCount  = 0;
        PolygonCount        = 0;
        ClosedPolygons.Clear();
        ActivePolygon.Clear();
        ClosedGpsPolygons.Clear();
        ActiveGpsPolygon.Clear();

        LogAndToast($"Recording aborted ({reason}) - please press Record to start over");
    }

    
    private static bool IsIdleVector((float lin, float ang) v)
        => MathF.Abs(v.lin) < 0.001f && MathF.Abs(v.ang) < 0.001f;

    private LocalPoint? ProjectToLocal(GpsPoint gps)
    {
        if (_baseGps is not GpsPoint b) return null;
        if (gps.Latitude == 0 && gps.Longitude == 0) return null;

        var (east, north) = new LocalProjection(b).ToLocal(gps);
        return new LocalPoint((float)(east * 100.0), (float)(north * 100.0));
    }

    private BoundaryZone? BuildWalkedGpsZone()
    {
        var gpsPolygons = ClosedGpsPolygons.Select(p => p.ToList()).ToList();
        if (ActiveGpsPolygon.Count >= 3)
            gpsPolygons.Add(ActiveGpsPolygon.ToList());

        var gpsOuter = gpsPolygons.OrderByDescending(p => p.Count).FirstOrDefault();
        if (gpsOuter is { Count: >= 3 })
        {
            return new BoundaryZone
            {
                Name      = $"Walked {DateTime.Now:HH:mm}",
                Points    = gpsOuter,
                CreatedAt = DateTime.UtcNow
            };
        }

        if (_baseGps is not GpsPoint baseGps)
        {
            _evt.Warn(Source, "walked boundary not saved for geofence - no GPS base datum");
            return null;
        }

        var polygons = ClosedPolygons.Select(p => p.ToList()).ToList();
        if (ActivePolygon.Count >= 3)
            polygons.Add(ActivePolygon.ToList());

        var outer = polygons.OrderByDescending(p => p.Count).FirstOrDefault();
        if (outer is null || outer.Count < 3)
            return null;

        var proj = new LocalProjection(baseGps);
        var gps  = outer
            .Select(lp => proj.ToGps(lp.XCm / 100.0, lp.YCm / 100.0))
            .ToList();

        return new BoundaryZone
        {
            Name      = $"Walked {DateTime.Now:HH:mm}",
            Points    = gps,
            CreatedAt = DateTime.UtcNow
        };
    }

    private async Task PersistWalkedZoneAsync(BoundaryZone zone)
    {
        try
        {
            await _repo.SaveAsync(zone);
            await _geofence.ReloadAsync();
            _evt.State(Source, $"walked boundary saved as GPS zone \"{zone.Name}\" ({zone.Points.Count} pts) - geofence armed");
        }
        catch (Exception ex)
        {
            _evt.Error(Source, $"failed to save walked boundary: {ex.Message}");
        }
    }

    private void LogAndToast(string text)
    {
        LastEvent = $"{DateTime.Now:HH:mm:ss}  {text}";
        _evt.Info(Source, text);
        try { _ = Toast.Make(text, ToastDuration.Short).Show(); }
        catch { }
    }

public void OnJoystickMoved(float normalizedX, float normalizedY)
    {
        float lin =  normalizedY * 0.5f;
        float ang = -normalizedX * 1.0f;
        LinearVelocity  = lin;
        AngularVelocity = ang;
        _joystickSubject.OnNext((lin, ang));
    }

    public void OnJoystickReleased()
    {
        LinearVelocity  = 0;
        AngularVelocity = 0;
        _joystickSubject.OnNext((0, 0));
    }

[RelayCommand]
    private async Task CaptureBaseAsync()
    {
        _evt.Info(Source, $"user pressed Set Base  (HasGpsFix={HasGpsFix} acc={GpsAccuracyMm:F0}mm)");
        await RunSafeAsync(
               () => _control.SendActionAsync(RobotAction.CaptureBase),
               "Base capture failed - ensure GPS has a good fix");
    }

[RelayCommand(CanExecute = nameof(CanStartRecording))]
    private async Task StartBoundaryRecordingAsync()
    {
        _evt.Info(Source, $"user pressed Record  (HasDatum={HasDatum} HasPath={HasPath})");
        BoundaryPointCount = 0;
        PolygonCount       = 0;
        ClosedPolygons.Clear();
        ActivePolygon.Clear();
        await RunSafeAsync(() => _control.SendActionAsync(RobotAction.BoundaryRecordStart));
    }

    [RelayCommand(CanExecute = nameof(CanCapturePoint))]
    private async Task CapturePointAsync()
    {
        _evt.Info(Source, $"user pressed Mark  (pts={BoundaryPointCount} zones={PolygonCount})");
        await RunSafeAsync(() => _control.SendActionAsync(RobotAction.BoundaryCapturePoint));
    }

    [RelayCommand(CanExecute = nameof(CanCaptureOutline))]
    private async Task CaptureOutlineAsync()
    {
       
        _evt.Info(Source, $"user pressed New Outline  (closing current with {BoundaryPointCount} pts, starting next)");
        await RunSafeAsync(() => _control.SendActionAsync(RobotAction.CaptureOutline));
    }

    [RelayCommand(CanExecute = nameof(CanSaveBoundary))]
    private async Task SaveBoundaryAsync()
    {
        _evt.Info(Source, $"user pressed Save  (zones={PolygonCount} pts-pending={BoundaryPointCount})");
        await RunSafeAsync(() => _control.SendActionAsync(RobotAction.BoundaryRecordEnd));
    }

    [RelayCommand]
    private void SelectMode(string mode)
        => IsPlanMode = string.Equals(mode, "Plan", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void CapturePlanPoint()
    {
        if (!HasDatum || _baseGps is not GpsPoint baseGps) return;

        var gps = CurrentPosition;
        if (gps.Latitude == 0 && gps.Longitude == 0) return;

        var (east, north) = new LocalProjection(baseGps).ToLocal(gps);
        PlanPoints.Add(gps);
        PlanPointsLocal.Add(new LocalPoint((float)(east * 100.0), (float)(north * 100.0)));
        ResetPlanRoute();
        NotifyPlanChanged();
        LogAndToast($"Point {PlanPoints.Count} captured");
    }

    [RelayCommand]
    private void UndoPlanPoint()
    {
        if (PlanPoints.Count == 0) return;
        PlanPoints.RemoveAt(PlanPoints.Count - 1);
        PlanPointsLocal.RemoveAt(PlanPointsLocal.Count - 1);
        ResetPlanRoute();
        NotifyPlanChanged();
    }

    [RelayCommand]
    private void ClearPlanPoints()
    {
        PlanPoints.Clear();
        PlanPointsLocal.Clear();
        ResetPlanRoute();
        NotifyPlanChanged();
    }

    [RelayCommand]
    private void SelectPlanStrategy(string indexText)
    {
        if (!int.TryParse(indexText, out int idx) || _planner.Strategies.Count == 0) return;

        PlanStrategyIndex      = idx % _planner.Strategies.Count;
        _planner.SelectedIndex = PlanStrategyIndex;
        ComputePlanRoute();
    }

    [RelayCommand(CanExecute = nameof(CanPlanAct))]
    private async Task SavePlanZoneAsync()
    {
        if (PlanPoints.Count < 3) return;

        string? name = await Shell.Current.DisplayPromptAsync(
            "Save zone",
            "Name this zone:",
            accept: "Save",
            cancel: "Cancel",
            initialValue: $"Zone {DateTime.Now:HH:mm}",
            maxLength: 40);

        if (name is null) return;

        var zone = BuildPlanZone(name);
        if (zone is null) return;

        await RunSafeAsync(async () =>
        {
            await _repo.SaveAsync(zone);
            LogAndToast($"Zone \"{zone.Name}\" saved");
        }, "Save failed");
    }

    [RelayCommand(CanExecute = nameof(CanPlanAct))]
    private async Task MowPlanNowAsync()
    {
        var zone = BuildPlanZone();
        if (zone is null) return;

        await RunSafeAsync(async () =>
        {
            var route = _planner.Plan(zone, PlanStrategyIndex);
            if (route.Count == 0) { ErrorMessage = "Route is empty - add more area"; return; }

            await _boundary.SendRouteAsync(route);
            await _control.SendActionAsync(RobotAction.StartMowing);
            LogAndToast("Mowing the planned route");
        }, "Failed to start mowing");
    }

    private void ComputePlanRoute()
    {
        var zone = BuildPlanZone();
        if (zone is null) return;

        var routeGps = _planner.Plan(zone, PlanStrategyIndex);

        PlanRoute.Clear();
        if (_baseGps is GpsPoint baseGps)
        {
            var proj = new LocalProjection(baseGps);
            foreach (var g in routeGps)
            {
                var (east, north) = proj.ToLocal(g);
                PlanRoute.Add(new LocalPoint((float)(east * 100.0), (float)(north * 100.0)));
            }
        }

        HasPlanRoute = routeGps.Count > 0;
        NotifyPlanChanged();
    }

    private BoundaryZone? BuildPlanZone(string? name = null)
    {
        if (PlanPoints.Count < 3) return null;

        return new BoundaryZone
        {
            Name   = string.IsNullOrWhiteSpace(name) ? $"Zone {DateTime.Now:HH:mm}" : name.Trim(),
            Points = PlanPoints.ToList()
        };
    }

    private void ResetPlanRoute()
    {
        PlanRoute.Clear();
        HasPlanRoute = false;
    }

    private void NotifyPlanChanged()
    {
        PlanRevision++;
        OnPropertyChanged(nameof(PlanPointCount));
        OnPropertyChanged(nameof(PlanHint));
        OnPropertyChanged(nameof(CanPickPlanStrategy));
        OnPropertyChanged(nameof(CanPlanAct));
        SavePlanZoneCommand.NotifyCanExecuteChanged();
        MowPlanNowCommand.NotifyCanExecuteChanged();
    }

public override void OnDisappearing()
    {
        
        _ = _control.SendActionAsync(RobotAction.Stop);
    }
}
