using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace GpuxMine.Core.Licensing;

public sealed record DeviceRegistration(bool Ok, string? Message);

/// <summary>The identity a node is given when its owner pairs it from the website.</summary>
public sealed record NodeCredentials(
    bool Ok,
    string? WorkerId,
    string? Token,
    string? RelayUrl,
    string? Label,
    string? Owner,
    string? Message);

public sealed record LicenseState(
    bool Valid,
    string? Status,
    string? Plan,
    DateTimeOffset? ExpiresAt,
    string? Message)
{
    /// <summary>What the agent assumes when XMAN Studio cannot be reached.</summary>
    /// <remarks>
    /// Deliberately permissive. A node that stops earning because the licence
    /// server had a bad ten minutes is a node whose owner uninstalls us; the
    /// paid tier is an upgrade, not a gate on running at all. Anything that
    /// genuinely must not happen without a licence is enforced server-side when
    /// work is dispatched, where an offline client cannot vote.
    /// </remarks>
    public static LicenseState Unknown(string reason) => new(false, "unknown", null, null, reason);
}

public sealed record VersionInfo(
    string? Latest,
    bool UpdateAvailable,
    string? DownloadUrl,
    string? Changelog,
    /// <summary>Set by the server when a build is too old to keep running.</summary>
    bool ForceUpdate);

/// <summary>
/// Talks to XMAN Studio for the things the account system owns: which machine
/// this is, whether it holds a licence, and what the newest published build is.
/// </summary>
/// <remarks>
/// The binary update itself does NOT come through here — Velopack pulls that
/// straight from GitHub Releases, exactly as the BrainX client does. This class
/// is the control plane: it is what lets the platform see its fleet and, when
/// a build has to go, say so.
///
/// Every call fails soft. Losing contact with the website must never stop a
/// node rendering work it has already been given.
/// </remarks>
public sealed class XmanStudioClient(HttpClient http, string baseUrl, string productSlug = "gpuxmine", Action<string>? log = null)
{
    private readonly string _base = baseUrl.TrimEnd('/');

    /// <summary>
    /// Says out loud that the website refused, and with what.
    /// </summary>
    /// <remarks>
    /// Every call here fails soft, which is right — the fleet must keep
    /// rendering through a website outage. What was wrong is that it also failed
    /// silently: a refusal and an outage and a machine with no token all came
    /// out as the same nothing, and the owner got a blank referrals screen with
    /// no way to tell which. Soft is not the same as quiet.
    /// </remarks>
    private void Refused(string call, HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        log?.Invoke($"[net] XMAN Studio ปฏิเสธ {call} — HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
            + (response.Headers.TryGetValues("cf-ray", out var ray) ? $" · cf-ray {string.Join(",", ray)}" : ""));
    }

    public async Task<DeviceRegistration> RegisterDeviceAsync(string appVersion, CancellationToken ct = default)
    {
        try
        {
            using HttpResponseMessage response = await http.PostAsJsonAsync(
                $"{_base}/api/v1/product/{productSlug}/register-device",
                new
                {
                    machine_id = MachineIdentity.MachineId(),
                    machine_name = MachineIdentity.MachineName(),
                    os_version = MachineIdentity.OsVersion(),
                    app_version = appVersion,
                    hardware_hash = MachineIdentity.HardwareHash(),
                },
                ct);

            Refused("register-device", response);
            var body = await ReadAsync<ApiEnvelope>(response, ct);
            return new DeviceRegistration(
                response.IsSuccessStatusCode && (body?.Success ?? false),
                body?.Message ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return new DeviceRegistration(false, ex.Message);
        }
    }

    /// <summary>
    /// What this machine's owner has earned by inviting other people.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Referrals screen used to be prose and dashes: an invite code it
    /// could not show, a network it said was "on XMAN Studio", and an earnings
    /// figure that was permanently an em dash. The website has had all of it
    /// the whole time; nothing asked for it.
    /// </para>
    /// <para>
    /// Authenticated with the worker id and relay token rather than the machine
    /// id the other calls use. A machine id is derivable by anything running on
    /// the same computer, and this answer contains what somebody has earned.
    /// </para>
    /// </remarks>
    public async Task<ReferralSummary?> ReferralAsync(string workerId, string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workerId) || string.IsNullOrWhiteSpace(token)) return null;

        try
        {
            using HttpResponseMessage response = await http.PostAsJsonAsync(
                $"{_base}/api/v1/product/{productSlug}/referral",
                new { worker_id = workerId, token },
                ct);

            Refused("referral", response);
            var body = await ReadAsync<ReferralEnvelope>(response, ct);
            if (!response.IsSuccessStatusCode || body?.Success != true || body.Data is null) return null;

            return body.Data;
        }
        catch
        {
            // The website being unreachable is not a fault in the node, and the
            // screen already knows how to say it has nothing yet.
            return null;
        }
    }

    /// <summary>
    /// What the pool makes of this machine, and what its owner has earned
    /// (contract C6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The node used to know only its own side: it said "sharing" while the
    /// pool had reaped it, an administrator had suspended it, or XMAN Studio's
    /// push to the pool had been failing for a day — and every money figure on
    /// the screen was a dash, because nothing told it what any job had paid.
    /// </para>
    /// <para>
    /// Authenticated like <see cref="ReferralAsync"/>: the worker id and the
    /// machine's own relay token, never the machine id, because the answer is
    /// somebody's money. Fails soft like every call here, but says which kind
    /// of failure it was — a refused identity needs the owner to re-pair, an
    /// outage needs nobody, and the caller waits very differently for each.
    /// </para>
    /// <para>
    /// <paramref name="updatedAfter"/> asks for the jobs that changed after that
    /// moment, oldest first, a page at a time (<see cref="NodeStatus.JobsMore"/>)
    /// — instead of the latest fifty, which a fast machine outruns within one
    /// hold window and so never sees its older jobs cleared, paid or voided.
    /// A website from before the cursor ignores it and answers the latest fifty
    /// with no <c>jobs_more</c>, which is how the caller tells the two apart.
    /// </para>
    /// </remarks>
    public async Task<NodeStatusResult> StatusAsync(string workerId, string token, DateTimeOffset? updatedAfter = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workerId) || string.IsNullOrWhiteSpace(token))
            return new NodeStatusResult(StudioOutcome.IdentityRejected, null, null, "เครื่องนี้ยังไม่ได้ลงทะเบียน");

        try
        {
            // Not sent at all without a cursor, so the call is byte for byte the
            // one every website since C6 has answered.
            object request = updatedAfter is { } after
                ? new { worker_id = workerId, token, updated_after = StudioTime.Format(after) }
                : new { worker_id = workerId, token };

            using HttpResponseMessage response = await http.PostAsJsonAsync(
                $"{_base}/api/v1/product/{productSlug}/status",
                request,
                ct);

            Refused("status", response);
            int code = (int)response.StatusCode;
            var body = await ReadAsync<StatusEnvelope>(response, ct);

            if (response.IsSuccessStatusCode)
            {
                return body is { Success: true, Data: { } data }
                    ? new NodeStatusResult(StudioOutcome.Ok, data, code, null)
                    : new NodeStatusResult(StudioOutcome.Unavailable, null, code, $"XMAN Studio ตอบกลับในรูปแบบที่อ่านไม่ได้ (HTTP {code})");
            }

            // The website refused the cursor, not the machine: a 422 that
            // names updated_after comes after the identity was accepted. Read
            // as a refused identity it would tell the owner to re-pair a
            // machine that is fine, so ask once more the old way instead.
            if (code == 422 && updatedAfter is not null && body?.Errors?.ContainsKey("updated_after") == true)
            {
                log?.Invoke("[net] XMAN Studio ไม่รับ updated_after ที่ส่งไป — อ่านงานล่าสุดแบบเดิมแทน");
                return await StatusAsync(workerId, token, null, ct);
            }

            return code switch
            {
                // Who this machine is was not accepted: removed from the
                // account, paired again elsewhere, or a token the website no
                // longer holds. Retrying will not change the answer.
                401 or 422 => new NodeStatusResult(StudioOutcome.IdentityRejected, null, code, body?.Message),

                // A website from before this call existed. Not a fault, and
                // not worth asking again every few minutes.
                404 or 405 => new NodeStatusResult(StudioOutcome.NotSupported, null, code, null),

                _ => new NodeStatusResult(StudioOutcome.Unavailable, null, code, $"XMAN Studio ตอบ HTTP {code}"),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Unreachable is the ordinary case on a laptop that just woke up.
            return new NodeStatusResult(StudioOutcome.Unavailable, null, null, $"ติดต่อ XMAN Studio ไม่ได้: {ex.Message}");
        }
    }

    /// <summary>
    /// Exchanges the pairing code from the website for this node's identity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one call that makes an installed client usable. Before it existed, a
    /// worker id and token could only be minted by someone with the relay's
    /// admin key, so a person who downloaded the installer opened the app,
    /// read "ยังไม่ได้ลงทะเบียน", and had nothing to click.
    /// </para>
    /// <para>
    /// The website issues the code, not the client: the person logged in there
    /// has proved who they are, and a freshly installed program has proved
    /// nothing. The code is short-lived and single-use because it creates a
    /// worker in that account's name.
    /// </para>
    /// </remarks>
    public async Task<NodeCredentials> ClaimAsync(string pairingCode, string appVersion, CancellationToken ct = default)
    {
        try
        {
            using HttpResponseMessage response = await http.PostAsJsonAsync(
                $"{_base}/api/v1/product/{productSlug}/claim",
                new
                {
                    pairing_code = pairingCode,
                    machine_id = MachineIdentity.MachineId(),
                    machine_name = MachineIdentity.MachineName(),
                    os_version = MachineIdentity.OsVersion(),
                    app_version = appVersion,
                    hardware_hash = MachineIdentity.HardwareHash(),
                },
                ct);

            Refused("claim", response);
            var body = await ReadAsync<ClaimEnvelope>(response, ct);

            if (body is null)
            {
                return new NodeCredentials(false, null, null, null, null, null,
                    $"เซิร์ฟเวอร์ตอบกลับไม่ถูกต้อง (HTTP {(int)response.StatusCode})");
            }

            if (!response.IsSuccessStatusCode || !body.Success || body.Data?.Token is null)
            {
                // The server's message is the useful one — it distinguishes a
                // mistyped code from an expired one from a relay that is down.
                return new NodeCredentials(false, null, null, null, null, null,
                    body.Message ?? "ลงทะเบียนไม่สำเร็จ");
            }

            return new NodeCredentials(
                true,
                body.Data.WorkerId,
                body.Data.Token,
                body.Data.RelayUrl,
                body.Data.Label,
                body.Data.Owner,
                body.Message);
        }
        catch (Exception ex)
        {
            return new NodeCredentials(false, null, null, null, null, null,
                $"ติดต่อ XMAN Studio ไม่ได้: {ex.Message}");
        }
    }

    public async Task<LicenseState> ValidateAsync(string? licenseKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
            return new LicenseState(false, "none", "free", null, "ไม่ได้ใส่ license key — ใช้งานระดับฟรี");

        try
        {
            using HttpResponseMessage response = await http.PostAsJsonAsync(
                $"{_base}/api/v1/product/{productSlug}/validate",
                new
                {
                    license_key = licenseKey,
                    machine_id = MachineIdentity.MachineId(),
                },
                ct);

            Refused("validate", response);
            var body = await ReadAsync<LicenseEnvelope>(response, ct);
            if (body is null) return LicenseState.Unknown($"อ่านคำตอบจากเซิร์ฟเวอร์ไม่ได้ (HTTP {(int)response.StatusCode})");

            // The server's verdict is `is_valid` (status active, not expired, and
            // bound to this machine). `data.status` alone would say "active" for
            // a key that is active on somebody else's PC.
            return new LicenseState(
                body.Success && body.IsValid,
                body.Data?.Status ?? (body.Success ? null : "invalid"),
                body.Data?.LicenseType,
                body.Data?.ExpiresAt,
                body.Message);
        }
        catch (Exception ex)
        {
            return LicenseState.Unknown(ex.Message);
        }
    }

    public async Task<VersionInfo?> CheckUpdateAsync(string currentVersion, string? licenseKey, CancellationToken ct = default)
    {
        try
        {
            using HttpResponseMessage response = await http.PostAsJsonAsync(
                $"{_base}/api/v1/products/{productSlug}/check-update",
                new
                {
                    current_version = currentVersion,
                    license_key = licenseKey,
                },
                ct);

            Refused("check-update", response);
            var body = await ReadAsync<UpdateEnvelope>(response, ct);
            if (body is null || !body.Success) return null;

            return new VersionInfo(
                body.LatestVersion ?? body.Update?.Version,
                body.HasUpdate ?? false,
                body.Update?.DownloadUrl,
                body.Update?.Changelog,
                // Not something the product API says yet. Kept so the agent's
                // loop is already wired for the day the server can order a
                // build off the network — a security fix needs that lever.
                body.ForceUpdate ?? false);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct) where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(ct);
        }
        catch
        {
            // A proxy or error page in place of JSON is a failure to answer, not
            // a crash. Callers treat null as "could not tell".
            return null;
        }
    }

    // --- wire shapes -------------------------------------------------------
    // Field names follow XMAN Studio's existing product API. Optional
    // everywhere: these endpoints serve several products and have grown fields
    // over time, so a missing one must never throw.

    private sealed class ApiEnvelope
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    // Shapes verified against ProductLicenseController::validate and
    // VersionController::check in the xmanstudio source, 2026-09-18. A guess
    // here fails silently — every licensed node would report the free tier.

    private sealed class ClaimEnvelope
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public ClaimData? Data { get; set; }

        internal sealed class ClaimData
        {
            [JsonPropertyName("worker_id")] public string? WorkerId { get; set; }
            [JsonPropertyName("token")] public string? Token { get; set; }
            [JsonPropertyName("relay_url")] public string? RelayUrl { get; set; }
            [JsonPropertyName("label")] public string? Label { get; set; }
            [JsonPropertyName("owner")] public string? Owner { get; set; }
        }
    }

    private sealed class LicenseEnvelope
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("is_valid")] public bool IsValid { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public LicenseData? Data { get; set; }

        internal sealed class LicenseData
        {
            [JsonPropertyName("license_type")] public string? LicenseType { get; set; }
            [JsonPropertyName("status")] public string? Status { get; set; }
            [JsonPropertyName("expires_at")] public DateTimeOffset? ExpiresAt { get; set; }
            [JsonPropertyName("days_remaining")] public int? DaysRemaining { get; set; }
            [JsonPropertyName("is_expired")] public bool? IsExpired { get; set; }
        }
    }

    private sealed class UpdateEnvelope
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("has_update")] public bool? HasUpdate { get; set; }
        [JsonPropertyName("latest_version")] public string? LatestVersion { get; set; }
        [JsonPropertyName("force_update")] public bool? ForceUpdate { get; set; }
        [JsonPropertyName("update")] public UpdateBlock? Update { get; set; }

        internal sealed class UpdateBlock
        {
            [JsonPropertyName("version")] public string? Version { get; set; }
            [JsonPropertyName("changelog")] public string? Changelog { get; set; }
            [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
        }
    }
}

/// <summary>The owner's referral standing, exactly as XMAN Studio reports it.</summary>
/// <remarks>
/// Money arrives as baht with two decimals because that is how the website
/// stores it. It is not converted on the way here: a unit change in transit is
/// how a figure ends up a hundred times wrong on one screen and right on the
/// other.
/// </remarks>
public sealed class ReferralSummary
{
    /// <summary>False when the owner has an account but has not joined the programme.</summary>
    [JsonPropertyName("enrolled")] public bool Enrolled { get; set; }

    [JsonPropertyName("referral_code")] public string? ReferralCode { get; set; }
    [JsonPropertyName("referral_url")] public string? ReferralUrl { get; set; }
    [JsonPropertyName("commission_rate")] public decimal CommissionRate { get; set; }
    [JsonPropertyName("total_referrals")] public int TotalReferrals { get; set; }
    [JsonPropertyName("total_conversions")] public int TotalConversions { get; set; }
    [JsonPropertyName("total_earned")] public decimal TotalEarned { get; set; }
    [JsonPropertyName("total_paid")] public decimal TotalPaid { get; set; }
    [JsonPropertyName("total_pending")] public decimal TotalPending { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }

    /// <summary>Where to join, when not enrolled; where to look, when enrolled.</summary>
    [JsonPropertyName("join_url")] public string? JoinUrl { get; set; }
    [JsonPropertyName("dashboard_url")] public string? DashboardUrl { get; set; }
}

internal sealed class ReferralEnvelope
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("data")] public ReferralSummary? Data { get; set; }
}

/// <summary>How a call to XMAN Studio ended, as far as what to do next is concerned.</summary>
public enum StudioOutcome
{
    /// <summary>Answered.</summary>
    Ok,

    /// <summary>The website does not accept this machine's identity (401). Only re-pairing fixes it.</summary>
    IdentityRejected,

    /// <summary>The website predates the call (404/405). Ask rarely.</summary>
    NotSupported,

    /// <summary>Unreachable, overloaded, throttled or an unreadable reply. Ask again later.</summary>
    Unavailable,
}

/// <param name="Status">The answer, when <paramref name="Outcome"/> is <see cref="StudioOutcome.Ok"/>.</param>
/// <param name="HttpStatus">What the website said, when it said anything.</param>
/// <param name="Message">Why there is no answer, in the owner's words where the website gave some.</param>
public sealed record NodeStatusResult(StudioOutcome Outcome, NodeStatus? Status, int? HttpStatus, string? Message);

/// <summary>
/// XMAN Studio's view of one machine and its owner's money (contract C6).
/// </summary>
/// <remarks>
/// <para>
/// Money is integer satang throughout, exactly as the website sends it, and
/// nullable: a field the website did not send is "not known", which the screen
/// shows as a dash — never as zero.
/// </para>
/// <para>
/// <see cref="Earnings"/> covers the owner's whole account (every machine,
/// because there is one wallet); <see cref="Jobs"/> covers this machine only.
/// </para>
/// </remarks>
public sealed class NodeStatus
{
    [JsonPropertyName("node")] public NodeDispatch? Node { get; set; }
    [JsonPropertyName("earnings")] public EarningsSummary? Earnings { get; set; }
    [JsonPropertyName("jobs")] public List<SettledJob>? Jobs { get; set; }

    /// <summary>
    /// Only in an answer to <c>updated_after</c>: true when more changed jobs
    /// follow the last one listed. Null means the website ignored the cursor
    /// (it predates it) and <see cref="Jobs"/> is the latest fifty as before.
    /// </summary>
    [JsonPropertyName("jobs_more")] public bool? JobsMore { get; set; }
}

/// <summary>What the pool last said about this machine, as XMAN Studio recorded it.</summary>
public sealed class NodeDispatch
{
    /// <summary>
    /// The pool's verdict on the last push: eligible · unassessed ·
    /// no-matching-model · offline · retired · suspended · rejected — or
    /// XMAN Studio's own: unconfigured · error. Null when never pushed.
    /// </summary>
    [JsonPropertyName("dispatch_status")] public string? DispatchStatus { get; set; }

    /// <summary>The pool's explanation, already in Thai.</summary>
    [JsonPropertyName("dispatch_note")] public string? DispatchNote { get; set; }

    /// <summary>The pool's row for this machine: ready · busy · warming · draining · terminated … Null from an older pool.</summary>
    [JsonPropertyName("dispatch_worker_status")] public string? WorkerStatus { get; set; }

    /// <summary>Whether XMAN Studio saw the machine on the relay at its last sync.</summary>
    [JsonPropertyName("relay_online")] public bool? RelayOnline { get; set; }

    [JsonPropertyName("suspended")] public bool Suspended { get; set; }
    [JsonPropertyName("suspended_reason")] public string? SuspendedReason { get; set; }

    /// <summary>ISO 8601, kept as text: a format surprise must cost one field, not the whole answer.</summary>
    [JsonPropertyName("last_seen_at")] public string? LastSeenAtText { get; set; }

    [JsonIgnore] public DateTimeOffset? LastSeenAt => StudioTime.Parse(LastSeenAtText);
}

/// <summary>The owner's earnings from sharing, across every machine on the account. Integer satang.</summary>
public sealed class EarningsSummary
{
    /// <summary>Finished, inside the hold window.</summary>
    [JsonPropertyName("pending_satang")] public long? PendingSatang { get; set; }

    /// <summary>Held for an administrator to look at.</summary>
    [JsonPropertyName("review_satang")] public long? ReviewSatang { get; set; }

    /// <summary>Past the hold, waiting for the next hourly credit to the wallet.</summary>
    [JsonPropertyName("cleared_satang")] public long? ClearedSatang { get; set; }

    /// <summary>Already credited to the XMAN wallet.</summary>
    [JsonPropertyName("paid_satang")] public long? PaidSatang { get; set; }

    [JsonPropertyName("today_satang")] public long? TodaySatang { get; set; }
    [JsonPropertyName("month_satang")] public long? MonthSatang { get; set; }

    /// <summary>What free-share jobs would have paid over the last 30 days.</summary>
    [JsonPropertyName("donated_satang_30d")] public long? DonatedSatang30d { get; set; }

    /// <summary>The owner's XMAN wallet balance — spendable on the website, not cash.</summary>
    [JsonPropertyName("wallet_balance_satang")] public long? WalletBalanceSatang { get; set; }

    /// <summary>How long a finished job waits before it is credited.</summary>
    [JsonPropertyName("hold_hours")] public int? HoldHours { get; set; }
}

/// <summary>One of this machine's jobs as the pool settled it.</summary>
public sealed class SettledJob
{
    /// <summary>The pool's id for the job (aix-gpu-job-N). Not the join key.</summary>
    [JsonPropertyName("job_id")] public string? JobId { get; set; }

    /// <summary>ComfyUI's prompt id on this machine — what the local ledger is keyed by.</summary>
    [JsonPropertyName("prompt_id")] public string? PromptId { get; set; }

    [JsonPropertyName("kind")] public string? Kind { get; set; }

    /// <summary>What the owner receives for it, after the platform's share and any referral.</summary>
    [JsonPropertyName("amount_satang")] public long AmountSatang { get; set; }

    /// <summary>For a free-share job: what it would have paid.</summary>
    [JsonPropertyName("donated_value_satang")] public long DonatedValueSatang { get; set; }

    /// <summary>pending · review · cleared · paid · void.</summary>
    [JsonPropertyName("status")] public string? Status { get; set; }

    [JsonPropertyName("completed_at")] public string? CompletedAtText { get; set; }

    [JsonIgnore] public DateTimeOffset? CompletedAt => StudioTime.Parse(CompletedAtText);

    /// <summary>
    /// When the website last changed this row — the cursor the node sends back
    /// as <c>updated_after</c>. Absent from a website that predates the cursor.
    /// </summary>
    [JsonPropertyName("updated_at")] public string? UpdatedAtText { get; set; }

    [JsonIgnore] public DateTimeOffset? UpdatedAt => StudioTime.Parse(UpdatedAtText);
}

internal sealed class StatusEnvelope
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("data")] public NodeStatus? Data { get; set; }

    /// <summary>Laravel's validation errors, by field — only read to tell a refused cursor from a refused machine.</summary>
    [JsonPropertyName("errors")] public Dictionary<string, System.Text.Json.JsonElement>? Errors { get; set; }
}

internal static class StudioTime
{
    public static DateTimeOffset? Parse(string? text) =>
        DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    /// <summary>
    /// ISO 8601 in UTC to the second — the shape XMAN Studio's own
    /// <c>updated_at</c> comes in, whatever the machine's culture or calendar.
    /// A Thai culture would otherwise write the Buddhist year, 2569, and the
    /// website would read a cursor five centuries in the future.
    /// </summary>
    public static string Format(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'+00:00'", System.Globalization.CultureInfo.InvariantCulture);
}
