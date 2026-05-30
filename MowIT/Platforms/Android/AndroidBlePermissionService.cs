using Android.Bluetooth;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using MowIT.Domain.Interfaces;

namespace MowIT.Platforms.Android;

public class AndroidBlePermissionService : IBlePermissionService
{
    private static TaskCompletionSource<bool>? _permissionTcs;
    private const int RequestCode = 1001;

    public async Task<bool> RequestPermissionsAsync()
    {
        var activity = Platform.CurrentActivity;
        if (activity is null) return false;

        string[] permissions;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
        {
            permissions = new[]
            {
                global::Android.Manifest.Permission.BluetoothScan,
                global::Android.Manifest.Permission.BluetoothConnect,
                global::Android.Manifest.Permission.AccessFineLocation
            };
        }
        else
        {
            permissions = new[] { global::Android.Manifest.Permission.AccessFineLocation };
        }

        bool allGranted = permissions.All(p =>
            ContextCompat.CheckSelfPermission(activity, p) == Permission.Granted);

        if (allGranted) return true;

        _permissionTcs = new TaskCompletionSource<bool>();
        ActivityCompat.RequestPermissions(activity, permissions, RequestCode);
        return await _permissionTcs.Task;
    }

    public Task<bool> IsBluetoothEnabledAsync()
    {
        var manager = (BluetoothManager?)Platform.AppContext
            .GetSystemService(Context.BluetoothService);
        return Task.FromResult(manager?.Adapter?.IsEnabled == true);
    }

public static void OnPermissionsResult(int requestCode, Permission[] grantResults)
    {
        if (requestCode == RequestCode && _permissionTcs is not null)
        {
            bool granted = grantResults.Length > 0 &&
                           grantResults.All(r => r == Permission.Granted);
            _permissionTcs.TrySetResult(granted);
            _permissionTcs = null;
        }
    }
}
