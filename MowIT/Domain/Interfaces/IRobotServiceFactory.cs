namespace MowIT.Domain.Interfaces;

public interface IRobotServiceFactory
{
    string RobotName { get; }

void RegisterServices(IServiceCollection services);
}
