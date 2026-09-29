using Valheim.Testing.Bindings;
using Xunit;

public class BindingCheckTests : IClassFixture<GameAssemblies>
{
    private readonly GameAssemblies _game;

    public BindingCheckTests(GameAssemblies game) => _game = game;

    private static BindingCheckOptions Directories(params string[] directories)
    {
        var options = new BindingCheckOptions();
        foreach (string directory in directories) options.GameDirectories.Add(directory);
        return options;
    }

    private static string[] Members(IEnumerable<BindingFinding> findings) => findings.Select(f => $"{f.Kind} {f.Member}").OrderBy(s => s, StringComparer.Ordinal).ToArray();

    private static BindingFinding Finding(IEnumerable<BindingFinding> findings, string member) => Assert.Single(findings, f => f.Member == member);

    [Fact] public void NegativeControlTheModAgainstTheAssemblyItWasBuiltWithReportsNothing()
    {
        foreach (string mod in new[] { _game.Mod, _game.ModDeclared })
        {
            BindingReport report = BindingCheck.Check(mod, Directories(_game.V1));
            Assert.True(report.Binds);
            Assert.Empty(report.Missing);
            Assert.Empty(report.Access);
            Assert.Empty(report.MissingRequired);
            CheckedAssembly game = Assert.Single(report.Checked);
            Assert.Equal("assembly_valheim", game.Name);
            Assert.True(game.References > 20, "only " + game.References + " references were checked");
        }
    }

    [Fact] public void RemovedRenamedAndChangedMembersAreMissingInTheUpdateAndMatchingOnesAreNot()
    {
        BindingReport report = BindingCheck.Check(_game.Mod, Directories(_game.V2));

        Assert.False(report.Binds);
        // Exactly these: every other reference (kept fields and properties, a field moved to the base class, the
        // remaining overload, byref/out/array/multi-dimensional parameters, generic types and methods, nested types and
        // the type forwarded to assembly_utils) binds and must not be reported.
        Assert.Equal(new[]
        {
            "MissingField System.Single Terminal::m_speed",
            "MissingField System.String Terminal::m_input",
            "MissingMethod System.Int32 Terminal::get_Level()",
            "MissingMethod System.Void Terminal::Collect(System.Collections.Generic.List`1<System.String>)",
            "MissingMethod System.Void Terminal::InputText()",
            "MissingMethod System.Void Terminal::Overloaded(System.String)",
            "MissingMethod System.Void Terminal::Pass(System.Int32&)",
            "MissingMethod System.Void Terminal::Shape(System.Int32[])",
            "MissingType Removed",
            "MissingType Terminal/GoneNested",
        }, Members(report.Missing));
        Assert.All(report.Missing, f => Assert.Equal("assembly_valheim", f.Assembly));

        Assert.Equal(new[] { "MyMod.Patches::UseInput(Terminal)" }, Finding(report.Missing, "System.String Terminal::m_input").UsedBy);
        Assert.Equal(new[] { "MyMod.Patches::UseRename(Terminal)" }, Finding(report.Missing, "System.Void Terminal::InputText()").UsedBy);
        Assert.Equal(new[] { "MyMod.Patches::UseGoneNested()" }, Finding(report.Missing, "Terminal/GoneNested").UsedBy);
        Assert.Contains("System.Void Terminal::Overloaded(System.Int32)", Finding(report.Missing, "System.Void Terminal::Overloaded(System.String)").Detail);
        Assert.Contains("has type System.Double", Finding(report.Missing, "System.Single Terminal::m_speed").Detail);
        Assert.Contains("Terminal::Level is a field", Finding(report.Missing, "System.Int32 Terminal::get_Level()").Detail);

        Assert.Equal(new[] { "assembly_utils", "assembly_valheim" }, report.Checked.Select(c => c.Name).ToArray());
        Assert.Contains(report.NotChecked, a => a.Name == "System.Private.CoreLib");
    }

    [Fact] public void AccessChangesAreReportedSeparatelyWithProtectedAccessAllowedFromDerivedTypes()
    {
        BindingReport report = BindingCheck.Check(_game.Mod, Directories(_game.V2));

        Assert.Equal(new[]
        {
            "InaccessibleField System.Int32 Terminal::m_secret",
            "InaccessibleMethod System.Void Terminal::Guarded()",
            "InaccessibleMethod System.Void Terminal::Hidden()",
        }, Members(report.Access));
        Assert.Equal("private", Finding(report.Access, "System.Void Terminal::Hidden()").Detail);
        BindingFinding guarded = Finding(report.Access, "System.Void Terminal::Guarded()");
        Assert.Equal("protected", guarded.Detail);
        // MyTerminal derives from Terminal, so its call is allowed; only the unrelated class is reported.
        Assert.Equal(new[] { "MyMod.Patches::UseGuarded(Terminal)" }, guarded.UsedBy);
        Assert.All(report.Access, f => Assert.False(f.AccessDeclared));
    }

    [Fact] public void APublicizedBuildAgainstTheRealAssemblyHasOnlyAccessFindingsDeclaredOrNot()
    {
        BindingReport undeclared = BindingCheck.Check(_game.Mod, Directories(_game.V1Real));
        Assert.True(undeclared.Binds);
        Assert.Equal(new[] { "InaccessibleField System.Int32 Terminal::m_secret", "InaccessibleMethod System.Void Terminal::Hidden()" }, Members(undeclared.Access));
        Assert.Equal(2, undeclared.UndeclaredAccess.Count());

        BindingReport declared = BindingCheck.Check(_game.ModDeclared, Directories(_game.V1Real));
        Assert.True(declared.Binds);
        Assert.Equal(new[] { "assembly_valheim" }, declared.IgnoresAccessChecksTo);
        Assert.Equal(2, declared.Access.Count);
        Assert.All(declared.Access, f => Assert.True(f.AccessDeclared));
        Assert.Empty(declared.UndeclaredAccess);
    }

    [Fact] public void APinnedFileIsMatchedByTheNameInsideItAndUnsuppliedForwardingTargetsAreNotChecked()
    {
        string pinned = Path.Combine(_game.Root, "pinned", "assembly_valheim-v2.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(pinned)!);
        File.Copy(Path.Combine(_game.V2, "assembly_valheim.dll"), pinned, overwrite: true);
        var options = new BindingCheckOptions();
        options.GameFiles.Add(pinned);

        BindingReport report = BindingCheck.Check(_game.Mod, options);

        Assert.Equal(pinned, Assert.Single(report.Checked).Path);
        Assert.Equal(10, report.Missing.Count);
        // Helper is forwarded to assembly_utils, which was not supplied: not checked, and not missing.
        Assert.DoesNotContain(report.Missing, f => f.Member.Contains("Helper"));
        Assert.Contains(report.NotChecked, a => a.Name == "assembly_utils" && a.References >= 2);
    }

    [Fact] public void OnlyLimitsWhichDirectoryAssembliesAreChecked()
    {
        BindingCheckOptions options = Directories(_game.V2);
        options.OnlyAssemblies.Add("assembly_valheim");

        BindingReport report = BindingCheck.Check(_game.Mod, options);

        Assert.Equal("assembly_valheim", Assert.Single(report.Checked).Name);
        Assert.Contains(report.NotChecked, a => a.Name == "assembly_utils");
    }

    [Fact] public void ARequiredAssemblyThatWasNotSuppliedMakesTheReportIncomplete()
    {
        BindingReport report = BindingCheck.Check(_game.Mod, Directories(_game.UtilsOnly));

        Assert.Equal(new[] { "assembly_valheim" }, report.MissingRequired);
        Assert.Empty(report.Missing);
        Assert.Contains(report.NotChecked, a => a.Name == "assembly_valheim");
    }

    [Fact] public void MissingInputsAreRefused()
    {
        Assert.Throws<FileNotFoundException>(() => BindingCheck.Check(Path.Combine(_game.Root, "absent.dll"), Directories(_game.V1)));
        Assert.Throws<DirectoryNotFoundException>(() => BindingCheck.Check(_game.Mod, Directories(Path.Combine(_game.Root, "absent"))));
        var options = new BindingCheckOptions();
        options.GameFiles.Add(Path.Combine(_game.Root, "absent", "assembly_valheim.dll"));
        Assert.Throws<FileNotFoundException>(() => BindingCheck.Check(_game.Mod, options));
    }
}
