using System.Security.Cryptography;
using Valheim.Testing.Game;

namespace Valheim.Testing.NativeAcceptance.Tests;

// An owned, source-only install for scripted hosted scenarios. The metadata is hand-authored and has the game's verified
// enum/predicate shape; no game bytes or native process are involved. The supplied ClientSession.Launch uses FakeOwnedProcess.
internal static class SyntheticWorldHost
{
    internal static void Prepare(ClientRunPlan plan, string root)
    {
        plan.Mode = "owned"; plan.Install = root;
        string managed = Path.Combine(root, "valheim_Data/Managed");
        Directory.CreateDirectory(managed);
        string executable = Path.Combine(root, "valheim.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!); File.WriteAllText(executable, "synthetic executable, never launched");
        File.WriteAllBytes(Path.Combine(managed, "assembly_valheim.dll"), GameAssemblyFixture.Create("1.0.17", 40u));
        string plugins = Path.Combine(root, "BepInEx", "plugins"); Directory.CreateDirectory(plugins);
        foreach (string guid in plan.Pins.Keys.Where(key => key != "valheimCLI.valheimCLI").ToArray())
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes("synthetic plugin " + guid);
            File.WriteAllBytes(Path.Combine(plugins, guid + ".dll"), bytes);
            plan.Pins[guid] = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
        }
    }

}
