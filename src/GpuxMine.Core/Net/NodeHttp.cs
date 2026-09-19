using System.Net.Http.Headers;
using GpuxMine.Core.Updates;

namespace GpuxMine.Core.Net;

/// <summary>
/// The one place an outbound <see cref="HttpClient"/> is made, so every request
/// this product sends says who is sending it.
/// </summary>
/// <remarks>
/// <para>
/// .NET sends no <c>User-Agent</c> header at all unless one is set, so until
/// now every call this node made to XMAN Studio arrived anonymous. That is not
/// a fault today — measured on 2026-09-19, the live site answers an anonymous
/// .NET client with <c>200</c> — but it is a standing liability. The site is
/// behind Cloudflare, and Cloudflare's bot rules judge clients by their
/// signature: in the same session, the identical POST from Python's urllib came
/// back <c>403 Error 1010 — browser_signature_banned</c>, and adding any user
/// agent made it <c>200</c>. A rule that bans unidentified clients is one
/// dashboard toggle away, and the day it is thrown the whole fleet stops
/// registering, stops checking for updates, and stops showing anybody their
/// referral figures — quietly, because every one of those calls is written to
/// fail soft.
/// </para>
/// <para>
/// A real version in the string, so the other side's logs can tell one build
/// from another and so the platform can see what its fleet is actually running.
/// </para>
/// <para>
/// The relay does not need it: <c>relay.xman4289.com</c> resolves straight to
/// the origin and never sees Cloudflare. It is set there anyway, because a
/// server log that names its clients is worth more than one that does not.
/// </para>
/// </remarks>
public static class NodeHttp
{
    /// <summary>What this product calls itself on the wire.</summary>
    public static string UserAgent { get; } = $"GPUxMINE/{SelfUpdater.CurrentVersion} (Windows; +https://xman4289.com)";

    public static HttpClient Create(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        Identify(http.DefaultRequestHeaders);
        return http;
    }

    /// <summary>Puts the agent line on headers that belong to something else — a
    /// websocket's options, a request built by hand.</summary>
    public static void Identify(HttpHeaders headers)
    {
        // Without validation: a malformed agent string must not be the reason a
        // node cannot start.
        headers.TryAddWithoutValidation("User-Agent", UserAgent);
    }
}
