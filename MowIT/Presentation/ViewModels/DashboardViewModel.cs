using System.Reactive.Linq;
using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using MowIT.Application.Logging;
using MowIT.Application.Messages;
using MowIT.Application.Services;
using MowIT.Domain.Entities;
using MowIT.Domain.Enums;
using MowIT.Domain.Interfaces;
using MowIT.Presentation.ViewModels.Base;

namespace MowIT.Presentation.ViewModels;

public partial class DashboardViewModel : BaseViewModel
{
    private readonly IRobotSensors    _sensors;
    private readonly IRobotControl    _control;
    private readonly IRobotConnection _connection;
    private readonly ILogger<DashboardViewModel> _logger;
    private readonly EventLogService _evt;
    private readonly LastMowSession  _lastMow;
    private const string Source = "DASH";

    public EventLogService EventLog => _evt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateColor))]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    [NotifyPropertyChangedFor(nameof(IsOn))]
    [NotifyPropertyChangedFor(nameof(OnOffLabel))]
    private RobotState _robotState;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBattery))]
    private int _batteryPct;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlade))]
    [NotifyPropertyChangedFor(nameof(BladeColor))]
    [NotifyPropertyChangedFor(nameof(BladeStateLabel))]
    private bool _bladeOn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRain))]
    private bool _rainDetected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartMowing))]
    [NotifyPropertyChangedFor(nameof(ReadinessText))]
    [NotifyPropertyChangedFor(nameof(ConnectionLabel))]
    [NotifyCanExecuteChangedFor(nameof(StartMowingCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartMowing))]
    [NotifyPropertyChangedFor(nameof(ReadinessText))]
    [NotifyCanExecuteChangedFor(nameof(StartMowingCommand))]
    private bool _hasDatum;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartMowing))]
    [NotifyPropertyChangedFor(nameof(ReadinessText))]
    [NotifyCanExecuteChangedFor(nameof(StartMowingCommand))]
    private bool _hasPath;

    [ObservableProperty] private string _lastMowedLabel = "Never";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedLabel))]
    private float _speedMps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpsFixLabel))]
    [NotifyPropertyChangedFor(nameof(GpsFixColor))]
    private GpsFixType _gpsFix;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpsAccuracyLabel))]
    private float _gpsAccuracyMm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeadingLabel))]
    private float _headingRad;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeLabel))]
    [NotifyPropertyChangedFor(nameof(ModeColor))]
    private bool _isManualMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MotionLabel))]
    [NotifyPropertyChangedFor(nameof(MotionColor))]
    private bool _isMotorMoving;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UptimeLabel))]
    private int _uptimeMinutes;

    public bool HasBattery => BatteryPct > 0;
    public bool HasBlade   => BladeOn;
    public bool HasRain    => RainDetected;

    public Color BladeColor => BladeOn ? Color.FromArgb("#4CAF50") : Color.FromArgb("#9AA5A0");
    public string BladeStateLabel => BladeOn ? "ON" : "OFF";

    public string SpeedLabel => $"{Math.Abs(SpeedMps):0.00} m/s";

    public string GpsFixLabel => GpsFix switch
    {
        GpsFixType.RtkFixed => "RTK Fixed",
        GpsFixType.RtkFloat => "RTK Float",
        GpsFixType.Standard => "GPS",
        _                   => "No Fix"
    };

    public Color GpsFixColor => GpsFix switch
    {
        GpsFixType.RtkFixed => Color.FromArgb("#4CAF50"),
        GpsFixType.RtkFloat => Color.FromArgb("#2196F3"),
        GpsFixType.Standard => Color.FromArgb("#FF9800"),
        _                   => Color.FromArgb("#F44336")
    };

    public string GpsAccuracyLabel => GpsFix == GpsFixType.NoFix
        ? "no signal"
        : $"± {GpsAccuracyMm / 10f:0.0} cm";

    public string HeadingLabel
    {
        get
        {
            var deg = HeadingRad * 180.0 / Math.PI;
            deg = ((deg % 360) + 360) % 360;
            string[] dirs = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            var idx = (int)Math.Round(deg / 45.0) % 8;
            return $"{dirs[idx]} {deg:0}°";
        }
    }

    public string ModeLabel  => IsManualMode ? "Manual" : "Auto";
    public Color  ModeColor  => IsManualMode ? Color.FromArgb("#FF9800") : Color.FromArgb("#4CAF50");

    public string MotionLabel => IsMotorMoving ? "Moving" : "Stopped";
    public Color  MotionColor => IsMotorMoving ? Color.FromArgb("#4CAF50") : Color.FromArgb("#9AA5A0");

    public string UptimeLabel => UptimeMinutes <= 0
        ? "-"
        : UptimeMinutes < 60
            ? $"{UptimeMinutes}m"
            : $"{UptimeMinutes / 60}h {UptimeMinutes % 60}m";

    public bool IsOn => RobotState != RobotState.Idle && RobotState != RobotState.Error;
    public string OnOffLabel => IsOn ? "ON" : "OFF";

    public string ConnectionLabel => IsConnected ? "Connected" : "Disconnected";

    public string Greeting
    {
        get
        {
            var name = Preferences.Get("profile_name", string.Empty);
            return string.IsNullOrWhiteSpace(name) ? "GreenTitan dashboard" : $"Welcome, {name}";
        }
    }

    public bool CanStartMowing => IsConnected && HasDatum && HasPath && RobotState != RobotState.Mowing;

    public string ReadinessText => (IsConnected, HasDatum, HasPath) switch
    {
        (false, _, _)  => "Connect to mower first",
        (_, false, _)  => "Set base on the Control page",
        (_, _, false)  => "Record + save a boundary",
        _              => "Ready to mow"
    };

    public Color StateColor => RobotState switch
    {
        RobotState.Mowing           => Color.FromArgb("#4CAF50"),
        RobotState.Paused           => Color.FromArgb("#FF9800"),
        RobotState.Returning        => Color.FromArgb("#2196F3"),
        RobotState.Charging         => Color.FromArgb("#9C27B0"),
        RobotState.RecordingBoundary => Color.FromArgb("#FF9800"),
        RobotState.Error            => Color.FromArgb("#F44336"),
        _                           => Color.FromArgb("#607D8B")
    };

    public string StateLabel => RobotState switch
    {
        RobotState.Idle              => "IDLE",
        RobotState.Mowing            => "MOWING",
        RobotState.Paused            => "PAUSED",
        RobotState.Returning         => "RETURNING",
        RobotState.Charging          => "CHARGING",
        RobotState.RecordingBoundary => "RECORDING",
        RobotState.Error             => "ERROR",
        _                            => RobotState.ToString().ToUpper()
    };

    public DashboardViewModel(
        IRobotSensors sensors,
        IRobotControl control,
        IRobotConnection connection,
        ILogger<DashboardViewModel> logger,
        EventLogService evt,
        LastMowSession lastMow)
    {
        _sensors    = sensors;
        _control    = control;
        _connection = connection;
        _logger     = logger;
        _evt        = evt;
        _lastMow    = lastMow;
        Title       = "Dashboard";

        IsConnected = connection.IsConnected;
        RefreshLastMowedLabel();
        _lastMow.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(RefreshLastMowedLabel);

        _sensors.StatusStream
            .Sample(TimeSpan.FromSeconds(1))
            .Subscribe(s => MainThread.BeginInvokeOnMainThread(() => UpdateStatus(s)));

        _sensors.SensorStream
            .Sample(TimeSpan.FromMilliseconds(500))
            .Subscribe(s => MainThread.BeginInvokeOnMainThread(() => UpdateSensor(s)));

        _connection.ConnectionState
            .Subscribe(s => MainThread.BeginInvokeOnMainThread(() =>
            {
                IsConnected = s == RobotConnectionState.Connected;
                _evt.State(Source, $"connection to {s}  (IsConnected={IsConnected})");
            }));

        WeakReferenceMessenger.Default.Register<BaseCapturedMessage>(this, (_, _) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                HasDatum = true;
                HasPath  = false;
                _evt.State(Source, "HasDatum=true, HasPath=false (re-base clears path)");
                Toast("Base captured");
            }));

        WeakReferenceMessenger.Default.Register<CaptureEndMessage>(this, (_, m) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (m.Success)
                {
                    HasPath = true;
                    _evt.State(Source, "HasPath=true (Mow now allowed)");
                    Toast("Boundary saved - ready to mow");
                }
                else
                {
                    _evt.Warn(Source, $"CaptureEnd FAIL  reason={m.FailReason ?? "unknown"}");
                }
            }));

        WeakReferenceMessenger.Default.Register<BoundaryClearedMessage>(this, (_, _) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                HasPath = false;
                _evt.State(Source, "HasPath=false (firmware cleared CONFIG_PATH)");
            }));

        WeakReferenceMessenger.Default.Register<RobotConnectedMessage>(this, (_, m) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _evt.State(Source, $"connected to {m.DeviceName}");
                Toast($"Connected to {m.DeviceName}");
            }));

        WeakReferenceMessenger.Default.Register<RobotDisconnectedMessage>(this, (_, m) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _evt.Warn(Source, $"disconnected ({m.Reason})");
                IsConnected = false;
                Toast("Disconnected from mower");
            }));

        WeakReferenceMessenger.Default.Register<RobotErrorMessage>(this, (_, m) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _evt.Error(Source, m.Code);
                Toast(FriendlyError(m.Code));
            }));

        WeakReferenceMessenger.Default.Register<GeofenceBreachMessage>(this, (_, m) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _evt.Warn(Source, $"GEOFENCE breach in \"{m.ZoneName}\" - mower stopped");
                Toast($"⚠ Left zone \"{m.ZoneName}\" - stopping");
            }));
    }

    private static string FriendlyError(string code) => code switch
    {
        "MOWER_START_FAIL/NO_DATUM" => "Can't start - set base first",
        "MOWER_START_FAIL/NO_PATH"  => "Can't start - record + save a boundary first",
        "MOWER_START_FAIL"          => "Mower refused to start",
        "BASE_CAPTURE_FAIL/ACCURACY" => "GPS not accurate enough yet - wait for RTK",
        var s when s.StartsWith("CAPTURE_POINT_FAIL") => "Couldn't mark point - session reset",
        var s when s.StartsWith("CAPTURE_OUTLINE_FAIL") => "Couldn't close zone - session reset",
        var s when s.StartsWith("CAPTURE_EXIT_FAIL") => "Couldn't set exit - session reset",
        _ => $"{code}"
    };

    private void UpdateStatus(RobotStatus s)
    {
        RobotState    = s.State;
        BladeOn       = s.BladeOn;
        RainDetected  = s.RainDetected;
        UptimeMinutes = s.UptimeMinutes;
        OnPropertyChanged(nameof(CanStartMowing));
        StartMowingCommand.NotifyCanExecuteChanged();

        if (s.BatteryPct < 20 && BatteryPct >= 20)
            WeakReferenceMessenger.Default.Send(new LowBatteryWarningMessage(s.BatteryPct));
        BatteryPct = s.BatteryPct;
    }

    private void UpdateSensor(SensorSnapshot s)
    {
        SpeedMps      = s.LinearSpeed;
        GpsFix        = s.GpsFixType;
        GpsAccuracyMm = s.GpsAccuracyMm;
        HeadingRad    = s.HeadingRad;
        IsManualMode  = s.IsManualMode;
        IsMotorMoving = s.IsMotorMoving;
    }

    private void RefreshLastMowedLabel()
    {
        var at = _lastMow.LastMowAtLocal;
        if (at is null) { LastMowedLabel = "Never"; return; }

        var diff = DateTime.Now - at.Value;
        LastMowedLabel = diff switch
        {
            { TotalMinutes: < 1 }  => "Just now",
            { TotalMinutes: < 60 } => $"{(int)diff.TotalMinutes}m ago",
            { TotalHours: < 24 }   => $"{(int)diff.TotalHours}h ago",
            { TotalDays: < 7 }     => $"{(int)diff.TotalDays}d ago",
            _                      => at.Value.ToString("dd MMM yyyy")
        };
    }

    public override Task OnAppearingAsync()
    {
        RefreshLastMowedLabel();
        OnPropertyChanged(nameof(Greeting));
        return Task.CompletedTask;
    }

    private static void Toast(string text)
    {
        try { _ = CommunityToolkit.Maui.Alerts.Toast.Make(text, ToastDuration.Short).Show(); }
        catch { }
    }

    [RelayCommand(CanExecute = nameof(CanStartMowing))]
    private async Task StartMowingAsync()
    {
        _evt.Info(Source, $"user pressed Mow  (IsConnected={IsConnected} HasDatum={HasDatum} HasPath={HasPath})");
        _lastMow.MarkMowedNow();
        RefreshLastMowedLabel();
        await RunSafeAsync(() => _control.SendActionAsync(RobotAction.StartMowing));
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        _evt.Info(Source, "user pressed Stop");
        await RunSafeAsync(() => _control.SendActionAsync(RobotAction.Stop));
    }
}
