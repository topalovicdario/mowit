using MowIT.Domain.Enums;
using MowIT.Domain.Interfaces;

namespace MowIT.Infrastructure.Ble;

public class ConnectionGuardProxy : IRobotControl
{
    private readonly IRobotControl _inner;
    private readonly IRobotConnection _connection;

    public ConnectionGuardProxy(IRobotControl inner, IRobotConnection connection)
    {
        _inner      = inner;
        _connection = connection;
    }

    public Task SendMotorCommandAsync(float linearVel, float angularVel)
    {
        if (!_connection.IsConnected) return Task.CompletedTask;
        return _inner.SendMotorCommandAsync(linearVel, angularVel);
    }

    public Task SendActionAsync(RobotAction action, byte param = 0)
    {
        if (!_connection.IsConnected) return Task.CompletedTask;
        return _inner.SendActionAsync(action, param);
    }
}
