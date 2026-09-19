using System.Text.Json;
using System.Text.Json.Serialization;

namespace GpuxMine.Node;

/// <summary>
/// The node's identity on disk: <c>agent.json</c> in the data directory.
/// </summary>
/// <remarks>
/// <para>
/// This file is the reason an installed client survives its own updates.
/// Velopack restarts the executable after applying one, and it does not pass on
/// the arguments the previous process was started with — measured, not assumed:
/// the first real update test came back up logging <c>worker  , runtime …</c>
/// with an empty worker id. Anything passed only on the command line is gone
/// the first time the node updates itself.
/// </para>
/// <para>
/// It lives beside the database, in <c>%AppData%\GPUxMINE</c>, and for the same
/// reason: the old location was inside the folder the installer cleans.
/// </para>
/// </remarks>
public static class NodeIdentityFile
{
    public const string FileName = "agent.json";

    public static string PathIn(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    /// <summary>
    /// Reads the worker id, token and relay straight out of the file, bypassing
    /// the configuration stack entirely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A safety net for a failure that has been seen and is not yet explained.
    /// The installed client occasionally starts with an empty worker id while
    /// this file sits on disk, complete and unchanged — recorded on this
    /// machine at 10:43 and again at 10:59 on 2026-09-19, with a correct start
    /// nine minutes later from the same binary and the same file. Building the
    /// configuration in isolation resolves it every time, with and without
    /// arguments, inside and outside the install directory, so the fault is not
    /// in the paths it looks at.
    /// </para>
    /// <para>
    /// What that run costs is not cosmetic: a node with no worker id never
    /// opens the relay socket, so the machine comes up, looks like it is
    /// running, and earns nothing until somebody restarts it.
    /// </para>
    /// <para>
    /// So when the composed configuration has no identity, the file is read
    /// again by hand. If it has one, that run is rescued; if it genuinely has
    /// none, nothing changes and the node is correctly unregistered.
    /// </para>
    /// </remarks>
    public static (string WorkerId, string Token, string? RelayUrl)? ReadDirect(
        string path, Action<string>? note = null)
    {
        // Read again before giving up.
        //
        // One read was a coin toss. This machine came up unregistered five
        // times on 2026-09-19 with the file present, complete and unchanged,
        // and came up correctly from the same binary minutes later — so
        // whatever the file was busy with, it was busy with it briefly.
        // Something holding a small file in %AppData% for a moment is ordinary
        // on Windows: a scanner, a backup client, a sync agent, an installer
        // hook that has just finished writing next to it. Three tries over
        // roughly a third of a second costs a startup nothing and turns a
        // transient into a non-event.
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    note?.Invoke($"ไม่มีไฟล์ {path}");
                    return null;
                }

                Dictionary<string, JsonElement> values = ReadOrThrow(path);

                string? Find(string key) => values
                    .FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                    .Value is { ValueKind: JsonValueKind.String } element ? element.GetString() : null;

                string? worker = Find("WorkerId");
                string? token = Find("Token");
                if (string.IsNullOrWhiteSpace(worker) || string.IsNullOrWhiteSpace(token))
                {
                    // Readable and genuinely without an identity. Not a fault,
                    // and not worth retrying.
                    note?.Invoke($"{path} อ่านได้แต่ไม่มี WorkerId/Token");
                    return null;
                }

                if (attempt > 1) note?.Invoke($"อ่าน {path} สำเร็จในครั้งที่ {attempt}");
                return (worker, token, Find("RelayUrl"));
            }
            catch (Exception ex)
            {
                // Said out loud, every time. The silent version of this catch is
                // the reason a node coming up unregistered went a full day
                // without anybody being able to name the cause.
                note?.Invoke($"อ่าน {path} ไม่สำเร็จ (ครั้งที่ {attempt}): {ex.GetType().Name} {ex.Message}");
                if (attempt < 3) Thread.Sleep(120);
            }
        }

        // A rescue that throws is worse than no rescue: the node would stop
        // starting at all, which is the thing this exists to prevent.
        return null;
    }

    /// <summary>
    /// Writes the credentials, keeping any settings already in the file.
    /// </summary>
    /// <remarks>
    /// Merged rather than replaced because an owner may have hand-edited
    /// <c>ComfyUrl</c> or a licence key into it, and pairing a machine is no
    /// reason to throw that away.
    /// </remarks>
    public static void Save(string dataDirectory, string workerId, string token, string? relayUrl)
    {
        Directory.CreateDirectory(dataDirectory);
        string path = PathIn(dataDirectory);

        Dictionary<string, JsonElement> existing = Read(path);
        var document = new Dictionary<string, object?>();

        // The relay we are already using, kept in case the caller has no
        // opinion. Read before the loop drops it, because the loop is what
        // used to lose it.
        string? previousRelay = null;
        foreach (var (key, value) in existing)
        {
            if (key.Equals("RelayUrl", StringComparison.OrdinalIgnoreCase)
                && value.ValueKind == JsonValueKind.String)
            {
                previousRelay = value.GetString();
            }
        }

        foreach (var (key, value) in existing)
        {
            // Skip the three we are about to write, so a stale value cannot
            // survive under a different spelling of the same key.
            if (key.Equals("WorkerId", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Token", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("RelayUrl", StringComparison.OrdinalIgnoreCase)) continue;
            document[key] = value;
        }

        document["WorkerId"] = workerId;
        document["Token"] = token;

        // Falling back to what was already there, not to nothing.
        //
        // This line used to write the new relay only when it was non-empty —
        // and the loop above had already dropped the old one. So a pairing
        // response that omitted `relay_url` deleted a working relay address,
        // the node fell back to the compiled-in default, and the owner was
        // left looking at a client pointed at localhost with no way to fix it
        // from the interface. Re-registering could break a node that had been
        // earning for weeks.
        string? relay = string.IsNullOrWhiteSpace(relayUrl) ? previousRelay : relayUrl;
        if (!string.IsNullOrWhiteSpace(relay)) document["RelayUrl"] = relay;

        string json = JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });

        // Written whole and then moved into place: a half-written identity file
        // is a node that will not start, and it would be written at exactly the
        // moment the owner is watching.
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, overwrite: true);
    }

    private static Dictionary<string, JsonElement> ReadOrThrow(string path) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path)) ?? [];

    private static Dictionary<string, JsonElement> Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            return ReadOrThrow(path);
        }
        catch
        {
            // Unreadable means we are about to replace it with something valid.
            return [];
        }
    }
}
