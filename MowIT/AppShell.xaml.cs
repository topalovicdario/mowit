using CommunityToolkit.Mvvm.Messaging;
using MowIT.Application.Messages;
using MowIT.Domain.Enums;
using MowIT.Domain.Interfaces;

namespace MowIT;

public partial class AppShell : Shell
{
    private bool _wasConnected;
    private bool _userInitiatedDisconnect;
    private IDisposable? _connectionSub;

    public AppShell()
    {
        InitializeComponent();
        WireDisconnectNavigation();

        // The dashboard raises this immediately before it drops the link on purpose, so the
        // navigation below still runs but the "connection was lost" alert is skipped.
        WeakReferenceMessenger.Default.Register<UserDisconnectRequestedMessage>(
            this, (_, _) => _userInitiatedDisconnect = true);
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

                    bool intentional = _userInitiatedDisconnect;
                    _userInitiatedDisconnect = false;

                    await Current.GoToAsync("//scan");

                    if (!intentional)
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
