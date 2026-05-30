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
using MowIT.Domain.Entities;
using MowIT.Domain.Enums;
using MowIT.Domain.Interfaces;
using MowIT.Presentation.ViewModels.Base;

namespace MowIT.Presentation.ViewModels;

public partial class ControlViewModel : BaseViewModel
{
    private readonly IRobotControl   _control;
    private readonly IRobotSensors   _sensors;
    private readonly EventLogService _evt;
    private const string Source = "VM";
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
    [ObservableProperty] private GpsFixType _gpsFixType     = GpsFixType.NoFix;

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

    [ObservableProperty] private string _lastEvent = "Waiting…";

    // Local NEU frame state — drives the on-screen local map.
    public ObservableCollection<List<LocalPoint>> ClosedPolygons { get; } = new();
    public ObservableCollection<LocalPoint>       ActivePolygon  { get; } = new();
    [ObservableProperty] private LocalPoint? _mowerLocal;
    [ObservableProperty] private float _mowerHeadingRad;

    // Base GPS — captured when the user presses Set Base. Used to project subsequent GPS
    // readings into the local NEU frame so the mower position can be drawn on the local map.
    private GpsPoint? _baseGps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualStateText))]
    private bool _isManualMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualStateText))]
    private bool _isMotorMoving;

public ObservableCollection<GpsPoint> RobotTrail { get; } = new();

public bool HasGpsFix      => GpsFixType != GpsFixType.NoFix || CurrentPosition.Latitude != 0 || CurrentPosition.Longitude != 0;
    public bool IsNotRecording => !IsRecordingBoundary;
    public bool CanStartRecording => HasDatum && !IsRecordingBoundary;

    // Strict workflow gates — both app and mower stay in lock-step.
    // OUTLINE is a polygon SEPARATOR on the firmware — it closes the current outline
    // and starts a new one. So the flow is:
    //   single zone:      Point × N (≥3) → Save
    //   multiple zones:   Point × N → Outline → Point × M → Save
    // Save sends MOWER/CAPTURE/END which finalizes & persists the boundary in one shot.
    public bool CanCapturePoint    => IsRecordingBoundary;
    public bool CanCaptureOutline  => IsRecordingBoundary && BoundaryPointCount >= 3;
    // Save is allowed when either:
    //   - the in-progress polygon has ≥3 points (firmware will close it on END), or
    //   - at least one polygon was already closed via Outline and no points are pending.
    public bool CanSaveBoundary    => IsRecordingBoundary
                                      && (BoundaryPointCount >= 3
                                          || (PolygonCount >= 1 && BoundaryPointCount == 0));

    public string GpsAccuracyText => GpsAccuracyMm <= 0
        ? "–"
        : GpsAccuracyMm < 50
            ? $"RTK ±{GpsAccuracyMm:F0}mm"
            : $"±{GpsAccuracyMm / 1000:F2}m";

    public string SetupStatusText => (HasDatum, HasPath) switch
    {
        (false, _)    => "Set GPS base before recording",
        (true, false) => "No boundary recorded yet",
        _             => "Ready to mow"
    };

    public string RecordingStatusText => IsRecordingBoundary
        ? $"Recording — {PolygonCount} zone{(PolygonCount == 1 ? "" : "s")} · {BoundaryPointCount} point{(BoundaryPointCount == 1 ? "" : "s")}"
        : (HasPath ? "Boundary saved" : "Not recording");

    public string ManualStateText => IsManualMode
        ? (IsMotorMoving ? "● Moving" : "■ Stopped")
        : string.Empty;

    public ControlViewModel(IRobotControl control, IRobotSensors sensors, EventLogService evt)
    {
        _control = control;
        _sensors = sensors;
        _evt     = evt;
        Title    = "Robot Setup & Control";

        _joystickSub = _joystickSubject
            .Sample(TimeSpan.FromMilliseconds(100))
            .SelectMany(v => Observable.FromAsync(
                () => _control.SendMotorCommandAsync(v.lin, v.ang)))
            .Subscribe();

        _sensorSub = _sensors.SensorStream
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

                // Project current GPS to local NEU cm if we have a base. This is what the
                // local map uses to draw the mower position.
                MowerLocal       = ProjectToLocal(s.Gps);
                MowerHeadingRad  = s.HeadingRad;
            }));

        _statusSub = _sensors.StatusStream
            .Subscribe(s => MainThread.BeginInvokeOnMainThread(() =>
            {
                bool wasRecording = IsRecordingBoundary;
                IsRecordingBoundary = s.State == RobotState.RecordingBoundary;
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
                LogAndToast("📍 Base captured");
            }));

        WeakReferenceMessenger.Default.Register<BoundaryPointCapturedMessage>(this, (_, m) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                BoundaryPointCount++;
                ActivePolygon.Add(new LocalPoint(m.XCm, m.YCm));
                LogAndToast($"📌 Point {BoundaryPointCount} marked  ({m.XCm/100f:F2}, {m.YCm/100f:F2}) m");
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
                BoundaryPointCount = 0;
                LogAndToast($"＋ Outline {PolygonCount} closed — mark next zone or press Save");
            }));

        WeakReferenceMessenger.Default.Register<CaptureEndMessage>(this, (_, m) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (m.Success)
                {
                    HasPath = true;
                    IsRecordingBoundary = false;
                    BoundaryPointCount  = 0;
                    PolygonCount        = 0;
                    ActivePolygon.Clear();
                    _evt.State(Source, "HasPath=true, IsRecordingBoundary=false (path saved)");
                    LogAndToast("✓ Boundary saved to mower");
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
                // Capture-related failures abort the whole session so app + mower stay in sync.
                if (m.Code.StartsWith("CAPTURE_", StringComparison.OrdinalIgnoreCase)
                    || m.Code.StartsWith("BASE_CAPTURE_", StringComparison.OrdinalIgnoreCase))
                {
                    AbortRecording(m.Code);
                }
                else
                {
                    LogAndToast($"⚠ {m.Code}");
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
        // App returns to "not recording" locally. We don't send anything to the firmware here:
        // BoundaryClear maps to MOWER/CAPTURE/START which would re-arm capture and bounce us
        // straight back into the recording state via the status stream. The firmware just waits
        // in its capture-started state harmlessly until the user clicks Record again, which sends
        // a fresh START that clears + re-arms (it's idempotent).
        IsRecordingBoundary = false;
        BoundaryPointCount  = 0;
        PolygonCount        = 0;
        ClosedPolygons.Clear();
        ActivePolygon.Clear();

        LogAndToast($"✗ Recording aborted ({reason}) — please press Record to start over");
    }

    // Flat-earth projection of GPS to local NEU centimetres relative to the captured base.
    // X = east (cm), Y = north (cm). Approximation good for distances < ~1 km from base.
    private LocalPoint? ProjectToLocal(GpsPoint gps)
    {
        if (_baseGps is not GpsPoint b) return null;
        if (gps.Latitude == 0 && gps.Longitude == 0) return null;

        const double MetersPerDeg = 111319.444;
        double latRad = b.Latitude * Math.PI / 180.0;
        double dEast  = (gps.Longitude - b.Longitude) * MetersPerDeg * Math.Cos(latRad);
        double dNorth = (gps.Latitude  - b.Latitude)  * MetersPerDeg;
        return new LocalPoint((float)(dEast * 100.0), (float)(dNorth * 100.0));
    }

    private void LogAndToast(string text)
    {
        LastEvent = $"{DateTime.Now:HH:mm:ss}  {text}";
        _evt.Info(Source, text);
        try { _ = Toast.Make(text, ToastDuration.Short).Show(); }
        catch { /* toast unavailable on some platforms — non-fatal */ }
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
               "Base capture failed — ensure GPS has a good fix");
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
        // OUTLINE on firmware = "save current outline, start a new one" — used as a
        // separator between polygons when the boundary has multiple disjoint zones.
        _evt.Info(Source, $"user pressed New Outline  (closing current with {BoundaryPointCount} pts, starting next)");
        await RunSafeAsync(() => _control.SendActionAsync(RobotAction.CaptureOutline));
    }

    [RelayCommand(CanExecute = nameof(CanSaveBoundary))]
    private async Task SaveBoundaryAsync()
    {
        _evt.Info(Source, $"user pressed Save  (zones={PolygonCount} pts-pending={BoundaryPointCount})");
        await RunSafeAsync(() => _control.SendActionAsync(RobotAction.BoundaryRecordEnd));
    }

public override void OnDisappearing()
    {
        // Stop motors when leaving so the mower doesn't keep moving in the background.
        // Do NOT dispose the streams or unregister message handlers here:
        // MAUI Shell tabs CACHE pages, so this VM is reused when the user comes back.
        // The constructor only runs once, so disposing here would leave the VM permanently
        // deaf to status updates / capture messages on the next visit (the bug that caused
        // "press Record, nothing happens" after a Dashboard round-trip).
        _ = _control.SendActionAsync(RobotAction.Stop);
    }
}
