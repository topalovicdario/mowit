using System.Reactive.Linq;

namespace MowIT.Infrastructure.Transport;

public sealed class NullRobotTransportSwitch : IRobotTransportSwitch
{
    public TransportKind CurrentKind => TransportKind.Bluetooth;
    public IObservable<TransportKind> KindChanges => Observable.Return(TransportKind.Bluetooth);
    public Task SelectAsync(TransportKind kind) => Task.CompletedTask;
}
