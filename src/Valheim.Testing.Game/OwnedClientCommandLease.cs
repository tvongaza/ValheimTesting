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
    }

    // A joined client's world is learned from the server fixture, not HostWorld. Publish this file only
    // after the client has verified that exact world; a concurrent diagnostic must never read half a pin set.
    internal static void WriteVerifiedWorld(string output, ClientRunPlan plan, string worldUid)
    {
        if (!plan.Pinned || !File.Exists(Path.Combine(output, FileName))) return;
        string path = Path.Combine(output, WorldPins);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporary, [.. plan.Pins.Select(pair => pair.Key + "=" + pair.Value), "worlduid=" + worldUid]);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void LeaveWorld(string output) => File.Delete(Path.Combine(output, WorldPins));

    // Only the owned run's evidence files are retired. Command records remain as evidence.
    internal static void Retire(string output)
    {
        LeaveWorld(output);
        File.Delete(Path.Combine(output, MenuPins));
        File.Delete(Path.Combine(output, FileName));
    }
}
