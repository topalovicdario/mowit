using MowIT.Domain.Interfaces;

namespace MowIT.Application.Pipeline.Handlers;

public class DispatchHandler : CommandHandler
{
    private readonly IRobotControl _inner;

    public DispatchHandler(IRobotControl inner) => _inner = inner;

    protected override Task ProcessAsync(RobotCommandContext ctx) =>
        ctx.IsMotorCommand
            ? _inner.SendMotorCommandAsync(ctx.LinearVel, ctx.AngularVel)
            : _inner.SendActionAsync(ctx.Action!.Value, ctx.Param);
}
