using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using GpuxMine.Protocol;
using GpuxMine.Relay;

var builder = WebApplication.CreateBuilder(args);

string storePath = builder.Configuration["Relay:StorePath"]
    ?? Path.Combine(AppContext.BaseDirectory, "data", "workers.json");

// No default admin key: a relay that ships with a known one is a relay anybody
// can enrol nodes on. Absent means "mint a fresh one and say so once".
string adminKey = builder.Configuration["Relay:AdminKey"]
    ?? Environment.GetEnvironmentVariable("GPUXMINE_ADMIN_KEY")
    ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

builder.Services.AddSingleton(new WorkerStore(storePath));
builder.Services.AddSingleton<AgentRegistry>();

var app = builder.Build();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("relay");

if (builder.Configuration["Relay:AdminKey"] is null && Environment.GetEnvironmentVariable("GPUXMINE_ADMIN_KEY") is null)
    log.LogWarning("No admin key configured — generated one for this run: {AdminKey}", adminKey);

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

// How long the relay will hold an aixman request open while the node works.
// aixman's own ceilings are lower (60s for /object_info, 120s for a download),
// so this only decides when the relay stops waiting on a node that went quiet.
var TunnelTimeout = TimeSpan.FromSeconds(180);

static bool IsAdmin(HttpRequest request, string adminKey)
    => request.Headers["X-Admin-Key"].ToString() is { Length: > 0 } presented
       && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(presented),
            System.Text.Encoding.UTF8.GetBytes(adminKey));

static string? BearerOf(HttpRequest request)
{
    string header = request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? header["Bearer ".Length..].Trim()
        : null;
}

// Headers that describe this hop only. Forwarding them corrupts the next hop's
// framing — Content-Length in particular, which the receiving stack recomputes.
var HopByHop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "connection", "keep-alive", "proxy-authenticate", "proxy-authorization",
    "te", "trailer", "transfer-encoding", "upgrade", "host", "content-length",
};

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
    if (!IsAdmin(request, adminKey)) return Results.Unauthorized();

    string? label = request.Query["label"];
    var (record, token) = store.Enroll(label);

    string origin = $"{request.Scheme}://{request.Host}";
    return Results.Ok(new
    {
        workerId = record.WorkerId,
        token,
        agentRelayUrl = origin.Replace("http", "ws", StringComparison.Ordinal) + "/agent",
        // Exactly what goes into ai_gpu_workers.endpoint on the aixman side.
        aixmanEndpoint = $"{origin}/w/{record.WorkerId}",
    });
});

app.MapGet("/admin/workers", (HttpRequest request, WorkerStore store, AgentRegistry registry) =>
{
    if (!IsAdmin(request, adminKey)) return Results.Unauthorized();

    var online = registry.All().ToDictionary(s => s.WorkerId, StringComparer.Ordinal);
    return Results.Ok(store.All().Select(w => new
    {
        w.WorkerId,
        w.Label,
        w.EnrolledAt,
        online = online.ContainsKey(w.WorkerId),
        agentVersion = online.GetValueOrDefault(w.WorkerId)?.AgentVersion,
        connectedAt = online.GetValueOrDefault(w.WorkerId)?.ConnectedAt,
        lastSeenAt = online.GetValueOrDefault(w.WorkerId)?.LastSeenAt,
        telemetry = online.GetValueOrDefault(w.WorkerId)?.Telemetry,
    }));
});

// --- the agent's dial-out --------------------------------------------------
app.MapGet("/agent", async (HttpContext context, WorkerStore store, AgentRegistry registry, IHostApplicationLifetime life) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    string workerId = context.Request.Headers["X-Worker-Id"].ToString();
    string? token = BearerOf(context.Request);

    if (string.IsNullOrEmpty(workerId) || token is null || !store.TokenMatches(workerId, token))
    {
        log.LogWarning("Rejected agent connection for {WorkerId} from {Ip}",
            string.IsNullOrEmpty(workerId) ? "(none)" : workerId, context.Connection.RemoteIpAddress);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
    string? agentVersion = context.Request.Headers["X-Agent-Version"].ToString();

    var session = new AgentSession(workerId, agentVersion, socket, log);
    await registry.AddAsync(session);
    log.LogInformation("Worker {WorkerId} online (agent {Version})", workerId, agentVersion);

    try
    {
        await session.SendAsync(new TunnelHeader { Kind = FrameKind.HelloAck, WorkerId = workerId },
            ReadOnlyMemory<byte>.Empty, context.RequestAborted);

        await session.RunAsync(life.ApplicationStopping);
    }
    finally
    {
        registry.Remove(session);
        await session.DisposeAsync();
        log.LogInformation("Worker {WorkerId} offline", workerId);
    }
});

// --- the tunnel aixman talks to -------------------------------------------
app.Map("/w/{workerId}/{**path}", async (
    HttpContext context,
    string workerId,
    string? path,
    WorkerStore store,
    AgentRegistry registry) =>
{
    string? token = BearerOf(context.Request);
    if (token is null || !store.TokenMatches(workerId, token))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "bad worker token" });
        return;
    }

    AgentSession? session = registry.Get(workerId);
    if (session is null)
    {
        // "warming", not "failed": a community node whose owner closed the lid
        // will be back. Telling aixman it failed would have it reap the worker
        // and lose the node for good.
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new { stage = "offline", ready = false });
        return;
    }

    using var bodyBuffer = new MemoryStream();
    await context.Request.Body.CopyToAsync(bodyBuffer, context.RequestAborted);

    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var header in context.Request.Headers)
    {
        // The relay's own credential must not travel further: the node never
        // needs it, and forwarding it would hand every node a token that opens
        // the tunnel to itself.
        if (HopByHop.Contains(header.Key) || string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
            continue;
        headers[header.Key] = header.Value.ToString();
    }

    string pathAndQuery = "/" + (path ?? string.Empty) + context.Request.QueryString;

    TunnelReply reply;
    try
    {
        reply = await session.ExchangeAsync(
            context.Request.Method, pathAndQuery, headers, bodyBuffer.ToArray(), TunnelTimeout, context.RequestAborted);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        return; // aixman hung up; nothing to answer.
    }
    catch (OperationCanceledException)
    {
        context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
        await context.Response.WriteAsJsonAsync(new { error = "node did not answer in time" });
        return;
    }
    catch (IOException ex)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new { stage = "offline", ready = false, detail = ex.Message });
        return;
    }

    context.Response.StatusCode = reply.Status;
    foreach (var header in reply.Headers)
    {
        if (HopByHop.Contains(header.Key)) continue;
        context.Response.Headers[header.Key] = header.Value;
    }
    await context.Response.Body.WriteAsync(reply.Body, context.RequestAborted);
});

app.Run();
