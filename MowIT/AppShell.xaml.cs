using MowIT.Domain.Enums;
using MowIT.Domain.Interfaces;

namespace MowIT;

public partial class AppShell : Shell
{
    private bool _wasConnected;
    private IDisposable? _connectionSub;

    public AppShell()
    {
        InitializeComponent();
        WireDisconnectNavigation();
    }

    private void WireDisconnectNavigation()
    {
        var connection = IPlatformApplication.Current?.Services
            .GetService<IRobotConnection>();

        if (connection is null) return;

        _connectionSub = connection.ConnectionState
            .Subscribe(state => MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (state == RobotConnectionState.Connected)
                {
                    _wasConnected = true;
                }
                else if (state == RobotConnectionState.Disconnected && _wasConnected)
                {
                    _wasConnected = false;
                    await Current.GoToAsync("//scan");
                    await Current.DisplayAlert(
                        "Disconnected",
                        "Connection to the mower was lost. Please reconnect.",
                        "OK");
                }
            }));
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (Handler is null)
        {
            _connectionSub?.Dispose();
            _connectionSub = null;
        }
    }
}
