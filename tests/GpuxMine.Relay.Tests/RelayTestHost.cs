using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GpuxMine.Relay;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GpuxMine.Relay.Tests;

/// <summary>What <c>/enroll</c> answered.</summary>
public sealed record Enrolled(string WorkerId, string? Token, string TunnelToken, string AgentRelayUrl, string AixmanEndpoint);

/// <summary>
/// The real relay — <see cref="RelayHost.Build"/>, every route and limit —
/// running in-process on a TestServer, with its own store in a temp folder.
/// </summary>
public sealed class RelayTestHost : IAsyncDisposable
{
    public const string AdminKey = "test-admin-key";
    public const string ObserverKey = "test-observer-key";

    private RelayTestHost(WebApplication app, string directory)
    {
        App = app;
        Directory = directory;
        Server = app.GetTestServer();
        Http = Server.CreateClient();
    }

    public WebApplication App { get; }
    public TestServer Server { get; }
    public HttpClient Http { get; }
    public string Directory { get; }
    public string StorePath => Path.Combine(Directory, "workers.json");

    public static async Task<RelayTestHost> StartAsync(
        IDictionary<string, string?>? settings = null,
        string? storeJson = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), "gxm-relay-tests", Guid.NewGuid().ToString("n"));
        System.IO.Directory.CreateDirectory(directory);
        string store = Path.Combine(directory, "workers.json");
        if (storeJson is not null) await File.WriteAllTextAsync(store, storeJson);

        var config = new Dictionary<string, string?>
        {
            ["Relay:StorePath"] = store,
            ["Relay:AdminKey"] = AdminKey,
            ["Relay:ObserverKey"] = ObserverKey,
            ["Relay:IssueTunnelTokens"] = "true",
            // Tests connect far more often than any node would.
            ["Relay:AgentConnectsPerMinutePerIp"] = "100000",
            ["Relay:AgentConnectsPerMinutePerWorker"] = "100000",
            ["Relay:AdminRequestsPerMinutePerIp"] = "100000",
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>()) config[key] = value;

        WebApplication app = RelayHost.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(config);
            builder.Logging.ClearProviders();
        });
        await app.StartAsync();
        return new RelayTestHost(app, directory);
    }

    public T Service<T>() where T : notnull => App.Services.GetRequiredService<T>();

    public async Task<Enrolled> EnrollAsync(string label = "test-node")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/enroll?label={Uri.EscapeDataString(label)}");
        request.Headers.Add("X-Admin-Key", AdminKey);
        using HttpResponseMessage response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Enrolled>(Json))!;
    }

    public async Task<HttpResponseMessage> AdminAsync(HttpMethod method, string path, string key = AdminKey, string header = "X-Admin-Key")
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(header, key);
        return await Http.SendAsync(request);
    }

    public static HttpRequestMessage Tunnel(HttpMethod method, string workerId, string pathAndQuery, string? token, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, $"/w/{workerId}{pathAndQuery}") { Content = content };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public Task<FakeAgent> ConnectAgentAsync(string workerId, string token, string version = "0.1.9")
        => FakeAgent.ConnectAsync(this, workerId, token, version);

    /// <summary>The hello-ack is sent before the session is registered; a request sent in between would find the node offline.</summary>
    public async Task WaitOnlineAsync(string workerId, bool online = true)
    {
        var registry = Service<AgentRegistry>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((registry.Get(workerId) is not null) != online)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"{workerId} never went {(online ? "online" : "offline")}");
            await Task.Delay(10);
        }
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
