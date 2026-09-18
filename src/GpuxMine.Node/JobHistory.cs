using GpuxMine.Node.Storage;

namespace GpuxMine.Node;

public enum JobStatus { Queued, Running, Completed, Failed }

/// <summary>One job as this node saw it. Payout is filled in by the pool later, never guessed here.</summary>
public sealed class JobRecord
{
    public required string PromptId { get; init; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public DateTimeOffset SubmittedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int NodesTotal { get; set; }
    public string? OutputFilename { get; set; }
    public string? Error { get; set; }

    /// <summary>Set by the pool when it settles the job. Null until then — shown as "—", never as 0.</summary>
    public decimal? PayoutThb { get; set; }

    /// <summary>What kind of work, as far as the graph tells us (sampler = image gen, etc.).</summary>
    public string Kind { get; set; } = "job";

    public TimeSpan? Duration => StartedAt is { } s && CompletedAt is { } c ? c - s : null;
}

/// <summary>
/// The Live Queue screen's data: what ran, what is running, how it went.
/// </summary>
/// <remarks>
/// Every row goes to SQLite. Counters are queried, never accumulated in a
/// field, so the number on screen cannot drift from the rows behind it — and
/// "jobs done today" survives a reboot, which the in-memory version did not.
/// </remarks>
public sealed class JobHistory(NodeStore store)
{
    public event Action<JobRecord>? Changed;

    private readonly Lock _currentGate = new();
    private JobRecord? _current;

    public JobRecord Submitted(string promptId, int nodesTotal, string kind, bool freeShare = false)
    {
        var job = new JobRecord { PromptId = promptId, NodesTotal = nodesTotal, Kind = kind };
        store.JobSubmitted(promptId, kind, nodesTotal, freeShare);
        lock (_currentGate) _current = job;
        Changed?.Invoke(job);
        return job;
    }

    public void Started(string promptId)
    {
        store.JobStarted(promptId);
        lock (_currentGate)
        {
            if (_current?.PromptId == promptId)
            {
                _current.Status = JobStatus.Running;
                _current.StartedAt = DateTimeOffset.Now;
            }
        }
        Changed?.Invoke(Current ?? new JobRecord { PromptId = promptId });
    }

    public void Finished(string promptId, bool success, string? filename, string? error)
    {
        store.JobFinished(promptId, success, filename, error);
        lock (_currentGate)
        {
            if (_current?.PromptId == promptId) _current = null;
        }
        Changed?.Invoke(new JobRecord { PromptId = promptId, Status = success ? JobStatus.Completed : JobStatus.Failed });
    }

    /// <summary>The job in flight, held in memory: it changes several times a second while rendering.</summary>
    public JobRecord? Current
    {
        get { lock (_currentGate) return _current; }
    }

    public IReadOnlyList<JobRecord> Snapshot(int limit = 100) => store.RecentJobs(limit);

    public (int Completed, int Failed) CountsSince(DateTimeOffset since)
    {
        var (ok, failed, _) = store.Totals(since);
        return (ok, failed);
    }

    /// <summary>What the pool has actually paid for work finished since <paramref name="since"/>. Null when it has paid nothing yet.</summary>
    public decimal? EarnedSince(DateTimeOffset since) => store.Totals(since).EarnedThb;
}
