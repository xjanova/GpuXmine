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
/// <para>
/// It renders into the owner's own ComfyUI, so what it leaves there is
/// cleared away after every run — see <paramref name="runtime"/>.
/// </para>
/// </remarks>
/// <param name="runtime">
/// Clears the benchmark's images out of the owner's output folder, and its
/// prompts out of ComfyUI's history, once a run is over. Without one they
/// stay, as they did before.
/// </param>
public sealed class Assessor(
    NodeOptions options,
    ILoggerish log,
    Storage.NodeStore? history = null,
    IHostHealthSource? health = null,
    IProgress<AssessmentProgress>? progress = null,
    ComfyRuntime? runtime = null) : IDisposable
{
    /// <summary>
    /// What the benchmark's SaveImage names its files by. ComfyUI writes
    /// <c>{prefix}_{counter}_.png</c>, and the clean-up deletes nothing whose
    /// name does not start with it.
    /// </summary>
    public const string OutputPrefix = "gpuxmine_assess";

    private readonly AssessmentProgressTracker _steps = new(progress);

    /// <summary>
    /// How far back an unexpected power loss counts against a node.
    /// </summary>
    /// <remarks>
    /// Long enough to catch a machine that is failing under load, short enough
    /// that one bad evening — a tripped breaker, a plug pulled — does not
    /// follow the owner around for a month. It clears itself.
    /// </remarks>
    public static readonly TimeSpan HardShutdownWindow = TimeSpan.FromDays(7);

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
    /// How many real jobs of a kind this node must have finished before its own
    /// ledger is allowed to overrule the estimate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole grading model turns on this. Until a node has run this many,
    /// it keeps the top lane on trust; from here on it is graded on its median,
    /// up as well as down. Three is the smallest number that has a median at
    /// all, and one slow job — a cold cache, the owner opening a game — should
    /// not cost a machine its lane.
    /// </para>
    /// <para>
    /// The estimate does not gate anything any more, which is the point: it is
    /// an extrapolation from a three-second blur, and on the card this was
    /// written on it predicted 246 s for an image job that really took 279 s.
    /// Close, and still no substitute for having run one.
    /// </para>
    /// </remarks>
    public const int MinSamples = 3;

    private readonly HttpClient _http = Core.Net.NodeHttp.Create(TimeSpan.FromMinutes(5));
    private readonly Random _random = new();

    /// <summary>
    /// Every prompt this run has sent, with when — kept here, not in the
    /// runtime, until the run is over.
    /// </summary>
    private readonly List<(string PromptId, DateTimeOffset SentAt)> _sent = [];

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
    /// <remarks>
    /// <para>
    /// Every deadline comes in two: the one a customer waiting on the page will
    /// accept, and the one a queued job nobody is watching will accept. A
    /// machine that misses the first and makes the second is not turned away —
    /// it is put in the slow lane and scored lower, which is the whole point of
    /// a network made of other people's home PCs.
    /// </para>
    /// <para>
    /// The VRAM numbers are floors for running at all, not for running well.
    /// They were 6 GB across the board and that alone disqualified the card
    /// this client was built on from three of the five kinds of work — while
    /// the same card had already been measured finishing an SDXL image and a
    /// Wan video. A floor should mean "cannot", and 6 GB did not.
    /// </para>
    /// </remarks>
    private static readonly (string Kind, int MinVramMb, double BaselineSeconds, double DeadlineSeconds, double SlowDeadlineSeconds, string Needs, bool WeightsSpill)[] Kinds =
    [
        ("upscale", 2_048, 2, 60, 240, "upscale-model", false),
        // เพลง — หมวดที่ใหญ่ที่สุดในแคตตาล็อกจริง (3 จาก 5 โมเดล) และเป็นหมวด
        // ที่การ์ดบ้านมีโอกาสที่สุด: ACE-Step โหลดไฟล์รวม 10 GB ก็จริง แต่ชิ้น
        // ใหญ่สุดที่ต้องอยู่ใน VRAM พร้อมกันคือ 4.79 GB — text encoder ทำงาน
        // ก่อนแล้วถูก offload ไม่ได้อยู่พร้อมกันทั้งหมด
        //
        // คนรอเพลงได้นานกว่ารอภาพ แต่ไม่นานเท่ารอวิดีโอ
        //
        // 4.79 GB is the largest single piece ACE-Step needs resident, so the
        // floor is just above it rather than at the 10 GB the whole download
        // weighs. Half an hour is a long wait, but a track nobody is sitting in
        // front of is worth more finished late than not made at all.
        ("audio", 5_120, 30, 600, 1_800, "diffusion-model", false),
        // An SDXL-class checkpoint is 6-7 GB, which is what spills on the cards
        // most of this network will be built from. It spills — it does not
        // fail: the measured 279 s on an 8 GB card is a real image, delivered.
        ("image", 4_096, 12, 120, 360, "checkpoint", true),
        // The video models an 8 GB card can actually run are small — Wan 1.3B is
        // 2.5 GB and stays resident, which is why it was measured at the
        // unpenalised estimate rather than ten times it.
        ("video", 5_120, 90, 900, 2_700, "diffusion-model", false),
        ("embed", 2_048, 1, 30, 120, "checkpoint", false),
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
            _steps.Begin("spec");
            await ReadSystemAsync(assessment, ct);
            // The card this report is about, so a swap voids it the way a new
            // machine does — see NodeAssessment.GpuHash.
            assessment.GpuHash = NodeAssessment.GpuHashOf(assessment.GpuName, assessment.VramTotalMb);
            _steps.Finish("spec");

            _steps.Begin("models");
            await ReadInventoryAsync(assessment, ct);
            _steps.Finish("models");

            _steps.Begin("power");
            ReadHostHealth(assessment);
            _steps.Finish("power");

            assessment.ReferenceSeconds = await TimeReferenceWorkloadAsync(ct);

            _steps.Begin("score");
            Score(assessment, history);
            _steps.Finish("score");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A node that cannot be assessed is not a node that gets work. The
            // failure is recorded so the owner can see why rather than sitting
            // at "connected, earning nothing" with no explanation.
            assessment.Failed = ex.Message;
            log.Warn($"[gpu] assessment failed: {ex.Message}");
        }
        finally
        {
            // Including on the way out of a failure: a bar stopped partway with
            // nothing running looks exactly like the hang this was built to
            // rule out. The report itself says whether it worked.
            _steps.Done();

            // Worked, failed or cancelled, what it wrote is not the owner's to
            // keep. A pass still rendering — one that ran out the clock — is
            // left to the node's reconcile loop, which clears it once it ends.
            await ClearRendersAsync(ct);
        }

        return assessment;
    }

    /// <summary>
    /// How long the run waits on its own clean-up. Past it, what is left is
    /// the reconcile loop's to clear; the report is what this run is for.
    /// </summary>
    private static readonly TimeSpan ClearBudget = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long it gets when the run was cancelled — the node closing, which
    /// waits three seconds for its loops before it lets ComfyUI's client go.
    /// </summary>
    private static readonly TimeSpan ClearBudgetOnCancel = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Hands the prompts this run sent to the runtime, and clears their images
    /// and history entries out of the owner's ComfyUI. Never throws.
    /// </summary>
    private async Task ClearRendersAsync(CancellationToken ct)
    {
        if (runtime is null) return;

        // Only now, with the run over: see ComfyRuntime.NoteBenchmark.
        foreach (var (promptId, sentAt) in _sent) runtime.NoteBenchmark(promptId, sentAt);
        _sent.Clear();

        using var budget = ct.IsCancellationRequested
            ? new CancellationTokenSource(ClearBudgetOnCancel)
            : CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!ct.IsCancellationRequested) budget.CancelAfter(ClearBudget);

        try
        {
            await runtime.PurgeBenchmarksAsync(budget.Token);
        }
        catch (OperationCanceledException)
        {
            // Out of time: the reconcile loop takes what is left.
        }
        catch (Exception ex)
        {
            // A file left behind is not a reason to lose the report.
            log.Warn($"[gpu] ล้างภาพจากการประเมินเครื่องออกจาก ComfyUI ไม่สำเร็จ: {ex.Message}");
        }
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

    /// <summary>
    /// What the card is allowed to draw and whether this host has been dying
    /// under load — read before the benchmark, so the timing that follows can
    /// be read in the light of both.
    /// </summary>
    /// <remarks>
    /// Taken outside the try/catch that fails an assessment. A host that will
    /// not say whether it has been losing power is a host we know nothing
    /// about, which is the same as a brand new one; it is not a broken node.
    /// </remarks>
    private void ReadHostHealth(NodeAssessment a)
    {
        HostHealth reading;
        try
        {
            reading = health?.Read(HardShutdownWindow) ?? HostHealth.Unknown;
        }
        catch (Exception ex)
        {
            log.Warn($"[gpu] could not read host health: {ex.Message}");
            reading = HostHealth.Unknown;
        }

        a.PowerLimitW = reading.PowerLimitW;
        a.PowerDefaultW = reading.PowerDefaultW;
        a.HardShutdowns = reading.HardShutdowns;
        a.LastHardShutdown = reading.LastHardShutdown;
    }

    private async Task ReadInventoryAsync(NodeAssessment a, CancellationToken ct)
    {
        a.Checkpoints = await OptionsOfAsync("CheckpointLoaderSimple", "ckpt_name", ct);
        _steps.At("models", 33);
        a.Upscalers = await OptionsOfAsync("UpscaleModelLoader", "model_name", ct);
        _steps.At("models", 66);
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

        // Nothing is known about this card yet, so the first bar is paced
        // against a card slower than most. The second is paced against what
        // this machine has just been seen to do, which makes it honest.
        double expected = 6.0;

        for (int pass = 0; pass < 2; pass++)
        {
            string step = pass == 0 ? "warmup" : "measure";
            _steps.Begin(step);

            var sw = Stopwatch.StartNew();
            await RunGraphAsync(
                _random.Next(0, 0xFFFFFF),
                ct,
                elapsed => _steps.At(step, (int)(elapsed.TotalSeconds / expected * 100)));
            sw.Stop();

            _steps.Finish(step);

            expected = Math.Max(sw.Elapsed.TotalSeconds, 0.5);
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
    /// <param name="promptId">
    /// The id the node chose for it, so the clean-up knows it even when
    /// ComfyUI's answer never arrives. A ComfyUI too old to take a client's id
    /// answers with its own.
    /// </param>
    private static string ReferenceGraph(int color, string promptId)
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
            ["filename_prefix"] = OutputPrefix,
        });

        return new JsonObject
        {
            ["prompt"] = nodes,
            ["prompt_id"] = promptId,
            ["client_id"] = "gpuxmine-assessment",
        }.ToJsonString();

        static JsonObject Node(string classType, JsonObject inputs)
            => new() { ["class_type"] = classType, ["inputs"] = inputs };

        static JsonArray Link(string fromNode) => new(fromNode, 0);
    }

    /// <param name="onElapsed">
    /// Called on every poll with how long this graph has been running, so the
    /// caller can pace a bar. ComfyUI only sends per-node progress to the
    /// client id that owns the render, and the assessment does not hold that
    /// socket — the job runtime does — so time against a known expectation is
    /// the honest signal available here. It is capped below 100 either way:
    /// the bar completes when the render does, never before.
    /// </param>
    private async Task RunGraphAsync(int color, CancellationToken ct, Action<TimeSpan>? onElapsed = null)
    {
        // Noted before it is sent: whatever becomes of the answer, what this
        // prompt writes into the owner's output folder is cleared after.
        string chosen = Guid.NewGuid().ToString();
        int noted = _sent.Count;
        _sent.Add((chosen, DateTimeOffset.UtcNow));

        using var submit = await _http.PostAsync(
            $"{options.ComfyUrl.TrimEnd('/')}/prompt",
            new StringContent(ReferenceGraph(color, chosen), Encoding.UTF8, "application/json"), ct);

        string body = await submit.Content.ReadAsStringAsync(ct);
        if (!submit.IsSuccessStatusCode)
            throw new InvalidOperationException($"reference workload rejected (HTTP {(int)submit.StatusCode}): {body[..Math.Min(200, body.Length)]}");

        string promptId = JsonNode.Parse(body)?["prompt_id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("ComfyUI returned no prompt_id for the reference workload");

        // A ComfyUI from before client-chosen ids ran it under its own.
        if (promptId != chosen) _sent[noted] = (promptId, _sent[noted].SentAt);

        var running = Stopwatch.StartNew();

        for (int i = 0; i < 600; i++)
        {
            await Task.Delay(250, ct);
            onElapsed?.Invoke(running.Elapsed);

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
    /// VRAM on the card the tiers are drawn against — the same RTX 3060 that
    /// <see cref="ReferenceBaselineSeconds"/> is taken from, so 12 GB.
    /// </summary>
    public const int ReferenceVramMb = 12_288;

    /// <summary>
    /// Turns the measurements into a score, a tier, and a per-kind lane.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three things decide what a machine is worth, and they measure different
    /// things, so none of them is double-counted:
    /// </para>
    /// <list type="number">
    ///   <item><b>Speed</b>, from the blur benchmark. What the card does per
    ///     second.</item>
    ///   <item><b>Capacity</b>, from VRAM. The benchmark barely touches memory,
    ///     so it says nothing at all about which weights will fit — and that is
    ///     what decides whether the machine sees the good work.</item>
    ///   <item><b>Power</b>, from the card's own ceiling. Also invisible to a
    ///     three-second benchmark, which may never reach the limit at all,
    ///     while a card held at half its rated watts will not hold its clocks
    ///     through a five-minute render.</item>
    /// </list>
    /// <para>
    /// Unexpected shutdowns are deliberately <i>not</i> in here. They are real
    /// and they are reported, but a machine cannot tell a failing power supply
    /// from a pulled plug or from an experiment somebody ran on it — and two of
    /// the three on the host this was written against were exactly that. They
    /// are a warning for the owner to act on, not a fine.
    /// </para>
    /// <para>
    /// This number is what the owner is shown, not what anybody is paid on.
    /// The client is a C# binary on someone else's PC and its IL opens in a
    /// decompiler in seconds, so the payout formula is not in here — the raw
    /// components go up with the report and the server does its own sums.
    /// </para>
    /// <para>
    /// Per kind of work, a machine now gets a lane rather than a verdict.
    /// It is refused only when the weights do not fit, when the models are not
    /// on disk, or when it misses even the relaxed deadline; everything between
    /// the two deadlines is work nobody is waiting on, which is most of what a
    /// network of home PCs is good for.
    /// </para>
    /// </remarks>
    private static void Score(NodeAssessment a, Storage.NodeStore? history)
    {
        // Where grading starts. Zero unless the owner has asked to be graded
        // from the top again, in which case only the work done since counts.
        long? epoch = history is null ? null : GradingEpoch(history);
        a.GradingSince = epoch is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(epoch.Value) : null;

        double factor = a.ReferenceSeconds > 0 ? a.ReferenceSeconds / ReferenceBaselineSeconds : double.MaxValue;

        // 1000 on a reference card, halving as the card gets twice as slow.
        double speed = Math.Clamp(1000 / Math.Max(factor, 0.1), 0, 10_000);

        // Square-rooted so that twice the VRAM is worth more than a card with
        // half of it without being worth twice as much — memory opens doors,
        // it does not do the work.
        double capacity = a.VramTotalMb > 0
            ? Math.Clamp(Math.Sqrt(a.VramTotalMb / (double)ReferenceVramMb), 0.55, 1.25)
            : 0.55;

        // Unmeasured is not penalised: a card that does not report its limits
        // is not thereby a card running below them.
        double power = a is { PowerLimitW: > 0, PowerDefaultW: > 0 }
            ? Math.Clamp(0.70 + 0.30 * a.PowerLimitW / a.PowerDefaultW, 0.70, 1.0)
            : 1.0;

        a.Score = (int)Math.Round(speed * capacity * power);

        // Lowered to match: the score now carries two more multipliers that can
        // only pull it down, so the old cut-offs would have moved every honest
        // home card a tier down for telling the truth about itself.
        a.Tier = a.Score switch
        {
            >= 1200 => "platinum",
            >= 550 => "gold",
            >= 250 => "silver",
            >= 90 => "bronze",
            _ => "basic",
        };

        Warn(a);

        foreach (var (kind, minVram, baseline, deadline, slowDeadline, needs, weightsSpill) in Kinds)
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
            double? measured = history?.MedianJobSeconds(kind, minSamples: MinSamples, sinceUnixMs: epoch);

            bool penalised = weightsSpill && a.LowVram;
            double expected = measured ?? baseline * factor * (penalised ? LowVramPenalty : 1);

            var capability = new Capability
            {
                Kind = kind,
                SecondsPerUnit = Math.Round(expected, 1),
                SpeedFactor = Math.Round(expected / baseline, 2),
                Source = measured is null ? "estimated" : "measured",
            };

            if (a.VramTotalMb < minVram)
            {
                // Physics, not policy: the weights will not fit, on any lane,
                // however long anybody is willing to wait.
                capability.Lane = "no";
                capability.Reason = $"ต้องการ VRAM อย่างน้อย {minVram / 1024.0:0.#} GB มี {a.VramTotalMb / 1024.0:0.#} GB";
            }
            else if (!hasAssets)
            {
                capability.Lane = "no";
                capability.Reason = $"ยังไม่มีโมเดลสำหรับงานนี้ ({needs})";
            }
            else if (measured is null)
            {
                // First round: the top bar, on trust. The machine has cleared
                // the two gates that are facts — the weights fit and the models
                // are there — and everything past that is a guess until it has
                // run the work. It gets graded on what it does.
                capability.Lane = "full";
                capability.Provisional = true;
                capability.Reason =
                    $"รอบแรก — ให้เต็มความเร็วไว้ก่อน (คาดว่า {expected:0} วินาที/ชิ้น) " +
                    "แล้วปรับตามเวลาจริงเมื่อทำงานไปแล้ว " + MinSamples + " ชิ้น" +
                    (penalised ? " · ComfyUI รันโหมด --lowvram อยู่" : "");
            }
            else if (expected <= deadline)
            {
                capability.Lane = "full";
            }
            else if (expected <= slowDeadline)
            {
                // Slower than someone watching a progress bar will accept, fine
                // for a job sitting in a queue. The owner is told which it is.
                capability.Lane = "slow";
                capability.Reason =
                    $"จากงานจริงที่เคยทำ ใช้เวลา {expected:0} วินาที/ชิ้น เกิน {deadline:0} วินาทีของงานด่วน — " +
                    $"ลดมารับเฉพาะงานที่ไม่มีคนรอ (ถึง {slowDeadline:0} วินาที)";
            }
            else
            {
                capability.Lane = "no";
                capability.Reason =
                    $"จากงานจริงที่เคยทำ ใช้เวลา {expected:0} วินาที/ชิ้น " +
                    $"เกินแม้แต่งานที่ไม่มีคนรอ ({slowDeadline:0} วินาที)";
            }

            capability.CanRun = capability.Lane != "no";
            a.Capabilities.Add(capability);
        }
    }

    /// <summary>
    /// Things the owner should know about their own machine, in their own
    /// language, that are not a reason to refuse it work.
    /// </summary>
    /// <remarks>
    /// All three of these cost real score, and none of them is visible from
    /// inside ComfyUI — an owner whose card sits at half its rated watts has
    /// almost certainly forgotten it, and one whose PC has been dropping out
    /// under load may not have connected that to the jobs that never paid.
    /// </remarks>
    private static void Warn(NodeAssessment a)
    {
        if (a.PowerCapped)
        {
            a.Warnings.Add(
                $"การ์ดถูกจำกัดไฟไว้ที่ {a.PowerLimitW} W จากสเปค {a.PowerDefaultW} W " +
                $"({a.PowerPct}%) — คะแนนจึงต่ำกว่าที่การ์ดรุ่นนี้ทำได้จริง");
        }

        if (a.HardShutdowns > 0)
        {
            string when = a.LastHardShutdown is { } at
                ? $" ล่าสุด {at.ToLocalTime():d MMM HH:mm}"
                : "";
            a.Warnings.Add(
                $"เครื่องดับเองโดยไม่ได้สั่งปิด {a.HardShutdowns} ครั้งในสัปดาห์ที่ผ่านมา{when} — " +
                "งานที่กำลังทำอยู่จะหายไปพร้อมกัน ตรวจพาวเวอร์ซัพพลายและสายไฟการ์ด");
        }

        if (a.LowVram)
        {
            a.Warnings.Add(
                "ComfyUI รันโหมด --lowvram อยู่ — งานภาพจะช้ากว่าปกติมาก " +
                "ถ้า VRAM พอ ลองเอาแฟล็กนี้ออกแล้วประเมินใหม่");
        }
    }

    /// <summary>
    /// Re-grades a report against the ledger, without touching the GPU.
    /// </summary>
    /// <remarks>
    /// This is the tuning loop. A node starts every kind of work at the top
    /// lane and keeps it until it has run <see cref="MinSamples"/> of them;
    /// from that job onward its own median decides, and this is what applies
    /// the decision. Without it a machine that had just proved it cannot hold
    /// an image deadline would keep taking urgent image work until the next
    /// full re-assessment, six hours later.
    ///
    /// It is cheap and deterministic — the same scoring maths over stored
    /// numbers and the job ledger — so it can run after every job.
    /// </remarks>
    /// <returns>True when any lane moved, which is the only time it is worth telling anybody.</returns>
    public static bool Regrade(NodeAssessment report, Storage.NodeStore history)
    {
        if (report.Failed is not null || report.ReferenceSeconds <= 0) return false;

        var before = report.Capabilities.ToDictionary(c => c.Kind, c => c.Lane);

        report.Capabilities.Clear();
        report.Warnings.Clear();
        Score(report, history);

        return report.Capabilities.Any(c => !before.TryGetValue(c.Kind, out string? was) || was != c.Lane);
    }

    // ------------------------------------------------- back to the maximum

    /// <summary>The setting that holds where grading starts, in unix milliseconds.</summary>
    public const string GradingEpochSetting = "grading_epoch";

    public static long GradingEpoch(Storage.NodeStore store)
    {
        try
        {
            return long.TryParse(store.GetSetting(GradingEpochSetting), out long epoch) ? epoch : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Sends the machine back to the top of the scale, to be graded down again
    /// only by what it does from now on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The grading is deliberately one-way in the short term: a node starts at
    /// the maximum lane on trust and is corrected by its own measured times.
    /// That is right while the conditions hold. It is wrong when they change —
    /// and on this machine they did. The medians that graded it down were
    /// collected with the card power-limited to 90&#160;W of its 180, and with
    /// ComfyUI running <c>--lowvram</c>. Raise the limit, drop the flag, and the
    /// node still carried the slow numbers: the window is the last
    /// <see cref="MinSamples"/>+ jobs, so the only way back up was to out-run
    /// the old times at the lane they had already cost it.
    /// </para>
    /// <para>
    /// This moves the line rather than deleting anything. Every job stays in the
    /// ledger and on the History screen; the ones before the line simply stop
    /// counting toward what the node is allowed to accept. The next
    /// <see cref="Score"/> therefore finds no samples, and every capability that
    /// passes VRAM and has its models comes back as <c>full</c> and
    /// <c>provisional</c> — the same state a freshly paired machine is in.
    /// </para>
    /// </remarks>
    public static void ResetGrading(Storage.NodeStore store)
        => store.SetSetting(GradingEpochSetting, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());

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
