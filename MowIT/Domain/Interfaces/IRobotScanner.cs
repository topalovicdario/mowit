using MowIT.Domain.Entities;

namespace MowIT.Domain.Interfaces;

public interface IRobotScanner
{
    IObservable<MowerDevice> DiscoveredDevices { get; }
    Task StartScanAsync(CancellationToken ct = default);
    Task StopScanAsync();
    bool IsScanning { get; }
}
