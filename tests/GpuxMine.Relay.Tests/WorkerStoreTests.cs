using System.Text.Json;

namespace GpuxMine.Relay.Tests;

public sealed class WorkerStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gxm-store-tests", Guid.NewGuid().ToString("n"));
    private string StorePath => Path.Combine(_directory, "workers.json");

    public WorkerStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    /// <summary>Exactly what the v0.1 relay wrote: one hash, no tunnel token.</summary>
    public static string LegacyStore(string workerId, string agentToken) => $$"""
        [
          {
            "WorkerId": "{{workerId}}",
            "TokenHash": "{{WorkerStore.HashToken(agentToken)}}",
            "Label": "old-node",
            "EnrolledAt": "2026-09-18T10:00:00+00:00"
          }
        ]
        """;

    [Fact]
    public void Enrolment_issues_two_different_tokens_each_for_its_own_door()
    {
        var store = new WorkerStore(StorePath);
        IssuedTokens issued = store.Enroll("rig");

        Assert.NotNull(issued.AgentToken);
        Assert.NotEqual(issued.AgentToken, issued.TunnelToken);

        string id = issued.Record.WorkerId;
        Assert.Equal(WorkerAuth.Ok, store.AuthenticateAgent(id, issued.AgentToken!));
        Assert.Equal(WorkerAuth.Ok, store.AuthenticateTunnel(id, issued.TunnelToken));

        // Each token opens its own door only.
        Assert.Equal(WorkerAuth.BadToken, store.AuthenticateAgent(id, issued.TunnelToken));
        Assert.Equal(WorkerAuth.BadToken, store.AuthenticateTunnel(id, issued.AgentToken!));
        Assert.Equal(WorkerAuth.Unknown, store.AuthenticateTunnel("gxm-nope", issued.TunnelToken));
    }

    [Fact]
    public void Enrolling_without_the_split_hands_back_one_token_for_both_doors()
    {
        var store = new WorkerStore(StorePath);
        IssuedTokens issued = store.Enroll("rig", splitTokens: false);

        Assert.Equal(issued.AgentToken, issued.TunnelToken);
        Assert.Null(issued.Record.TunnelTokenHash);
        Assert.Equal(WorkerAuth.Ok, store.AuthenticateTunnel(issued.Record.WorkerId, issued.TunnelToken));
    }

    [Fact]
    public void A_worker_from_before_the_split_is_still_reached_with_its_one_token()
    {
        File.WriteAllText(StorePath, LegacyStore("gxm-legacy01", "old-token"));
        var store = new WorkerStore(StorePath);

        Assert.Equal(WorkerAuth.Ok, store.AuthenticateAgent("gxm-legacy01", "old-token"));
        Assert.Equal(WorkerAuth.Ok, store.AuthenticateTunnel("gxm-legacy01", "old-token"));
        Assert.Equal(WorkerAuth.BadToken, store.AuthenticateTunnel("gxm-legacy01", "wrong"));
    }

    [Fact]
    public void Giving_a_legacy_worker_a_tunnel_token_retires_the_agent_token_on_the_tunnel_only()
    {
        File.WriteAllText(StorePath, LegacyStore("gxm-legacy01", "old-token"));
        var store = new WorkerStore(StorePath);

        IssuedTokens? issued = store.Rotate("gxm-legacy01", tunnelOnly: true);

        Assert.NotNull(issued);
        Assert.Null(issued.AgentToken);
        Assert.Equal(WorkerAuth.Ok, store.AuthenticateAgent("gxm-legacy01", "old-token"));
        Assert.Equal(WorkerAuth.BadToken, store.AuthenticateTunnel("gxm-legacy01", "old-token"));
        Assert.Equal(WorkerAuth.Ok, store.AuthenticateTunnel("gxm-legacy01", issued.TunnelToken));
    }

    [Fact]
    public void Full_rotation_retires_both_old_tokens()
    {
        var store = new WorkerStore(StorePath);
        IssuedTokens first = store.Enroll("rig");
        string id = first.Record.WorkerId;

        IssuedTokens second = store.Rotate(id, tunnelOnly: false)!;

        Assert.Equal(WorkerAuth.BadToken, store.AuthenticateAgent(id, first.AgentToken!));
        Assert.Equal(WorkerAuth.BadToken, store.AuthenticateTunnel(id, first.TunnelToken));
        Assert.Equal(WorkerAuth.Ok, store.AuthenticateAgent(id, second.AgentToken!));
        Assert.Equal(WorkerAuth.Ok, store.AuthenticateTunnel(id, second.TunnelToken));
        Assert.NotNull(second.Record.RotatedAt);
        Assert.Null(store.Rotate("gxm-nope", tunnelOnly: false));
    }

    [Fact]
    public void Disabled_is_told_only_to_whoever_holds_the_right_token()
    {
        var store = new WorkerStore(StorePath);
        IssuedTokens issued = store.Enroll("rig");
        string id = issued.Record.WorkerId;

        Assert.NotNull(store.SetDisabled(id, true, "fraud review"));

        Assert.Equal(WorkerAuth.Disabled, store.AuthenticateAgent(id, issued.AgentToken!));
        Assert.Equal(WorkerAuth.Disabled, store.AuthenticateTunnel(id, issued.TunnelToken));
        Assert.Equal(WorkerAuth.BadToken, store.AuthenticateAgent(id, "guess"));
        Assert.Equal("fraud review", store.Find(id)!.DisabledReason);

        store.SetDisabled(id, false, null);
        Assert.Equal(WorkerAuth.Ok, store.AuthenticateAgent(id, issued.AgentToken!));
        Assert.Null(store.Find(id)!.DisabledReason);
        Assert.Null(store.SetDisabled("gxm-nope", true, null));
    }

    [Fact]
    public void State_survives_a_restart_and_deletes_stay_deleted()
    {
        var store = new WorkerStore(StorePath);
        IssuedTokens kept = store.Enroll("kept");
        IssuedTokens gone = store.Enroll("gone");
        store.SetDisabled(kept.Record.WorkerId, true, "paused by admin");
        Assert.True(store.Delete(gone.Record.WorkerId));
        Assert.False(store.Delete(gone.Record.WorkerId));

        var reopened = new WorkerStore(StorePath);

        Assert.Null(reopened.Find(gone.Record.WorkerId));
        Assert.Equal(WorkerAuth.Disabled, reopened.AuthenticateTunnel(kept.Record.WorkerId, kept.TunnelToken));
    }

    [Fact]
    public void The_file_keeps_the_v01_field_name_so_a_rollback_still_loads_it()
    {
        var store = new WorkerStore(StorePath);
        IssuedTokens issued = store.Enroll("rig");

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(StorePath));
        JsonElement record = doc.RootElement[0];
        Assert.Equal(WorkerStore.HashToken(issued.AgentToken!), record.GetProperty("TokenHash").GetString());
        Assert.Equal(WorkerStore.HashToken(issued.TunnelToken), record.GetProperty("TunnelTokenHash").GetString());

        // What the v0.1 relay's record type needs is all there.
        var old = JsonSerializer.Deserialize<List<V01Record>>(File.ReadAllText(StorePath))!;
        Assert.Equal(issued.Record.WorkerId, old[0].WorkerId);
        Assert.Equal(WorkerStore.HashToken(issued.AgentToken!), old[0].TokenHash);
    }

    private sealed record V01Record(string WorkerId, string TokenHash, string? Label, DateTimeOffset EnrolledAt);

    [Fact]
    public void Every_write_keeps_the_previous_file_and_a_daily_copy()
    {
        var store = new WorkerStore(StorePath);
        store.Enroll("first");
        string afterFirst = File.ReadAllText(StorePath);

        store.Enroll("second");

        Assert.Equal(afterFirst, File.ReadAllText(StorePath + ".bak"));
        string[] daily = Directory.GetFiles(Path.Combine(_directory, "backups"), "workers-*.json");
        Assert.Single(daily);
        Assert.Equal(2, new WorkerStore(StorePath).All().Count);
    }

    [Fact]
    public void An_unreadable_store_refuses_to_start_and_says_where_the_backup_is()
    {
        File.WriteAllText(StorePath, "{ this is not json");

        var ex = Assert.Throws<InvalidOperationException>(() => new WorkerStore(StorePath));
        Assert.Contains("workers.json.bak", ex.Message);
    }
}
