using MowIT.Domain.Entities;
using MowIT.Domain.Enums;

namespace MowIT.Domain.Interfaces;

public interface IRobotConnection
{
    IObservable<RobotConnectionState> ConnectionState { get; }
    RobotConnectionState CurrentState { get; }
    Task<bool> ConnectAsync(MowerDevice device, CancellationToken ct = default);
    Task DisconnectAsync();
    bool IsConnected { get; }
}
