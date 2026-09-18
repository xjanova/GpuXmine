using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GpuxMine.Node;

/// <summary>
/// A stand-in ComfyUI, for proving the tunnel on a machine with no GPU.
/// </summary>
/// <remarks>
/// It is honest about what it is: it answers the shape of the ComfyUI API but
/// renders nothing, and its <c>/object_info</c> is a stub. That is enough to
/// verify that a request leaves aixman, crosses the relay, reaches the node and
/// comes back with bytes — which is the one thing M1 has to prove. It is NOT
/// enough to satisfy aixman's graph validator, which checks a real schema; a
/// genuine end-to-end render needs a real ComfyUI behind <c>--ComfyUrl</c>.
/// </remarks>
public sealed class MockComfy : IDisposable
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _prompts = new(StringComparer.Ordinal);
    private readonly ILoggerish _log;
    private readonly TimeSpan _renderTime = TimeSpan.FromSeconds(4);

    public MockComfy(int port, ILoggerish log)
    {
        _log = log;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public void Start(CancellationToken ct)
    {
        _listener.Start();
        _log.Info($"[mock] stand-in ComfyUI on {string.Join(", ", _listener.Prefixes)}");
        _ = Task.Run(() => LoopAsync(ct), ct);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _log.Warn($"[mock] {ex.Message}"); continue; }

            _ = Task.Run(() => Serve(context), ct);
        }
    }

    private void Serve(HttpListenerContext context)
    {
        string route = context.Request.Url?.AbsolutePath ?? "/";
        try
        {
            if (route == "/ws" && context.Request.IsWebSocketRequest)
            {
                // The progress tracker dials this the way it dials real ComfyUI.
                // Holding the socket open (and saying nothing) is enough to stop
                // it retrying every three seconds and filling the log.
                _ = HoldWebSocketAsync(context);
                return;
            }

            switch (route)
            {
                case "/system_stats":
                    Json(context, 200, new { system = new { comfyui_version = "mock" }, devices = Array.Empty<object>() });
                    return;

                case "/object_info":
                    Json(context, 200, new Dictionary<string, object>());
                    return;

                case "/prompt":
                {
                    string promptId = Guid.NewGuid().ToString();
                    _prompts[promptId] = DateTimeOffset.UtcNow;
                    _log.Info($"[mock] accepted prompt {promptId}");
                    Json(context, 200, new { prompt_id = promptId, number = 1, node_errors = new Dictionary<string, object>() });
                    return;
                }

                case "/queue":
                {
                    var running = _prompts
                        .Where(p => DateTimeOffset.UtcNow - p.Value < _renderTime)
                        .Select(p => p.Key).ToArray();
                    Json(context, 200, new { queue_running = running, queue_pending = Array.Empty<string>() });
                    return;
                }

                case "/view":
                    Bytes(context, 200, "image/png", OnePixelPng);
                    return;
            }

            if (route.StartsWith("/history/", StringComparison.Ordinal))
            {
                string promptId = route["/history/".Length..];
                if (!_prompts.TryGetValue(promptId, out var submittedAt))
                {
                    Json(context, 200, new Dictionary<string, object>());
                    return;
                }

                if (DateTimeOffset.UtcNow - submittedAt < _renderTime)
                {
                    // Present but unfinished is how ComfyUI reports a render in
                    // flight; an empty history would read as "lost".
                    Json(context, 200, new Dictionary<string, object>
                    {
                        [promptId] = new { status = new { status_str = "running", completed = false } },
                    });
                    return;
                }

                Json(context, 200, new Dictionary<string, object>
                {
                    [promptId] = new
                    {
                        status = new { status_str = "success", completed = true, messages = Array.Empty<object>() },
                        outputs = new Dictionary<string, object>
                        {
                            ["9"] = new
                            {
                                images = new[]
                                {
                                    new { filename = "gpuxmine_mock_00001_.png", subfolder = "", type = "output" },
                                },
                            },
                        },
                    },
                });
                return;
            }

            Json(context, 404, new { error = "mock has no route for " + route });
        }
        catch (Exception ex)
        {
            _log.Warn($"[mock] handler failed for {route}: {ex.Message}");
            try { context.Response.Abort(); } catch { /* client already gone */ }
        }
    }

    private static async Task HoldWebSocketAsync(HttpListenerContext context)
    {
        try
        {
            var ws = await context.AcceptWebSocketAsync(subProtocol: null);
            var buffer = new byte[1024];
            while (ws.WebSocket.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var r = await ws.WebSocket.ReceiveAsync(buffer, CancellationToken.None);
                if (r.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
            }
        }
        catch
        {
            // The tracker reconnects on its own; a dropped mock socket is not news.
        }
    }

    private static void Json(HttpListenerContext context, int status, object payload)
        => Bytes(context, status, "application/json", JsonSerializer.SerializeToUtf8Bytes(payload));

    private static void Bytes(HttpListenerContext context, int status, string contentType, byte[] body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = body.Length;
        context.Response.OutputStream.Write(body, 0, body.Length);
        context.Response.OutputStream.Close();
    }

    public void Dispose()
    {
        if (_listener.IsListening) _listener.Stop();
        _listener.Close();
    }
}
