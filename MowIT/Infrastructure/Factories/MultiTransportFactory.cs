using Microsoft.Extensions.Logging;
using MowIT.Application.Logging;
using MowIT.Application.Pipeline;
using MowIT.Application.StateMachine;
using MowIT.Domain.Interfaces;
using MowIT.Infrastructure.Ble;
using MowIT.Infrastructure.Simulator;
using MowIT.Infrastructure.Transport;
using MowIT.Infrastructure.Wifi;

namespace MowIT.Infrastructure.Factories;

public class MultiTransportFactory : IRobotServiceFactory
{
    public string RobotName => "Multi-Transport (Bluetooth + WiFi)";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<SimulatedRobotService>();
        services.AddSingleton<WifiRobotService>();

        services.AddSingleton<RobotTransportRouter>(sp =>
        {
            var bluetooth = sp.GetRequiredService<SimulatedRobotService>();
            var wifi      = sp.GetRequiredService<WifiRobotService>();
            var evt       = sp.GetRequiredService<EventLogService>();

            var transports = new Dictionary<TransportKind, IRobotTransport>
            {
                [TransportKind.Bluetooth] = bluetooth,
                [TransportKind.Wifi]      = wifi,
            };
            return new RobotTransportRouter(transports, TransportKind.Bluetooth, evt);
        });

        services.AddSingleton<IRobotTransportSwitch>(sp => sp.GetRequiredService<RobotTransportRouter>());
        services.AddSingleton<IRobotScanner>   (sp => sp.GetRequiredService<RobotTransportRouter>());
        services.AddSingleton<IRobotConnection>(sp => sp.GetRequiredService<RobotTransportRouter>());
        services.AddSingleton<IRobotSensors>   (sp => sp.GetRequiredService<RobotTransportRouter>());
        services.AddSingleton<IRobotBoundary>  (sp => sp.GetRequiredService<RobotTransportRouter>());

        services.AddSingleton<RobotStateMachine>();
        services.AddSingleton<IRobotControl>(sp =>
        {
            var router       = sp.GetRequiredService<RobotTransportRouter>();
            var connection   = sp.GetRequiredService<IRobotConnection>();
            var sensors      = sp.GetRequiredService<IRobotSensors>();
            var stateMachine = sp.GetRequiredService<RobotStateMachine>();
            var logger       = sp.GetRequiredService<ILogger<CommandPipeline>>();

            stateMachine.Start(sensors);

            IRobotControl inner = new ConnectionGuardProxy(new RetryDecorator(router), connection);
            return new CommandPipeline(inner, sensors, stateMachine, logger);
        });
    }
}
