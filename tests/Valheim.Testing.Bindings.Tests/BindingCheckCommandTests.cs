using Valheim.Testing.Bindings;
using Xunit;

public class BindingCheckCommandTests : IClassFixture<GameAssemblies>
{
    private readonly GameAssemblies _game;

    public BindingCheckCommandTests(GameAssemblies game) => _game = game;

    private static (int Exit, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = BindingCheckCommand.Run(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact] public void AMalformedModIsIncompleteNotACrash()
    {
        // Overwrite the IL of the mod's largest method body with 0xFF: Cecil then throws while decoding it (not an
        // IOException or BadImageFormatException), and the command must still answer with exit code 2.
        string mod = Path.Combine(_game.Root, "malformed", "MyMod.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(mod)!);
        byte[] bytes = File.ReadAllBytes(_game.Mod);
        using (var module = Mono.Cecil.ModuleDefinition.ReadModule(new MemoryStream(bytes)))
        using (var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(bytes)))
        {
            var method = module.Types.SelectMany(t => t.Methods).Where(m => m.HasBody).OrderByDescending(m => m.Body.CodeSize).First();
            int rva = method.RVA, size = method.Body.CodeSize;
            var section = pe.PEHeaders.SectionHeaders.Single(h => rva >= h.VirtualAddress && rva < h.VirtualAddress + h.VirtualSize);
            int offset = rva - section.VirtualAddress + section.PointerToRawData;
            int header = (bytes[offset] & 3) == 2 ? 1 : (bytes[offset + 1] >> 4) * 4; // tiny, or a fat header's own size
            Array.Fill(bytes, (byte)0xFF, offset + header, size);
        }
        File.WriteAllBytes(mod, bytes);
        var (exit, output, error) = Run(mod, _game.Mod, "--game-dir", _game.V1);
        Assert.Equal(BindingCheckCommand.Incomplete, exit);
        Assert.Contains("valheim-bindings: cannot check " + mod + ": ", error);
        Assert.Contains("PASS: every checked reference binds", output); // the next mod is still checked
    }

    [Fact] public void TheBuildItWasCompiledAgainstPasses()
    {
        var (exit, output, _) = Run(_game.Mod, "--game-dir", _game.V1);
        Assert.Equal(BindingCheckCommand.Binds, exit);
        Assert.Contains("PASS: every checked reference binds; no access findings.", output);
        Assert.DoesNotContain("MISSING", output);
    }

    [Fact] public void AnUpdateWithMissingReferencesFailsAndNamesTheReferencingMethod()
    {
        var (exit, output, _) = Run(_game.Mod, "--game-dir", _game.V2);
        Assert.Equal(BindingCheckCommand.Fails, exit);
        Assert.Contains("MISSING field System.String Terminal::m_input in assembly_valheim", output);
        Assert.Contains("  used by MyMod.Patches::UseInput(Terminal)", output);
        Assert.Contains("MISSING method System.Void Terminal::InputText() in assembly_valheim", output);
        Assert.Contains("ACCESS warning: method System.Void Terminal::Hidden() is private in assembly_valheim", output);
        Assert.Contains("FAIL: 10 missing references; 3 access findings (3 without IgnoresAccessChecksTo).", output);
    }

    [Fact] public void AccessFindingsFailOnlyWhenAskedAndOnlyWithoutIgnoresAccessChecksTo()
    {
        Assert.Equal(BindingCheckCommand.Binds, Run(_game.Mod, "--game-dir", _game.V1Real).Exit);
        Assert.Equal(BindingCheckCommand.Fails, Run(_game.Mod, "--game-dir", _game.V1Real, "--fail-on-access").Exit);
        var (exit, output, _) = Run(_game.ModDeclared, "--game-dir", _game.V1Real, "--fail-on-access");
        Assert.Equal(BindingCheckCommand.Binds, exit);
        Assert.Contains("ACCESS info: method System.Void Terminal::Hidden() is private in assembly_valheim; the mod declares IgnoresAccessChecksTo(\"assembly_valheim\")", output);
    }

    [Fact] public void SeveralModsReportTheWorstOutcome()
    {
        // The declared mod passes on its own; the undeclared one fails.
        Assert.Equal(BindingCheckCommand.Binds, Run(_game.ModDeclared, "--game-dir", _game.V1Real, "--fail-on-access").Exit);
        Assert.Equal(BindingCheckCommand.Fails, Run(_game.ModDeclared, _game.Mod, "--game-dir", _game.V1Real, "--fail-on-access").Exit);
    }

    [Fact] public void ARequiredAssemblyThatWasNotSuppliedIsIncomplete()
    {
        var (exit, output, _) = Run(_game.Mod, "--game-dir", _game.UtilsOnly);
        Assert.Equal(BindingCheckCommand.Incomplete, exit);
        Assert.Contains("INCOMPLETE: the mod references assembly_valheim, which was not supplied", output);
    }

    [Fact] public void BadArgumentsAndInputsAreIncomplete()
    {
        Assert.Equal(BindingCheckCommand.Incomplete, Run().Exit);
        Assert.Equal(BindingCheckCommand.Incomplete, Run(_game.Mod).Exit);
        Assert.Equal(BindingCheckCommand.Incomplete, Run(_game.Mod, "--game-dir").Exit);
        Assert.Equal(BindingCheckCommand.Incomplete, Run(_game.Mod, "--game-dir", _game.V1, "--max-users", "x").Exit);
        Assert.Equal(BindingCheckCommand.Incomplete, Run(_game.Mod, "--game-dir", _game.V1, "--frobnicate").Exit);
        var (exit, _, error) = Run(Path.Combine(_game.Root, "absent.dll"), "--game-dir", _game.V1);
        Assert.Equal(BindingCheckCommand.Incomplete, exit);
        Assert.Contains("cannot check", error);
        Assert.Equal(BindingCheckCommand.Incomplete, Run(_game.Mod, "--game-dir", Path.Combine(_game.Root, "absent")).Exit);
    }

    [Fact] public void HelpPrintsUsage()
    {
        var (exit, output, _) = Run("--help");
        Assert.Equal(BindingCheckCommand.Binds, exit);
        Assert.Contains("Exit codes:", output);
    }
}
