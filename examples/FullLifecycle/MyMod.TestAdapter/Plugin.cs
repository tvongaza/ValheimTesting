using System.Collections;
using BepInEx;
using BepInEx.Bootstrap;
using Valheim.Testing.Adapter;
using valheimCLI.Extensions;

namespace MyMod.TestAdapter;

/// <summary>
/// The example's test adapter. It serves only the toolkit's owned-session identity (<c>mymod.testing/session</c>): the
/// scenario drives the mod through its own console command and observes with ValheimCLI's generic commands. A mod that
/// needs a test-only action or observation adds it here as another extension command.
/// </summary>
[BepInPlugin("example.mymod.testadapter", "MyMod Test Adapter (ValheimTesting example)", "0.1.0")]
[BepInDependency("example.mymod")]
[BepInDependency("valheimCLI.valheimCLI")]
public sealed class Plugin : BaseUnityPlugin
{
    private ExtensionRegistration? _registration;
    // The session is complete once the world is up and the mod itself is loaded.
    private IEnumerator Start() => TestExtension.Register("mymod.testing", "0.1.0", "MYMOD_TEST_SESSION_TOKEN",
        () => Chainloader.PluginInfos.ContainsKey("example.mymod"), registration => _registration = registration, Logger.LogError);
    private void OnDestroy() => _registration?.Dispose();
}
