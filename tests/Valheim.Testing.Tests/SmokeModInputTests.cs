using Valheim.Testing.Game;
using Xunit;

public sealed class SmokeModInputTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("smoke-mod-input-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void ExplicitModWinsWithoutReadingTheCurrentProject()
    {
        var result = SmokeModInput.Select([Path.Combine(_root, "explicit.dll")], _root);
        Assert.Equal("explicit --mod", result.Reason);
        Assert.Equal(Path.Combine(_root, "explicit.dll"), Assert.Single(result.Mods));
    }

    [Fact]
    public void OneProjectSelectsItsNewestBuildsSinglePlugin()
    {
        Project("MyMod.csproj");
        string older = Plugin("bin/Debug/net10.0/MyMod.dll", "old");
        string latest = Plugin("bin/Release/net10.0/MyMod.dll", "new");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(latest, DateTime.UtcNow.AddHours(-1));

        var result = SmokeModInput.Select([], _root);

        Assert.Equal(latest, Assert.Single(result.Mods));
        Assert.Contains("MyMod.csproj", result.Reason);
    }

    [Fact]
    public void RecentlyCopiedDependencyDoesNotSelectAnOlderBuild()
    {
        Project("MyMod.csproj");
        string older = Plugin("bin/Debug/net10.0/MyMod.dll", "old");
        string latest = Plugin("bin/Release/net10.0/MyMod.dll", "new");
        string dependency = Write("bin/Debug/net10.0/Library.dll", RegressionRig.Assembly("Library", null));
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-3));
        File.SetLastWriteTimeUtc(latest, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(dependency, DateTime.UtcNow.AddHours(-1));

        Assert.Equal(latest, Assert.Single(SmokeModInput.Select([], _root).Mods));
    }

    [Fact]
    public void ChangedProjectRequiresRebuild()
    {
        Project("MyMod.csproj");
        string plugin = Plugin("bin/Release/net10.0/MyMod.dll", "one");
        File.SetLastWriteTimeUtc(plugin, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "MyMod.csproj"), DateTime.UtcNow.AddHours(-1));
        Assert.Contains("rebuild", Assert.Throws<ArgumentException>(() => SmokeModInput.Select([], _root)).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoProjectNamesTheDirectory()
    {
        Assert.Contains(_root, Assert.Throws<ArgumentException>(() => SmokeModInput.Select([], _root)).Message);
    }

    [Fact]
    public void SeveralProjectsNameTheCandidates()
    {
        Project("First.csproj"); Project("Second.csproj");
        string message = Assert.Throws<ArgumentException>(() => SmokeModInput.Select([], _root)).Message;
        Assert.Contains("First.csproj", message);
        Assert.Contains("Second.csproj", message);
    }

    [Fact]
    public void NoBuildOutputNamesTheProject()
    {
        Project("MyMod.csproj");
        Assert.Contains("MyMod.csproj", Assert.Throws<ArgumentException>(() => SmokeModInput.Select([], _root)).Message);
    }

    [Fact]
    public void NoPluginNamesTheInspectedDlls()
    {
        Project("MyMod.csproj");
        string library = Write("bin/Release/net10.0/Library.dll", RegressionRig.Assembly("Library", null));
        Assert.Contains(library, Assert.Throws<ArgumentException>(() => SmokeModInput.Select([], _root)).Message);
    }

    [Fact]
    public void TwoPluginsNameBothCandidates()
    {
        Project("MyMod.csproj");
        string first = Plugin("bin/Release/net10.0/First.dll", "one");
        string second = Plugin("bin/Release/net10.0/Second.dll", "two");
        string message = Assert.Throws<ArgumentException>(() => SmokeModInput.Select([], _root)).Message;
        Assert.Contains(first, message);
        Assert.Contains(second, message);
    }

    [Fact]
    public void NewerSourceRequiresARebuild()
    {
        Project("MyMod.csproj");
        string plugin = Plugin("bin/Release/net10.0/MyMod.dll", "one");
        string source = Write("Feature.cs", "class Feature { }");
        File.SetLastWriteTimeUtc(plugin, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddHours(-1));
        string message = Assert.Throws<ArgumentException>(() => SmokeModInput.Select([], _root)).Message;
        Assert.Contains("rebuild", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(plugin, message);
    }

    [Fact]
    public void LegacyRunSourcesDoNotInvalidateAnUnchangedBuild()
    {
        Project("MyMod.csproj");
        string plugin = Plugin("bin/Release/net10.0/MyMod.dll", "one");
        File.SetLastWriteTimeUtc(plugin, DateTime.UtcNow.AddMinutes(-1));
        Write("valheim-test-runs/old/adapter/TestAdapter.cs", "class TestAdapter { }");
        Assert.Equal(plugin, Assert.Single(SmokeModInput.Select([], _root).Mods));
    }

    private void Project(string name)
    {
        string path = Write(name, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-4));
    }
    private string Plugin(string relative, string guid) => Write(relative, RegressionRig.Assembly(Path.GetFileNameWithoutExtension(relative), new(guid)));
    private string Write(string relative, string content) => Write(relative, System.Text.Encoding.UTF8.GetBytes(content));
    private string Write(string relative, byte[] bytes)
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
