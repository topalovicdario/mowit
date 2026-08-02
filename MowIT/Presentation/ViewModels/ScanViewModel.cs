using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MowIT.Application.Messages;
using MowIT.Domain.Entities;
using MowIT.Domain.Enums;
using MowIT.Domain.Interfaces;
using MowIT.Infrastructure.Transport;
using MowIT.Presentation.ViewModels.Base;
using MowIT.Application.Logging;
namespace MowIT.Presentation.ViewModels;

public partial class ScanViewModel : BaseViewModel
{
    private readonly IRobotScanner _scanner;
    private readonly IRobotConnection _connection;
    private readonly IBlePermissionService _permissions;
    private readonly IRobotTransportSwitch _transport;
    private readonly EventLogService _evt;

    private const string Source = "SCAN";

    public EventLogService EventLog => _evt;
    private IDisposable? _deviceSub, _stateSub;
    private CancellationTokenSource? _scanCts;

    public ObservableCollection<MowerDevice> DiscoveredDevices { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanButtonText))]
    private bool _isScanning;

    [ObservableProperty]
    private RobotConnectionState _connectionState = RobotConnectionState.Disconnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBluetooth))]
    [NotifyPropertyChangedFor(nameof(IsWifi))]
    [NotifyPropertyChangedFor(nameof(ModeSubtitle))]
    [NotifyPropertyChangedFor(nameof(ScanButtonText))]
    private TransportKind _selectedTransport;

    public bool IsBluetooth => SelectedTransport == TransportKind.Bluetooth;
    public bool IsWifi      => SelectedTransport == TransportKind.Wifi;

    public string ModeSubtitle => IsWifi
        ? "Reach a robot over WiFi through the cloud"
        : "Power on GreenTitan and tap Scan";

    public string ScanButtonText => IsScanning
        ? "Stop Scan"
        : IsWifi ? "Find Robot Online" : "Scan for Mowers";

    public ScanViewModel(
        IRobotScanner scanner,
        IRobotConnection connection,
        IBlePermissionService permissions,
        IRobotTransportSwitch transport,
        EventLogService evt)
    {
        _scanner     = scanner;
        _connection  = connection;
        _permissions = permissions;
        _transport   = transport;
        _evt = evt;
        Title = "Find Your Mower";
        _selectedTransport = transport.CurrentKind;

        _deviceSub = _scanner.DiscoveredDevices
    .Subscribe(d => MainThread.BeginInvokeOnMainThread(() =>
    {
        if (!DiscoveredDevices.Any(x => x.Id == d.Id))
        {
            DiscoveredDevices.Add(d);
            ErrorMessage = string.Empty;
            _evt.Info(Source, $"found device {d.Name}");
        }
    }));

        _stateSub = _connection.ConnectionState
     .Subscribe(s => MainThread.BeginInvokeOnMainThread(() =>
     {
         ConnectionState = s;
         _evt.State(Source, $"connection to {s}");
     }));

        WeakReferenceMessenger.Default.Register<RobotErrorMessage>(this, (_, m) =>
        {
            if (m.Code.StartsWith("WIFI/", StringComparison.Ordinal))
                MainThread.BeginInvokeOnMainThread(() =>
                    ErrorMessage = m.Code["WIFI/".Length..]);
        });
    }

    [RelayCommand]
    private async Task ToggleScanAsync()
    {
        if (IsScanning)
        {
            _evt.Info(Source, "scan stopped by user");
            _scanCts?.Cancel();
            await _scanner.StopScanAsync();
            IsScanning = false;
            return;
        }

        if (IsBluetooth)
        {
            if (!await _permissions.IsBluetoothEnabledAsync())
            {
                ErrorMessage = "Please enable Bluetooth to scan for mowers";
                _evt.Warn(Source, "Bluetooth is disabled");
                return;
            }

            if (!await _permissions.RequestPermissionsAsync())
            {
                ErrorMessage = "Bluetooth permissions are required to scan for mowers";
                _evt.Warn(Source, "Bluetooth permissions denied");
                return;
            }
        }

        DiscoveredDevices.Clear();
        ErrorMessage = string.Empty;
        _evt.Info(Source, "scan started");

        _scanCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        IsScanning = true;

        try
        {
            await _scanner.StartScanAsync(_scanCts.Token);
        }
        catch (OperationCanceledException)
        {
            _evt.Info(Source, "scan cancelled");
        }

        IsScanning = false;
        _evt.Info(Source, "scan finished");
    }

    [RelayCommand]
    private async Task SelectTransportAsync(string kindName)
    {
        if (!Enum.TryParse<TransportKind>(kindName, out var kind)) return;
        if (kind == SelectedTransport) return;

        if (IsScanning)
        {
            _scanCts?.Cancel();
            await _scanner.StopScanAsync();
            IsScanning = false;
        }

        await _transport.SelectAsync(kind);
        SelectedTransport = kind;

        DiscoveredDevices.Clear();
        ConnectionState = RobotConnectionState.Disconnected;
        ErrorMessage = string.Empty;
        _evt.State(Source, $"transport mode to {kind}");
    }

    [RelayCommand]

    private async Task ConnectAsync(MowerDevice device)
    {
        await RunSafeAsync(async () =>
        {
            _evt.Info(Source, $"connecting to {device.Name}");

            var success = await _connection.ConnectAsync(device);

            if (success)
            {
                _evt.State(Source, $"connected to {device.Name}");
                await Shell.Current.GoToAsync("//dashboard");
            }
            else
            {
                _evt.Warn(Source, $"connection failed: {device.Name}");
            }

        }, "Connection failed");
    }

    public override void OnDisappearing()
    {
        _scanCts?.Cancel();
        _deviceSub?.Dispose();
        _stateSub?.Dispose();
        WeakReferenceMessenger.Default.Unregister<RobotErrorMessage>(this);
    }
}
