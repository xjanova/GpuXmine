using System.Collections.Concurrent;

namespace GpuxMine.Relay;

/// <summary>Which agents are online right now. In-memory by design — a relay restart re-learns it in seconds.</summary>
public sealed class AgentRegistry(ILogger<AgentRegistry> log)
{
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a session, evicting any earlier one for the same worker.
    /// </summary>
    /// <remarks>
    /// A node that reconnects after a network blip — or an owner who launches
    /// the agent twice — would otherwise leave a zombie session in the map.
    /// Whichever one the map happened to hold would receive the jobs, and half
    /// the time that is the socket nobody is reading any more.
    /// Last writer wins: the newest socket is the one demonstrably alive.
    /// </remarks>
    public async Task<AgentSession?> AddAsync(AgentSession session)
    {
        AgentSession? evicted = null;
        _sessions.AddOrUpdate(session.WorkerId, session, (_, existing) =>
        {
            evicted = existing;
            return session;
        });

        if (evicted is not null)
        {
            log.LogWarning("Worker {WorkerId} reconnected while an older session was live — dropping the old one", session.WorkerId);
            await evicted.DisposeAsync();
        }

        return evicted;
    }

    /// <summary>Removes the session only if it is still the registered one (a later reconnect must survive).</summary>
    public void Remove(AgentSession session)
        => _sessions.TryRemove(new KeyValuePair<string, AgentSession>(session.WorkerId, session));

    public AgentSession? Get(string workerId) => _sessions.GetValueOrDefault(workerId);

    public IReadOnlyList<AgentSession> All() => _sessions.Values.ToList();
}
