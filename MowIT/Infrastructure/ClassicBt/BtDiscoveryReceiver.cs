#if ANDROID
using Android.Bluetooth;
using Android.Content;

namespace MowIT.Infrastructure.ClassicBt;

internal sealed class BtDiscoveryReceiver : BroadcastReceiver
{
    private readonly Action<BluetoothDevice> _onFound;

    public BtDiscoveryReceiver(Action<BluetoothDevice> onFound)
    {
        _onFound = onFound;
    }

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent?.Action != BluetoothDevice.ActionFound) return;

        var device = (BluetoothDevice?)intent.GetParcelableExtra(BluetoothDevice.ExtraDevice);
        if (device != null)
            _onFound(device);
    }
}
#endif
