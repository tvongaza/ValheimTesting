using System.Text.RegularExpressions;
using Valheim.Testing.GameSessions;
using Xunit;

// #743: every recursive delete in the text of a host script goes through the one remover in ProtectedPaths (vt_remove for bash,
// Remove-VtTree for pwsh), which unlinks a link instead of emptying its target and refuses a path outside its root. A bare
// `rm -rf`, `Remove-Item -Recurse` or `[IO.Directory]::Delete(path, $true)` written into script text anywhere else is the defect.
// The scan reads the toolkit's sources (this reads the file system, so the class carries no Os "neutral" trait, #735); a recursive
// delete in C# code itself (Directory.Delete(path, true)) is not script text and is not matched.
public sealed class HostScriptDeleteGuardTests
{
    // The one list of patterns, each with the kind of script it belongs to. A line is judged whole; comment lines are skipped.
    private static readonly (string Name, Regex Pattern)[] Patterns =
    [
        ("bash rm -r", new Regex(@"\brm\s+(?:-\S+\s+)*(?:-[A-Za-z]*[rR][A-Za-z]*|--recursive)\b", RegexOptions.CultureInvariant)),
        ("PowerShell Remove-Item -Recurse", new Regex(@"\b(?:Remove-Item|ri|del|rd|rm)\b[^\r\n]*\s-r(?:ec\w*)?\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)),
        ("[IO.Directory]::Delete(path, $true)", new Regex(@"\[(?:System\.)?IO\.Directory\]::Delete\([^\r\n]*,\s*\$true\s*\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)),
        ("DirectoryInfo.Delete($true)", new Regex(@"\.Delete\(\s*\$true\s*\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)),
        ("find -delete", new Regex(@"\bfind\b[^\r\n]*\s-delete\b", RegexOptions.CultureInvariant)),
        ("cmd rmdir /s", new Regex(@"\b(?:rmdir|rd)\s+/s\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)),
    ];

    // The owner: the only file whose script text may delete recursively.
    private const string Owner = "src/Valheim.Testing.Game/ProtectedPaths.cs";

    internal static IEnumerable<string> BareRecursiveDeletes(string name, string text)
    {
        string[] lines = text.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            // A comment (C#, bash or PowerShell) deletes nothing.
            if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('#')) continue;
            foreach (var (kind, pattern) in Patterns)
                if (pattern.IsMatch(line)) yield return $"{name}:{index + 1}: {kind}: {line}";
        }
    }

    private static Dictionary<string, string> Sources()
    {
        string root = FixtureProjects.RepositoryRoot();
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new Stack<string>([Path.Combine(root, "src")]);
        while (pending.Count != 0)
        {
            string directory = pending.Pop();
            foreach (string child in Directory.EnumerateDirectories(directory))
                if (Path.GetFileName(child) is not ("bin" or "obj")) pending.Push(child);
            foreach (string file in Directory.EnumerateFiles(directory, "*.cs"))
                sources[Path.GetRelativePath(root, file).Replace('\\', '/')] = File.ReadAllText(file);
        }
        return sources;
    }

    [Fact]
    public void NoHostScriptDeletesRecursivelyOutsideTheProtectedPathOwner()
    {
        var sources = Sources();
        // The scan sees the scripts it should judge, and the owner it exempts really holds the recursive deletes.
        Assert.Contains("src/Valheim.Testing.GameSessions/HostScripts.cs", sources.Keys);
        Assert.Contains("src/Valheim.Testing.GameSessions/HostedRuntimeStage.cs", sources.Keys);
        Assert.NotEmpty(BareRecursiveDeletes(Owner, sources[Owner]));

        var found = sources.Where(source => source.Key != Owner).SelectMany(source => BareRecursiveDeletes(source.Key, source.Value)).ToList();
        Assert.True(found.Count == 0, "A host script deletes recursively without vt_remove / Remove-VtTree (ProtectedPaths):\n" + string.Join("\n", found));
    }

    // ---- Negative controls: the patterns refuse what they should and let the owner's callers and plain deletes through ----

    [Theory]
    [InlineData("rm -rf -- \"$stage\"")]
    [InlineData("if [ -d \"$d\" ]; then rm -fr \"$d\"; fi")]
    [InlineData("rm -r \"$d\"")]
    [InlineData("rm -f -r \"$d\"")]
    [InlineData("rm --recursive \"$d\"")]
    [InlineData("Remove-Item -LiteralPath $path -Recurse -Force")]
    [InlineData("Remove-Item $path -Force -Recurse -ErrorAction SilentlyContinue")]
    [InlineData("if ($x) { Remove-Item -LiteralPath $dest -recurse }")]
    [InlineData("[IO.Directory]::Delete($stage, $true)")]
    [InlineData("if ([IO.Directory]::Exists($d)) { [System.IO.Directory]::Delete($d,$true) }")]
    [InlineData("rmdir /s /q \"%d%\"")]
    [InlineData("[IO.Directory]::Delete((Join-Path $a 'b'), $true)")]
    [InlineData("Remove-Item -LiteralPath $p -r -Force")]
    [InlineData("Remove-Item -LiteralPath $p -Rec -Force")]
    [InlineData("(Get-Item -LiteralPath $p).Delete($true)")]
    [InlineData("find \"$d\" -mindepth 1 -delete")]
    [InlineData("find \"$d\" -exec rm -rf {} +")]
    public void ABareRecursiveDeleteIsRefused(string line) =>
        Assert.Single(BareRecursiveDeletes("script.cs", "var text = \"\"\"\n    " + line + "\n    \"\"\";"));

    [Theory]
    [InlineData("rm -f -- \"$temporary\"")]
    [InlineData("rm -- \"$file\"")]
    [InlineData("rmdir \"$d\" 2> /dev/null")]
    [InlineData("Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue")]
    [InlineData("Copy-Item -LiteralPath $entry -Destination $dest -Recurse")]
    [InlineData("[IO.Directory]::Delete($link[0])")]
    [InlineData("[IO.Directory]::Delete($parent)")]
    [InlineData("[IO.File]::Delete($file)")]
    [InlineData("vt_remove \"$stage\" \"$parent\" || exit 3")]
    [InlineData("Remove-VtTree $stage $parent")]
    [InlineData("# rm -rf -- \"$stage\" (a comment)")]
    [InlineData("// Remove-Item -Recurse in a C# comment")]
    [InlineData("Directory.Delete(payload, recursive: true);")]
    [InlineData("Directory.Delete(_root, true);")]
    [InlineData("[IO.Directory]::Delete((Join-Path $a 'b'))")]
    [InlineData("find \"$d\" -name '*.log' -print")]
    [InlineData("grep -r pattern \"$dir\"")]
    [InlineData("cp -r \"$source\" \"$dest\"")]
    public void ADeleteThroughTheOwnerOrANonRecursiveOneIsAllowed(string line) =>
        Assert.Empty(BareRecursiveDeletes("script.cs", "var text = \"\"\"\n    " + line + "\n    \"\"\";"));

    // The scan reports where, so a failure names the line to route through the owner.
    [Fact]
    public void AFindingNamesTheFileAndTheLine()
    {
        var found = BareRecursiveDeletes("src/X.cs", "a\nb\n        rm -rf -- \"$x\"\nc").ToList();
        Assert.Equal("src/X.cs:3: bash rm -r: rm -rf -- \"$x\"", Assert.Single(found));
    }

    // The wrapper is sent as -EncodedCommand inside one command line (cmd.exe's limit is 8191 characters, with the shell's own path and flags
    // in front); it was 6716 before #743 moved its cleanup off a recursive delete.
    [Fact]
    public void ThePowerShellWrapperStaysWellUnderTheCommandLineLimit() =>
        Assert.True(ScriptedGameHost.EncodedPowerShellWrapper.Length < 7200, "The encoded wrapper is " + ScriptedGameHost.EncodedPowerShellWrapper.Length + " characters.");
}
