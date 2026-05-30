using MowIT.Domain.Entities;

namespace MowIT.Domain.Interfaces;

public interface IRobotSensors
{
    IObservable<SensorSnapshot> SensorStream { get; }
    IObservable<RobotStatus>   StatusStream  { get; }
    SensorSnapshot? LastSensor { get; }
    RobotStatus?   LastStatus  { get; }
}
