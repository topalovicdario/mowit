using MowIT.Domain.Interfaces;

namespace MowIT.Application.Pipeline.Handlers;

public class BatteryCheckHandler : CommandHandler
{
    private readonly IRobotSensors _sensors;
    private const int CriticalThreshold = 5;

    public BatteryCheckHandler(IRobotSensors sensors) => _sensors = sensors;

    protected override Task ProcessAsync(RobotCommandContext ctx)
    {
        if (ctx.IsMotorCommand) return Task.CompletedTask;

       
        var status = _sensors.LastStatus;
        if (status is not null && status.BatteryPct > 0 && status.BatteryPct < CriticalThreshold)
            ctx.Abort($"Battery critically low ({status.BatteryPct}%) - command blocked");

        return Task.CompletedTask;
    }
}
