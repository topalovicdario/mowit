using Microsoft.Extensions.Logging;
using MowIT.Application.Logging;
using MowIT.Application.Pipeline;
using MowIT.Application.StateMachine;
using MowIT.Domain.Interfaces;
using MowIT.Infrastructure.Ble;
using MowIT.Infrastructure.ClassicBt;

namespace MowIT.Infrastructure.Factories;

public class GreenTitanSppFactory : IRobotServiceFactory
{
    public string RobotName => "GreenTitan (Classic BT)";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<GreenTitanSppService>(sp =>
            new GreenTitanSppService(
                sp.GetRequiredService<ILogger<GreenTitanSppService>>(),
                sp.GetRequiredService<EventLogService>()));

        services.AddSingleton<IRobotScanner>   (sp => sp.GetRequiredService<GreenTitanSppService>());
        services.AddSingleton<IRobotConnection>(sp => sp.GetRequiredService<GreenTitanSppService>());
        services.AddSingleton<IRobotSensors> (sp => sp.GetRequiredService<GreenTitanSppService>());
        services.AddSingleton<IRobotBoundary>(sp => sp.GetRequiredService<GreenTitanSppService>());

services.AddSingleton<RobotStateMachine>();
        services.AddSingleton<IRobotControl>(sp =>
        {
            var spp          = sp.GetRequiredService<GreenTitanSppService>();
            var connection   = sp.GetRequiredService<IRobotConnection>();
            var sensors      = sp.GetRequiredService<IRobotSensors>();
            var stateMachine = sp.GetRequiredService<RobotStateMachine>();
            var logger       = sp.GetRequiredService<ILogger<CommandPipeline>>();

            stateMachine.Start(sensors);

            IRobotControl inner = new ConnectionGuardProxy(new RetryDecorator(spp), connection);
            return new CommandPipeline(inner, sensors, stateMachine, logger);
        });
    }
}
