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
        if (!string.IsNullOrWhiteSpace(relayUrl)) document["RelayUrl"] = relayUrl;

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

    private static Dictionary<string, JsonElement> Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path)) ?? [];
        }
        catch
        {
            // Unreadable means we are about to replace it with something valid.
            return [];
        }
    }
}
