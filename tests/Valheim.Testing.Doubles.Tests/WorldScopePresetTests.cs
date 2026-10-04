using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using Valheim.Testing.Doubles;
using Xunit;

// A plugin that localizes in Awake: fine on a 1.0.16 client and dedicated server; it failed on some pre-1.0 clients.
[BepInPlugin("example.eager", "Eager", "1.0.0")]
public sealed class EagerPlugin : BaseUnityPlugin
{
    public string Greeting = "";
    private void Awake() => Greeting = Localization.instance.Localize("$eager_hello");
}

// The same plugin written to wait: Awake only binds config; the text is made when the game asks for it.
[BepInPlugin("example.patient", "Patient", "1.0.0")]
public sealed class PatientPlugin : BaseUnityPlugin
{
    public ConfigEntry<bool> Enabled = null!;
    private void Awake() => Enabled = Config.Bind("General", "Enabled", true, "Switch the mod on.");
    public string Greeting() => Localization.instance.Localize("$eager_hello");
}

// A Player patch as mods write one (a postfix on Player.Start): it must do its work only for the local player.
internal static class NameplatePatch
{
    public static readonly List<string> Shown = new();
    public static void Postfix(Player __instance)
    {
        if (__instance != Player.m_localPlayer || !__instance.IsOwner()) return;
        Shown.Add(__instance.GetPlayerName());
    }
}

public sealed class WorldScopePresetTests
{
    [Fact] public void EachPresetHasTheGamesFlagsForItsRole()
    {
        using (new ValheimWorldScope().AsDedicatedServer())
        {
            Assert.True(ZNet.instance.IsServer()); Assert.True(ZNet.instance.IsDedicated()); Assert.Null(Player.m_localPlayer);
            Assert.NotNull(ZDOMan.instance); Assert.NotNull(ZNetScene.instance); Assert.Empty(Player.GetAllPlayers());
        }
        using (new ValheimWorldScope().AsHost(new UnityEngine.Vector3(1, 2, 3)))
        {
            Assert.True(ZNet.instance.IsServer()); Assert.False(ZNet.instance.IsDedicated());
            var local = Player.m_localPlayer!;
            Assert.True(local.IsOwner()); Assert.Equal(3f, local.transform.position.z); Assert.Same(local, Player.GetAllPlayers().Single());
            Assert.Equal(ZDOMan.instance!.m_sessionID, ZDOMan.instance.GetZDO(local.GetZDOID())!.GetOwner());
        }
        using (new ValheimWorldScope().AsClient())
        {
            Assert.False(ZNet.instance.IsServer()); Assert.False(ZNet.instance.IsDedicated()); Assert.NotNull(Player.m_localPlayer);
        }
        using (var menu = new ValheimWorldScope().AtMainMenu())
        {
            Assert.Null(ZNet.instance); Assert.Null(ZDOMan.instance); Assert.Null(ZNetScene.instance); Assert.Null(WorldGenerator.instance);
            Assert.Null(Player.m_localPlayer);
            var preview = menu.PreviewPlayer!;
            Assert.Same(preview, Player.GetAllPlayers().Single()); // the preview is a player object too, as in the game
            Assert.False(preview.m_nview.IsValid()); Assert.False(preview.IsOwner()); Assert.Equal("", preview.GetPlayerName());
            Assert.Throws<InvalidOperationException>(() => menu.AddRemotePlayer(5, default));
        }
        Assert.NotNull(ZNet.instance); Assert.False(ZNet.instance.IsDedicated()); // everything is back
    }

    [Fact] public void APlayerPatchRunsForTheMenuPreviewAndRemotePlayersButActsOnlyForTheLocalOne()
    {
        NameplatePatch.Shown.Clear();
        using (var menu = new ValheimWorldScope().AtMainMenu())
            foreach (var player in Player.GetAllPlayers()) NameplatePatch.Postfix(player); // Player.Start runs for the preview
        Assert.Empty(NameplatePatch.Shown);

        using var host = new ValheimWorldScope().AsHost();
        var remote = host.AddRemotePlayer(7, new UnityEngine.Vector3(10, 30, 10), "Visitor");
        Assert.Equal("Visitor", remote.GetPlayerName()); Assert.False(remote.IsOwner());
        var peer = ZNet.instance.GetPeers().Single();
        Assert.Equal(7L, peer.m_uid); Assert.Equal(remote.GetZDOID(), peer.m_characterID); // on a server the remote player is a connected peer
        foreach (var player in Player.GetAllPlayers()) NameplatePatch.Postfix(player);
        Assert.Equal(new[] { "..." }, NameplatePatch.Shown); // only the host's own player (no name set in its ZDO yet)
        Assert.Equal(2, Player.GetAllPlayers().Count);
        Assert.Throws<ArgumentException>(() => host.AddRemotePlayer(ZDOMan.instance!.m_sessionID, default));
    }

    [Fact] public void OnAClientARemotePlayerIsNotAPeer()
    {
        using var client = new ValheimWorldScope().AsClient();
        var remote = client.AddRemotePlayer(9, default, "Other");
        Assert.Empty(ZNet.instance.GetPeers()); Assert.Contains(remote, Player.GetAllPlayers());
    }


    [Fact] public void APluginAwakeThatLocalizesWorksOnADedicatedServer()
    {
        // In 1.0.16 PlatformPrefs falls back to PlayerPrefs on a dedicated server, so this is safe there.
        using var scope = new ValheimWorldScope().AsDedicatedServer().WithConfigFiles().WithScene();
        var plugins = new Chainloader();
        Assert.Equal("[eager_hello]", plugins.Load<EagerPlugin>().Greeting);
        Assert.True(plugins.Load<PatientPlugin>().Enabled.Value);
    }

    [Fact] public void OptingIntoThePre10PlatformFailureMakesALocalizingAwakeThrow()
    {
        using var scope = new ValheimWorldScope().AsClient().WithConfigFiles().WithScene();
        PlatformPrefs.Unavailable = "Steamworks is not initialized yet (a pre-1.0 client)";
        var plugins = new Chainloader();
        var error = Assert.Throws<InvalidOperationException>(() => plugins.Load<EagerPlugin>());
        Assert.Contains("Steamworks is not initialized yet", error.Message);
        Assert.Throws<InvalidOperationException>(() => PlatformPrefs.GetString("language"));
        // Written to wait, the same work is safe: Awake binds config only, and the text is made once the platform is up.
        var patient = plugins.Load<PatientPlugin>();
        Assert.True(patient.Enabled.Value);
        PlatformPrefs.Unavailable = null;
        Localization.instance.AddWord("eager_hello", "Hello");
        Assert.Equal("Hello", patient.Greeting());
    }

    [Fact] public void TheOptInIsRestoredWithTheScope()
    {
        using (new ValheimWorldScope().AsClient()) PlatformPrefs.Unavailable = "down";
        Assert.Null(PlatformPrefs.Unavailable);
    }
}
