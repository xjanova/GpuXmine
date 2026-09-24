using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GpuxMine.Relay;

/// <summary>One enrolled worker, as the registry file stores it.</summary>
/// <remarks>
/// <para>
/// Two credentials, because two different parties use them. The node holds
/// the <em>agent</em> token and uses it only to dial <c>/agent</c>; aixman
/// holds the <em>tunnel</em> token and uses it only on <c>/w/</c>. Before the
/// split one token opened both doors, so anybody with a copy of a node's
/// agent.json could also be the dispatcher, and anybody with aixman's
/// database could also be the node.
/// </para>
/// <para>
/// The agent token hash keeps its original JSON name <c>TokenHash</c>, so a
/// store written by this build still loads in the build before it — a
/// rollback must not lock out the fleet. That older build ignores the fields
/// it does not know, and falls back to one token for both doors.
/// </para>
/// </remarks>
public sealed record WorkerRecord
{
    public required string WorkerId { get; init; }

    /// <summary>SHA-256 of the token the node presents on <c>/agent</c>.</summary>
    [JsonPropertyName("TokenHash")]
    public required string AgentTokenHash { get; init; }

    /// <summary>
    /// SHA-256 of the token aixman presents on <c>/w/</c>. Null for a worker
    /// enrolled before the split, which is still reached with its agent token
    /// until it is given one of its own.
    /// </summary>
    public string? TunnelTokenHash { get; init; }

    public string? Label { get; init; }

    public DateTimeOffset EnrolledAt { get; init; }

    /// <summary>Refused on both doors until enabled again. The record, and so the owner's history, stays.</summary>
    public bool Disabled { get; init; }

    public DateTimeOffset? DisabledAt { get; init; }

    public string? DisabledReason { get; init; }

    public DateTimeOffset? RotatedAt { get; init; }
}

/// <summary>Why a presented token was or was not accepted.</summary>
public enum WorkerAuth
{
    Ok,

    /// <summary>No such worker. Answered 401 — the same as a wrong token, so an id cannot be probed for.</summary>
    Unknown,

    /// <summary>Wrong token for this worker, or the right token on the wrong door. 401.</summary>
    BadToken,

    /// <summary>Right token, but an operator has switched the worker off. 403, which tells the node to stop retrying hard.</summary>
    Disabled,
}

/// <summary>What <see cref="WorkerStore.Enroll"/> and <see cref="WorkerStore.Rotate"/> hand out. The plaintext tokens exist only here.</summary>
public sealed record IssuedTokens(WorkerRecord Record, string? AgentToken, string TunnelToken);

/// <summary>
/// Who is allowed to connect, and which bearer token aixman will present when
/// it wants to reach them.
/// </summary>
/// <remarks>
/// A JSON file is enough for M1 — a few dozen beta nodes, one relay process.
/// It is deliberately the only piece here that would be swapped for the shared
/// MySQL in M2, so everything else can be written against the interface now.
///
/// Tokens are stored hashed. The relay never needs the plaintext again: it only
/// ever has to answer "does the token this caller presented match this worker",
/// and a stolen store should not hand the thief a working credential for
/// every node in the fleet.
///
/// Every write keeps the previous file as <c>workers.json.bak</c> and the
/// first version of each day under <c>backups/</c>. This file is the only
/// copy of the fleet's credentials; losing it tells every node at once that
/// its token is invalid.
/// </remarks>
public sealed class WorkerStore
{
    private const int DailyBackupsKept = 14;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly ILogger? _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, WorkerRecord> _byId;

    public WorkerStore(string path, ILogger? log = null)
    {
        _path = path;
        _log = log;
        _byId = Load(path);

        if (!File.Exists(path) && File.Exists(path + ".bak"))
        {
            // Not restored automatically: the backup is one write behind, and
            // that write may have been the delete or disable of a worker that
            // must stay gone. An operator decides; RELAY-DEPLOY.md says how.
            _log?.LogError("Worker store {Path} is missing but {Backup} exists — starting EMPTY. Every node will be refused until the store is restored.",
                path, path + ".bak");
        }
    }

    private static Dictionary<string, WorkerRecord> Load(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, WorkerRecord>(StringComparer.Ordinal);
        try
        {
            var records = JsonSerializer.Deserialize<List<WorkerRecord>>(File.ReadAllText(path)) ?? [];
            return records.ToDictionary(r => r.WorkerId, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // Refusing to start is worse than starting empty would be silent, so
            // be loud and refuse: an unreadable store means every node in the
            // fleet is about to be told its token is invalid.
            throw new InvalidOperationException(
                $"Worker store at {path} is unreadable: {ex.Message}. " +
                $"The previous version is {path}.bak and daily copies are in {Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "backups")}.",
                ex);
        }
    }

    private void Persist()
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(_byId.Values.ToList(), WriteOptions);
        string directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(directory);

        // Flushed to the disk before the rename. The relay shares its host with
        // other services and has already lived through a power cut; a rename
        // that lands before its data leaves an empty registry behind.
        string tmp = _path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(json);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(_path)) Backup(directory);
        File.Move(tmp, _path, overwrite: true);
    }

    private void Backup(string directory)
    {
        try
        {
            File.Copy(_path, _path + ".bak", overwrite: true);

            string backups = Path.Combine(directory, "backups");
            Directory.CreateDirectory(backups);
            string today = Path.Combine(backups, $"workers-{DateTimeOffset.UtcNow:yyyyMMdd}.json");
            if (!File.Exists(today)) File.Copy(_path, today);

            foreach (string old in Directory.GetFiles(backups, "workers-*.json")
                         .OrderDescending(StringComparer.Ordinal)
                         .Skip(DailyBackupsKept))
            {
                File.Delete(old);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A backup that failed must not fail the write it was protecting.
            _log?.LogWarning("Could not back up the worker store: {Message}", ex.Message);
        }
    }

    public static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string NewToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>
    /// Mints a worker id and its tokens. The plaintext tokens are returned once and never stored.
    /// </summary>
    /// <param name="splitTokens">
    /// A separate tunnel token. Without one the worker is enrolled the v0.1
    /// way — one token for both doors — and the tunnel token handed back is
    /// that same token, so a caller that always uses <c>TunnelToken</c> for
    /// aixman is right either way.
    /// </param>
    public IssuedTokens Enroll(string? label, bool splitTokens = true)
    {
        string workerId = "gxm-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        string agentToken = NewToken();
        string tunnelToken = splitTokens ? NewToken() : agentToken;

        var record = new WorkerRecord
        {
            WorkerId = workerId,
            AgentTokenHash = HashToken(agentToken),
            TunnelTokenHash = splitTokens ? HashToken(tunnelToken) : null,
            Label = label,
            EnrolledAt = DateTimeOffset.UtcNow,
        };
        lock (_gate)
        {
            _byId[workerId] = record;
            Persist();
        }
        return new IssuedTokens(record, agentToken, tunnelToken);
    }

    public WorkerRecord? Find(string workerId)
    {
        lock (_gate) return _byId.GetValueOrDefault(workerId);
    }

    /// <summary>The node's door. Only the agent token opens it — never the tunnel token.</summary>
    public WorkerAuth AuthenticateAgent(string workerId, string presentedToken)
    {
        WorkerRecord? record = Find(workerId);
        if (record is null) return WorkerAuth.Unknown;
        if (!HashMatches(record.AgentTokenHash, presentedToken)) return WorkerAuth.BadToken;
        return record.Disabled ? WorkerAuth.Disabled : WorkerAuth.Ok;
    }

    /// <summary>
    /// aixman's door. The tunnel token once the worker has one; before that,
    /// the agent token, so a worker enrolled before the split keeps working.
    /// </summary>
    /// <remarks>
    /// Once a tunnel token exists the agent token stops working here for good:
    /// that is the whole point of issuing one, and a fallback would quietly
    /// undo it.
    /// </remarks>
    public WorkerAuth AuthenticateTunnel(string workerId, string presentedToken)
    {
        WorkerRecord? record = Find(workerId);
        if (record is null) return WorkerAuth.Unknown;

        string expected = record.TunnelTokenHash ?? record.AgentTokenHash;
        if (!HashMatches(expected, presentedToken)) return WorkerAuth.BadToken;
        return record.Disabled ? WorkerAuth.Disabled : WorkerAuth.Ok;
    }

    /// <summary>
    /// Constant-time so a caller cannot learn a valid token one byte at a time
    /// by measuring how long the comparison took.
    /// </summary>
    private static bool HashMatches(string storedHash, string presentedToken)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(storedHash),
            Encoding.UTF8.GetBytes(HashToken(presentedToken)));

    /// <summary>Removes the worker and both its credentials. False when there was nothing to remove.</summary>
    public bool Delete(string workerId)
    {
        lock (_gate)
        {
            if (!_byId.Remove(workerId)) return false;
            Persist();
            return true;
        }
    }

    /// <summary>Switches a worker off or back on. Null when the worker is unknown.</summary>
    public WorkerRecord? SetDisabled(string workerId, bool disabled, string? reason)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(workerId, out WorkerRecord? record)) return null;
            if (record.Disabled == disabled && (!disabled || record.DisabledReason == reason)) return record;

            record = disabled
                ? record with { Disabled = true, DisabledAt = DateTimeOffset.UtcNow, DisabledReason = reason }
                : record with { Disabled = false, DisabledAt = null, DisabledReason = null };
            _byId[workerId] = record;
            Persist();
            return record;
        }
    }

    /// <summary>
    /// Re-issues credentials. Both by default; with <paramref name="tunnelOnly"/>
    /// only aixman's, leaving the node's agent token — and its live session —
    /// alone. That second form is how a worker enrolled before the split gets
    /// a tunnel token without its owner having to pair again.
    /// </summary>
    /// <returns>Null when the worker is unknown.</returns>
    public IssuedTokens? Rotate(string workerId, bool tunnelOnly)
    {
        string? agentToken = tunnelOnly ? null : NewToken();
        string tunnelToken = NewToken();

        lock (_gate)
        {
            if (!_byId.TryGetValue(workerId, out WorkerRecord? record)) return null;

            record = record with
            {
                AgentTokenHash = agentToken is null ? record.AgentTokenHash : HashToken(agentToken),
                TunnelTokenHash = HashToken(tunnelToken),
                RotatedAt = DateTimeOffset.UtcNow,
            };
            _byId[workerId] = record;
            Persist();
            return new IssuedTokens(record, agentToken, tunnelToken);
        }
    }

    public IReadOnlyList<WorkerRecord> All()
    {
        lock (_gate) return _byId.Values.ToList();
    }
}
