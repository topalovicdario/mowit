using MowIT.Domain.Interfaces;

namespace MowIT.Infrastructure.Transport;

public interface IRobotTransport
    : IRobotScanner, IRobotConnection, IRobotSensors, IRobotControl, IRobotBoundary
{
}
