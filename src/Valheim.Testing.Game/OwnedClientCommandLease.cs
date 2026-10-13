using System.Text.Json;
using System.Runtime.Versioning;
namespace Valheim.Testing.Game;

// A short-lived pointer to one owned client. It is useful only while that exact process is alive;
// a separate valheim-test invocation checks the start identity again before sending a strictly pinned command.
internal sealed record OwnedClientCommandLease(int Pid, string StartFileTimeUtc, int Port, string Install)
{
    internal const UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    internal const string FileName = "owned-cli.json";
    internal const string MenuPins = "owned-cli-menu.pins";
    internal const string WorldPins = "owned-cli-world.pins";

    internal static void Write(string output, int pid, string startFileTimeUtc, ClientRunPlan plan)
    {
        var lease = new OwnedClientCommandLease(pid, startFileTimeUtc, plan.Port, plan.Install);
        string path = Path.Combine(output, FileName);
        string json = JsonSerializer.Serialize(lease, new JsonSerializerOptions { WriteIndented = true });
        if (OperatingSystem.IsWindows()) File.WriteAllText(path, json);
        else WritePrivateUnixFile(path, json);
        string[] pluginPins = plan.Pins.Select(pair => pair.Key + "=" + pair.Value).ToArray();
        WritePins(Path.Combine(output, MenuPins), pluginPins);
    }

    [UnsupportedOSPlatform("windows")]
    private static void WritePrivateUnixFile(string path, string contents)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = PrivateMode,
            };
            using (var stream = new FileStream(temporary, options))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                File.SetUnixFileMode(stream.SafeFileHandle, PrivateMode);
                writer.Write(contents);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // A joined client's world is learned from the server fixture, not HostWorld. Publish this file only
    // after the client has verified that exact world; a concurrent diagnostic must never read half a pin set.
    internal static void WriteVerifiedWorld(string output, ClientRunPlan plan, string worldUid)
    {
        if (!File.Exists(Path.Combine(output, FileName))) return;
        string path = Path.Combine(output, WorldPins);
        string[] pins = [.. plan.Pins.Select(pair => pair.Key + "=" + pair.Value), "worlduid=" + worldUid];
        if (!OperatingSystem.IsWindows())
        {
            WritePins(path, pins);
            return;
        }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporary, pins);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WritePins(string path, IReadOnlyCollection<string> pins)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllLines(path, pins);
            return;
        }
        WritePrivateUnixFile(path, string.Join(Environment.NewLine, pins) + Environment.NewLine);
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
