using Valheim.Testing.Game;
using Xunit;

public sealed class DirectWorldStartTests
{
    [Fact]
    public void DedicatedJoinWritesAOneUsePasswordFreeRequestAndRequiresPreparedSpawn()
    {
        string output = Directory.CreateTempSubdirectory("vt-direct-start-").FullName;
        try
        {
            var plan = Plan(output);
            Assert.Contains("prepared character", Assert.Throws<ArgumentException>(() => DirectWorldStart.Write(plan, output)).Message);
            plan.StartAtCharacterSave = true;
            plan.CharacterStart = new CharacterStartPlan
            {
                PreparedFile = Path.Combine(output, "fresh.fch"), Sha256 = new string('a', 64),
                CharactersLocalDirectory = Path.Combine(output, "characters_local"),
                SteamUserDataDirectory = Path.Combine(output, "userdata"), CharacterStore = output,
            };
            string file = DirectWorldStart.Write(plan, output);
            string text = File.ReadAllText(file);
            Assert.Contains("mode=join\n", text);
            Assert.Contains("character=fresh\n", text);
            Assert.Contains("target=127.0.0.1:2456\n", text);
            Assert.Contains("passwordVariable=VT_SECRET\n", text);
            Assert.Contains("devcommands=true\n", text);
            Assert.DoesNotContain("the-secret", text);
            Assert.Throws<IOException>(() => DirectWorldStart.Write(plan, output)); // Never overwrite a request or evidence.
        }
        finally { Directory.Delete(output, recursive: true); }
    }

    [Fact]
    public void DirectStartCannotAttachCrossplayOrNameAnotherWorld()
    {
        string output = Path.GetFullPath("direct-start-evidence");
        var plan = Plan(output);
        plan.Mode = "attach"; plan.Install = "";
        Assert.Contains("owned client", Assert.Throws<ArgumentException>(() => plan.Validate()).Message);
        plan.Mode = "owned"; plan.Install = output; plan.Crossplay = true; plan.Join = ""; plan.PasswordVariable = null;
        Assert.Contains("crossplay", Assert.Throws<ArgumentException>(() => plan.Validate()).Message);
        plan.Crossplay = false; plan.Join = "127.0.0.1:2456"; plan.DirectStartWorldUid = "";
        Assert.Contains("directStartWorldUid", Assert.Throws<ArgumentException>(() => plan.Validate()).Message);
    }

    private static ClientRunPlan Plan(string output) => new()
    {
        Mode = "owned", Install = output, Port = 5556, Join = "127.0.0.1:2456", Character = "fresh",
        DirectStart = true, DirectStartWorldUid = "12345", PasswordVariable = "VT_SECRET", Pinning = "none",
    };
}
