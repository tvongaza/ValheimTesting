using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// Read-only checks shared by local one-shot commands and inventory preflight. A caller supplies only the roles it will
/// actually use; a server-only run need not have a client desktop or Steam account. No check creates run evidence or copies
/// game files. Campaigns use the same individual checks for their local actors and add their remote-host checks.
/// </summary>
internal static class LocalHostPreflight
{
    private static readonly AsyncLocal<Probes?> TestDefaults = new();

    internal sealed record Actor(string Name, EnvironmentRecipe Recipe);

    internal sealed record Probes(
        Func<IGameHost, bool, TimeSpan, CancellationToken, Task>? Processes = null,
        Func<IGameHost, int, TimeSpan, CancellationToken, Task>? Port = null,
        Func<IGameHost, string, TimeSpan, CancellationToken, Task<HostLockResult>>? Lock = null,
        Func<CancellationToken, Task>? Desktop = null,
        Action? MacDesktop = null,
        Func<bool>? SteamRunning = null,
        Func<EnvironmentInventory, CancellationToken, Task<IReadOnlyList<CampaignPreflightProblem>>>? Journals = null,
        Func<string?>? Packaged = null);

    internal static IDisposable ReplaceDefaultProbesForTest(Probes probes)
    {
        var previous = TestDefaults.Value;
        TestDefaults.Value = probes;
        return new TestProbeScope(previous);
    }

    private sealed class TestProbeScope(Probes? previous) : IDisposable
    {
        public void Dispose() => TestDefaults.Value = previous;
    }

    internal static async Task<IReadOnlyList<CampaignPreflightProblem>> InspectAsync(EnvironmentInventory inventory,
        IEnumerable<Actor> selected, TimeSpan timeout, CancellationToken cancellation = default,
        Func<string, IGameHost>? hostFactory = null, Probes? probes = null, bool includeJournal = true)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        probes ??= TestDefaults.Value ?? new Probes();
        var roles = selected.Where(actor => inventory.Hosts[actor.Recipe.Host].Kind == "local").ToArray();
        var problems = new List<CampaignPreflightProblem>();
        try
        {
            string? refusal = (probes.Packaged ?? PackagedApp.Refusal)();
            if (refusal != null) problems.Add(new("this-machine", "packaged app", refusal));
        }
        catch (InvalidOperationException error) { problems.Add(new("this-machine", "packaged app", error.Message)); }
        if (includeJournal)
            try
            {
                problems.AddRange(await (probes.Journals ?? ((item, token) => item.LocalJournalProblemsAsync(token)))(inventory, cancellation)
                    .ConfigureAwait(false));
            }
            catch (Exception error) when (Refusal(error)) { problems.Add(new("this-machine", "run journal", error.Message)); }
        foreach (var group in roles.GroupBy(actor => actor.Recipe.Host, StringComparer.Ordinal))
        {
            IGameHost host;
            try { host = hostFactory?.Invoke(group.Key) ?? new ResolvedEnvironment { Hosts = inventory.Hosts }.CreateHost(group.Key); }
            catch (Exception error) when (Refusal(error))
            {
                problems.Add(new(group.Key, "host", error.Message));
                continue;
            }
            bool client = group.Any(actor => actor.Recipe.Roles.Contains("client"));
            try
            {
                var claim = probes.Lock is { } lockProbe
                    ? await lockProbe(host, inventory.Hosts[group.Key].Lock, timeout, cancellation).ConfigureAwait(false)
                    : await host.CheckLockAsync(inventory.Hosts[group.Key].Lock, "valheim-test-preflight", timeout, cancellation).ConfigureAwait(false);
                if (claim.State != HostLockState.Free)
                    problems.Add(new(group.Key, "host lock", $"The host lock is not free: {claim.Detail}"));
            }
            catch (Exception error) when (Refusal(error)) { problems.Add(new(group.Key, "host lock", error.Message)); }
            if (client)
            {
                try
                {
                    if (inventory.Hosts[group.Key].Platform == "macos") (probes.MacDesktop ?? MacGuiSession.Require)();
                    if (inventory.Hosts[group.Key].Platform == "windows")
                        await (probes.Desktop ?? DesktopClientSession.PreflightAsync)(cancellation).ConfigureAwait(false);
                }
                catch (Exception error) when (Refusal(error)) { problems.Add(new(group.Key, "client desktop", error.Message)); }
                try
                {
                    if (!(probes.SteamRunning ?? ClientSession.SteamRunning)())
                        problems.Add(new(group.Key, "Steam session", "No Steam client is running in this desktop session; open Steam and sign in before running a client."));
                }
                catch (Exception error) when (Refusal(error)) { problems.Add(new(group.Key, "Steam session", error.Message)); }
            }
            try
            {
                if (probes.Processes is { } process) await process(host, client, timeout, cancellation).ConfigureAwait(false);
                // Recipe.Runtime is a parent directory for future copies, not one owned runtime. Treating the whole
                // parent as owned would wrongly refuse an unrelated dedicated server on the same host.
                else await HostedRuntimeStage.RequireStoppedAsync(host, timeout, cancellation, clientSession: client)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (Refusal(error)) { problems.Add(new(group.Key, "session", error.Message)); }
            foreach (var actor in group)
                try
                {
                    if (probes.Port is { } port) await port(host, actor.Recipe.CliPort, timeout, cancellation).ConfigureAwait(false);
                    else await HostInstall.RequirePortFreeAsync(host, actor.Recipe.CliPort, timeout, cancellation).ConfigureAwait(false);
                }
                catch (Exception error) when (Refusal(error)) { problems.Add(new(actor.Name, "ValheimCLI port", error.Message)); }
        }
        return problems;
    }

    private static bool Refusal(Exception error) => error is ArgumentException or InvalidOperationException or IOException or
        InvalidDataException or PlatformNotSupportedException or UnauthorizedAccessException or HostOperationException;

}
