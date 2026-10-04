using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Diagnostics;
using Valheim.Testing.Game;

namespace MyMod.SystemTests;

/// <summary>
/// The example's one-command wrapper around the ordinary pinned runner. The manifest supplies an arbitrary number of
/// named roles; this particular scenario reads client-a and client-b. It never modifies the source game installs.
/// </summary>
public static class ThreeActorCampaign
{
    public static bool Handles(string[] args) => args.Length > 0 && args[0] == "campaign";

    public static async Task<int> RunAsync(string[] args, PinnedServerRunOptions<LifecyclePlan> options)
    {
        if (args is not ["campaign", "check" or "run", var manifestFile, var templateFile, .. var rest] ||
            (args[1] == "check" ? rest.Length != 0 : rest.Length != 1))
        {
            Console.Error.WriteLine("Usage: MyMod.SystemTests campaign check <campaign.json> <scenario-template.json> | campaign run <campaign.json> <scenario-template.json> <new-output-directory>");
            return 2;
        }
        using var cancellation = new RunCancellation();
        try
        {
            HostedCampaignPreparation.Check(manifestFile);
            var manifest = HostedCampaignManifest.Read(manifestFile);
            var template = ServerRunPlan.Read<LifecyclePlan>(templateFile);
            var profile = manifest.Inventory.Length != 0
                ? EnvironmentInventory.Read(manifest.Inventory).Resolve(manifest).Profile
                : EnvironmentProfile.Read(manifest.Profile);
            if (!NativeDependencyLock.ReadReady(manifest.Server.DependencyLock).CliManifest.Files.Any(file =>
                    file.Plugins.Contains("valheimCLI.worldtools", StringComparer.Ordinal)))
                throw new InvalidOperationException("The server needs the pinned ValheimCLI WorldTools pack for cli_peers before the three-actor run starts.");
            if (template.Scenario is not (LifecyclePlan.ThreeActorScenario or LifecyclePlan.OwnershipHandoffScenario))
                throw new ArgumentException("This example campaign runs three-actor-smoke or ownership-handoff.");
            if (!manifest.Clients.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["client-a", "client-b"]))
                throw new ArgumentException("This example needs client-a and client-b; the toolkit's preparation supports any number of named clients.");
            if (profile.SteamAccounts == null ||
                (manifest.Inventory.Length == 0 &&
                 profile.Clients.Values.Any(client => string.IsNullOrWhiteSpace(client.SteamAccount))))
                throw new ArgumentException("This simultaneous-client example needs either an inventory that verifies signed-in accounts or a fixed profile with distinct leased accounts.");
            if (manifest.World.Length == 0 || manifest.Join.Length == 0)
                throw new ArgumentException("Set world and join in the campaign manifest.");
            if (template.Client == null || template.SecondClient == null)
                throw new ArgumentException("The three-actor template needs client and secondClient sections.");
            if (template.Arguments.Any(argument => argument.Contains('<') || argument.Contains('>')))
                throw new ArgumentException("Replace the template's password and other angle-bracket placeholders before preparing any host.");
            if (args[1] == "check")
            {
                Console.WriteLine("READY: reviewed mod and ValheimCLI locks, fixture, two independent disposable characters and host/account profile. No host was changed.");
                return 0;
            }
            string output = Path.GetFullPath(rest[0]);
            if (Path.Exists(output)) throw new IOException("Use a new campaign output directory; evidence is never overwritten.");
            Directory.CreateDirectory(output);
            var clock = Stopwatch.StartNew();
            var prepared = await HostedCampaignPreparation.PrepareAsync(manifestFile, Path.Combine(output, "prepared"),
                TimeSpan.FromMinutes(10), cancellation: cancellation.Token).ConfigureAwait(false);
            double preparationSeconds = clock.Elapsed.TotalSeconds;
            Console.WriteLine($"Campaign preparation: {preparationSeconds:F1}s for {manifest.Clients.Count + 1} actors.");
            int result;
            try
            {
                var plan = template;
                prepared.ApplyTo(plan, manifest, new Dictionary<string, ClientRunPlan>
                {
                    ["client-a"] = plan.Client!, ["client-b"] = plan.SecondClient!,
                }, output);
                string planFile = Path.Combine(output, "plan.json");
                var json = JsonSerializer.SerializeToNode(plan, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
                    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
                })!.AsObject();
                if (plan.Scenario == LifecyclePlan.ThreeActorScenario)
                    foreach (string unused in new[] { "drySite", "wetSite", "arrival" }) json.Remove(unused);
                File.WriteAllText(planFile, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
                _ = LifecyclePlan.ReadValidated(planFile);
                result = await PinnedServerRun.MainAsync([PinnedServerRun.ProfileOption, prepared.ProfileFile,
                    "run", planFile, Path.Combine(output, "run")], options).ConfigureAwait(false);
            }
            catch
            {
                // The retirement code acquires each host lock and refuses any active game process. An uncertain
                // process remains visible instead of losing its character or install underneath it.
                try { await prepared.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { Console.Error.WriteLine("Prepared copies retained: " + cleanup.Message); }
                throw;
            }
            double scenarioSeconds = clock.Elapsed.TotalSeconds - preparationSeconds;
            bool cleanupSucceeded = true;
            try { await prepared.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup)
            {
                cleanupSucceeded = false;
                Console.Error.WriteLine("Prepared copies retained; inspect the host and result before retrying: " + cleanup.Message);
            }
            File.WriteAllText(Path.Combine(output, "campaign-times.json"), JsonSerializer.Serialize(new
            {
                actors = manifest.Clients.Count + 1,
                preparationSeconds,
                scenarioSeconds,
                cleanupSeconds = clock.Elapsed.TotalSeconds - preparationSeconds - scenarioSeconds,
                cleanupSucceeded,
                result,
            }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            return result == 0 && !cleanupSucceeded ? 1 : result;
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or HostOperationException)
        {
            Console.Error.WriteLine("Campaign setup: " + error.Message);
            return 2;
        }
        catch (OperationCanceledException) when (cancellation.Token.IsCancellationRequested)
        {
            Console.Error.WriteLine("Campaign setup was interrupted; owned preparation cleanup was attempted before exit.");
            return 130;
        }
    }

}
