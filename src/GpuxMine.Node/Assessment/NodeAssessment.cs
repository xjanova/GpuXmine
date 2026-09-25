using System.Text.Json.Serialization;

namespace GpuxMine.Node.Assessment;

/// <summary>What the node can do with one kind of work, measured rather than claimed.</summary>
public sealed class Capability
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("canRun")] public bool CanRun { get; set; }

    /// <summary>
    /// Which lane this machine earns for this kind of work: <c>full</c> inside
    /// the normal deadline, <c>slow</c> inside the relaxed one, <c>no</c> when
    /// it cannot do the work at all.
    /// </summary>
    /// <remarks>
    /// A single pass/fail bar threw away every machine that was merely slow,
    /// which on a network built out of home cards is most of them. Missing the
    /// fast lane is a lower score and a job nobody is waiting on, not a closed
    /// door. Only three things still close it: the weights do not fit at all,
    /// the models are not on disk, or the machine is past even the slow lane.
    /// </remarks>
    [JsonPropertyName("lane")] public string Lane { get; set; } = "no";

    /// <summary>Why not — shown to the owner, so "no video work" is never a mystery.</summary>
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    /// <summary>Measured seconds for one unit, where the node was able to run it.</summary>
    [JsonPropertyName("secondsPerUnit")] public double? SecondsPerUnit { get; set; }
    /// <summary>Expected ÷ what the reference card takes for the same unit.</summary>
    [JsonPropertyName("speedFactor")] public double? SpeedFactor { get; set; }

    /// <summary>"measured" once this node's own ledger has enough of these jobs; "estimated" until then.</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "estimated";

    /// <summary>
    /// This lane was given on trust, not earned: the machine has not yet run
    /// enough jobs of this kind for its own ledger to have an opinion.
    /// </summary>
    /// <remarks>
    /// A new node starts at the top bar and is graded down by what it actually
    /// does, rather than being sorted on the day it arrives by an extrapolation
    /// from a three-second blur. The estimate is a guess about hardware; the
    /// ledger is a fact about this machine.
    /// </remarks>
    [JsonPropertyName("provisional")] public bool Provisional { get; set; }
}

/// <summary>
/// A node's capability report: the thing that has to exist before it may be
/// sent any work at all.
/// </summary>
/// <remarks>
/// <para>
/// Everything in here is <b>reported by the node</b>, which is not trusted. It
/// is a declaration, not proof. The pool checks it for plausibility (a card
/// that claims a 4090 and benchmarks like a 1060 is lying about one of the
/// two), and the canary jobs in M5 are what actually verify output. What this
/// buys today is the thing that was missing entirely: a node can no longer be
/// handed work its card cannot do.
/// </para>
/// <para>
/// Measured, not assumed. On the machine this was written on, SDXL at 768²
/// took 279 s against a ~12 s reference — a node that reported "8 GB, NVIDIA,
/// yes please" without a timing would have been sent image work and made a
/// customer wait five minutes for something a proper card does in ten seconds.
/// </para>
/// </remarks>
public sealed class NodeAssessment
{
    /// <summary>
    /// Bumped when the probes or the scoring change, so old reports are re-run
    /// rather than trusted.
    /// </summary>
    /// <remarks>
    /// 2: the reference workload got heavier (the first one was 0.24 s of
    /// overhead on a 1.0 s measurement and could not tell two cards apart), and
    /// the deadline model replaced an abstract speed factor. A version 1 report
    /// is not comparable with a version 2 one, so it is re-measured.
    ///
    /// 3: audio was missing entirely. Three of the five models the platform
    /// actually dispatches are music, and no node could ever be matched to one
    /// because none of them said they could do it.
    ///
    /// 4: the report says nothing about whether the host can keep the card fed.
    /// A power-capped card and a host that has been losing power under load are
    /// both invisible to a timing, and both change what the timing means.
    ///
    /// 5: lanes are no longer handed out by extrapolating a three-second blur.
    /// A node starts every kind of work at the top bar and is graded by its own
    /// finished jobs, so a version 4 verdict was reached a different way and is
    /// not comparable with a version 5 one.
    /// </remarks>
    public const int SchemaVersion = 5;

    [JsonPropertyName("schemaVersion")] public int Version { get; set; } = SchemaVersion;
    [JsonPropertyName("agentVersion")] public string AgentVersion { get; set; } = "";
    [JsonPropertyName("measuredAt")] public DateTimeOffset MeasuredAt { get; set; }

    // --- what the card is, from torch via ComfyUI rather than from us ---
    [JsonPropertyName("gpuName")] public string? GpuName { get; set; }
    [JsonPropertyName("vramTotalMb")] public int VramTotalMb { get; set; }
    [JsonPropertyName("driver")] public string? Driver { get; set; }
    [JsonPropertyName("torchVersion")] public string? TorchVersion { get; set; }
    [JsonPropertyName("comfyVersion")] public string? ComfyVersion { get; set; }
    [JsonPropertyName("ramTotalMb")] public int RamTotalMb { get; set; }

    /// <summary>
    /// ComfyUI is running with --lowvram/--novram/--cpu, read from its own argv.
    /// </summary>
    /// <remarks>
    /// The single biggest thing a compute benchmark cannot see. Measured on the
    /// card this was written on: a 768² SDXL image took 279 s under --lowvram,
    /// against the ~24 s the blur benchmark alone would have predicted.
    /// </remarks>
    [JsonPropertyName("lowVram")] public bool LowVram { get; set; }

    /// <summary>Ties this report to the machine it was measured on — a report cannot be moved to a slower PC.</summary>
    [JsonPropertyName("hardwareHash")] public string? HardwareHash { get; set; }

    /// <summary>
    /// Ties this report to the card it was measured on: <see cref="GpuHashOf"/>
    /// of the name and memory torch reported. Null on reports from before it
    /// existed, which are then held to <see cref="HardwareHash"/> alone.
    /// </summary>
    /// <remarks>
    /// <see cref="HardwareHash"/> is built from the CPU and the board, so a
    /// card swapped in the same PC — up or down — kept its old report, and its
    /// old lanes, for up to a month.
    /// </remarks>
    [JsonPropertyName("gpuHash")] public string? GpuHash { get; set; }

    /// <summary>
    /// A stable hash of a card as torch names it, or null when there is no
    /// card to name.
    /// </summary>
    /// <remarks>
    /// torch's device name carries more than the card —
    /// <c>cuda:0 NVIDIA GeForce GTX 1070 Ti : cudaMallocAsync</c> — and the
    /// allocator suffix changes with ComfyUI's launch flags. Only the model
    /// name is kept, so a flag change is not mistaken for a new card. Memory is
    /// rounded to half a gigabyte for the same reason: the figure is the card's,
    /// but not every driver reports it to the byte.
    /// </remarks>
    public static string? GpuHashOf(string? torchName, int vramMb)
    {
        if (string.IsNullOrWhiteSpace(torchName) || vramMb <= 0) return null;

        string name = torchName.Trim();
        if (name.StartsWith("cuda:", StringComparison.OrdinalIgnoreCase) && name.IndexOf(' ') is > 0 and var space)
            name = name[(space + 1)..];
        if (name.IndexOf(" : ", StringComparison.Ordinal) is > 0 and var allocator)
            name = name[..allocator];

        string key = $"{name.Trim().ToLowerInvariant()}|{Math.Round(vramMb / 512.0)}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))
            .ToLowerInvariant()[..32];
    }

    // --- whether the host can keep the card fed ---

    /// <summary>Watts the card is allowed to draw, and what it is rated for. 0 when unread.</summary>
    /// <remarks>
    /// The card this client was built on was held at 90 W against a 180 W
    /// rating. Every number ever measured on it is therefore a half-power
    /// number — the benchmark is still right about what this machine will do,
    /// but it says nothing about what the model of card can do, and the two had
    /// been confused. Recording both makes a slow score explainable instead of
    /// mysterious, and tells the owner about a setting they may not know is on.
    /// </remarks>
    [JsonPropertyName("powerLimitW")] public int PowerLimitW { get; set; }

    /// <inheritdoc cref="PowerLimitW"/>
    [JsonPropertyName("powerDefaultW")] public int PowerDefaultW { get; set; }

    /// <summary>Times this host lost power without shutting down, in the week before the assessment.</summary>
    /// <remarks>
    /// A node that dies mid-render loses the customer's job and the owner's
    /// payout, and until this field existed nothing recorded that it had
    /// happened. It does not disqualify a machine on its own — someone may
    /// simply have pulled the plug — which is why it is reported rather than
    /// enforced.
    /// </remarks>
    [JsonPropertyName("hardShutdowns")] public int HardShutdowns { get; set; }

    /// <inheritdoc cref="HardShutdowns"/>
    [JsonPropertyName("lastHardShutdown")] public DateTimeOffset? LastHardShutdown { get; set; }

    /// <summary>
    /// Things that are true and worth saying out loud, but are not a reason to
    /// refuse work. Shown to the owner in their own language.
    /// </summary>
    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = [];

    /// <summary>The card is held below what it was built to draw, so its timings are not this model's timings.</summary>
    [JsonIgnore]
    public bool PowerCapped =>
        PowerLimitW > 0 && PowerDefaultW > 0 && PowerLimitW < PowerDefaultW * 0.95;

    /// <summary>Share of the factory power rating this card is allowed, as a percentage. 0 when unread.</summary>
    [JsonIgnore]
    public int PowerPct =>
        PowerLimitW > 0 && PowerDefaultW > 0
            ? (int)Math.Round(PowerLimitW * 100.0 / PowerDefaultW)
            : 0;

    // --- what it actually ran ---
    /// <summary>Seconds for the weight-free reference workload. The one number every node has.</summary>
    [JsonPropertyName("referenceSeconds")] public double ReferenceSeconds { get; set; }
    [JsonPropertyName("capabilities")] public List<Capability> Capabilities { get; set; } = [];

    // --- what it has on disk ---
    [JsonPropertyName("checkpoints")] public List<string> Checkpoints { get; set; } = [];
    [JsonPropertyName("upscalers")] public List<string> Upscalers { get; set; } = [];
    [JsonPropertyName("diffusionModels")] public List<string> DiffusionModels { get; set; } = [];

    // --- the verdict ---
    [JsonPropertyName("score")] public int Score { get; set; }
    [JsonPropertyName("tier")] public string Tier { get; set; } = "unrated";
    [JsonPropertyName("failed")] public string? Failed { get; set; }

    /// <summary>
    /// When the owner last sent this machine back to a maximum assessment. Null
    /// when grading has always counted every job, which is the normal case.
    /// </summary>
    /// <remarks>
    /// Kept on the report so the Benchmark screen can say the lanes are back at
    /// the top on purpose, rather than leaving a node that was "slow" yesterday
    /// and "full" today looking like it changed its mind on its own.
    /// </remarks>
    [JsonPropertyName("gradingSince")] public DateTimeOffset? GradingSince { get; set; }

    /// <summary>
    /// Assessments go stale: drivers change, other software starts competing
    /// for the card, a GPU gets swapped. A month is long enough not to be a
    /// nuisance and short enough that the pool is not dispatching on a year-old
    /// measurement.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    /// <param name="gpuHash">
    /// The card in the machine now, as <see cref="GpuHashOf"/> hashes it. Null
    /// when nobody could ask — ComfyUI not answering says nothing about the
    /// card, and must not throw away a good report.
    /// </param>
    public bool IsUsable(string agentVersion, string? hardwareHash, string? gpuHash = null) =>
        Failed is null
        && Version == SchemaVersion
        && DateTimeOffset.UtcNow - MeasuredAt < MaxAge
        // A new build may measure differently; re-run rather than carry the old
        // number forward under a version it was not taken on.
        && AgentVersion == agentVersion
        // Catches the report being copied onto another machine.
        && (hardwareHash is null || HardwareHash is null || HardwareHash == hardwareHash)
        // And the card being swapped under a node that was already enrolled.
        && !GpuChanged(gpuHash);

    /// <summary>The card now is known, the report names one, and they differ.</summary>
    public bool GpuChanged(string? gpuHash) => gpuHash is not null && GpuHash is not null && GpuHash != gpuHash;

    public Capability? For(string kind) => Capabilities.FirstOrDefault(c => c.Kind == kind);

    /// <summary>One line for the log and the admin list.</summary>
    public string Summary() =>
        Failed is not null
            ? $"assessment failed: {Failed}"
            : $"{GpuName ?? "GPU"} · {VramTotalMb / 1024.0:0.#} GB · score {Score} · tier {Tier} · " +
              $"{Capabilities.Count(c => c.Lane == "full")} เต็มความเร็ว + " +
              $"{Capabilities.Count(c => c.Lane == "slow")} ไม่เร่ง / {Capabilities.Count} งาน" +
              (PowerCapped ? $" · ไฟ {PowerPct}%" : "");
}
