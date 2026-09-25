using System.Text.Json;

namespace GpuxMine.Protocol;

/// <summary>What <see cref="TunnelAllowlist.Check"/> decided about a request.</summary>
public enum TunnelVerdict
{
    /// <summary>Outside the list. Answer 403 and do not forward it.</summary>
    Denied,

    /// <summary>On the list.</summary>
    Allowed,

    /// <summary>On the list only for some bodies — pass the body to <see cref="TunnelAllowlist.IsAllowedBody"/>.</summary>
    NeedsBodyCheck,
}

/// <summary>
/// The requests aixman may send down the tunnel to a node's ComfyUI. Deny by
/// default: everything not written here is refused.
/// </summary>
/// <remarks>
/// <para>
/// The tunnel ends in the owner's own ComfyUI, and a stock ComfyUI install
/// answers far more than image generation: ComfyUI-Manager installs custom
/// nodes (arbitrary code), <c>/userdata</c> writes files, <c>/free</c> and
/// <c>/history {clear}</c> wipe the owner's own work. None of that is anything
/// aixman does, so none of it is reachable — whoever holds a worker's tunnel
/// token, including someone who stole aixman's database.
/// </para>
/// <para>
/// The same list is enforced twice: by the relay, which protects agents
/// already installed that predate it, and by the agent, which protects a node
/// talking to a relay that predates it. It lives here so the two cannot drift.
/// </para>
/// <para>
/// It decides routes, not whose work a route reaches: the relay cannot see
/// the owner's ComfyUI. <c>/history</c>, <c>/queue</c>, <c>/view</c>,
/// <c>/interrupt</c> and <c>/upload/image</c> are allowed here because aixman
/// needs them for its own jobs; the agent holds each of them to the prompts
/// that came down the tunnel (<c>ComfyRuntime.Scope.cs</c>). An agent that
/// predates that has only this list.
/// </para>
/// <para>
/// Paths are checked exactly as they travel on the tunnel (path plus query, as
/// the relay forwards them), and any segment that could walk out of the route
/// it names — <c>..</c>, an encoded slash, a backslash — is refused outright
/// rather than normalised, because ComfyUI's router and this list would have
/// to agree on the normalisation for that to be safe.
/// </para>
/// </remarks>
public static class TunnelAllowlist
{
    /// <summary>The answer body for a refused request, so every refusal looks the same to aixman.</summary>
    public const string DeniedError = "path-not-allowed";

    private static readonly HashSet<string> ViewTypes = new(StringComparer.Ordinal) { "output", "input", "temp" };
    private static readonly HashSet<string> HistoryListKeys = new(StringComparer.Ordinal) { "max_items", "offset" };

    /// <summary>Decides on method and path alone. See <see cref="TunnelVerdict"/>.</summary>
    public static TunnelVerdict Check(string method, string pathAndQuery)
    {
        if (string.IsNullOrEmpty(pathAndQuery) || pathAndQuery[0] != '/') return TunnelVerdict.Denied;

        string[] parts = pathAndQuery.Split('?', 2);
        string path = parts[0];
        string query = parts.Length > 1 ? parts[1] : "";

        if (path.Length > 1024 || query.Length > 4096) return TunnelVerdict.Denied;

        string[] segments = path[1..].Split('/');
        foreach (string segment in segments)
        {
            if (!IsSafeSegment(segment)) return TunnelVerdict.Denied;
        }

        bool get = method == "GET";
        bool post = method == "POST";

        switch (segments)
        {
            case ["object_info"] when get:
            case ["object_info", _] when get:
            case ["prompt"] when post:
            case ["queue"] when get:
            case ["upload", "image"] when post:
            case ["interrupt"] when post:
                return TunnelVerdict.Allowed;

            case ["history", var promptId] when get:
                return IsId(promptId) ? TunnelVerdict.Allowed : TunnelVerdict.Denied;

            case ["history"] when get:
                return QueryKeysWithin(query, HistoryListKeys) ? TunnelVerdict.Allowed : TunnelVerdict.Denied;

            case ["history"] when post:
                // Deleting aixman's own finished prompts is how a job is
                // cleaned off the owner's machine; clearing the whole history
                // would take the owner's own work with it.
                return TunnelVerdict.NeedsBodyCheck;

            case ["view"] when get:
                return IsSafeViewQuery(query) ? TunnelVerdict.Allowed : TunnelVerdict.Denied;

            case ["aixman", .. var rest] when (get || post) && rest.Length > 0:
                // The agent's own surface (ready, progress, log, purge). It
                // never reaches ComfyUI, so what matters is only that the
                // names are plain.
                foreach (string name in rest)
                {
                    if (!IsId(name)) return TunnelVerdict.Denied;
                }
                return TunnelVerdict.Allowed;

            default:
                return TunnelVerdict.Denied;
        }
    }

    /// <summary>The second half of <see cref="TunnelVerdict.NeedsBodyCheck"/>.</summary>
    public static bool IsAllowedBody(string method, string pathAndQuery, ReadOnlySpan<byte> body)
    {
        string path = pathAndQuery.Split('?', 2)[0];
        if (method == "POST" && path == "/history") return IsAllowedHistoryPost(body);
        return false;
    }

    /// <summary>Method, path and — where it matters — body, in one call.</summary>
    public static bool IsAllowed(string method, string pathAndQuery, ReadOnlySpan<byte> body)
        => Check(method, pathAndQuery) switch
        {
            TunnelVerdict.Allowed => true,
            TunnelVerdict.NeedsBodyCheck => IsAllowedBody(method, pathAndQuery, body),
            _ => false,
        };

    /// <summary>
    /// <c>{"delete": ["id", ...]}</c> and/or <c>{"clear": false}</c>, nothing else.
    /// </summary>
    public static bool IsAllowedHistoryPost(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            using JsonDocument doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            foreach (JsonProperty property in doc.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "delete":
                        if (property.Value.ValueKind != JsonValueKind.Array) return false;
                        foreach (JsonElement id in property.Value.EnumerateArray())
                        {
                            if (id.ValueKind != JsonValueKind.String || !IsId(id.GetString() ?? "")) return false;
                        }
                        break;

                    case "clear":
                        if (property.Value.ValueKind != JsonValueKind.False) return false;
                        break;

                    default:
                        return false;
                }
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// One path segment that names exactly what it says. Refuses dot segments,
    /// anything percent-encoded (the relay sees <c>%2F</c> undecoded, ComfyUI
    /// may not), backslashes, a <c>#</c> the next hop would read as a fragment,
    /// and control characters.
    /// </summary>
    private static bool IsSafeSegment(string segment)
    {
        if (segment.Length is 0 or > 256) return false;
        if (segment is "." or "..") return false;
        if (segment.Contains("..", StringComparison.Ordinal)) return false;

        foreach (char c in segment)
        {
            if (c < 0x20 || c == 0x7f) return false;
            if (c is '\\' or '%' or ':' or '#') return false;
        }
        return true;
    }

    /// <summary>Prompt ids, <c>/aixman/*</c> names: letters, digits, dash, underscore, dot.</summary>
    private static bool IsId(string value)
    {
        if (value.Length is 0 or > 128) return false;
        if (value.Contains("..", StringComparison.Ordinal)) return false;
        foreach (char c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) return false;
        }
        return true;
    }

    private static bool QueryKeysWithin(string query, HashSet<string> allowed)
    {
        foreach (var (key, _) in ParseQuery(query))
        {
            if (!allowed.Contains(key)) return false;
        }
        return true;
    }

    /// <summary>
    /// <c>/view</c> reads a file by name, so the name is the dangerous part:
    /// no traversal, no absolute or drive-rooted path, and only the three
    /// folders ComfyUI itself serves from.
    /// </summary>
    private static bool IsSafeViewQuery(string query)
    {
        bool sawFilename = false;
        foreach (var (key, value) in ParseQuery(query))
        {
            switch (key)
            {
                case "filename":
                case "subfolder":
                    if (key == "filename")
                    {
                        if (value.Length == 0) return false;
                        sawFilename = true;
                    }
                    if (value.Length > 512) return false;
                    if (value.Contains("..", StringComparison.Ordinal)) return false;
                    if (value.StartsWith('/') || value.Contains('\\') || value.Contains(':')) return false;
                    if (value.Any(c => c < 0x20 || c == 0x7f)) return false;
                    break;

                case "type":
                    if (!ViewTypes.Contains(value)) return false;
                    break;
            }
        }
        return sawFilename;
    }

    private static IEnumerable<(string Key, string Value)> ParseQuery(string query)
    {
        if (string.IsNullOrEmpty(query)) yield break;
        foreach (string pair in query.Split('&'))
        {
            if (pair.Length == 0) continue;
            string[] kv = pair.Split('=', 2);
            yield return (Unescape(kv[0]), kv.Length > 1 ? Unescape(kv[1]) : "");
        }
    }

    private static string Unescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            // Unreadable escaping reads as a name nobody can have meant; the
            // checks above then refuse it on its raw characters.
            return value;
        }
    }
}
