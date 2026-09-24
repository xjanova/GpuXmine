using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace GpuxMine.Relay;

/// <summary>Which agents are online right now. In-memory by design — a relay restart re-learns it in seconds.</summary>
public sealed class AgentRegistry(ILogger<AgentRegistry> log)
{
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// When each worker that is not connected now was last heard from, since
    /// this relay started. Kept so "offline for three hours" can be told from
    /// "never seen", which the back office shows the owner.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.Ordinal);

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
    public AgentSession? Add(AgentSession session)
    {
        AgentSession? evicted = null;
        _sessions.AddOrUpdate(session.WorkerId, session, (_, existing) =>
        {
            evicted = existing;
            return session;
        });

        if (evicted is not null)
        {
            if (!string.Equals(evicted.RemoteIp, session.RemoteIp, StringComparison.Ordinal))
            {
                // Not a blip: a second machine holds this worker's token. Two
                // PCs sharing an identity, or a copied agent.json — either way
                // the operator needs to see it, and it is the evidence that
                // decides whether to rotate the worker.
                log.LogWarning("Worker {WorkerId} connected from {NewIp} while a session from {OldIp} was live — dropping the old one",
                    session.WorkerId, session.RemoteIp ?? "(unknown)", evicted.RemoteIp ?? "(unknown)");
            }
            else
            {
                log.LogWarning("Worker {WorkerId} reconnected while an older session was live — dropping the old one", session.WorkerId);
            }
            // Not awaited: the old socket is usually the dead one, and saying
            // goodbye to it can take seconds the new session should not wait.
            _ = evicted.CloseAsync(WebSocketCloseStatus.PolicyViolation, "replaced by a newer connection").AsTask();
        }

        return evicted;
    }

    /// <summary>Removes the session only if it is still the registered one (a later reconnect must survive).</summary>
    public void Remove(AgentSession session)
    {
        if (_sessions.TryRemove(new KeyValuePair<string, AgentSession>(session.WorkerId, session)))
            _lastSeen[session.WorkerId] = session.LastSeenAt;
    }

    /// <summary>
    /// Ends a worker's live session, if it has one, telling the agent why.
    /// For an operator's delete, disable or rotate: the connection was
    /// authorised by a credential that no longer holds.
    /// </summary>
    public async Task<bool> DisconnectAsync(string workerId, WebSocketCloseStatus status, string reason)
    {
        if (!_sessions.TryRemove(workerId, out AgentSession? session)) return false;
        _lastSeen[workerId] = session.LastSeenAt;
        await session.CloseAsync(status, reason);
        return true;
    }

    /// <summary>Ends this particular session — not a newer one for the same worker.</summary>
    public async Task<bool> DisconnectAsync(AgentSession session, WebSocketCloseStatus status, string reason)
    {
        if (!_sessions.TryRemove(new KeyValuePair<string, AgentSession>(session.WorkerId, session))) return false;
        _lastSeen[session.WorkerId] = session.LastSeenAt;
        await session.CloseAsync(status, reason);
        return true;
    }

    /// <summary>The worker is gone from the registry for good; so is its history here.</summary>
    public void Forget(string workerId) => _lastSeen.TryRemove(workerId, out _);

    public AgentSession? Get(string workerId) => _sessions.GetValueOrDefault(workerId);

    /// <summary>Last time anything arrived from the worker: now-ish when connected, since this relay started otherwise.</summary>
    public DateTimeOffset? LastSeen(string workerId)
        => _sessions.TryGetValue(workerId, out AgentSession? session)
            ? session.LastSeenAt
            : _lastSeen.TryGetValue(workerId, out DateTimeOffset at) ? at : null;

    public IReadOnlyList<AgentSession> All() => _sessions.Values.ToList();
}
