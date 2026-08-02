using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;

namespace MowIT.Application.Logging;

public enum EventLogLevel
{
    Tx,       
    Rx,        
    Info,      
    State,     
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
        EventLogLevel.Tx    => "TX",
        EventLogLevel.Rx    => "RX",
        EventLogLevel.Info  => "i",
        EventLogLevel.State => "=",
        EventLogLevel.Warn  => "!",
        EventLogLevel.Error => "x",
        _                   => "?"
    };

    public Color LevelColor => Level switch
    {
        EventLogLevel.Tx    => Color.FromArgb("#2E7D32"),  
        EventLogLevel.Rx    => Color.FromArgb("#1565C0"),  
        EventLogLevel.Info  => Color.FromArgb("#546E7A"),  
        EventLogLevel.State => Color.FromArgb("#6A1B9A"),  
        EventLogLevel.Warn  => Color.FromArgb("#EF6C00"),  
        EventLogLevel.Error => Color.FromArgb("#C62828"),  
        _                   => Colors.Gray,
    };
}

public sealed class EventLogService
{
    private const int MaxEntries = 2000;
    private readonly ILogger<EventLogService> _logger;
    private readonly object       _fileLock = new();
    private StreamWriter?         _file;

    public ObservableCollection<EventLogEntry> Entries { get; } = new();
    public event EventHandler<EventLogEntry>? EntryAdded;

    public string? SessionFilePath { get; private set; }

    public EventLogService(ILogger<EventLogService> logger)
    {
        _logger = logger;
        OpenSessionFile();
    }

    private void OpenSessionFile()
    {
        try
        {
            var dir = Path.Combine(FileSystem.AppDataDirectory, "logs");
            Directory.CreateDirectory(dir);

            SessionFilePath = Path.Combine(dir, $"session_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            _file = new StreamWriter(SessionFilePath, append: true) { AutoFlush = true };
            _file.WriteLine($"# MowIT session started {DateTime.Now:O}");
            _file.WriteLine("# time\tlevel\tsource\tmessage");
        }
        catch (Exception ex)
        {
            _file = null;
            SessionFilePath = null;
            _logger.LogWarning(ex, "Could not open session log file");
        }
    }

    public void Tx   (string source, string cmd)  => Add(EventLogLevel.Tx,    source, cmd);
    public void Rx   (string source, string resp) => Add(EventLogLevel.Rx,    source, resp);
    public void Info (string source, string msg)  => Add(EventLogLevel.Info,  source, msg);
    public void State(string source, string msg)  => Add(EventLogLevel.State, source, msg);
    public void Warn (string source, string msg)  => Add(EventLogLevel.Warn,  source, msg);
    public void Error(string source, string msg)  => Add(EventLogLevel.Error, source, msg);

    private void Add(EventLogLevel level, string source, string message)
    {
        var entry = new EventLogEntry(DateTime.Now, level, source, message);

        WriteToFile(entry);

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

    private void WriteToFile(EventLogEntry entry)
    {
        if (_file is null) return;

        lock (_fileLock)
        {
            try
            {
                _file.WriteLine($"{entry.Timestamp:HH:mm:ss.fff}\t{entry.LevelTag}\t{entry.Source}\t{entry.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session log write failed");
                _file = null;
            }
        }
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
