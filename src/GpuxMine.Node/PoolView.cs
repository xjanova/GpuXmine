using GpuxMine.Core.Licensing;

namespace GpuxMine.Node;

/// <summary>Where the node stands with XMAN Studio's status call.</summary>
public enum PoolOutcome
{
    /// <summary>Not asked yet this launch.</summary>
    NotAsked,

    /// <summary>No identity to ask with.</summary>
    Unpaired,

    /// <summary>The last call answered.</summary>
    Ok,

    /// <summary>The last call got no answer. <see cref="PoolView.Last"/> may still hold an older one.</summary>
    Unavailable,

    /// <summary>XMAN Studio does not accept this machine's identity. Only re-pairing fixes it.</summary>
    IdentityRejected,

    /// <summary>The website predates the status call.</summary>
    NotSupported,
}

/// <summary>
/// The pool's view of this machine and its owner's money, as the node last
/// heard it from XMAN Studio — and how to say it to the owner.
/// </summary>
/// <remarks>
/// <para>
/// Before this the node's screen was built only from what the node knew about
/// itself, so it read "SHARING · ACTIVE" on a machine the pool had reaped, an
/// administrator had suspended, or XMAN Studio had failed to push for a day.
/// The owner had no way to learn why nothing arrived.
/// </para>
/// <para>
/// A failed call keeps the last answer (<see cref="Last"/>) and says how old
/// it is, rather than blanking the screen: an outage of the website says
/// nothing new about the machine. Immutable, so the window can tell a new
/// answer from the one it has already drawn by reference.
/// </para>
/// </remarks>
public sealed record PoolView
{
    public static readonly PoolView None = new() { Outcome = PoolOutcome.NotAsked };

    public PoolOutcome Outcome { get; init; }

    /// <summary>The last answer XMAN Studio gave, kept through later failures.</summary>
    public NodeStatus? Last { get; init; }

    /// <summary>When <see cref="Last"/> was received.</summary>
    public DateTimeOffset? LastOkAt { get; init; }

    /// <summary>When XMAN Studio was last asked, whatever it said.</summary>
    public DateTimeOffset? CheckedAt { get; init; }

    /// <summary>Why the latest call gave nothing, in the owner's words. Null after a good answer.</summary>
    public string? Problem { get; init; }

    /// <summary>How many of this machine's jobs the latest answer changed in the ledger.</summary>
    public int LedgerChanges { get; init; }

    public NodeDispatch? Node => Last?.Node;
    public EarningsSummary? Earnings => Last?.Earnings;

    /// <summary>An administrator suspended the machine at XMAN Studio. No work is sent to it.</summary>
    public bool Suspended => Node?.Suspended == true || Node?.DispatchStatus == "suspended";

    /// <summary>
    /// The pool is not going to send this machine work until somebody acts —
    /// what the Dashboard's connection badge must not paper over with "ACTIVE".
    /// </summary>
    public bool Blocked => Outcome == PoolOutcome.IdentityRejected
        || Suspended
        || Node?.DispatchStatus is "retired" or "rejected";

    /// <summary>One line: where this machine stands with the pool.</summary>
    public string Headline => Outcome switch
    {
        PoolOutcome.Unpaired => "ยังไม่ได้ลงทะเบียนเครื่อง",
        PoolOutcome.NotSupported => "XMAN Studio ยังไม่ส่งสถานะ pool ให้โปรแกรม",
        PoolOutcome.IdentityRejected => "XMAN Studio ไม่รู้จักเครื่องนี้แล้ว",
        _ when Last is null && Outcome == PoolOutcome.NotAsked => "กำลังอ่านสถานะจาก XMAN Studio…",
        _ when Last is null => "ยังอ่านสถานะจาก XMAN Studio ไม่ได้",
        _ => DescribeDispatch(Node),
    };

    /// <summary>The detail under the headline: the pool's own note, or why there is none.</summary>
    public string? Detail => Outcome switch
    {
        PoolOutcome.Unpaired => "ขอรหัสจับคู่จากหน้าเครื่องของฉันบนเว็บ แล้วกรอกในหน้า Settings",
        PoolOutcome.NotSupported => "ดูสถานะและรายได้ของเครื่องได้ที่หน้า GPUxMINE บนเว็บ XMAN Studio",
        PoolOutcome.IdentityRejected => null,   // said by Alert, which is louder
        _ when Last is null => Problem,
        _ => Suspended
            ? (string.IsNullOrWhiteSpace(Node?.SuspendedReason) ? Node?.DispatchNote : $"เหตุผล: {Node!.SuspendedReason}")
            : Node?.DispatchNote,
    };

    /// <summary>
    /// Something only the owner (or an administrator they contact) can fix.
    /// Null when there is nothing for them to do.
    /// </summary>
    public string? Alert
    {
        get
        {
            if (Outcome == PoolOutcome.IdentityRejected)
                return "XMAN Studio ไม่รู้จักตัวตนเครื่องนี้แล้ว (ถูกถอดออกจากบัญชี หรือจับคู่ใหม่ไปแล้ว) — ลงทะเบียนเครื่องใหม่ในหน้า Settings";

            if (Suspended)
            {
                string why = string.IsNullOrWhiteSpace(Node?.SuspendedReason) ? "" : $" ({Node!.SuspendedReason.Trim()})";
                return $"XMAN Studio ระงับเครื่องนี้ไว้{why} — pool ไม่ส่งงานมาจนกว่าผู้ดูแลจะยกเลิก ติดต่อผู้ดูแลระบบ";
            }

            return Node?.DispatchStatus switch
            {
                "retired" => "ผู้ดูแลระบบ AIXMAN ปลดเครื่องนี้ออกจากการรับงาน — ติดต่อผู้ดูแลระบบ",
                "rejected" => "relay ไม่รับ token ของเครื่องนี้แล้ว — ลงทะเบียนเครื่องใหม่ในหน้า Settings",
                _ => null,
            };
        }
    }

    /// <summary>
    /// The pool's verdict in the owner's words. The raw codes come from two
    /// systems — aixman's push reply and XMAN Studio's own — and none of them
    /// mean anything to somebody who just wants to know whether work is coming.
    /// </summary>
    public static string DescribeDispatch(NodeDispatch? node)
    {
        if (node is null) return "XMAN Studio ยังไม่มีข้อมูล pool ของเครื่องนี้";
        if (node.Suspended) return "ถูกระงับโดย XMAN Studio";

        return node.DispatchStatus switch
        {
            "eligible" => node.WorkerStatus switch
            {
                "ready" => "อยู่ใน pool · พร้อมรับงาน",
                "busy" => "อยู่ใน pool · กำลังทำงานให้ลูกค้า",
                "warming" or "provisioning" => "อยู่ใน pool · pool กำลังตรวจความพร้อมของเครื่อง",
                "draining" => "อยู่ใน pool · pool พักการส่งงานชั่วคราว",
                "terminated" or "failed" => "pool พักเครื่องนี้ไว้ — จะรับกลับเองเมื่อเครื่องพร้อม",
                null => "pool รับเครื่องนี้แล้ว",
                string other => $"อยู่ใน pool · สถานะ {other}",
            },
            "unassessed" => "pool รอผลประเมินเครื่อง",
            "no-matching-model" => "ยังไม่มีงานที่ตรงกับเครื่องนี้",
            "offline" => "pool เห็นเครื่องนี้ออฟไลน์",
            "retired" => "ผู้ดูแลระบบปลดเครื่องนี้ออกจาก pool",
            "suspended" => "ถูกระงับโดย XMAN Studio",
            "rejected" => "relay ไม่รับ token ของเครื่องนี้",
            "unconfigured" => "XMAN Studio ยังไม่ได้เชื่อมกับ pool",
            "error" => "XMAN Studio ส่งเครื่องนี้เข้า pool ไม่สำเร็จ — จะลองใหม่เอง",
            null => "ยังไม่ได้ส่งเครื่องนี้เข้า pool",
            string other => $"สถานะ pool: {other}",
        };
    }

    /// <summary>A time as the screens show it: the clock today, the date and clock before that.</summary>
    /// <remarks>Invariant, so a Thai machine does not print the year in the Buddhist era on one line and not the next.</remarks>
    public static string When(DateTimeOffset at)
    {
        DateTimeOffset local = at.ToLocalTime();
        return local.ToString(local.Date == DateTimeOffset.Now.Date ? "HH:mm" : "d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A money figure as the screens show it: baht with two decimals, or a dash when it is not known.</summary>
    public static string Baht(long? satang) => satang is { } s ? $"฿{s / 100m:N2}" : "—";

    /// <summary>A settlement state in the owner's words — the History screen's per-job column.</summary>
    public static string DescribePayoutStatus(string? status, int? holdHours = null) => status switch
    {
        "pending" => holdHours is { } h ? $"พัก {h} ชม." : "อยู่ในระยะพัก",
        "review" => "รอตรวจสอบ",
        "cleared" => "รอเข้ากระเป๋า",
        "paid" => "เข้ากระเป๋าแล้ว",
        "void" => "ยกเลิก",
        null => "รอตัดยอด",
        string other => other,
    };
}
