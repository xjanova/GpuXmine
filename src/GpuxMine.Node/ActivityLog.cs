using GpuxMine.Node.Storage;

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
/// the owner attaches to a support ticket.
/// </summary>
/// <remarks>
/// Backed by SQLite, not a ring buffer in RAM. The buffer held 2,000 lines and
/// lost all of them on restart, so the one question support actually asks —
/// "what happened last Tuesday when it stopped earning?" — had no answer.
/// Messages that start with <c>[channel]</c> are filed under that channel,
/// which is how the log screen colours and filters them.
/// </remarks>
public sealed class ActivityLog(NodeStore store, ILoggerish? also = null) : ILoggerish
{
    public event Action<LogEntry>? EntryAdded;

    public void Info(string message) => Add(message, LogLevel.Info);
    public void Warn(string message) => Add(message, LogLevel.Warn);

    public void Add(string message, LogLevel level, string? channel = null)
    {
        (string ch, string text) = channel is null ? Split(message) : (channel, message);
        var entry = new LogEntry(DateTimeOffset.Now, ch, text, level);

        try
        {
            store.AppendLog(entry.At, ch, level, text);
        }
        catch (Exception ex)
        {
            // Losing a log line must never take down the thing being logged.
            also?.Warn($"[warn] could not write log: {ex.Message}");
        }

        if (level == LogLevel.Warn) also?.Warn(message); else also?.Info(message);
        EntryAdded?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> Snapshot(int limit = 500) => store.RecentLog(limit);

    /// <param name="filter">all · jobs · payouts · warnings — the Activity Log screen's four buttons.</param>
    public IReadOnlyList<LogEntry> Filtered(string filter, int limit = 500) => filter switch
    {
        "jobs" => store.RecentLog(limit, """["job","auto","comfy"]"""),
        "payouts" => store.RecentLog(limit, """["pay"]"""),
        "warnings" => store.RecentLog(limit, warningsOnly: true),
        _ => store.RecentLog(limit),
    };

    public string Export(int limit = 20_000)
        => string.Join('\n', store.RecentLog(limit).Select(e =>
            $"{e.At.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)} [{e.Channel}] {(e.Level == LogLevel.Warn ? "WARN " : "")}{e.Message}"));

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
