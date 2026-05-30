using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;

namespace MowIT.Application.Logging;

public enum EventLogLevel
{
    Tx,        // command sent over BT
    Rx,        // response received over BT
    Info,      // ordinary status
    State,     // app/sim/firmware state mutation
    Warn,
    Error,
}

public sealed record EventLogEntry(
    DateTime Timestamp,
    EventLogLevel Level,
    string Source,
    string Message)
{
    public string TimeText => Timestamp.ToString("HH:mm:ss.fff");

    public string LevelTag => Level switch
    {
        EventLogLevel.Tx    => "→",
        EventLogLevel.Rx    => "←",
        EventLogLevel.Info  => "·",
        EventLogLevel.State => "≡",
        EventLogLevel.Warn  => "!",
        EventLogLevel.Error => "✗",
        _                   => "?"
    };

    public Color LevelColor => Level switch
    {
        EventLogLevel.Tx    => Color.FromArgb("#2E7D32"),  // forest green — outgoing
        EventLogLevel.Rx    => Color.FromArgb("#1565C0"),  // blue — incoming
        EventLogLevel.Info  => Color.FromArgb("#546E7A"),  // slate
        EventLogLevel.State => Color.FromArgb("#6A1B9A"),  // purple — state change
        EventLogLevel.Warn  => Color.FromArgb("#EF6C00"),  // orange
        EventLogLevel.Error => Color.FromArgb("#C62828"),  // red
        _                   => Colors.Gray,
    };
}

// Single shared event log for the whole app. Every service / view-model funnels through here
// so the user can see, in one place, exactly what the app sent, what the firmware (or simulator)
// replied, and how the app's own state mutated in response. Mirrors every entry to ILogger so
// the same lines show up in the debug output window for offline diffing.
public sealed class EventLogService
{
    private const int MaxEntries = 400;
    private readonly ILogger<EventLogService> _logger;

    public ObservableCollection<EventLogEntry> Entries { get; } = new();
    public event EventHandler<EventLogEntry>? EntryAdded;

    public EventLogService(ILogger<EventLogService> logger) => _logger = logger;

    public void Tx   (string source, string cmd)  => Add(EventLogLevel.Tx,    source, cmd);
    public void Rx   (string source, string resp) => Add(EventLogLevel.Rx,    source, resp);
    public void Info (string source, string msg)  => Add(EventLogLevel.Info,  source, msg);
    public void State(string source, string msg)  => Add(EventLogLevel.State, source, msg);
    public void Warn (string source, string msg)  => Add(EventLogLevel.Warn,  source, msg);
    public void Error(string source, string msg)  => Add(EventLogLevel.Error, source, msg);

    private void Add(EventLogLevel level, string source, string message)
    {
        var entry = new EventLogEntry(DateTime.Now, level, source, message);

        // Mirror to ILogger so the entry shows up in Visual Studio's Debug Output
        // / Android Logcat with the same wording you see in-app.
        var line = $"[{source}] {entry.LevelTag} {message}";
        switch (level)
        {
            case EventLogLevel.Error: _logger.LogError  ("{Line}", line); break;
            case EventLogLevel.Warn:  _logger.LogWarning("{Line}", line); break;
            default:                  _logger.LogInformation("{Line}", line); break;
        }

        if (MainThread.IsMainThread) AppendOnUi(entry);
        else MainThread.BeginInvokeOnMainThread(() => AppendOnUi(entry));
    }

    private void AppendOnUi(EventLogEntry entry)
    {
        Entries.Insert(0, entry);

        while (Entries.Count > MaxEntries)
            Entries.RemoveAt(Entries.Count - 1);

        EntryAdded?.Invoke(this, entry);
    }

    public void Clear()
    {
        if (MainThread.IsMainThread) Entries.Clear();
        else MainThread.BeginInvokeOnMainThread(() => Entries.Clear());
    }
}
