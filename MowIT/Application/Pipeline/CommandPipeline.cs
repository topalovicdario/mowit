using Microsoft.Extensions.Logging;
using MowIT.Application.Pipeline.Handlers;
using MowIT.Application.StateMachine;
using MowIT.Domain.Enums;
using MowIT.Domain.Interfaces;

namespace MowIT.Application.Pipeline;

public class CommandPipeline : IRobotControl
{
    private readonly CommandHandler _head;
    private readonly ILogger<CommandPipeline> _logger;

    public CommandPipeline(
        IRobotControl            inner,
        IRobotSensors            sensors,
        RobotStateMachine        stateMachine,
        ILogger<CommandPipeline> logger)
    {
        _logger = logger;

        var logging  = new LoggingCommandHandler(logger);
        var dispatch = new DispatchHandler(inner);

        logging.SetNext(dispatch);
        _head = logging;
    }

    public async Task SendActionAsync(RobotAction action, byte param = 0)
    {
        var ctx = new RobotCommandContext { Action = action, Param = param };
        await _head.HandleAsync(ctx);
        if (ctx.IsAborted)
            _logger.LogWarning("Action {Action} aborted: {Reason}", action, ctx.AbortReason);
    }

    public async Task SendMotorCommandAsync(float linearVel, float angularVel)
    {
        var ctx = new RobotCommandContext { LinearVel = linearVel, AngularVel = angularVel };
        await _head.HandleAsync(ctx);
        if (ctx.IsAborted)
            _logger.LogDebug("Motor cmd aborted: {Reason}", ctx.AbortReason);
    }
}
