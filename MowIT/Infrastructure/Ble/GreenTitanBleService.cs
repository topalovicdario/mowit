using System.Reactive.Subjects;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using MowIT.Application.Messages;
using MowIT.Domain.Entities;
using MowIT.Domain.Enums;
using MowIT.Domain.Interfaces;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;

namespace MowIT.Infrastructure.Ble;

public sealed class GreenTitanBleService
    : IRobotScanner, IRobotConnection, IRobotSensors, IRobotControl, IRobotBoundary, IDisposable
{
    private readonly IBluetoothLE _ble;
    private readonly IAdapter     _adapter;
    private readonly ILogger<GreenTitanBleService> _logger;

    private IDevice?   _device;
    private IService?  _service;

private ICharacteristic? _gpsChar, _imuChar, _odometryChar, _statusChar;
    
    private ICharacteristic? _motorChar, _actionChar, _boundaryChar, _scheduleChar;

private readonly Subject<MowerDevice>        _deviceSubject = new();
    private readonly Subject<RobotConnectionState> _stateSubject  = new();
    private readonly Subject<SensorSnapshot>     _sensorSubject = new();
    private readonly Subject<RobotStatus>        _statusSubject = new();

private SensorSnapshot _latestSensor = new() { Timestamp = DateTime.UtcNow };
    private RobotStatus?   _latestStatus;

    public GreenTitanBleService(IBluetoothLE ble, IAdapter adapter, ILogger<GreenTitanBleService> logger)
    {
        _ble     = ble;
        _adapter = adapter;
        _logger  = logger;
        _adapter.ScanTimeout = 30000;

        _adapter.DeviceDisconnected += (_, args) =>
        {
            CurrentState = RobotConnectionState.Disconnected;
            _stateSubject.OnNext(RobotConnectionState.Disconnected);
            _logger.LogWarning("BLE device disconnected unexpectedly");
            WeakReferenceMessenger.Default.Send(
                new RobotDisconnectedMessage(args.Device?.Name ?? "unknown device"));
        };
    }

public IObservable<MowerDevice> DiscoveredDevices => _deviceSubject;
    public bool IsScanning => _adapter.IsScanning;

    public async Task StartScanAsync(CancellationToken ct = default)
    {
        if (_ble.State != BluetoothState.On)
        {
            _logger.LogWarning("Bluetooth is off or unavailable (state={State})", _ble.State);
            return;
        }
        _adapter.DeviceDiscovered += OnDeviceDiscovered;
        _logger.LogInformation("BLE scan started");
        await _adapter.StartScanningForDevicesAsync(
           cancellationToken: ct);
        _adapter.DeviceDiscovered -= OnDeviceDiscovered;
    }

    public async Task StopScanAsync()
    {
        await _adapter.StopScanningForDevicesAsync();
        _logger.LogInformation("BLE scan stopped");
    }

    private void OnDeviceDiscovered(object? sender, Plugin.BLE.Abstractions.EventArgs.DeviceEventArgs args)
    {
        
            var device = new MowerDevice { Id = args.Device.Id, Name = args.Device.Name, Rssi = args.Device.Rssi };
            _deviceSubject.OnNext(device);
            _logger.LogDebug("Discovered: {Name} RSSI={Rssi}", args.Device.Name, args.Device.Rssi);
        
    }

public IObservable<RobotConnectionState> ConnectionState => _stateSubject;
    public RobotConnectionState CurrentState { get; private set; } = RobotConnectionState.Disconnected;
    public bool IsConnected => CurrentState == RobotConnectionState.Connected;

    public async Task<bool> ConnectAsync(MowerDevice mowerDevice, CancellationToken ct = default)
    {
        _stateSubject.OnNext(RobotConnectionState.Connecting);
        CurrentState = RobotConnectionState.Connecting;
        try
        {
            _device  = await _adapter.ConnectToKnownDeviceAsync(mowerDevice.Id, cancellationToken: ct);
            await _device.RequestMtuAsync(512);
            _service = await _device.GetServiceAsync(BleGattProfile.ServiceUuid, ct);

            if (_service is null) throw new Exception("GreenTitan GATT service not found on device");

            await DiscoverCharacteristicsAsync(ct);
            await StartNotificationsAsync();

            CurrentState = RobotConnectionState.Connected;
            _stateSubject.OnNext(RobotConnectionState.Connected);
            _logger.LogInformation("Connected to {Name}", mowerDevice.Name);
            WeakReferenceMessenger.Default.Send(new RobotConnectedMessage(mowerDevice.Name));
            return true;
        }
        catch (Exception ex)
        {
            CurrentState = RobotConnectionState.Disconnected;
            _stateSubject.OnNext(RobotConnectionState.Disconnected);
            _logger.LogError(ex, "Connection to {Name} failed", mowerDevice.Name);
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_device is not null)
            await _adapter.DisconnectDeviceAsync(_device);
        CurrentState = RobotConnectionState.Disconnected;
        _stateSubject.OnNext(RobotConnectionState.Disconnected);
    }

    private async Task DiscoverCharacteristicsAsync(CancellationToken ct)
    {
        _gpsChar      = await _service!.GetCharacteristicAsync(BleGattProfile.GpsDataUuid);
        _imuChar      = await _service!.GetCharacteristicAsync(BleGattProfile.ImuDataUuid);
        _odometryChar = await _service!.GetCharacteristicAsync(BleGattProfile.OdometryUuid);
        _statusChar   = await _service!.GetCharacteristicAsync(BleGattProfile.RobotStatusUuid);
        _motorChar    = await _service!.GetCharacteristicAsync(BleGattProfile.MotorCommandUuid);
        if (_motorChar is not null)
            _motorChar.WriteType = Plugin.BLE.Abstractions.CharacteristicWriteType.WithoutResponse;
        _actionChar   = await _service!.GetCharacteristicAsync(BleGattProfile.ActionCommandUuid);
        _boundaryChar = await _service!.GetCharacteristicAsync(BleGattProfile.BoundaryChunkUuid);
        _scheduleChar = await _service!.GetCharacteristicAsync(BleGattProfile.ScheduleDataUuid);
    }

    private async Task StartNotificationsAsync()
    {
        if (_gpsChar is not null)
        {
            _gpsChar.ValueUpdated += (_, e) => OnGpsUpdated(e.Characteristic.Value);
            await _gpsChar.StartUpdatesAsync();
        }
        if (_imuChar is not null)
        {
            _imuChar.ValueUpdated += (_, e) => OnImuUpdated(e.Characteristic.Value);
            await _imuChar.StartUpdatesAsync();
        }
        if (_odometryChar is not null)
        {
            _odometryChar.ValueUpdated += (_, e) => OnOdometryUpdated(e.Characteristic.Value);
            await _odometryChar.StartUpdatesAsync();
        }
        if (_statusChar is not null)
        {
            _statusChar.ValueUpdated += (_, e) => OnStatusUpdated(e.Characteristic.Value);
            await _statusChar.StartUpdatesAsync();
        }
    }

public IObservable<SensorSnapshot> SensorStream => _sensorSubject;
    public IObservable<RobotStatus>    StatusStream  => _statusSubject;
    public SensorSnapshot? LastSensor => _latestSensor;
    public RobotStatus?    LastStatus  => _latestStatus;

    private void OnGpsUpdated(byte[] data)
    {
        if (data.Length < 21) return;
        var snap = BlePacketSerializer.DeserializeGps(data);
        _latestSensor = snap with
        {
            AccX = _latestSensor.AccX, AccY = _latestSensor.AccY, AccZ = _latestSensor.AccZ,
            GyroX = _latestSensor.GyroX, GyroY = _latestSensor.GyroY, GyroZ = _latestSensor.GyroZ,
            PosX = _latestSensor.PosX, PosY = _latestSensor.PosY,
            HeadingRad = _latestSensor.HeadingRad, LinearSpeed = _latestSensor.LinearSpeed
        };
        _sensorSubject.OnNext(_latestSensor);
    }

    private void OnImuUpdated(byte[] data)
    {
        if (data.Length < 24) return;
        _latestSensor = BlePacketSerializer.MergeImu(_latestSensor, data);
        _sensorSubject.OnNext(_latestSensor);
    }

    private void OnOdometryUpdated(byte[] data)
    {
        if (data.Length < 16) return;
        _latestSensor = BlePacketSerializer.MergeOdometry(_latestSensor, data);
        _sensorSubject.OnNext(_latestSensor);
    }

    private void OnStatusUpdated(byte[] data)
    {
        if (data.Length < 6) return;
        _latestStatus = BlePacketSerializer.DeserializeStatus(data);
        _statusSubject.OnNext(_latestStatus);
    }

public async Task SendMotorCommandAsync(float linearVel, float angularVel)
    {
        if (_motorChar is null) return;
        try
        {
            var data = BlePacketSerializer.SerializeMotorCommand(linearVel, angularVel);
            await _motorChar.WriteAsync(data);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Motor command write failed (ignored)");
        }
    }

    public async Task SendActionAsync(RobotAction action, byte param = 0)
    {
        if (_actionChar is null) return;
        var data = BlePacketSerializer.SerializeActionCommand(action, param);
        await _actionChar.WriteAsync(data);
        _logger.LogInformation("Action sent: {Action}", action);
    }

public async Task SendBoundaryAsync(BoundaryZone zone, IProgress<int>? progress = null)
    {
        if (_boundaryChar is null) throw new InvalidOperationException("BLE not connected");

        var points = zone.Points;
        _logger.LogInformation("Sending boundary: {Count} points", points.Count);

        for (int i = 0; i < points.Count; i++)
        {
            var chunk = BlePacketSerializer.SerializeBoundaryChunk(
                (byte)i, (byte)points.Count, 0, points[i]);
            await _boundaryChar.WriteAsync(chunk);
            await Task.Delay(50); 
            progress?.Report((i + 1) * 100 / points.Count);
        }
    }

    public async Task SendRouteAsync(List<GpsPoint> route, IProgress<int>? progress = null)
    {
        if (_boundaryChar is null) throw new InvalidOperationException("BLE not connected");

        for (int i = 0; i < route.Count; i++)
        {
            var chunk = BlePacketSerializer.SerializeBoundaryChunk(
                (byte)i, (byte)route.Count, 1, route[i]);
            await _boundaryChar.WriteAsync(chunk);
            await Task.Delay(50);
            progress?.Report((i + 1) * 100 / route.Count);
        }
    }

    public async Task ClearBoundaryAsync()
    {
        if (_actionChar is null) return;
        await SendActionAsync(RobotAction.BoundaryClear);
    }

    public void Dispose()
    {
        _deviceSubject.OnCompleted();
        _stateSubject.OnCompleted();
        _sensorSubject.OnCompleted();
        _statusSubject.OnCompleted();
        _deviceSubject.Dispose();
        _stateSubject.Dispose();
        _sensorSubject.Dispose();
        _statusSubject.Dispose();
    }
}
