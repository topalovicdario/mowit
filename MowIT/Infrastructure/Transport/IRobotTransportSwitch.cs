namespace MowIT.Infrastructure.Transport;

public interface IRobotTransportSwitch
{
    TransportKind CurrentKind { get; }
    IObservable<TransportKind> KindChanges { get; }
    Task SelectAsync(TransportKind kind);
}
