using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GpuxMine.Relay;

public sealed record WorkerRecord(
    string WorkerId,
    string TokenHash,
    string? Label,
    DateTimeOffset EnrolledAt);

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
/// </remarks>
public sealed class WorkerStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private Dictionary<string, WorkerRecord> _byId;

    public WorkerStore(string path)
    {
        _path = path;
        _byId = Load(path);
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
            throw new InvalidOperationException($"Worker store at {path} is unreadable: {ex.Message}", ex);
        }
    }

    private void Persist()
    {
        string json = JsonSerializer.Serialize(_byId.Values.ToList(), new JsonSerializerOptions { WriteIndented = true });
        string tmp = _path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }

    public static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Mints a worker id and a token. The plaintext token is returned once and never stored.</summary>
    public (WorkerRecord Record, string Token) Enroll(string? label)
    {
        string workerId = "gxm-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var record = new WorkerRecord(workerId, HashToken(token), label, DateTimeOffset.UtcNow);
        lock (_gate)
        {
            _byId[workerId] = record;
            Persist();
        }
        return (record, token);
    }

    public WorkerRecord? Find(string workerId)
    {
        lock (_gate) return _byId.GetValueOrDefault(workerId);
    }

    /// <summary>
    /// Constant-time so a caller cannot learn a valid token one byte at a time
    /// by measuring how long the comparison took.
    /// </summary>
    public bool TokenMatches(string workerId, string presentedToken)
    {
        WorkerRecord? record = Find(workerId);
        if (record is null) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(record.TokenHash),
            Encoding.UTF8.GetBytes(HashToken(presentedToken)));
    }

    public IReadOnlyList<WorkerRecord> All()
    {
        lock (_gate) return _byId.Values.ToList();
    }
}
