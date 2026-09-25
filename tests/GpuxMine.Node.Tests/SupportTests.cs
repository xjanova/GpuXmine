using GpuxMine.Node.Assessment;
using GpuxMine.Node.Storage;

namespace GpuxMine.Node.Tests;

/// <summary>The smaller pieces the node's behaviour rests on.</summary>
public class SupportTests
{
    // ------------------------------------------------------------ relaunch

    [Fact]
    public void A_relaunch_keeps_the_owners_arguments_and_puts_its_switches_last()
    {
        string[] original = ["--DataDirectory", @"D:\node2", "--minimized"];

        string[] next = LaunchFlags.ForRestart(original, LaunchFlags.Minimized, LaunchFlags.AfterRestart);

        Assert.Equal(new[] { "--DataDirectory", @"D:\node2", "--minimized", "--after-restart" }, next);
        Assert.True(LaunchFlags.Has(next, LaunchFlags.AfterRestart));
        Assert.True(LaunchFlags.Has(["--MINIMIZED=true"], LaunchFlags.Minimized));
        Assert.False(LaunchFlags.Has(["--DataDirectory", "x"], LaunchFlags.Minimized));
    }

    [Fact]
    public void A_refusal_stays_on_screen_until_something_else_is_actually_true()
    {
        var state = new NodeState();
        state.SetRejected("ลงทะเบียนเครื่องใหม่");

        // The retry loop reports "reconnecting" between attempts; that is not news.
        state.SetConnection(ConnectionState.Reconnecting);
        Assert.Equal(ConnectionState.Rejected, state.Connection);
        Assert.Equal("ลงทะเบียนเครื่องใหม่", state.ConnectionNote);

        // Let back in by the operator.
        state.SetConnection(ConnectionState.Connected);
        Assert.Equal(ConnectionState.Connected, state.Connection);
        Assert.Null(state.ConnectionNote);
    }

    [Fact]
    public void A_relaunched_copy_waits_for_the_old_one_instead_of_exiting()
    {
        using var dir = new TempDir();
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        // Mutexes belong to a thread; the "old process" holds it on its own.
        var old = new Thread(() =>
        {
            using var held = NodeInstanceLock.TryAcquire(dir.Path);
            Assert.NotNull(held);
            holding.Set();
            release.Wait();
        });
        old.Start();
        holding.Wait();

        Assert.Null(NodeInstanceLock.TryAcquire(dir.Path));   // an ordinary second launch fronts the first

        release.Set();
        using var next = NodeInstanceLock.TryAcquire(dir.Path, TimeSpan.FromSeconds(10));
        Assert.NotNull(next);
        old.Join();
    }

    // ------------------------------------------------------------ settings

    [Fact]
    public void Settings_from_before_the_switch_load_as_undecided()
    {
        using var dir = new TempDir();
        using var store = new NodeStore(Path.Combine(dir.Path, "node.db"));
        store.SetSetting("node-settings", """{"PowerPercent":60,"FreeSharePercent":10}""");

        NodeSettings loaded = NodeSettings.Load(store);

        Assert.Null(loaded.SharingEnabled);
        Assert.Equal(60, loaded.PowerPercent);

        loaded.SharingEnabled = false;
        loaded.Save(store);
        Assert.False(NodeSettings.Load(store).SharingEnabled);
    }

    // ---------------------------------------------------------- assessment

    [Fact]
    public void The_card_hash_ignores_the_allocator_suffix_but_not_the_card()
    {
        string? a = NodeAssessment.GpuHashOf("cuda:0 NVIDIA GeForce RTX 3060 : cudaMallocAsync", 12288);
        string? b = NodeAssessment.GpuHashOf("cuda:0 NVIDIA GeForce RTX 3060 : native", 12287);
        string? c = NodeAssessment.GpuHashOf("cuda:0 NVIDIA GeForce RTX 4090 : cudaMallocAsync", 24564);
        string? d = NodeAssessment.GpuHashOf("cuda:0 NVIDIA GeForce RTX 3060 : cudaMallocAsync", 8192);

        Assert.NotNull(a);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
        Assert.Null(NodeAssessment.GpuHashOf(null, 8192));
        Assert.Null(NodeAssessment.GpuHashOf("GPU", 0));
    }

    [Fact]
    public void A_report_is_void_on_another_card_but_not_when_the_card_is_unknown()
    {
        var report = new NodeAssessment
        {
            AgentVersion = "1.0.0",
            MeasuredAt = DateTimeOffset.UtcNow,
            GpuHash = NodeAssessment.GpuHashOf("RTX 3060", 12288),
        };

        Assert.True(report.IsUsable("1.0.0", null, NodeAssessment.GpuHashOf("RTX 3060", 12288)));
        Assert.True(report.IsUsable("1.0.0", null, gpuHash: null));
        Assert.False(report.IsUsable("1.0.0", null, NodeAssessment.GpuHashOf("RTX 4090", 24576)));

        // A report from before the field existed is held to the machine hash alone.
        report.GpuHash = null;
        Assert.True(report.IsUsable("1.0.0", null, NodeAssessment.GpuHashOf("RTX 4090", 24576)));
    }

    // --------------------------------------------------------------- ledger

    [Fact]
    public void The_ledger_pages_through_every_open_job()
    {
        using var dir = new TempDir();
        using var store = new NodeStore(Path.Combine(dir.Path, "node.db"));
        for (int i = 0; i < 120; i++) store.JobSubmitted($"job-{i:000}", "image", 1, false);
        store.JobFinished("job-005", true, null, null);

        var all = new List<string>();
        NodeStore.OpenJob? after = null;
        while (true)
        {
            var page = store.UnsettledPage(TimeSpan.Zero - TimeSpan.FromSeconds(1), limit: 50, after);
            all.AddRange(page.Select(p => p.PromptId));
            if (page.Count < 50) break;
            after = page[^1];
        }

        Assert.Equal(119, all.Count);
        Assert.Equal(119, all.Distinct().Count());
        Assert.DoesNotContain("job-005", all);
    }

    [Fact]
    public void The_ledger_keeps_a_jobs_inputs_and_whether_it_was_purged()
    {
        using var dir = new TempDir();
        using var store = new NodeStore(Path.Combine(dir.Path, "node.db"));
        store.JobSubmitted("p1", "image", 1, false);
        store.JobInputs("p1", [new ComfyFile("face.png", "uploads", "input")]);
        store.JobFinished("p1", true, "out.png", null);

        Assert.True(store.HasJob("p1"));
        Assert.False(store.HasJob("owners"));
        Assert.Equal(new ComfyFile("face.png", "uploads", "input"), store.InputsOf("p1").Single());

        DateTimeOffset since = DateTimeOffset.UtcNow.AddMinutes(-5);
        Assert.Equal(new[] { "p1" }, store.JobsToPurge(DateTimeOffset.UtcNow.AddMinutes(1), since));
        // Jobs from before the build that purges are left alone.
        Assert.Empty(store.JobsToPurge(DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow.AddMinutes(1)));

        store.JobPurged("p1");
        Assert.Empty(store.JobsToPurge(DateTimeOffset.UtcNow.AddMinutes(1), since));
    }

    [Fact]
    public void The_ledger_picks_its_own_jobs_out_of_a_long_queue()
    {
        using var dir = new TempDir();
        using var store = new NodeStore(Path.Combine(dir.Path, "node.db"));
        store.JobSubmitted("customer-1", "image", 1, false);
        store.JobSubmitted("customer-2", "image", 1, false);

        // More ids than one query takes, most of them the owner's.
        var queue = Enumerable.Range(0, 1200).Select(i => $"owners-{i}").Append("customer-2").Append("customer-1").ToArray();

        Assert.Equal(new[] { "customer-1", "customer-2" }, store.JobsAmong(queue).Order().ToArray());
        Assert.Empty(store.JobsAmong([]));
        Assert.Empty(store.JobsAmong(["owners-only"]));
    }

    [Fact]
    public void A_ledger_from_an_earlier_build_gains_the_new_columns()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "node.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE jobs (
                    prompt_id TEXT PRIMARY KEY, kind TEXT NOT NULL, status TEXT NOT NULL,
                    nodes_total INTEGER NOT NULL DEFAULT 0, submitted_at INTEGER NOT NULL,
                    started_at INTEGER, completed_at INTEGER, output_file TEXT, error TEXT,
                    payout_satang INTEGER, free_share INTEGER NOT NULL DEFAULT 0);
                INSERT INTO jobs (prompt_id, kind, status, submitted_at, completed_at) VALUES ('old', 'image', 'completed', 1, 2);
                """;
            command.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var store = new NodeStore(path);

        Assert.True(store.HasJob("old"));
        store.JobPurged("old");
        Assert.Empty(store.InputsOf("old"));
    }
}
