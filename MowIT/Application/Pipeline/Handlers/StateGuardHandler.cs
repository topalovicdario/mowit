using MowIT.Application.StateMachine;

namespace MowIT.Application.Pipeline.Handlers;

public class StateGuardHandler : CommandHandler
{
    private readonly RobotStateMachine _sm;

    public StateGuardHandler(RobotStateMachine sm) => _sm = sm;

    protected override Task ProcessAsync(RobotCommandContext ctx)
    {
        if (ctx.IsMotorCommand) return Task.CompletedTask;

        if (!_sm.CanExecute(ctx.Action!.Value))
            ctx.Abort($"Action {ctx.Action} not valid in state {_sm.CurrentState}");

        return Task.CompletedTask;
    }
}
