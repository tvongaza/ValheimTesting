using System.Collections;
using BepInEx;
using BepInEx.Bootstrap;
using Valheim.Testing.Adapter;
using valheimCLI.Extensions;

namespace ExampleMod.TestAdapter;

/// <summary>
/// The example's test adapter, installed only in test runtimes. It serves the toolkit's owned-session identity
/// (<c>examplemod.testing/session</c>), which proves the runner talks to its own server. ValheimCLI's optional
/// Observe pack owns <c>valheim.observe/harmony</c>, which checks the mod's declared patches. The scenario drives
/// the mod through its own console command. A mod that needs a test-only action or observation adds it here as
/// another extension command; generic game observations belong in ValheimCLI.
/// <para>
/// The adapter never references ExampleMod's types and depends on it only softly, so it also loads where the mod is absent.
/// </para>
/// </summary>
[BepInPlugin("example.examplemod.testadapter", "ExampleMod Test Adapter (ValheimTesting example)", "0.1.0")]
[BepInDependency("example.examplemod", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("valheimCLI.valheimCLI")]
public sealed class Plugin : BaseUnityPlugin
{
    /// <summary>The owned session's token, which only the runner sets.</summary>
    public const string TokenVariable = "EXAMPLEMOD_TEST_SESSION_TOKEN";
    private ExtensionRegistration? _registration;

    // The session is complete once the world is up and the mod itself is loaded.
    private IEnumerator Start() => TestExtension.Register("examplemod.testing", "0.1.0", TokenVariable,
        () => Chainloader.PluginInfos.ContainsKey("example.examplemod"), registration => _registration = registration, Logger.LogError);
    private void OnDestroy() => _registration?.Dispose();
}
