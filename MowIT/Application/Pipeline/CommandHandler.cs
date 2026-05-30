namespace MowIT.Application.Pipeline;

public abstract class CommandHandler
{
    private CommandHandler? _next;

    public CommandHandler SetNext(CommandHandler next)
    {
        _next = next;
        return next;
    }

    public async Task HandleAsync(RobotCommandContext ctx)
    {
        if (ctx.IsAborted) return;
        await ProcessAsync(ctx);
        if (!ctx.IsAborted && _next is not null)
            await _next.HandleAsync(ctx);
    }

    protected abstract Task ProcessAsync(RobotCommandContext ctx);
}
