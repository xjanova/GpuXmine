using System.Text.Json.Serialization;

namespace GpuxMine.Node.Assessment;

/// <summary>What the node can do with one kind of work, measured rather than claimed.</summary>
public sealed class Capability
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("canRun")] public bool CanRun { get; set; }
    /// <summary>Why not — shown to the owner, so "no video work" is never a mystery.</summary>
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    /// <summary>Measured seconds for one unit, where the node was able to run it.</summary>
    [JsonPropertyName("secondsPerUnit")] public double? SecondsPerUnit { get; set; }
    /// <summary>Expected ÷ what the reference card takes for the same unit.</summary>
    [JsonPropertyName("speedFactor")] public double? SpeedFactor { get; set; }

    /// <summary>"measured" once this node's own ledger has enough of these jobs; "estimated" until then.</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "estimated";
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
    /// </remarks>
    public const int SchemaVersion = 3;

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
    /// Assessments go stale: drivers change, other software starts competing
    /// for the card, a GPU gets swapped. A month is long enough not to be a
    /// nuisance and short enough that the pool is not dispatching on a year-old
    /// measurement.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    public bool IsUsable(string agentVersion, string? hardwareHash) =>
        Failed is null
        && Version == SchemaVersion
        && DateTimeOffset.UtcNow - MeasuredAt < MaxAge
        // A new build may measure differently; re-run rather than carry the old
        // number forward under a version it was not taken on.
        && AgentVersion == agentVersion
        // Catches the report being copied onto another machine, and the card
        // being swapped under a node that was already enrolled.
        && (hardwareHash is null || HardwareHash is null || HardwareHash == hardwareHash);

    public Capability? For(string kind) => Capabilities.FirstOrDefault(c => c.Kind == kind);

    /// <summary>One line for the log and the admin list.</summary>
    public string Summary() =>
        Failed is not null
            ? $"assessment failed: {Failed}"
            : $"{GpuName ?? "GPU"} · {VramTotalMb / 1024.0:0.#} GB · score {Score} · tier {Tier} · " +
              $"{Capabilities.Count(c => c.CanRun)}/{Capabilities.Count} job kinds";
}
