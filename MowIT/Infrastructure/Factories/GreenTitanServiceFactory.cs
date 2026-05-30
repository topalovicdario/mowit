using Microsoft.Extensions.Logging;
using MowIT.Application.Pipeline;
using MowIT.Application.StateMachine;
using MowIT.Domain.Interfaces;
using MowIT.Infrastructure.Ble;
using Plugin.BLE;

namespace MowIT.Infrastructure.Factories;

public class GreenTitanServiceFactory : IRobotServiceFactory
{
    public string RobotName => "GreenTitan";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<GreenTitanBleService>(sp =>
            new GreenTitanBleService(
                CrossBluetoothLE.Current,
                CrossBluetoothLE.Current.Adapter,
                sp.GetRequiredService<ILogger<GreenTitanBleService>>()));

        services.AddSingleton<IRobotScanner>   (sp => sp.GetRequiredService<GreenTitanBleService>());
        services.AddSingleton<IRobotConnection>(sp => sp.GetRequiredService<GreenTitanBleService>());
        services.AddSingleton<IRobotSensors> (sp => sp.GetRequiredService<GreenTitanBleService>());
        services.AddSingleton<IRobotBoundary>(sp => sp.GetRequiredService<GreenTitanBleService>());

services.AddSingleton<RobotStateMachine>();
        services.AddSingleton<IRobotControl>(sp =>
        {
            var ble          = sp.GetRequiredService<GreenTitanBleService>();
            var connection   = sp.GetRequiredService<IRobotConnection>();
            var sensors      = sp.GetRequiredService<IRobotSensors>();
            var stateMachine = sp.GetRequiredService<RobotStateMachine>();
            var logger       = sp.GetRequiredService<ILogger<CommandPipeline>>();

            stateMachine.Start(sensors);

            IRobotControl inner = new ConnectionGuardProxy(new RetryDecorator(ble), connection);
            return new CommandPipeline(inner, sensors, stateMachine, logger);
        });
    }
}
