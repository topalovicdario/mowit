namespace MowIT.Domain.Interfaces;

public interface IBlePermissionService
{
    Task<bool> RequestPermissionsAsync();
    Task<bool> IsBluetoothEnabledAsync();
}
