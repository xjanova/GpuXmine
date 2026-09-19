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
public sealed class XmanStudioClient(HttpClient http, string baseUrl, string productSlug = "gpuxmine")
{
    private readonly string _base = baseUrl.TrimEnd('/');

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

            var body = await ReadAsync<ApiEnvelope>(response, ct);
            return new DeviceRegistration(response.IsSuccessStatusCode && (body?.Success ?? false), body?.Message);
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

            var body = await ReadAsync<LicenseEnvelope>(response, ct);
            if (body is null) return LicenseState.Unknown("อ่านคำตอบจากเซิร์ฟเวอร์ไม่ได้");

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
