using System.Text.RegularExpressions;
using Valheim.Testing.Game;

namespace MyMod.SystemTests;

/// <summary>
/// <c>synced-config</c> (#20): MyMod's one server-synced entry, <c>[Server] Greeting</c>, on a client that runs MyMod. The
/// adapter reads the live entry on each side (<see cref="SyncedConfig"/>); how MyMod syncs it is MyMod's business.
/// <list type="number">
/// <item>First round: server and client hold the same greeting after the join; the server's admin changes it once
/// (<c>mymod_greeting</c>); the client reads the new value within the wait, woken by MyMod's "received" line in the owned
/// client's live log (an attached client is re-read at the interval).</item>
/// <item>Confirmed save, restart of only the owned server; after the rejoin both sides hold the new greeting: the server
/// kept it in its config file, and the client, which starts each session from its own file, got it from the server.</item>
/// </list>
/// </summary>
public static class SyncedConfigScenario
{
    public static void Run(CampaignRun run)
    {
        var plan = run.Plan; var report = run.Report; var client = plan.Client!; string greeting = plan.NewGreeting!;
        var timeout = TimeSpan.FromSeconds(client.JoinSeconds);
        CampaignSteps.ModPatchesApplied(run.Server, report);
        new ClientRounds
        {
            Client = client, WorldUid = plan.WorldUid, Report = report, Output = run.Output, OwnedServer = run.OwnedServer, Cancellation = run.Cancellation,
        }.Run(run.Server, () => run.OpenClient(client, null), round =>
        {
            if (round.Index == 0)
            {
                round.Step("server and client hold the same greeting after the join", () =>
                {
                    var server = Read(round.Server);
                    var joined = Wait(run, round.Client, server.Value ?? throw new InvalidOperationException("The server has no greeting: " + server), timeout, null);
                    round.Write("greeting-joined", new { server, client = joined });
                    SyncedConfig.RequireSame(server, joined);
                    if (server.Value == greeting) throw new InvalidOperationException($"The server already holds \"{greeting}\": the change would prove nothing. Choose another newGreeting.");
                });
                // Opened before the change, so the client's "received" line cannot be missed.
                using var log = run.ClientLog(client) is string path ? new LogWait(path) : null;
                round.Step($"the server's admin changes the greeting to {greeting}, once", () =>
                {
                    var reply = round.Server.Execute("mymod_greeting " + greeting);
                    if (!reply.Output.Contains("OK: greeting " + greeting)) throw new InvalidOperationException("MyMod did not confirm the change: " + string.Join(" | ", reply.Output));
                });
                round.Step("the client reads the server's new greeting within the wait", () => round.Write("greeting-changed", Wait(run, round.Client, greeting, timeout, log)));
            }
            else round.Step("after the restart both sides hold the server's new greeting", () =>
            {
                var server = Read(round.Server);
                if (server.Value != greeting) throw new InvalidOperationException($"The server lost the admin's change across the save and restart: {server}.");
                var joined = Wait(run, round.Client, greeting, timeout, null);
                round.Write("greeting-after-restart", new { server, client = joined });
                SyncedConfig.RequireSame(server, joined);
            });
        });
    }

    private static ConfigValue Read(GameActor actor) => SyncedConfig.Read(actor, Capabilities.Config, LifecyclePlan.ModPlugin, "Server", "Greeting");

    private static ConfigValue Wait(CampaignRun run, GameActor client, string expected, TimeSpan timeout, LogWait? log)
    {
        Func<TimeSpan, CancellationToken, Task>? received = log == null ? null : (left, token) => log.WaitAsync(Received(expected), left, cancellation: token);
        return SyncedConfig.WaitForValue(client, Capabilities.Config, LifecyclePlan.ModPlugin, "Server", "Greeting", expected, timeout, run.Interval,
            received, run.Cancellation).GetAwaiter().GetResult();
    }

    /// <summary>MyMod's log line when a client receives the server's greeting.</summary>
    public static Regex Received(string greeting) => new($"Greeting \"{Regex.Escape(greeting)}\" received from -?\\d+ \\(client\\)", RegexOptions.CultureInvariant);
}
