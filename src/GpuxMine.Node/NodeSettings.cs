using System.Text.Json;
using System.Text.Json.Serialization;

namespace GpuxMine.Node;

/// <summary>
/// Everything the owner decides about how their machine is shared. Persisted
/// locally; mirrored to the pool through the heartbeat so dispatch can respect
/// it without a round-trip.
/// </summary>
/// <remarks>
/// Owner-editable by design, so nothing here is trusted for money: earnings,
/// scores and prices are computed server-side from what the node actually did.
/// A hand-edited file can only make the node share more or less — never earn
/// more than it worked for.
/// </remarks>
public sealed class NodeSettings
{
    public const int HoursPerWeek = 7 * 24;

    // --- power & limits ---

    /// <summary>How much of the card to offer, 20–100.</summary>
    public int PowerPercent { get; set; } = 75;

    /// <summary>Share of accepted work donated to the pool for free, 0–100.</summary>
    public int FreeSharePercent { get; set; } = 0;

    /// <summary>Stop taking new jobs while the owner is at the keyboard or in a game.</summary>
    public bool YieldWhenActive { get; set; } = true;

    /// <summary>Only share inside the weekly schedule; otherwise share whenever running.</summary>
    public bool ScheduleOnly { get; set; } = false;

    public int TempCeilingC { get; set; } = 78;
    public int VramOfferMb { get; set; } = 0;     // 0 = whatever the card has
    public int UploadCapMbps { get; set; } = 40;

    // --- schedule: [day * 24 + hour], Monday = 0; true = share ---

    public bool[] Schedule { get; set; } = DefaultSchedule();

    // --- jobs ---

    public bool AutoMatch { get; set; } = true;

    /// <summary>Job kinds the owner accepts when auto-matching is off.</summary>
    public HashSet<string> AcceptedJobTypes { get; set; } = ["image", "upscale", "text"];

    // --- economics (inputs to the profit calculator; not money) ---

    public decimal TariffPerKwh { get; set; } = 4.20m;
    public int OffPeakStartHour { get; set; } = 22;
    public int OffPeakEndHour { get; set; } = 8;
    public decimal OffPeakRatePerKwh { get; set; } = 2.60m;

    // --- app ---

    public bool StartWithWindows { get; set; } = false;
    public bool Notifications { get; set; } = true;
    public bool LeaderboardAnonymous { get; set; } = false;

    /// <summary>Which screen opens first — a small courtesy that costs nothing.</summary>
    public string LastScreen { get; set; } = "dashboard";

    // ------------------------------------------------------------------

    private static bool[] DefaultSchedule()
    {
        // Share at all hours except the evening the family is likely using the
        // PC, and weekend daytime. The prototype's rule, kept as the default so
        // a fresh install behaves like the design the owner approved.
        var s = new bool[HoursPerWeek];
        for (int day = 0; day < 7; day++)
        for (int hour = 0; hour < 24; hour++)
        {
            bool evening = hour is >= 18 and <= 21;
            bool weekendDay = day >= 5 && hour is >= 10 and <= 17;
            s[day * 24 + hour] = !(evening || weekendDay);
        }
        return s;
    }

    public bool IsScheduledNow(DateTime localNow)
    {
        int day = ((int)localNow.DayOfWeek + 6) % 7;   // Monday = 0
        int index = day * 24 + localNow.Hour;
        return Schedule.Length == HoursPerWeek && Schedule[index];
    }

    public bool IsOffPeak(DateTime localNow)
    {
        int h = localNow.Hour;
        return OffPeakStartHour > OffPeakEndHour
            ? h >= OffPeakStartHour || h < OffPeakEndHour     // wraps midnight
            : h >= OffPeakStartHour && h < OffPeakEndHour;
    }

    public void Clamp()
    {
        PowerPercent = Math.Clamp(PowerPercent, 20, 100);
        FreeSharePercent = Math.Clamp(FreeSharePercent, 0, 100);
        TempCeilingC = Math.Clamp(TempCeilingC, 60, 95);
        UploadCapMbps = Math.Clamp(UploadCapMbps, 1, 10_000);
        OffPeakStartHour = Math.Clamp(OffPeakStartHour, 0, 23);
        OffPeakEndHour = Math.Clamp(OffPeakEndHour, 0, 23);
        if (TariffPerKwh < 0) TariffPerKwh = 0;
        if (OffPeakRatePerKwh < 0) OffPeakRatePerKwh = 0;
        if (Schedule.Length != HoursPerWeek) Schedule = DefaultSchedule();
        AcceptedJobTypes ??= [];
    }

    // --- persistence -------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string DefaultPath(string dataDirectory) => Path.Combine(dataDirectory, "settings.json");

    public static NodeSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<NodeSettings>(File.ReadAllText(path), Json);
                if (loaded is not null)
                {
                    loaded.Clamp();
                    return loaded;
                }
            }
        }
        catch
        {
            // A corrupt settings file must not stop the node from starting.
            // Defaults are safe; the owner's choices are one screen away.
        }
        return new NodeSettings();
    }

    public void Save(string path)
    {
        Clamp();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);   // never a half-written file
    }
}
