using MowIT.Domain.Enums;
using MowIT.Domain.Interfaces;

namespace MowIT.Infrastructure.Ble;

public class RetryDecorator : IRobotControl
{
    private readonly IRobotControl _inner;
    private const int MaxRetries = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    public RetryDecorator(IRobotControl inner) => _inner = inner;

    public Task SendMotorCommandAsync(float linearVel, float angularVel)
        => _inner.SendMotorCommandAsync(linearVel, angularVel);

    public async Task SendActionAsync(RobotAction action, byte param = 0)
    {
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                await _inner.SendActionAsync(action, param);
                return;
            }
            catch when (attempt < MaxRetries - 1)
            {
                await Task.Delay(RetryDelay);
            }
        }
    }
}
