using MowIT.Domain.Interfaces;

namespace MowIT.Infrastructure;

public class NullBlePermissionService : IBlePermissionService
{
    public Task<bool> RequestPermissionsAsync() => Task.FromResult(true);
    public Task<bool> IsBluetoothEnabledAsync() => Task.FromResult(true);
}
