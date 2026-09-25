using GpuxMine.Core.Licensing;
using GpuxMine.Core.Updates;

namespace GpuxMine.Node;

/// <remarks>
/// <see cref="Rejected"/> is last so the values before it keep their numbers.
/// It means the relay answered and said no — 401 (it does not know this worker
/// or its token) or 403 (an operator switched it off) — which reconnecting
/// will not fix, unlike <see cref="Reconnecting"/>.
/// </remarks>
public enum ConnectionState { Stopped, Connecting, Connected, Reconnecting, Rejected }

/// <summary>
/// What the node is doing right now, for anything that wants to show it.
/// </summary>
/// <remarks>
/// Plain properties plus one <see cref="Changed"/> event, on purpose: the WPF
/// app marshals to its dispatcher and re-reads, the console prints a line, and
/// this library stays free of any UI framework. Every value here is either
/// measured on this machine or reported by the pool — nothing is estimated
/// into a money figure.
/// </remarks>
public sealed class NodeState
{
    public event Action? Changed;

    public ConnectionState Connection { get; private set; } = ConnectionState.Stopped;

    /// <summary>The owner pressed START. Independent of whether the relay is reachable.</summary>
    public bool Running { get; private set; }

    /// <summary>Would this node take a new job this second.</summary>
    public bool Accepting { get; private set; }

    /// <summary>Why not, when <see cref="Accepting"/> is false while running.</summary>
    public string? PauseReason { get; private set; }

    /// <summary>
    /// The owner pressed STOP while there was work to hand over. The node takes
    /// nothing new, and keeps the relay open until what it has is delivered.
    /// </summary>
    public bool Draining { get; private set; }

    /// <summary>What the drain is waiting for, in the owner's words. Null when not draining.</summary>
    public string? DrainNote { get; private set; }

    /// <summary>
    /// What the owner has to do about the connection, when that is something
    /// only they can do — set with <see cref="ConnectionState.Rejected"/>.
    /// </summary>
    public string? ConnectionNote { get; private set; }

    public GpuSnapshot Gpu { get; private set; } = GpuSnapshot.None;

    public JobRecord? CurrentJob { get; private set; }

    public DateTimeOffset? SessionStartedAt { get; private set; }

    public int JobsCompletedToday { get; private set; }
    public int JobsFailedToday { get; private set; }

    public int? RelayLatencyMs { get; private set; }

    public LicenseState? License { get; private set; }

    public string? UpdateStatus { get; private set; }

    /// <summary>The pool's view of this node, once it has one. Null = no server data yet.</summary>
    public decimal? EarnedTodayThb { get; private set; }

    /// <summary>The capability report. Null means this machine has never passed one, and gets no work.</summary>
    public Assessment.NodeAssessment? Assessment { get; private set; }

    /// <summary>True while the benchmark is running — the card is busy with it, so no job is taken.</summary>
    public bool Assessing { get; private set; }

    /// <summary>Which stage the running assessment is on, and how far through. Empty when none is running.</summary>
    public Assessment.AssessmentProgress AssessmentProgress { get; private set; }
        = GpuxMine.Node.Assessment.AssessmentProgress.None;

    public TimeSpan Uptime => SessionStartedAt is { } s ? DateTimeOffset.Now - s : TimeSpan.Zero;

    // --- mutation, only from the host --------------------------------------

    internal void Set(Action<NodeState> mutate)
    {
        mutate(this);
        Changed?.Invoke();
    }

    internal void SetConnection(ConnectionState c) => Set(s =>
    {
        // A refusal is sticky until something else is actually true: the loop
        // that retries after one reports "reconnecting" in between, and the
        // owner would lose the one line that tells them what to do.
        if (s.Connection == ConnectionState.Rejected && c == ConnectionState.Reconnecting) return;
        s.Connection = c;
        if (c != ConnectionState.Rejected) s.ConnectionNote = null;
    });

    internal void SetRejected(string note) => Set(s =>
    {
        s.Connection = ConnectionState.Rejected;
        s.ConnectionNote = note;
    });

    internal void SetRunning(bool running) => Set(s =>
    {
        s.Running = running;
        s.SessionStartedAt = running ? (s.SessionStartedAt ?? DateTimeOffset.Now) : null;
        if (!running)
        {
            s.Accepting = false;
            s.PauseReason = null;
            s.Draining = false;
            s.DrainNote = null;
        }
    });
    internal void SetDraining(bool draining, string? note) => Set(s => { s.Draining = draining; s.DrainNote = draining ? note : null; });
    internal void SetAccepting(bool accepting, string? reason) => Set(s => { s.Accepting = accepting; s.PauseReason = reason; });
    internal void SetGpu(GpuSnapshot g) => Set(s => s.Gpu = g);
    internal void SetCurrentJob(JobRecord? j) => Set(s => s.CurrentJob = j);
    internal void SetCounts(int ok, int failed) => Set(s => { s.JobsCompletedToday = ok; s.JobsFailedToday = failed; });
    internal void SetLatency(int? ms) => Set(s => s.RelayLatencyMs = ms);
    internal void SetLicense(LicenseState l) => Set(s => s.License = l);
    internal void SetUpdateStatus(string? text) => Set(s => s.UpdateStatus = text);
    internal void SetEarnedToday(decimal? thb) => Set(s => s.EarnedTodayThb = thb);
    internal void SetAssessment(Assessment.NodeAssessment? a) => Set(s => s.Assessment = a);
    internal void SetAssessing(bool busy) => Set(s => s.Assessing = busy);
    internal void SetAssessmentProgress(Assessment.AssessmentProgress p) => Set(s => s.AssessmentProgress = p);
}
