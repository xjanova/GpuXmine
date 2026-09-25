using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace GpuxMine.Node.Tests;

/// <summary>
/// ComfyUI, as far as the node talks to it: a queue, a history, prompts,
/// uploads, the system stats and the folder list — each one settable by the
/// test, and every request recorded so a test can say what reached it.
/// </summary>
public sealed class FakeComfy : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _nextPrompt;

    /// <summary>"METHOD /path?query" for every request that reached ComfyUI.</summary>
    public ConcurrentQueue<string> Hits { get; } = new();

    /// <summary>Prompt ids in queue_running / queue_pending, in ComfyUI's own row shape.</summary>
    public ConcurrentDictionary<string, bool> Queue { get; } = new();

    /// <summary>History entries by prompt id, as ComfyUI would return them.</summary>
    public ConcurrentDictionary<string, JsonObject> History { get; } = new();

    /// <summary>Ids POST /history was asked to delete.</summary>
    public ConcurrentQueue<string> HistoryDeletes { get; } = new();

    /// <summary>Bodies of POST /prompt, as the node forwarded them.</summary>
    public ConcurrentQueue<string> Prompts { get; } = new();

    public string GpuName { get; set; } = "cuda:0 NVIDIA GeForce RTX 3060 : cudaMallocAsync";
    public long VramBytes { get; set; } = 12L * 1024 * 1024 * 1024;
    public string[] Argv { get; set; } = ["main.py"];

    /// <summary>What /internal/folder_paths lists as custom_nodes; null answers 404 as an old ComfyUI does.</summary>
    public string? CustomNodesPath { get; set; }

    /// <summary>How long POST /prompt takes to answer, for races.</summary>
    public TimeSpan PromptDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Put an accepted prompt in the queue, as ComfyUI does, so the node sees it running.</summary>
    public bool QueueAcceptedPrompts { get; set; }

    public string Url { get; }

    private FakeComfy(WebApplication app)
    {
        _app = app;
        Url = app.Urls.First();
    }

    public static async Task<FakeComfy> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();

        FakeComfy? fake = null;
        app.Use(async (context, next) =>
        {
            fake!.Hits.Enqueue($"{context.Request.Method} {context.Request.Path}{context.Request.QueryString}");
            await next();
        });

        app.MapGet("/system_stats", () => Results.Json(new
        {
            system = new { comfyui_version = "test", argv = fake!.Argv },
            devices = new[] { new { name = fake.GpuName, vram_total = fake.VramBytes } },
        }));

        app.MapGet("/queue", () =>
        {
            var running = fake!.Queue.Keys.Select((id, n) => new object[] { n, id, new { }, new { }, Array.Empty<string>() }).ToArray();
            return Results.Json(new { queue_running = running, queue_pending = Array.Empty<object>() });
        });

        app.MapPost("/prompt", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            fake!.Prompts.Enqueue(await reader.ReadToEndAsync());
            if (fake.PromptDelay > TimeSpan.Zero) await Task.Delay(fake.PromptDelay);
            string id = $"prompt-{Interlocked.Increment(ref fake._nextPrompt)}";
            if (fake.QueueAcceptedPrompts) fake.Queue[id] = true;
            return Results.Json(new { prompt_id = id, number = 1, node_errors = new { } });
        });

        app.MapGet("/history/{id}", (string id) =>
            fake!.History.TryGetValue(id, out var entry)
                ? Results.Content(new JsonObject { [id] = entry.DeepClone() }.ToJsonString(), "application/json")
                : Results.Json(new { }));

        app.MapPost("/history", async (HttpRequest request) =>
        {
            var body = await JsonNode.ParseAsync(request.Body);
            if (body?["delete"] is JsonArray ids)
            {
                foreach (var id in ids)
                {
                    string value = id!.GetValue<string>();
                    fake!.HistoryDeletes.Enqueue(value);
                    fake.History.TryRemove(value, out _);
                }
            }
            return Results.Ok();
        });

        app.MapPost("/upload/image", () => Results.Json(new { name = "customer-face.png", subfolder = "", type = "input" }));

        app.MapGet("/internal/folder_paths", () =>
            fake!.CustomNodesPath is { } path
                ? Results.Json(new Dictionary<string, object> { ["custom_nodes"] = new[] { path } })
                : Results.NotFound());

        // Anything else "works", so a test can tell a refused request from one
        // that would have reached the owner's ComfyUI.
        app.MapFallback((HttpContext context) => Results.Json(new { reached = context.Request.Path.Value }));

        await app.StartAsync();
        fake = new FakeComfy(app);
        return fake;
    }

    /// <summary>A finished history entry with one output image.</summary>
    public void Finished(string promptId, string filename, string subfolder = "", string type = "output", bool success = true)
    {
        History[promptId] = new JsonObject
        {
            ["status"] = new JsonObject { ["status_str"] = success ? "success" : "error", ["completed"] = success },
            ["outputs"] = new JsonObject
            {
                ["9"] = new JsonObject
                {
                    ["images"] = new JsonArray(new JsonObject
                    {
                        ["filename"] = filename,
                        ["subfolder"] = subfolder,
                        ["type"] = type,
                    }),
                },
            },
        };
    }

    public bool Reached(string methodAndPathPrefix) => Hits.Any(h => h.StartsWith(methodAndPathPrefix, StringComparison.Ordinal));

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>A scratch folder that goes away with the test.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gxm-node-tests", Guid.NewGuid().ToString("n"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string relative, string content = "x")
    {
        string full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // A file still held by a test's own process; the temp folder is not ours to fail over.
        }
    }
}

public sealed class NullLog : ILoggerish
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public void Info(string message) => Lines.Enqueue(message);
    public void Warn(string message) => Lines.Enqueue("WARN " + message);
}

public static class Http
{
    public static readonly Dictionary<string, string> NoHeaders = new(StringComparer.OrdinalIgnoreCase);

    public static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value);

    public static JsonNode Body(LocalReply reply) => JsonNode.Parse(reply.Body)!;

    /// <summary>A minimal graph: a checkpoint loader, a sampler and a save — "image" by the node's heuristic.</summary>
    public static byte[] ImagePrompt(string? loadImage = null)
    {
        var graph = new JsonObject
        {
            ["4"] = new JsonObject { ["class_type"] = "CheckpointLoaderSimple", ["inputs"] = new JsonObject { ["ckpt_name"] = "sdxl.safetensors" } },
            ["6"] = new JsonObject { ["class_type"] = "CLIPTextEncode", ["inputs"] = new JsonObject { ["text"] = "a cat" } },
            ["3"] = new JsonObject { ["class_type"] = "KSampler", ["inputs"] = new JsonObject { ["seed"] = 1 } },
            ["9"] = new JsonObject { ["class_type"] = "SaveImage", ["inputs"] = new JsonObject { ["filename_prefix"] = "aix" } },
        };
        if (loadImage is not null)
            graph["10"] = new JsonObject { ["class_type"] = "LoadImage", ["inputs"] = new JsonObject { ["image"] = loadImage } };

        return Json(new JsonObject { ["prompt"] = graph, ["client_id"] = "aixman" });
    }

    public static string Started(string promptId) => Event("execution_start", promptId);
    public static string Succeeded(string promptId) => Event("execution_success", promptId);

    private static string Event(string type, string promptId) =>
        new JsonObject { ["type"] = type, ["data"] = new JsonObject { ["prompt_id"] = promptId } }.ToJsonString();
}
