using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MowIT.Domain.Entities;
using MowIT.Domain.Enums;
using MowIT.Domain.Interfaces;
using MowIT.Presentation.ViewModels.Base;
using MowIT.Application.Logging;
namespace MowIT.Presentation.ViewModels;

public partial class ScanViewModel : BaseViewModel
{
    private readonly IRobotScanner _scanner;
    private readonly IRobotConnection _connection;
    private readonly IBlePermissionService _permissions;
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

    public string ScanButtonText => IsScanning ? "Stop Scan" : "Scan for Mowers";

    public ScanViewModel(
        IRobotScanner scanner,
        IRobotConnection connection,
        IBlePermissionService permissions,
        EventLogService evt)
    {
        _scanner     = scanner;
        _connection  = connection;
        _permissions = permissions;
        _evt = evt;
        Title = "Find Your Mower";

        _deviceSub = _scanner.DiscoveredDevices
    .Subscribe(d => MainThread.BeginInvokeOnMainThread(() =>
    {
        if (!DiscoveredDevices.Any(x => x.Id == d.Id))
        {
            DiscoveredDevices.Add(d);
            _evt.Info(Source, $"found device {d.Name}");
        }
    }));

        _stateSub = _connection.ConnectionState
     .Subscribe(s => MainThread.BeginInvokeOnMainThread(() =>
     {
         ConnectionState = s;
         _evt.State(Source, $"connection → {s}");
     }));
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

        DiscoveredDevices.Clear();
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
    }
}
