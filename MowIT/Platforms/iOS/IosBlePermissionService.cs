using MowIT.Domain.Interfaces;

namespace MowIT.Platforms.iOS;

public class IosBlePermissionService : IBlePermissionService
{
    public Task<bool> RequestPermissionsAsync() => Task.FromResult(true);
    public Task<bool> IsBluetoothEnabledAsync() => Task.FromResult(true);
}
