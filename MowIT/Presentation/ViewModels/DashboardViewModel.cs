using System.Collections.ObjectModel;
using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using MowIT.Application.Logging;
using MowIT.Application.Messages;
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
    private const string Source = "DASH";

    public EventLogService EventLog => _evt;
    private IDisposable? _sensorSub, _statusSub, _connSub;

    private const int AccHistoryCapacity = 60;
    public ObservableCollection<float> AccXHistory { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGpsFix))]
    [NotifyPropertyChangedFor(nameof(GpsPositionText))]
    [NotifyPropertyChangedFor(nameof(GpsSecondaryText))]
    private double _latitude;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGpsFix))]
    [NotifyPropertyChangedFor(nameof(GpsPositionText))]
    [NotifyPropertyChangedFor(nameof(GpsSecondaryText))]
    private double _longitude;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpsAccuracyLabel))]
    private float _gpsAccuracyMm;

    [ObservableProperty] private GpsFixType _gpsFixType;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImu))]
    private float _accX;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImu))]
    private float _accY;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImu))]
    private float _accZ;
    [ObservableProperty] private float _gyroX;
    [ObservableProperty] private float _gyroY;
    [ObservableProperty] private float _gyroZ;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateColor))]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    private RobotState _robotState;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBattery))]
    private int _batteryPct;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlade))]
    private bool _bladeOn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRain))]
    private bool _rainDetected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSpeed))]
    private float _currentSpeed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUptime))]
    [NotifyPropertyChangedFor(nameof(UptimeLabel))]
    private int _uptimeMinutes;

    // Connection + boundary readiness — drive button gating + readable status text.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartMowing))]
    [NotifyPropertyChangedFor(nameof(ReadinessText))]
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

    [ObservableProperty] private string _lastEvent = "";

    public bool HasGpsFix  => Latitude != 0 || Longitude != 0 || GpsFixType != GpsFixType.NoFix;
    public bool HasBattery => BatteryPct > 0;
    public bool HasImu     => AccX != 0 || AccY != 0 || AccZ != 0;
    public bool HasSpeed   => CurrentSpeed > 0.01f;
    public bool HasBlade   => BladeOn;
    public bool HasRain    => RainDetected;
    public bool HasUptime  => UptimeMinutes > 0;

    public bool CanStartMowing => IsConnected && HasDatum && HasPath && RobotState != RobotState.Mowing;

    public string ReadinessText => (IsConnected, HasDatum, HasPath) switch
    {
        (false, _, _)  => "Connect to mower first",
        (_, false, _)  => "Set base on the Control page",
        (_, _, false)  => "Record + save a boundary",
        _              => "Ready to mow"
    };

    public string GpsPositionText  => HasGpsFix ? $"{Latitude:F6}°" : "No fix";
    public string GpsSecondaryText => HasGpsFix ? $"{Longitude:F6}°" : "Waiting for GPS signal…";

    public string GpsAccuracyLabel => GpsAccuracyMm <= 0
        ? "–"
        : GpsAccuracyMm < 50
            ? $"RTK ±{GpsAccuracyMm:F0}mm"
            : $"±{GpsAccuracyMm / 1000:F2}m";

    public string UptimeLabel => $"{UptimeMinutes / 60}h {UptimeMinutes % 60}m";

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
        EventLogService evt)
    {
        _sensors    = sensors;
        _control    = control;
        _connection = connection;
        _logger     = logger;
        _evt        = evt;
        Title       = "Dashboard";

        IsConnected = connection.IsConnected;

        _sensorSub = _sensors.SensorStream
            .Subscribe(s => MainThread.BeginInvokeOnMainThread(() => UpdateSensorData(s)));

        _statusSub = _sensors.StatusStream
            .Subscribe(s => MainThread.BeginInvokeOnMainThread(() => UpdateStatus(s)));

        _connSub = _connection.ConnectionState
            .Subscribe(s => MainThread.BeginInvokeOnMainThread(() =>
            {
                IsConnected = s == RobotConnectionState.Connected;
                _evt.State(Source, $"connection → {s}  (IsConnected={IsConnected})");
            }));

        // Track datum/path readiness from the same messages the Control page reacts to.
        WeakReferenceMessenger.Default.Register<BaseCapturedMessage>(this, (_, _) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                HasDatum = true;
                HasPath  = false;
                _evt.State(Source, "HasDatum=true, HasPath=false (re-base clears path)");
                Toast("📍 Base captured");
            }));

        WeakReferenceMessenger.Default.Register<CaptureEndMessage>(this, (_, m) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (m.Success)
                {
                    HasPath = true;
                    _evt.State(Source, "HasPath=true (Mow now allowed)");
                    Toast("✓ Boundary saved — ready to mow");
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
                Toast($"🔗 Connected to {m.DeviceName}");
            }));

        WeakReferenceMessenger.Default.Register<RobotDisconnectedMessage>(this, (_, m) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _evt.Warn(Source, $"disconnected ({m.Reason})");
                IsConnected = false;
                Toast("⚠ Disconnected from mower");
            }));

        WeakReferenceMessenger.Default.Register<RobotErrorMessage>(this, (_, m) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _evt.Error(Source, m.Code);
                Toast(FriendlyError(m.Code));
            }));
    }

    // Translate firmware-style codes into something a user can act on.
    private static string FriendlyError(string code) => code switch
    {
        "MOWER_START_FAIL/NO_DATUM" => "✗ Can't start — set base first",
        "MOWER_START_FAIL/NO_PATH"  => "✗ Can't start — record + save a boundary first",
        "MOWER_START_FAIL"          => "✗ Mower refused to start",
        "BASE_CAPTURE_FAIL/ACCURACY" => "✗ GPS not accurate enough yet — wait for RTK",
        var s when s.StartsWith("CAPTURE_POINT_FAIL") => "✗ Couldn't mark point — session reset",
        var s when s.StartsWith("CAPTURE_OUTLINE_FAIL") => "✗ Couldn't close zone — session reset",
        var s when s.StartsWith("CAPTURE_EXIT_FAIL") => "✗ Couldn't set exit — session reset",
        _ => $"⚠ {code}"
    };

    private void UpdateSensorData(SensorSnapshot s)
    {
        Latitude      = s.Gps.Latitude;
        Longitude     = s.Gps.Longitude;
        GpsAccuracyMm = s.GpsAccuracyMm;
        GpsFixType    = s.GpsFixType;
        AccX = s.AccX; AccY = s.AccY; AccZ = s.AccZ;
        GyroX = s.GyroX; GyroY = s.GyroY; GyroZ = s.GyroZ;
        CurrentSpeed  = s.LinearSpeed;

        if (HasImu)
        {
            AccXHistory.Add(s.AccX);
            if (AccXHistory.Count > AccHistoryCapacity)
                AccXHistory.RemoveAt(0);
        }
    }

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

    private static void Toast(string text)
    {
        try { _ = CommunityToolkit.Maui.Alerts.Toast.Make(text, ToastDuration.Short).Show(); }
        catch { /* toast not available on every platform */ }
    }

    [RelayCommand(CanExecute = nameof(CanStartMowing))]
    private async Task StartMowingAsync()
    {
        _evt.Info(Source, $"user pressed Mow  (IsConnected={IsConnected} HasDatum={HasDatum} HasPath={HasPath})");
        await RunSafeAsync(() => _control.SendActionAsync(RobotAction.StartMowing));
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        _evt.Info(Source, "user pressed Stop");
        await RunSafeAsync(() => _control.SendActionAsync(RobotAction.Stop));
    }

    public override void OnDisappearing()
    {
        // Don't dispose subs / unregister handlers here — MAUI Shell tabs cache pages,
        // so this VM is reused when the user comes back. Tearing things down here means
        // CaptureEndMessage / BaseCapturedMessage emitted while we're away never reach us,
        // and HasPath / HasDatum stay false → Mow stays disabled forever.
        // Subscriptions live for the app's lifetime, which is fine since the Dashboard tab
        // is always present.
    }
}
