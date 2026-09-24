using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using GpuxMine.Protocol;
using Microsoft.AspNetCore.RateLimiting;

namespace GpuxMine.Relay;

/// <summary>
/// The relay's whole HTTP surface, built in one place so the tests run the
/// same routes production does instead of a copy of them.
/// </summary>
public static class RelayHost
{
    /// <param name="configure">
    /// Runs before any configuration is read — where a test swaps in its own
    /// settings and a test server.
    /// </param>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        var options = builder.Configuration.GetSection("Relay").Get<RelayOptions>() ?? new RelayOptions();
        options.StorePath ??= Path.Combine(AppContext.BaseDirectory, "data", "workers.json");

        // No default admin key: a relay that ships with a known one is a relay
        // anybody can enrol nodes on. Absent means "mint a fresh one and say so once".
        if (string.IsNullOrEmpty(options.AdminKey))
            options.AdminKey = Environment.GetEnvironmentVariable("GPUXMINE_ADMIN_KEY");
        bool generatedAdminKey = string.IsNullOrEmpty(options.AdminKey);
        if (generatedAdminKey)
            options.AdminKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        if (string.IsNullOrEmpty(options.ObserverKey))
            options.ObserverKey = Environment.GetEnvironmentVariable("GPUXMINE_OBSERVER_KEY");

        // Kestrel's own default is 30,000,000 bytes and applied silently; the
        // relay's limit is written down, and enforced by the tunnel handler as
        // well so it holds under any server.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = options.MaxRequestBodyBytes);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(sp => new WorkerStore(options.StorePath, sp.GetRequiredService<ILoggerFactory>().CreateLogger("store")));
        builder.Services.AddSingleton<AgentRegistry>();
        builder.Services.AddSingleton(new RelayBudget(options.BufferBudgetBytes));
        builder.Services.AddHostedService<SessionSweeper>();
        builder.Services.AddRateLimiter(limiter => ConfigureRateLimits(limiter, options));

        // Behind a reverse proxy the relay sees plain HTTP on localhost, and the
        // enrolment reply is built from the request's own scheme and host. Without
        // this it hands every node `ws://relay…/agent` and every endpoint
        // `http://relay…/w/…` — the agent would connect unencrypted over the open
        // internet, and aixman rejects a non-HTTPS endpoint outright, so a node would
        // enrol and then never be dispatched to.
        //
        // Opt-in, because trusting X-Forwarded-* from anyone lets a caller claim any
        // scheme and host it likes. Set Relay:TrustProxy only where a proxy really is
        // in front.
        if (options.TrustProxy)
        {
            builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
            {
                forwarded.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
                                           | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost
                                           | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor;
                // The proxy is on the same machine and is the only hop we accept these
                // from; the defaults would refuse anything not in a known-proxy list.
                forwarded.KnownIPNetworks.Clear();
                forwarded.KnownProxies.Clear();
            });
        }

        var app = builder.Build();

        if (options.TrustProxy) app.UseForwardedHeaders();
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("relay");

        if (generatedAdminKey)
            log.LogWarning("No admin key configured — generated one for this run: {AdminKey}", options.AdminKey);

        // After the forwarded headers, so limits count the real caller and not the proxy.
        app.UseRateLimiter();

        var webSockets = new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) };
        if (options.KeepAliveTimeoutSeconds > 0)
            webSockets.KeepAliveTimeout = TimeSpan.FromSeconds(options.KeepAliveTimeoutSeconds);
        app.UseWebSockets(webSockets);

        MapRoutes(app, options, log);
        return app;
    }

    // Headers that describe this hop only. Forwarding them corrupts the next hop's
    // framing — Content-Length in particular, which the receiving stack recomputes.
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "keep-alive", "proxy-authenticate", "proxy-authorization",
        "te", "trailer", "transfer-encoding", "upgrade", "host", "content-length",
    };

    // Headers the node has no business seeing: the relay's own credentials, and
    // where aixman's requests come from.
    private static readonly HashSet<string> NotForTheNode = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "x-admin-key", "x-observer-key", "cookie",
        "x-forwarded-for", "x-forwarded-proto", "x-forwarded-host", "x-real-ip", "forwarded",
    };

    private static bool KeyMatches(string presented, string? expected)
        => !string.IsNullOrEmpty(expected)
           && presented.Length > 0
           && CryptographicOperations.FixedTimeEquals(
               System.Text.Encoding.UTF8.GetBytes(presented),
               System.Text.Encoding.UTF8.GetBytes(expected));

    private static bool IsAdmin(HttpRequest request, RelayOptions options)
        => KeyMatches(request.Headers["X-Admin-Key"].ToString(), options.AdminKey);

    /// <summary>
    /// The admin key, or the observer key in either header — the observer key
    /// is accepted as <c>X-Admin-Key</c> too, so a caller already sending that
    /// header can be moved to the weaker key without a code change.
    /// </summary>
    private static bool IsObserver(HttpRequest request, RelayOptions options)
    {
        if (IsAdmin(request, options)) return true;
        return KeyMatches(request.Headers["X-Observer-Key"].ToString(), options.ObserverKey)
               || KeyMatches(request.Headers["X-Admin-Key"].ToString(), options.ObserverKey);
    }

    private static string? BearerOf(HttpRequest request)
    {
        string header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
    }

    private static void MapRoutes(WebApplication app, RelayOptions options, ILogger log)
    {
        app.MapGet("/healthz", (AgentRegistry registry) => Results.Ok(new
        {
            ok = true,
            online = registry.All().Count,
            utc = DateTimeOffset.UtcNow,
        }));

        // --- enrolment -------------------------------------------------------------
        // M1 stand-in for the XMAN ID device flow that lands in M2. The operator runs
        // this once per node and pastes the result into the agent and into aixman.
        app.MapPost("/enroll", (HttpRequest request, WorkerStore store) =>
        {
            if (!IsAdmin(request, options)) return Results.Unauthorized();

            string? label = request.Query["label"];
            IssuedTokens issued = store.Enroll(label, options.IssueTunnelTokens);
            log.LogInformation("Enrolled worker {WorkerId} ({Label})", issued.Record.WorkerId, label);
            return Results.Ok(IssuedReply(request, issued));
        });

        app.MapGet("/admin/workers", (HttpRequest request, WorkerStore store, AgentRegistry registry) =>
        {
            if (!IsObserver(request, options)) return Results.Unauthorized();

            return Results.Ok(store.All().Select(w =>
            {
                AgentSession? session = registry.Get(w.WorkerId);
                AgentTelemetry? telemetry = session?.Telemetry;
                return new
                {
                    w.WorkerId,
                    w.Label,
                    w.EnrolledAt,
                    online = session is not null,
                    agentVersion = session?.AgentVersion,
                    connectedAt = session?.ConnectedAt,
                    lastSeenAt = registry.LastSeen(w.WorkerId),
                    telemetry,
                    // Pass-through from the node's heartbeat. Null means the
                    // relay has not heard — offline, or an agent too old to
                    // say — and must not be read as "idle" or "willing".
                    busy = telemetry?.Busy,
                    accepting = telemetry is null ? (bool?)null : telemetry.Accepting,
                    disabled = w.Disabled,
                    disabledAt = w.DisabledAt,
                    disabledReason = w.DisabledReason,
                    // False for a worker enrolled before tokens were split: its
                    // agent token still opens the tunnel until it is given one.
                    hasTunnelToken = w.TunnelTokenHash is not null,
                };
            }));
        });

        // Deleting is idempotent on purpose: the caller is XMAN Studio forgetting a
        // node, and "it was already gone" is the outcome it wanted, not an error
        // to retry forever.
        app.MapDelete("/admin/workers/{workerId}", async (HttpRequest request, string workerId, WorkerStore store, AgentRegistry registry) =>
        {
            if (!IsAdmin(request, options)) return Results.Unauthorized();

            bool existed = store.Delete(workerId);
            await registry.DisconnectAsync(workerId, WebSocketCloseStatus.PolicyViolation, "worker deleted");
            registry.Forget(workerId);
            if (existed) log.LogWarning("Deleted worker {WorkerId}", workerId);
            return Results.Ok(new { deleted = true, workerId, existed });
        });

        app.MapPost("/admin/workers/{workerId}/disable", async (HttpRequest request, string workerId, WorkerStore store, AgentRegistry registry) =>
        {
            if (!IsAdmin(request, options)) return Results.Unauthorized();

            string? reason = request.Query["reason"];
            if (reason is { Length: > 255 }) reason = reason[..255];

            WorkerRecord? record = store.SetDisabled(workerId, disabled: true, reason);
            if (record is null) return UnknownWorker(workerId);

            await registry.DisconnectAsync(workerId, WebSocketCloseStatus.PolicyViolation, "worker disabled");
            log.LogWarning("Disabled worker {WorkerId}: {Reason}", workerId, reason);
            return Results.Ok(new { workerId, disabled = true, disabledAt = record.DisabledAt, disabledReason = record.DisabledReason });
        });

        app.MapPost("/admin/workers/{workerId}/enable", (HttpRequest request, string workerId, WorkerStore store) =>
        {
            if (!IsAdmin(request, options)) return Results.Unauthorized();

            WorkerRecord? record = store.SetDisabled(workerId, disabled: false, reason: null);
            if (record is null) return UnknownWorker(workerId);

            log.LogInformation("Enabled worker {WorkerId}", workerId);
            return Results.Ok(new { workerId, disabled = false });
        });

        // Both tokens by default, which cuts off anyone holding either old one —
        // including the node itself, until it is paired again. `?only=tunnel`
        // re-issues aixman's alone and leaves the node connected: the way a worker
        // enrolled before the split gets a tunnel token of its own.
        app.MapPost("/admin/workers/{workerId}/rotate", async (HttpRequest request, string workerId, WorkerStore store, AgentRegistry registry) =>
        {
            if (!IsAdmin(request, options)) return Results.Unauthorized();

            string? only = request.Query["only"];
            if (only is not null && only != "tunnel")
                return Results.BadRequest(new { error = "only=tunnel is the one partial rotation" });
            bool tunnelOnly = only == "tunnel";

            IssuedTokens? issued = store.Rotate(workerId, tunnelOnly);
            if (issued is null) return UnknownWorker(workerId);

            if (!tunnelOnly)
                await registry.DisconnectAsync(workerId, WebSocketCloseStatus.PolicyViolation, "worker credentials rotated");

            log.LogWarning("Rotated {Which} for worker {WorkerId}", tunnelOnly ? "the tunnel token" : "both tokens", workerId);
            return Results.Ok(IssuedReply(request, issued));
        });

        // --- the agent's dial-out --------------------------------------------------
        app.MapGet("/agent", async (HttpContext context, WorkerStore store, AgentRegistry registry, RelayBudget budget, IHostApplicationLifetime life) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            string workerId = context.Request.Headers["X-Worker-Id"].ToString();
            string? token = BearerOf(context.Request);
            string? remoteIp = context.Connection.RemoteIpAddress?.ToString();

            WorkerAuth auth = string.IsNullOrEmpty(workerId) || token is null
                ? WorkerAuth.Unknown
                : store.AuthenticateAgent(workerId, token);

            if (auth != WorkerAuth.Ok)
            {
                log.LogWarning("Rejected agent connection for {WorkerId} from {Ip}: {Why}",
                    string.IsNullOrEmpty(workerId) ? "(none)" : workerId, remoteIp, auth);
                await RefuseAsync(context.Response, auth);
                return;
            }

            using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
            string? agentVersion = context.Request.Headers["X-Agent-Version"].ToString();
            if (agentVersion is { Length: > 64 }) agentVersion = agentVersion[..64];

            var session = new AgentSession(workerId, agentVersion, remoteIp, socket, options, budget.Budget, log);

            try
            {
                // Before the session is registered, so the agent has heard what
                // this relay understands before any request can reach it.
                await session.SendAsync(new TunnelHeader
                {
                    Kind = FrameKind.HelloAck,
                    WorkerId = workerId,
                    Caps = options.StreamReplies ? [TunnelCaps.ResponseStreaming] : null,
                    MaxChunkBytes = options.StreamReplies ? WireCodec.MaxChunkBytes : null,
                }, ReadOnlyMemory<byte>.Empty, context.RequestAborted);

                registry.Add(session);

                // An operator may have disabled, deleted or rotated the worker
                // between the check above and the registration; their
                // disconnect would have found nothing to close.
                if (store.AuthenticateAgent(workerId, token!) != WorkerAuth.Ok)
                {
                    await registry.DisconnectAsync(session, WebSocketCloseStatus.PolicyViolation, "worker credentials changed");
                    return;
                }

                log.LogInformation("Worker {WorkerId} online (agent {Version})", workerId, agentVersion);
                await session.RunAsync(life.ApplicationStopping);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Gone before it got going.
            }
            finally
            {
                registry.Remove(session);
                await session.DisposeAsync();
                log.LogInformation("Worker {WorkerId} offline", workerId);
            }
        });

        // --- the tunnel aixman talks to -------------------------------------------
        app.Map("/w/{workerId}/{**path}", (HttpContext context, string workerId, string? path, WorkerStore store, AgentRegistry registry, RelayBudget budget)
            => TunnelAsync(context, workerId, path, store, registry, budget.Budget, options, log));
    }

    private static object IssuedReply(HttpRequest request, IssuedTokens issued)
    {
        string origin = $"{request.Scheme}://{request.Host}";
        return new
        {
            workerId = issued.Record.WorkerId,
            // The node's credential, for /agent only. Null after a tunnel-only rotation.
            token = issued.AgentToken,
            // aixman's credential, for /w/ only. The node never needs it.
            tunnelToken = issued.TunnelToken,
            // Spelled out rather than a string replace: this is the URL every node
            // in the fleet dials for the life of its enrolment, and "https" → "wss"
            // by substring is the kind of cleverness that silently produces
            // "ws://" the day a host contains those four letters.
            agentRelayUrl = (request.IsHttps ? "wss://" : "ws://") + request.Host + "/agent",
            // Exactly what goes into ai_gpu_workers.endpoint on the aixman side.
            aixmanEndpoint = $"{origin}/w/{issued.Record.WorkerId}",
        };
    }

    private static IResult UnknownWorker(string workerId)
        => Results.NotFound(new { error = "unknown-worker", workerId });

    /// <summary>
    /// 401 for "who are you" (unknown worker, wrong token), 403 for "I know
    /// you, and no" (disabled). A node reads the difference to decide between
    /// asking its owner to pair again and waiting to be switched back on.
    /// </summary>
    private static Task RefuseAsync(HttpResponse response, WorkerAuth auth)
    {
        if (auth == WorkerAuth.Disabled)
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            return response.WriteAsJsonAsync(new { error = "worker-disabled" });
        }
        response.StatusCode = StatusCodes.Status401Unauthorized;
        return response.WriteAsJsonAsync(new { error = "bad worker token" });
    }

    private static async Task TunnelAsync(
        HttpContext context,
        string workerId,
        string? path,
        WorkerStore store,
        AgentRegistry registry,
        ByteBudget budget,
        RelayOptions options,
        ILogger log)
    {
        string? token = BearerOf(context.Request);
        WorkerAuth auth = token is null ? WorkerAuth.Unknown : store.AuthenticateTunnel(workerId, token);
        if (auth != WorkerAuth.Ok)
        {
            await RefuseAsync(context.Response, auth);
            return;
        }

        string method = context.Request.Method;
        string pathAndQuery = "/" + (path ?? string.Empty) + context.Request.QueryString;

        // The same list the agent enforces, applied here too so agents that
        // predate it are protected by the relay they already talk to.
        TunnelVerdict verdict = TunnelAllowlist.Check(method, pathAndQuery);
        if (verdict == TunnelVerdict.Denied)
        {
            log.LogWarning("Refused {Method} {Path} for worker {WorkerId}: not on the tunnel allowlist", method, "/" + path, workerId);
            await PathNotAllowedAsync(context.Response);
            return;
        }

        AgentSession? session = registry.Get(workerId);
        if (session is null)
        {
            // "warming", not "failed": a community node whose owner closed the lid
            // will be back. Telling aixman it failed would have it reap the worker
            // and lose the node for good.
            await OfflineAsync(context.Response, detail: null);
            return;
        }

        long? declared = context.Request.ContentLength;
        if (declared > options.MaxRequestBodyBytes)
        {
            await BodyTooLargeAsync(context.Response, options);
            return;
        }

        // Charged to the relay-wide budget from the moment it is read until it
        // has been sent to the node, so a burst of uploads cannot add up to more
        // than the relay has room for.
        long charged = 0;
        try
        {
            byte[] body;
            if (declared is long length)
            {
                if (!budget.TryReserve(length))
                {
                    await BusyAsync(context.Response);
                    return;
                }
                charged = length;
                body = new byte[length];
                await context.Request.Body.ReadExactlyAsync(body, context.RequestAborted);
            }
            else
            {
                UnsizedBody read = await ReadUnsizedBodyAsync(context, budget, options.MaxRequestBodyBytes);
                charged = read.Charged;
                if (read.TooLarge)
                {
                    await BodyTooLargeAsync(context.Response, options);
                    return;
                }
                if (read.Body is null)
                {
                    await BusyAsync(context.Response);
                    return;
                }
                body = read.Body;
            }

            if (verdict == TunnelVerdict.NeedsBodyCheck && !TunnelAllowlist.IsAllowedBody(method, pathAndQuery, body))
            {
                log.LogWarning("Refused {Method} {Path} for worker {WorkerId}: body not on the tunnel allowlist", method, "/" + path, workerId);
                await PathNotAllowedAsync(context.Response);
                return;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in context.Request.Headers)
            {
                // The relay's own credential must not travel further: the node never
                // needs it, and forwarding it would hand every node a token that opens
                // the tunnel to itself.
                if (HopByHop.Contains(header.Key) || NotForTheNode.Contains(header.Key)) continue;
                headers[header.Key] = header.Value.ToString();
            }

            TunnelExchange exchange;
            try
            {
                exchange = await session.OpenExchangeAsync(method, pathAndQuery, headers, body, context.RequestAborted);
            }
            catch (TunnelOfflineException)
            {
                await OfflineAsync(context.Response, "node disconnected");
                return;
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }
            finally
            {
                // On the wire (or never going to be): the relay no longer holds it.
                budget.Release(charged);
                charged = 0;
            }

            try
            {
                await RelayReplyAsync(context, exchange, log, workerId);
            }
            finally
            {
                session.Forget(exchange);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // aixman hung up; nothing to answer.
        }
        catch (IOException ex) when (!context.RequestAborted.IsCancellationRequested)
        {
            // aixman sent less than it said it would, or more than Kestrel allows.
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = "request body could not be read" });
            }
        }
        catch (IOException)
        {
            // The connection went while the body was being read.
        }
        finally
        {
            budget.Release(charged);
        }
    }

    /// <summary>A body that came without a Content-Length. <see cref="Charged"/> is reserved whatever else happened, and the caller releases it.</summary>
    private readonly record struct UnsizedBody(byte[]? Body, long Charged, bool TooLarge);

    private static async Task<UnsizedBody> ReadUnsizedBodyAsync(HttpContext context, ByteBudget budget, long max)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        long charged = 0;
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            if (buffer.Length + read > max) return new UnsizedBody(null, charged, TooLarge: true);
            if (!budget.TryReserve(read)) return new UnsizedBody(null, charged, TooLarge: false);
            charged += read;
            buffer.Write(chunk, 0, read);
        }
        return new UnsizedBody(buffer.ToArray(), charged, TooLarge: false);
    }

    /// <summary>
    /// Waits for the node's answer and passes it to aixman as it arrives.
    /// </summary>
    /// <remarks>
    /// Before the status line is sent, a failure can still be answered
    /// properly. After it, the only honest thing left is to cut the connection:
    /// ending the response cleanly would hand aixman half an image as if it
    /// were the whole one.
    /// </remarks>
    private static async Task RelayReplyAsync(HttpContext context, TunnelExchange exchange, ILogger log, string workerId)
    {
        CancellationToken aborted = context.RequestAborted;

        ReplyHead head;
        try
        {
            head = await exchange.Head.WaitAsync(aborted);
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            return; // aixman hung up; nothing to answer.
        }
        catch (TunnelOfflineException)
        {
            // The node went away mid-request. Same answer as "never connected":
            // aixman reads `stage: offline` as warming and keeps the worker, where
            // a 504 would look like a slow node and count against it.
            await OfflineAsync(context.Response, "node disconnected");
            return;
        }
        catch (TimeoutException)
        {
            context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            await context.Response.WriteAsJsonAsync(new { error = "node did not answer in time" });
            return;
        }
        catch (Exception ex) when (ex is TunnelProtocolException or OperationCanceledException)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsJsonAsync(new { error = "bad reply from node", detail = ex.Message });
            return;
        }

        if (head.Status is < 100 or > 599)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsJsonAsync(new { error = "bad reply from node", detail = $"status {head.Status}" });
            return;
        }

        context.Response.StatusCode = head.Status;
        foreach (var header in head.Headers)
        {
            if (HopByHop.Contains(header.Key)) continue;
            try
            {
                context.Response.Headers[header.Key] = header.Value;
            }
            catch (InvalidOperationException)
            {
                // A header value no HTTP stack would send (a raw newline). Dropped
                // rather than failing the whole reply over it.
            }
        }

        try
        {
            while (await exchange.ReadAsync(aborted) is { } piece)
            {
                try
                {
                    await context.Response.Body.WriteAsync(piece.Memory, aborted);
                }
                finally
                {
                    exchange.Release(piece);
                }
                exchange.Touch();
            }
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            // aixman hung up mid-reply. Forget() tells the node to stop.
        }
        catch (Exception ex)
        {
            log.LogInformation("Reply from {WorkerId} cut off: {Message}", workerId, ex.Message);
            context.Abort();
        }
    }

    private static Task OfflineAsync(HttpResponse response, string? detail)
    {
        response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return detail is null
            ? response.WriteAsJsonAsync(new { stage = "offline", ready = false })
            : response.WriteAsJsonAsync(new { stage = "offline", ready = false, detail });
    }

    private static Task PathNotAllowedAsync(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status403Forbidden;
        return response.WriteAsJsonAsync(new { error = TunnelAllowlist.DeniedError });
    }

    private static Task BodyTooLargeAsync(HttpResponse response, RelayOptions options)
    {
        response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return response.WriteAsJsonAsync(new { error = "body-too-large", maxBytes = options.MaxRequestBodyBytes });
    }

    /// <summary>
    /// The relay is holding as much as it will for everyone. No <c>stage</c>:
    /// this says nothing about the node, and must not send aixman down the
    /// "node is warming" path.
    /// </summary>
    private static Task BusyAsync(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        response.Headers.RetryAfter = "5";
        return response.WriteAsJsonAsync(new { error = "relay-busy" });
    }

    // --- rate limits ---------------------------------------------------------------

    private static void ConfigureRateLimits(RateLimiterOptions limiter, RelayOptions options)
    {
        limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        limiter.OnRejected = async (rejected, ct) =>
        {
            if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
                rejected.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await rejected.HttpContext.Response.WriteAsJsonAsync(new { error = "rate-limited" }, ct);
        };

        // Two limiters in a chain, so a request has to clear both: in-flight
        // concurrency, then rate over time. Each decides per route which key it
        // counts against, and lets everything else through.
        limiter.GlobalLimiter = PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var (route, key) = Classify(context);
                return route == RouteKind.Tunnel
                    ? RateLimitPartition.GetConcurrencyLimiter("w:" + key, _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = Math.Max(1, options.TunnelConcurrencyPerWorker),
                        QueueLimit = 0,
                    })
                    : RateLimitPartition.GetNoLimiter("-");
            }),
            PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var (route, key) = Classify(context);
                return route switch
                {
                    RouteKind.Tunnel => RateLimitPartition.GetTokenBucketLimiter("w:" + key, _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = Math.Max(1, options.TunnelRequestsPerMinutePerWorker / 5),
                        TokensPerPeriod = Math.Max(1, (int)Math.Ceiling(options.TunnelRequestsPerMinutePerWorker / 60.0)),
                        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }),
                    RouteKind.Agent => RateLimitPartition.GetFixedWindowLimiter("agent-ip:" + key, _ => PerMinute(options.AgentConnectsPerMinutePerIp)),
                    RouteKind.Admin => RateLimitPartition.GetFixedWindowLimiter("admin-ip:" + key, _ => PerMinute(options.AdminRequestsPerMinutePerIp)),
                    _ => RateLimitPartition.GetNoLimiter("-"),
                };
            }),
            PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                // Per worker as well as per address for /agent: two machines
                // sharing one identity each evict the other on connect, and
                // would otherwise reconnect in a loop.
                if (!HttpMethods.IsGet(context.Request.Method) || !context.Request.Path.Equals("/agent", StringComparison.Ordinal))
                    return RateLimitPartition.GetNoLimiter("-");

                string workerId = context.Request.Headers["X-Worker-Id"].ToString();
                if (workerId.Length is 0 or > 64 || context.RequestServices.GetRequiredService<WorkerStore>().Find(workerId) is null)
                    return RateLimitPartition.GetNoLimiter("-");

                return RateLimitPartition.GetFixedWindowLimiter("agent-worker:" + workerId, _ => PerMinute(options.AgentConnectsPerMinutePerWorker));
            }));
    }

    private static FixedWindowRateLimiterOptions PerMinute(int permits) => new()
    {
        PermitLimit = Math.Max(1, permits),
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        AutoReplenishment = true,
    };

    private enum RouteKind { Other, Tunnel, Agent, Admin }

    /// <summary>
    /// Which limit a request counts against. A tunnel request is keyed by its
    /// worker only when the relay knows that worker — otherwise any made-up id
    /// would get a fresh allowance — and falls back to the caller's address.
    /// </summary>
    private static (RouteKind Route, string Key) Classify(HttpContext context)
    {
        string path = context.Request.Path.Value ?? "";
        string ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (path.StartsWith("/w/", StringComparison.Ordinal))
        {
            int end = path.IndexOf('/', 3);
            string workerId = end < 0 ? path[3..] : path[3..end];
            if (workerId.Length is > 0 and <= 64 && context.RequestServices.GetRequiredService<WorkerStore>().Find(workerId) is not null)
                return (RouteKind.Tunnel, workerId);
            return (RouteKind.Admin, ip);
        }

        if (path.Equals("/agent", StringComparison.Ordinal)) return (RouteKind.Agent, ip);

        if (path.Equals("/enroll", StringComparison.Ordinal) || path.StartsWith("/admin/", StringComparison.Ordinal))
            return (RouteKind.Admin, ip);

        return (RouteKind.Other, ip);
    }
}

/// <summary>The relay-wide byte budget, as a service so every session and the tunnel handler share one.</summary>
public sealed class RelayBudget(long capacity)
{
    public ByteBudget Budget { get; } = new(capacity);
}
