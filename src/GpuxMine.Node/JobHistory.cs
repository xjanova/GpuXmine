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
/// Bounded like the log. Today's counters are derived from it on demand rather
/// than kept as separate mutable totals, so there is one source of truth for
/// "jobs done today" and it cannot drift from the list on screen.
/// </remarks>
public sealed class JobHistory
{
    public const int Capacity = 300;

    private readonly Lock _gate = new();
    private readonly LinkedList<JobRecord> _jobs = new();
    private readonly Dictionary<string, JobRecord> _byId = new(StringComparer.Ordinal);

    public event Action<JobRecord>? Changed;

    public JobRecord Submitted(string promptId, int nodesTotal, string kind)
    {
        var job = new JobRecord { PromptId = promptId, NodesTotal = nodesTotal, Kind = kind };
        lock (_gate)
        {
            _jobs.AddLast(job);
            _byId[promptId] = job;
            while (_jobs.Count > Capacity)
            {
                var evicted = _jobs.First!.Value;
                _jobs.RemoveFirst();
                _byId.Remove(evicted.PromptId);
            }
        }
        Changed?.Invoke(job);
        return job;
    }

    public void Started(string promptId)
    {
        JobRecord? job;
        lock (_gate)
        {
            if (!_byId.TryGetValue(promptId, out job)) return;
            job.Status = JobStatus.Running;
            job.StartedAt = DateTimeOffset.Now;
        }
        Changed?.Invoke(job);
    }

    public void Finished(string promptId, bool success, string? filename, string? error)
    {
        JobRecord? job;
        lock (_gate)
        {
            if (!_byId.TryGetValue(promptId, out job)) return;
            job.Status = success ? JobStatus.Completed : JobStatus.Failed;
            job.CompletedAt = DateTimeOffset.Now;
            job.StartedAt ??= job.CompletedAt;
            job.OutputFilename = filename;
            job.Error = error;
        }
        Changed?.Invoke(job);
    }

    public JobRecord? Current
    {
        get { lock (_gate) return _jobs.LastOrDefault(j => j.Status is JobStatus.Running or JobStatus.Queued); }
    }

    public IReadOnlyList<JobRecord> Snapshot()
    {
        lock (_gate) return _jobs.Reverse().ToList();   // newest first
    }

    public (int Completed, int Failed) CountsSince(DateTimeOffset since)
    {
        lock (_gate)
        {
            int ok = 0, bad = 0;
            foreach (var j in _jobs)
            {
                if (j.CompletedAt is not { } c || c < since) continue;
                if (j.Status == JobStatus.Completed) ok++; else if (j.Status == JobStatus.Failed) bad++;
            }
            return (ok, bad);
        }
    }
}
