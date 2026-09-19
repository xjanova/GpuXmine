using Microsoft.Extensions.Configuration;

namespace GpuxMine.Node;

/// <summary>
/// Builds the node's options from every place a setting can come from.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the window and the headless agent so the two cannot drift: a
/// setting that works in one has to work in the other, and the pairing file is
/// read by both.
/// </para>
/// <para>
/// <b>Two passes, and the second one is not optional.</b> The identity file
/// lives inside the data directory, and the data directory is itself a
/// setting — so the first pass exists only to find out where to look. Reading
/// once from the default path meant that a node started with a custom
/// <c>--DataDirectory</c> wrote its credentials to one folder and read them
/// from another: caught by pairing a node into a test folder and watching it
/// come up as the worker from the default one instead.
/// </para>
/// </remarks>
public static class NodeConfiguration
{
    public static NodeOptions Build(string[] args)
    {
        // Pass one: everything except the identity file, to learn where that is.
        NodeOptions first = Compose(args, extraIdentityDirectory: null).Get<NodeOptions>() ?? new NodeOptions();

        string resolved = first.DataDirectory;
        if (string.Equals(resolved, NodeOptions.DefaultDataDirectory(), StringComparison.OrdinalIgnoreCase))
            return Rescued(first);

        // Pass two: the same sources, plus the identity file that actually
        // belongs to this node. Command line and environment still win over it.
        return Rescued(Compose(args, resolved).Get<NodeOptions>() ?? first);
    }

    /// <summary>
    /// Last resort when the configuration came back without an identity but one
    /// is sitting on disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The installed client has been seen starting with an empty worker id
    /// while <c>agent.json</c> was present and correct — twice within twenty
    /// minutes on 2026-09-19, with good starts either side of it from the same
    /// binary. Composing the configuration in isolation has never reproduced
    /// it, so the cause is still open; what is not open is the cost. A node
    /// with no worker id never opens the relay socket. It comes up, shows a
    /// window, and earns nothing until somebody restarts it, and nothing on
    /// screen says why.
    /// </para>
    /// <para>
    /// This does not paper over a node that is genuinely unregistered: it only
    /// fills in what a readable file already says. When there is no file, or it
    /// has no identity in it, the options come back exactly as composed.
    /// </para>
    /// </remarks>
    private static NodeOptions Rescued(NodeOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.WorkerId)) return options;

        foreach (string directory in new[] { options.DataDirectory, NodeOptions.DefaultDataDirectory(), NodeOptions.LegacyDataDirectory() })
        {
            if (NodeIdentityFile.ReadDirect(NodeIdentityFile.PathIn(directory)) is not { } identity) continue;

            return options with
            {
                WorkerId = identity.WorkerId,
                Token = identity.Token,
                RelayUrl = string.IsNullOrWhiteSpace(identity.RelayUrl) ? options.RelayUrl : identity.RelayUrl,
                IdentityRescuedFrom = NodeIdentityFile.PathIn(directory),
            };
        }

        return options;
    }

    private static IConfigurationRoot Compose(string[] args, string? extraIdentityDirectory)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(NodeIdentityFile.FileName, optional: true, reloadOnChange: false)
            // The legacy path first, so the one in the current data folder wins
            // if both exist. The old one lived inside what became the install
            // directory, and an installer cleans that.
            .AddJsonFile(NodeIdentityFile.PathIn(NodeOptions.LegacyDataDirectory()), optional: true, reloadOnChange: false)
            .AddJsonFile(NodeIdentityFile.PathIn(NodeOptions.DefaultDataDirectory()), optional: true, reloadOnChange: false);

        if (extraIdentityDirectory is not null)
            builder.AddJsonFile(NodeIdentityFile.PathIn(extraIdentityDirectory), optional: true, reloadOnChange: false);

        return builder
            .AddEnvironmentVariables("GPUXMINE_")
            .AddCommandLine(args)
            .Build();
    }
}
