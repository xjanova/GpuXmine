namespace GpuxMine.Node;

public enum LogLevel { Info, Warn }

public sealed record LogEntry(DateTimeOffset At, string Channel, string Message, LogLevel Level);

/// <summary>Minimal logging seam so the node has no framework dependency for what is, on the console, plain output.</summary>
public interface ILoggerish
{
    void Info(string message);
    void Warn(string message);
}

public sealed class ConsoleLog : ILoggerish
{
    public void Info(string message) => Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {message}");
    public void Warn(string message) => Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} WARN {message}");
}

/// <summary>
/// The node's own memory of what it did — the Activity Log screen, and what
/// the owner pastes into a support ticket.
/// </summary>
/// <remarks>
/// A bounded ring: a node that runs for months would otherwise hold every line
/// it ever wrote. Messages that start with <c>[channel]</c> are filed under
/// that channel, which is how the log screen colours and filters them.
/// </remarks>
public sealed class ActivityLog : ILoggerish
{
    public const int Capacity = 2000;

    private readonly Lock _gate = new();
    private readonly LinkedList<LogEntry> _entries = new();
    private readonly ILoggerish? _also;

    public event Action<LogEntry>? EntryAdded;

    /// <param name="also">A second sink, e.g. the console when running headless.</param>
    public ActivityLog(ILoggerish? also = null) => _also = also;

    public void Info(string message) => Add(message, LogLevel.Info);
    public void Warn(string message) => Add(message, LogLevel.Warn);

    public void Add(string message, LogLevel level, string? channel = null)
    {
        (string ch, string text) = channel is null ? Split(message) : (channel, message);
        var entry = new LogEntry(DateTimeOffset.Now, ch, text, level);

        lock (_gate)
        {
            _entries.AddLast(entry);
            while (_entries.Count > Capacity) _entries.RemoveFirst();
        }

        if (level == LogLevel.Warn) _also?.Warn(message); else _also?.Info(message);
        EntryAdded?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate) return _entries.ToList();
    }

    public string Export()
    {
        lock (_gate)
        {
            return string.Join('\n', _entries.Select(e =>
                $"{e.At:yyyy-MM-dd HH:mm:ss} [{e.Channel}] {(e.Level == LogLevel.Warn ? "WARN " : "")}{e.Message}"));
        }
    }

    private static (string Channel, string Text) Split(string message)
    {
        if (message.Length > 2 && message[0] == '[')
        {
            int close = message.IndexOf(']');
            if (close > 1 && close < 12)
                return (message[1..close], message[(close + 1)..].TrimStart());
        }
        return ("node", message);
    }
}
