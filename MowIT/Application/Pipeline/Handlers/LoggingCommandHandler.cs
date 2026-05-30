using Microsoft.Extensions.Logging;

namespace MowIT.Application.Pipeline.Handlers;

public class LoggingCommandHandler : CommandHandler
{
    private readonly ILogger _logger;

    public LoggingCommandHandler(ILogger logger) => _logger = logger;

    protected override Task ProcessAsync(RobotCommandContext ctx)
    {
        if (ctx.IsMotorCommand)
            _logger.LogDebug("Motor cmd  lin={Lin:F2} ang={Ang:F2}", ctx.LinearVel, ctx.AngularVel);
        else
            _logger.LogInformation("Action cmd  {Action} param={Param}", ctx.Action, ctx.Param);
        return Task.CompletedTask;
    }
}
