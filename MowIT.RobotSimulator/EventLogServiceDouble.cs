namespace MowIT.Application.Logging;

public sealed class EventLogService
{
    public List<(DateTime Ts, string Level, string Source, string Message)> Entries { get; } = new();

    public void Tx   (string source, string message) => Add("TX",    source, message);
    public void Rx   (string source, string message) => Add("RX",    source, message);
    public void Info (string source, string message) => Add("INFO",  source, message);
    public void State(string source, string message) => Add("STATE", source, message);
    public void Warn (string source, string message) => Add("WARN",  source, message);
    public void Error(string source, string message) => Add("ERROR", source, message);

    private void Add(string level, string source, string message) =>
        Entries.Add((DateTime.UtcNow, level, source, message));
}
