using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GpuxMine.Node.Assessment;

/// <summary>
/// Runs the capability assessment: what this card is, how fast it really is,
/// and which kinds of work it may therefore be sent.
/// </summary>
/// <remarks>
/// <para>
/// The reference workload uses no model weights at all — a large image through
/// six wide Gaussian blurs. That matters for three reasons: every node can run
/// it on a fresh install, it is real GPU convolution rather than pipeline
/// overhead, and a random fill colour means ComfyUI can never answer it from
/// the cache it keeps for repeated prompts.
/// </para>
/// <para>
/// Sized by measurement, not by taste. The first version blurred a 1536² image
/// twice and took 1.0 s on the GTX 1070 Ti this was written on — of which
/// about 0.24 s is HTTP, JSON and PNG encoding, so it would have scored a
/// 4090 and a 1070 Ti almost the same. At 2048² and six blurs the same card
/// takes 3.0 s, over 90% of it GPU work, and the timings scale linearly with
/// the work (0.70 s / 1.21 s / 2.02 s / 3.03 s as the workload grows), which is
/// what makes the number worth storing.
/// </para>
/// </remarks>
public sealed class Assessor(NodeOptions options, ILoggerish log, Storage.NodeStore? history = null) : IDisposable
{
    /// <summary>Pixels per side of the reference image.</summary>
    private const int ReferenceSize = 2048;

    /// <summary>How many blur passes the reference image goes through.</summary>
    private const int ReferenceBlurs = 6;

    /// <summary>
    /// What the reference workload takes on the card the tiers are drawn
    /// against — roughly an RTX 3060. Change it and every tier in the fleet
    /// moves, so it is a published number, not a tuning knob.
    /// </summary>
    public const double ReferenceBaselineSeconds = 1.5;

    /// <summary>
    /// How much slower a job whose weights do not fit gets when ComfyUI is
    /// running in <c>--lowvram</c> or <c>--novram</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a guess, and not a blanket penalty. Two measurements on the same
    /// 1070 Ti, on the same <c>--lowvram</c> ComfyUI, on the same day:
    /// </para>
    /// <list type="bullet">
    ///   <item>SDXL, a 6.46 GB checkpoint on an 8 GB card: <b>279 s</b> against
    ///     the ~24 s the blur benchmark alone predicts — 85 s per step, because
    ///     the weights cross the PCIe bus on every one of them.</item>
    ///   <item>Wan 2.1 T2V 1.3B, which fits in 5.7 GB: <b>185 s</b> against a
    ///     predicted 194 s. No penalty at all.</item>
    /// </list>
    /// <para>
    /// So the cost is not "lowvram is on", it is "the weights do not fit". It
    /// is charged to the kind of work where that is the normal case and left
    /// off the kind where the measurement says it is not.
    /// </para>
    /// </remarks>
    public const double LowVramPenalty = 10.0;

    /// <summary>
    /// How far past the deadline an <i>unproven</i> estimate must fall before
    /// it refuses work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three levels of confidence, and the benefit of the doubt shrinks as the
    /// evidence gets better. A median from this node's own ledger is a fact and
    /// is held to the deadline exactly. So is an estimate carrying
    /// <see cref="LowVramPenalty"/>, because that penalty was calibrated
    /// against a direct measurement of the very failure mode it predicts. Only
    /// a plain extrapolation from the blur benchmark gets this margin, because
    /// a prediction that is 30% pessimistic would otherwise cost the owner
    /// every job of that kind for a month.
    /// </para>
    /// <para>
    /// The distinction is not academic: on the card this was written on the
    /// penalised estimate for an image job came out at 246 s against a 120 s
    /// deadline. At a flat 2x margin that is 246 against a limit of 240 — six
    /// seconds from admitting a machine that really takes 279 s.
    /// </para>
    /// </remarks>
    public const double EstimateMargin = 2.0;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly Random _random = new();

    /// <summary>
    /// What each kind of work needs, what the reference card takes for one
    /// unit, and how long a customer will actually wait for it.
    /// </summary>
    /// <remarks>
    /// The deadline is the honest gate, and it is what the owner is told. "Your
    /// card is 23x the reference" means nothing to anyone; "an image job has to
    /// be done inside two minutes and this machine needs about 279 seconds"
    /// is a sentence a node owner can act on — by adding VRAM, by dropping
    /// <c>--lowvram</c>, or by accepting that this machine does upscales.
    /// </remarks>
    private static readonly (string Kind, int MinVramMb, double BaselineSeconds, double DeadlineSeconds, string Needs, bool WeightsSpill)[] Kinds =
    [
        ("upscale", 2_048, 2, 60, "upscale-model", false),
        // เพลง — หมวดที่ใหญ่ที่สุดในแคตตาล็อกจริง (3 จาก 5 โมเดล) และเป็นหมวด
        // ที่การ์ดบ้านมีโอกาสที่สุด: ACE-Step โหลดไฟล์รวม 10 GB ก็จริง แต่ชิ้น
        // ใหญ่สุดที่ต้องอยู่ใน VRAM พร้อมกันคือ 4.79 GB — text encoder ทำงาน
        // ก่อนแล้วถูก offload ไม่ได้อยู่พร้อมกันทั้งหมด
        //
        // คนรอเพลงได้นานกว่ารอภาพ แต่ไม่นานเท่ารอวิดีโอ
        ("audio", 6_144, 30, 600, "diffusion-model", false),
        // An SDXL-class checkpoint is 6-7 GB, which is what spills on the cards
        // most of this network will be built from.
        ("image", 6_144, 12, 120, "checkpoint", true),
        // The video models an 8 GB card can actually run are small — Wan 1.3B is
        // 2.5 GB and stays resident, which is why it was measured at the
        // unpenalised estimate rather than ten times it.
        ("video", 6_144, 90, 900, "diffusion-model", false),
        ("embed", 2_048, 1, 30, "checkpoint", false),
    ];

    public async Task<NodeAssessment> RunAsync(string agentVersion, string? hardwareHash, string? driver, CancellationToken ct)
    {
        var assessment = new NodeAssessment
        {
            AgentVersion = agentVersion,
            HardwareHash = hardwareHash,
            Driver = driver,
            MeasuredAt = DateTimeOffset.UtcNow,
        };

        try
        {
            await ReadSystemAsync(assessment, ct);
            await ReadInventoryAsync(assessment, ct);
            assessment.ReferenceSeconds = await TimeReferenceWorkloadAsync(ct);
            Score(assessment, history);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A node that cannot be assessed is not a node that gets work. The
            // failure is recorded so the owner can see why rather than sitting
            // at "connected, earning nothing" with no explanation.
            assessment.Failed = ex.Message;
            log.Warn($"[gpu] assessment failed: {ex.Message}");
        }

        return assessment;
    }

    private async Task ReadSystemAsync(NodeAssessment a, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"{options.ComfyUrl.TrimEnd('/')}/system_stats", ct);
        response.EnsureSuccessStatusCode();

        JsonNode root = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))
            ?? throw new InvalidOperationException("ComfyUI returned no system stats");

        a.ComfyVersion = root["system"]?["comfyui_version"]?.GetValue<string>();
        a.TorchVersion = root["system"]?["pytorch_version"]?.GetValue<string>();
        a.RamTotalMb = (int)((root["system"]?["ram_total"]?.GetValue<long>() ?? 0) / 1024 / 1024);

        // ComfyUI reports its own command line, which is where the one flag
        // that dominates render time on a small card is visible.
        if (root["system"]?["argv"] is JsonArray argv)
        {
            a.LowVram = argv.Any(v =>
                v?.GetValue<string>() is "--lowvram" or "--novram" or "--cpu");
        }

        // torch's own view of the card, not ours: it is what will actually be
        // asked to hold the weights, and it already accounts for the driver.
        JsonNode? device = root["devices"]?[0];
        a.GpuName = device?["name"]?.GetValue<string>();
        a.VramTotalMb = (int)((device?["vram_total"]?.GetValue<long>() ?? 0) / 1024 / 1024);

        if (a.VramTotalMb <= 0)
            throw new InvalidOperationException("no CUDA device reported — this node has no usable GPU");
    }

    private async Task ReadInventoryAsync(NodeAssessment a, CancellationToken ct)
    {
        a.Checkpoints = await OptionsOfAsync("CheckpointLoaderSimple", "ckpt_name", ct);
        a.Upscalers = await OptionsOfAsync("UpscaleModelLoader", "model_name", ct);
        a.DiffusionModels = await OptionsOfAsync("UNETLoader", "unet_name", ct);
    }

    private async Task<List<string>> OptionsOfAsync(string nodeClass, string input, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(
                $"{options.ComfyUrl.TrimEnd('/')}/object_info/{nodeClass}", ct);
            if (!response.IsSuccessStatusCode) return [];

            JsonNode? root = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            if (root?[nodeClass]?["input"]?["required"]?[input] is not JsonArray slot) return [];
            if (slot.Count == 0 || slot[0] is not JsonArray choices) return [];

            return choices.Select(c => c?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList();
        }
        catch
        {
            // A missing loader class just means this node cannot do that kind
            // of work; it is not a reason to fail the whole assessment.
            return [];
        }
    }

    /// <summary>
    /// Times the weight-free reference workload, twice, keeping the faster run.
    /// </summary>
    /// <remarks>
    /// Twice because the first pass pays for CUDA context creation and kernel
    /// compilation, which is a one-off cost of the measurement rather than a
    /// property of the card — measured here as 1.91 s against 1.01 s on the
    /// identical second run. Scoring a node on its cold start would rate every
    /// machine by how recently it was restarted.
    /// </remarks>
    private async Task<double> TimeReferenceWorkloadAsync(CancellationToken ct)
    {
        double best = double.MaxValue;

        for (int pass = 0; pass < 2; pass++)
        {
            var sw = Stopwatch.StartNew();
            await RunGraphAsync(ReferenceGraph(_random.Next(0, 0xFFFFFF)), ct);
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalSeconds);
        }

        return Math.Round(best, 2);
    }

    /// <summary>
    /// A large canvas, blurred hard several times, then shrunk before it is written.
    /// </summary>
    /// <remarks>
    /// Every part of that sentence is load-bearing. The fill colour is random so
    /// ComfyUI's prompt cache can never serve an old result — three identical
    /// upscales in a row is exactly how a job once finished without emitting a
    /// single event. The blur radius is the maximum ComfyUI allows (31), which
    /// makes each pass a 63x63 convolution rather than a memory copy. And the
    /// result is scaled to 256² before SaveImage, so PNG encoding stays a
    /// rounding error instead of becoming the thing being measured.
    /// </remarks>
    private static string ReferenceGraph(int color)
    {
        // Built as a JSON tree rather than as text. A ComfyUI graph is almost
        // entirely braces, and every attempt to write one as an interpolated
        // raw string turns its `}}` into a compiler error.
        var nodes = new JsonObject
        {
            ["1"] = Node("EmptyImage", new JsonObject
            {
                ["width"] = ReferenceSize,
                ["height"] = ReferenceSize,
                ["batch_size"] = 1,
                ["color"] = color,
            }),
        };

        string previous = "1";
        for (int i = 0; i < ReferenceBlurs; i++)
        {
            string id = (10 + i).ToString(CultureInfo.InvariantCulture);
            nodes[id] = Node("ImageBlur", new JsonObject
            {
                ["image"] = Link(previous),
                ["blur_radius"] = 31,     // ComfyUI's maximum: a 63x63 kernel
                ["sigma"] = 10.0,
            });
            previous = id;
        }

        nodes["90"] = Node("ImageScale", new JsonObject
        {
            ["image"] = Link(previous),
            ["upscale_method"] = "bicubic",
            ["width"] = 256,
            ["height"] = 256,
            ["crop"] = "disabled",
        });
        nodes["99"] = Node("SaveImage", new JsonObject
        {
            ["images"] = Link("90"),
            ["filename_prefix"] = "gpuxmine_assess",
        });

        return new JsonObject
        {
            ["prompt"] = nodes,
            ["client_id"] = "gpuxmine-assessment",
        }.ToJsonString();

        static JsonObject Node(string classType, JsonObject inputs)
            => new() { ["class_type"] = classType, ["inputs"] = inputs };

        static JsonArray Link(string fromNode) => new(fromNode, 0);
    }

    private async Task RunGraphAsync(string graph, CancellationToken ct)
    {
        using var submit = await _http.PostAsync(
            $"{options.ComfyUrl.TrimEnd('/')}/prompt",
            new StringContent(graph, Encoding.UTF8, "application/json"), ct);

        string body = await submit.Content.ReadAsStringAsync(ct);
        if (!submit.IsSuccessStatusCode)
            throw new InvalidOperationException($"reference workload rejected (HTTP {(int)submit.StatusCode}): {body[..Math.Min(200, body.Length)]}");

        string promptId = JsonNode.Parse(body)?["prompt_id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("ComfyUI returned no prompt_id for the reference workload");

        for (int i = 0; i < 600; i++)
        {
            await Task.Delay(250, ct);

            using var historyResponse = await _http.GetAsync(
                $"{options.ComfyUrl.TrimEnd('/')}/history/{Uri.EscapeDataString(promptId)}", ct);
            if (!historyResponse.IsSuccessStatusCode) continue;

            JsonNode? entry = JsonNode.Parse(await historyResponse.Content.ReadAsStringAsync(ct))?[promptId];
            if (entry is null) continue;

            string? status = entry["status"]?["status_str"]?.GetValue<string>();
            if (status == "error") throw new InvalidOperationException("reference workload failed inside ComfyUI");
            if (entry["status"]?["completed"]?.GetValue<bool>() == true || status == "success") return;
        }

        // Two and a half minutes for a workload a mid-range card does in 1.5 s.
        // A machine that cannot finish it is not one to send paid work to.
        throw new TimeoutException("reference workload did not finish in 150 s");
    }

    /// <summary>
    /// Turns the measurements into a score, a tier, and a per-kind verdict.
    /// </summary>
    /// <remarks>
    /// Three gates per kind of work, and a node has to pass all three:
    /// enough VRAM to hold the weights, the models on disk to do it at all,
    /// and an expected time inside the deadline a customer will wait. The last
    /// one is the only one that needs the card to have actually been timed —
    /// and it is the one that stops a willing but hopeless machine from being
    /// handed a job somebody paid for.
    /// </remarks>
    private static void Score(NodeAssessment a, Storage.NodeStore? history)
    {
        double factor = a.ReferenceSeconds > 0 ? a.ReferenceSeconds / ReferenceBaselineSeconds : double.MaxValue;

        // 1000 on a reference card, halving as the card gets twice as slow.
        a.Score = (int)Math.Round(Math.Clamp(1000 / Math.Max(factor, 0.1), 0, 10_000));

        a.Tier = a.Score switch
        {
            >= 1500 => "platinum",
            >= 700 => "gold",
            >= 350 => "silver",
            >= 120 => "bronze",
            _ => "basic",
        };

        foreach (var (kind, minVram, baseline, deadline, needs, weightsSpill) in Kinds)
        {
            bool hasAssets = needs switch
            {
                "upscale-model" => a.Upscalers.Count > 0,
                "checkpoint" => a.Checkpoints.Count > 0,
                "diffusion-model" => a.DiffusionModels.Count > 0,
                _ => true,
            };

            // What this machine has actually done beats what a blur benchmark
            // predicts it will do. The ledger already holds every job's real
            // duration, so once there are a few of a kind, they are the answer.
            double? measured = history?.MedianJobSeconds(kind, minSamples: 3);

            bool penalised = weightsSpill && a.LowVram;
            double expected = measured ?? baseline * factor * (penalised ? LowVramPenalty : 1);

            // Only an unproven extrapolation gets the margin. A measurement, or
            // an estimate carrying the calibrated spill penalty, is held to the
            // deadline itself.
            double limit = measured is null && !penalised ? deadline * EstimateMargin : deadline;

            var capability = new Capability
            {
                Kind = kind,
                SecondsPerUnit = Math.Round(expected, 1),
                SpeedFactor = Math.Round(expected / baseline, 2),
                Source = measured is null ? "estimated" : "measured",
            };

            if (a.VramTotalMb < minVram)
            {
                capability.CanRun = false;
                capability.Reason = $"ต้องการ VRAM {minVram / 1024.0:0.#} GB มี {a.VramTotalMb / 1024.0:0.#} GB";
            }
            else if (!hasAssets)
            {
                capability.CanRun = false;
                capability.Reason = $"ยังไม่มีโมเดลสำหรับงานนี้ ({needs})";
            }
            else if (expected > limit)
            {
                capability.CanRun = false;
                capability.Reason =
                    (measured is null ? "คาดว่าใช้เวลา" : "จากงานจริงที่เคยทำ ใช้เวลา") +
                    $" {expected:0} วินาที/ชิ้น เกินกำหนด {deadline:0} วินาที" +
                    (penalised && measured is null ? " — ComfyUI รันโหมด --lowvram อยู่" : "");
            }
            else
            {
                capability.CanRun = true;
            }

            a.Capabilities.Add(capability);
        }
    }

    public static NodeAssessment? Load(Storage.NodeStore store)
    {
        try
        {
            string? json = store.GetSetting("assessment");
            return json is { Length: > 0 } ? JsonSerializer.Deserialize<NodeAssessment>(json) : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(Storage.NodeStore store, NodeAssessment assessment)
        => store.SetSetting("assessment", JsonSerializer.Serialize(assessment));

    public void Dispose() => _http.Dispose();
}
