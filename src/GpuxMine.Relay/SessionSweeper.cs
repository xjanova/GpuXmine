using System.Net.WebSockets;

namespace GpuxMine.Relay;

/// <summary>
/// Ends sessions that have gone silent.
/// </summary>
/// <remarks>
/// A laptop that sleeps with its lid shut leaves a socket that is dead but
/// looks open: nothing tells the relay, and TCP takes about fifteen minutes to
/// notice on its own. Until then the node is listed online, and every request
/// aixman sends it waits out the whole tunnel timeout and costs the customer an
/// attempt. An agent sends a heartbeat every few seconds, so a session that has
/// sent nothing at all for <see cref="RelayOptions.StaleAfterSeconds"/> is
/// gone; closing it turns the next request into an immediate
/// <c>503 offline</c>, which aixman treats as "warming", not "failed".
/// </remarks>
public sealed class SessionSweeper(AgentRegistry registry, RelayOptions options, ILogger<SessionSweeper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var staleAfter = TimeSpan.FromSeconds(Math.Max(10, options.StaleAfterSeconds));
        var period = TimeSpan.FromSeconds(Math.Clamp(staleAfter.TotalSeconds / 6, 1, 15));

        using var timer = new PeriodicTimer(period);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepAsync(staleAfter);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    public async Task<int> SweepAsync(TimeSpan staleAfter)
    {
        int swept = 0;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (AgentSession session in registry.All())
        {
            TimeSpan silent = now - session.LastSeenAt;
            if (silent < staleAfter) continue;

            log.LogInformation("Worker {WorkerId} silent for {Seconds:0}s — closing its session", session.WorkerId, silent.TotalSeconds);
            if (await registry.DisconnectAsync(session, WebSocketCloseStatus.PolicyViolation, "no heartbeat"))
                swept++;
        }
        return swept;
    }
}
