using System.Text.Json;
namespace Valheim.Testing.Game;

// A short-lived pointer to one owned Windows desktop client. It is useful only while that exact process is alive;
// a separate valheim-test invocation checks the start identity again before sending a strictly pinned command.
internal sealed record OwnedClientCommandLease(int Pid, string StartFileTimeUtc, int Port, string Install)
{
    internal const string FileName = "owned-cli.json";
    internal const string MenuPins = "owned-cli-menu.pins";
    internal const string WorldPins = "owned-cli-world.pins";

    internal static void Write(string output, int pid, string startFileTimeUtc, ClientRunPlan plan)
    {
        if (!plan.Pinned) return; // No strict expectation can be derived from an unpinned plan.
        var lease = new OwnedClientCommandLease(pid, startFileTimeUtc, plan.Port, plan.Install);
        File.WriteAllText(Path.Combine(output, FileName), JsonSerializer.Serialize(lease, new JsonSerializerOptions { WriteIndented = true }));
        string[] pluginPins = plan.Pins.Select(pair => pair.Key + "=" + pair.Value).ToArray();
        File.WriteAllLines(Path.Combine(output, MenuPins), pluginPins);
        if (plan.HostWorld?.WorldUid is { Length: > 0 } world)
            File.WriteAllLines(Path.Combine(output, WorldPins), [.. pluginPins, "worlduid=" + world]);
    }
}
