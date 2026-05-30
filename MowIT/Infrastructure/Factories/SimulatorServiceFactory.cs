using Microsoft.Extensions.Logging;
using MowIT.Application.Pipeline;
using MowIT.Application.StateMachine;
using MowIT.Domain.Interfaces;
using MowIT.Infrastructure.Ble;
using MowIT.Infrastructure.Simulator;

namespace MowIT.Infrastructure.Factories;

// Local-only factory that produces a fully simulated mower. The IRobotControl chain mirrors
// GreenTitanSppFactory exactly (RetryDecorator → ConnectionGuardProxy → CommandPipeline) so
// switching between simulator and real ESP32 exercises the same pipeline, state machine, and
// command-handler path. That way bugs caught in sim are real bugs, not artefacts of a
// simpler test harness.
public class SimulatorServiceFactory : IRobotServiceFactory
{
    public string RobotName => "Simulator";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<SimulatedRobotService>();

        services.AddSingleton<IRobotScanner>   (sp => sp.GetRequiredService<SimulatedRobotService>());
        services.AddSingleton<IRobotConnection>(sp => sp.GetRequiredService<SimulatedRobotService>());
        services.AddSingleton<IRobotSensors>   (sp => sp.GetRequiredService<SimulatedRobotService>());
        services.AddSingleton<IRobotBoundary>  (sp => sp.GetRequiredService<SimulatedRobotService>());

        services.AddSingleton<RobotStateMachine>();

        services.AddSingleton<IRobotControl>(sp =>
        {
            var sim          = sp.GetRequiredService<SimulatedRobotService>();
            var connection   = sp.GetRequiredService<IRobotConnection>();
            var sensors      = sp.GetRequiredService<IRobotSensors>();
            var stateMachine = sp.GetRequiredService<RobotStateMachine>();
            var logger       = sp.GetRequiredService<ILogger<CommandPipeline>>();

            stateMachine.Start(sensors);

            IRobotControl inner = new ConnectionGuardProxy(new RetryDecorator(sim), connection);
            return new CommandPipeline(inner, sensors, stateMachine, logger);
        });
    }
}
