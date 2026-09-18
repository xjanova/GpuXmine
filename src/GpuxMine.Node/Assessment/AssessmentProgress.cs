namespace GpuxMine.Node.Assessment;

/// <summary>One stage of the assessment as the owner sees it.</summary>
/// <param name="Key">Stable id, for the UI to key rows off rather than the Thai label.</param>
/// <param name="Label">What is being measured, in the owner's language.</param>
/// <param name="Percent">0–100 through this stage.</param>
/// <param name="Status">pending · running · done</param>
public sealed record AssessmentStep(string Key, string Label, int Percent, string Status);

/// <summary>
/// How far the assessment has got, stage by stage and overall.
/// </summary>
/// <remarks>
/// The assessment takes ten to fifteen seconds on a healthy machine and can
/// take minutes on a slow one, and until now it showed a single word —
/// "กำลังประเมินเครื่อง…" — for the whole of it. An owner watching that has no
/// way to tell a machine that is working from one that has hung, which is
/// exactly the moment they decide the client is broken and close it.
/// </remarks>
/// <param name="Steps">Every stage, in order, including the ones not started yet.</param>
/// <param name="Current">Label of the stage running now, empty when finished.</param>
/// <param name="OverallPercent">0–100 across all stages, weighted by how long each really takes.</param>
public sealed record AssessmentProgress(
    IReadOnlyList<AssessmentStep> Steps,
    string Current,
    int OverallPercent)
{
    public static readonly AssessmentProgress None = new([], "", 0);
}

/// <summary>
/// Tracks the stages and publishes a snapshot whenever one moves.
/// </summary>
/// <remarks>
/// The weights are how long each stage actually takes, not how important it
/// is: the two benchmark passes are the better part of the wall clock, and a
/// bar that gave the six stages a sixth each would sit at 50% for one second
/// and at 83% for ten.
/// </remarks>
internal sealed class AssessmentProgressTracker(IProgress<AssessmentProgress>? sink)
{
    private static readonly (string Key, string Label, double Weight)[] Plan =
    [
        ("spec", "อ่านสเปคเครื่องและการ์ดจอ", 6),
        ("models", "สำรวจโมเดลที่มีในเครื่อง", 10),
        ("power", "อ่านเพดานไฟและประวัติการดับ", 6),
        ("warmup", "วัดการ์ดจอ · รอบอุ่นเครื่อง", 34),
        ("measure", "วัดการ์ดจอ · รอบวัดจริง", 34),
        ("score", "สรุปคะแนนและงานที่รับได้", 10),
    ];

    private static readonly double TotalWeight = Plan.Sum(p => p.Weight);

    private readonly int[] _percent = new int[Plan.Length];
    private int _running = -1;

    public void Begin(string key)
    {
        _running = IndexOf(key);
        Publish();
    }

    /// <param name="percent">
    /// Never goes backwards, and never reaches 100 — only <see cref="Finish"/>
    /// does that, because a stage that shows complete while it is still running
    /// is worse than one that sits at 95.
    /// </param>
    public void At(string key, int percent)
    {
        int i = IndexOf(key);
        if (i < 0) return;

        int next = Math.Clamp(percent, 0, 95);
        if (next <= _percent[i]) return;

        _percent[i] = next;
        Publish();
    }

    public void Finish(string key)
    {
        int i = IndexOf(key);
        if (i < 0) return;

        _percent[i] = 100;
        if (_running == i) _running = -1;
        Publish();
    }

    /// <summary>
    /// Everything at 100, whatever it reached. Called on the way out, including
    /// after a failure — a bar frozen at 61% with nothing running is the same
    /// dead-looking screen this was built to remove.
    /// </summary>
    public void Done()
    {
        for (int i = 0; i < _percent.Length; i++) _percent[i] = 100;
        _running = -1;
        Publish();
    }

    private static int IndexOf(string key)
    {
        for (int i = 0; i < Plan.Length; i++)
            if (Plan[i].Key == key) return i;
        return -1;
    }

    private void Publish()
    {
        if (sink is null) return;

        var steps = new AssessmentStep[Plan.Length];
        double done = 0;

        for (int i = 0; i < Plan.Length; i++)
        {
            done += Plan[i].Weight * _percent[i] / 100.0;
            steps[i] = new AssessmentStep(
                Plan[i].Key,
                Plan[i].Label,
                _percent[i],
                _percent[i] >= 100 ? "done" : i == _running ? "running" : "pending");
        }

        sink.Report(new AssessmentProgress(
            steps,
            _running >= 0 ? Plan[_running].Label : "",
            (int)Math.Round(done / TotalWeight * 100)));
    }
}
