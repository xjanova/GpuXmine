using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GpuxMine.Node;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GpuxMine.Relay.Tests;

/// <summary>
/// The shipping agent code — <see cref="RelayConnection"/> and
/// <see cref="ComfyRuntime"/> — against the relay over real sockets, with a
/// stand-in ComfyUI behind it. The relay is run once as it ships (it offers
/// streamed replies) and once pretending to be the v0.1 relay in production
/// (it does not), which is the case a new agent meets the day it is released.
/// </summary>
public class AgentRoundTripTests
{
    private sealed class CapturingLog : ILoggerish
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public void Info(string message) => Lines.Enqueue(message);
        public void Warn(string message) => Lines.Enqueue("WARN " + message);
        public bool Saw(string text) => Lines.Any(l => l.Contains(text, StringComparison.Ordinal));
    }

    private static async Task<WebApplication> StartOnLoopbackAsync(WebApplication app)
    {
        await app.StartAsync();
        return app;
    }

    /// <summary>ComfyUI, as far as this test needs it: one big output file and a queue.</summary>
    private static async Task<(WebApplication App, byte[] Image)> StartFakeComfyAsync()
    {
        byte[] image = FakeAgent.Payload(3 * 1024 * 1024 + 123, seed: 11);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapGet("/view", () => Results.Bytes(image, "image/png"));
        app.MapGet("/queue", () => Results.Json(new { queue_running = Array.Empty<object>(), queue_pending = Array.Empty<object>() }));
        return (await StartOnLoopbackAsync(app), image);
    }

    private static async Task<(WebApplication App, string Directory)> StartRelayAsync(bool streamReplies)
    {
        string directory = Path.Combine(Path.GetTempPath(), "gxm-relay-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var app = RelayHost.Build([], builder =>
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Relay:StorePath"] = Path.Combine(directory, "workers.json"),
                ["Relay:AdminKey"] = RelayTestHost.AdminKey,
                ["Relay:StreamReplies"] = streamReplies ? "true" : "false",
                ["Relay:IssueTunnelTokens"] = "true",
            });
            builder.Logging.ClearProviders();
        });
        return (await StartOnLoopbackAsync(app), directory);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_large_output_comes_back_whole_whichever_reply_shape_the_relay_takes(bool relayStreams)
    {
        var (comfy, image) = await StartFakeComfyAsync();
        var (relay, directory) = await StartRelayAsync(relayStreams);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            string relayUrl = relay.Urls.First();
            using var http = new HttpClient { BaseAddress = new Uri(relayUrl) };

            using var enrol = new HttpRequestMessage(HttpMethod.Post, "/enroll?label=roundtrip");
            enrol.Headers.Add("X-Admin-Key", RelayTestHost.AdminKey);
            using HttpResponseMessage enrolled = await http.SendAsync(enrol, cts.Token);
            Enrolled worker = (await enrolled.Content.ReadFromJsonAsync<Enrolled>(RelayTestHost.Json, cts.Token))!;

            var options = new NodeOptions
            {
                RelayUrl = relayUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent",
                WorkerId = worker.WorkerId,
                Token = worker.Token!,
                ComfyUrl = comfy.Urls.First(),
                HeartbeatSeconds = 3,
            };
            var log = new CapturingLog();
            await using var runtime = new ComfyRuntime(options, log);
            var connection = new RelayConnection(options, runtime, log);
            Task running = connection.RunForeverAsync(cts.Token);

            await WaitUntilAsync(() => relay.Services.GetRequiredService<AgentRegistry>().Get(worker.WorkerId) is not null, cts.Token);

            using var view = new HttpRequestMessage(HttpMethod.Get, $"/w/{worker.WorkerId}/view?filename=ComfyUI_00001_.png&type=output");
            view.Headers.Authorization = new AuthenticationHeaderValue("Bearer", worker.TunnelToken);
            using HttpResponseMessage response = await http.SendAsync(view, cts.Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(image, await response.Content.ReadAsByteArrayAsync(cts.Token));

            // The shape really was the one negotiated.
            Assert.Equal(relayStreams, log.Saw("streamed replies"));

            // And the relay's own allowlist sits in front of the agent's.
            using var manager = new HttpRequestMessage(HttpMethod.Post, $"/w/{worker.WorkerId}/customnode/install");
            manager.Headers.Authorization = new AuthenticationHeaderValue("Bearer", worker.TunnelToken);
            using HttpResponseMessage refused = await http.SendAsync(manager, cts.Token);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

            await cts.CancelAsync();
            await running;
        }
        finally
        {
            await relay.StopAsync(CancellationToken.None);
            await relay.DisposeAsync();
            await comfy.StopAsync(CancellationToken.None);
            await comfy.DisposeAsync();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_disabled_worker_is_told_403_and_an_unknown_one_401()
    {
        var (relay, directory) = await StartRelayAsync(streamReplies: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            string relayUrl = relay.Urls.First();
            using var http = new HttpClient { BaseAddress = new Uri(relayUrl) };

            using var enrol = new HttpRequestMessage(HttpMethod.Post, "/enroll?label=refused");
            enrol.Headers.Add("X-Admin-Key", RelayTestHost.AdminKey);
            using HttpResponseMessage enrolled = await http.SendAsync(enrol, cts.Token);
            Enrolled worker = (await enrolled.Content.ReadFromJsonAsync<Enrolled>(RelayTestHost.Json, cts.Token))!;

            using var disable = new HttpRequestMessage(HttpMethod.Post, $"/admin/workers/{worker.WorkerId}/disable");
            disable.Headers.Add("X-Admin-Key", RelayTestHost.AdminKey);
            (await http.SendAsync(disable, cts.Token)).Dispose();

            Assert.Equal(403, await FirstRefusalAsync(relayUrl, worker.WorkerId, worker.Token!, cts.Token));
            Assert.Equal(401, await FirstRefusalAsync(relayUrl, worker.WorkerId, "not-the-token", cts.Token));
            Assert.Equal(401, await FirstRefusalAsync(relayUrl, "gxm-000000000000", worker.Token!, cts.Token));
        }
        finally
        {
            await relay.StopAsync(CancellationToken.None);
            await relay.DisposeAsync();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<int> FirstRefusalAsync(string relayUrl, string workerId, string token, CancellationToken ct)
    {
        var options = new NodeOptions
        {
            RelayUrl = relayUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent",
            WorkerId = workerId,
            Token = token,
            ComfyUrl = "http://127.0.0.1:9",
        };
        var log = new CapturingLog();
        await using var runtime = new ComfyRuntime(options, log);
        var connection = new RelayConnection(options, runtime, log);

        var refused = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Refused += status => refused.TrySetResult(status);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task running = connection.RunForeverAsync(stop.Token);
        int status = await refused.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        await stop.CancelAsync();
        await running;
        return status;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(20, ct);
        }
    }
}
