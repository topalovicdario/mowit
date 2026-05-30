using MowIT.Domain.Enums;

namespace MowIT.Domain.Interfaces;

public interface IRobotControl
{
    Task SendMotorCommandAsync(float linearVel, float angularVel);
    Task SendActionAsync(RobotAction action, byte param = 0);
}
