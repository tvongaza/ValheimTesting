using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

/// <summary>
/// Builds small net48 projects that import tools/game-references against a fake game folder of generated
/// stand-in assemblies (no game files), and checks the errors when parts are missing.
/// </summary>
public sealed class GameReferencesBuildTests : IDisposable
{
    private readonly GameReferencesProject _project = new();

    public void Dispose() => _project.Dispose();

    [Fact]
    public async Task BuildsAgainstAFakeGameFolderWithoutCopyingIt()
    {
        string game = _project.FakeGame("game", "valheim_Data/Managed");
        _project.Write(usesTypesOf: GameReferencesProject.DefaultAssemblies);

        BuildRun run = await _project.Build(new Dictionary<string, string?> { ["VALHEIM_PATH"] = null }, "-p:ValheimPath=" + game);

        run.AssertSucceeded();
        string output = _project.PathOf("bin/Debug/net48");
        Assert.True(File.Exists(Path.Combine(output, "Fixture.dll")), run.Output);
        Assert.Empty(Directory.GetFiles(output, "assembly_valheim.dll"));
        Assert.Empty(Directory.GetFiles(output, "BepInEx.dll"));
    }

    [Fact]
    public async Task FindsTheMacOSLayoutFromTheEnvironment()
    {
        string game = _project.FakeGame("game", "Valheim.app/Contents/Resources/Data/Managed");
        _project.Write(usesTypesOf: GameReferencesProject.DefaultAssemblies);

        BuildRun run = await _project.Build(new Dictionary<string, string?> { ["VALHEIM_PATH"] = game });

        run.AssertSucceeded();
    }

    [Fact]
    public async Task FindsTheMacDedicatedServerLayout()
    {
        string game = _project.FakeGame("game", "valheim_server/Data/Managed");
        _project.Write(usesTypesOf: GameReferencesProject.DefaultAssemblies);

        BuildRun run = await _project.Build(new Dictionary<string, string?> { ["VALHEIM_PATH"] = game });

        run.AssertSucceeded();
    }

    [UnixFact]
    public async Task FindsTheSteamFolderUnderHome()
    {
        string home = _project.Folder("home");
        (string steamGame, string managed) = GameReferencesProject.DefaultSteamGame();
        _project.FakeGame("home/" + steamGame, managed);
        _project.Write(usesTypesOf: GameReferencesProject.DefaultAssemblies);

        BuildRun run = await _project.Build(GameReferencesProject.WithHome(home));

        run.AssertSucceeded();
    }

    [Fact]
    public async Task AddsAndRemovesListedAssemblies()
    {
        string game = _project.FakeGame("game", "valheim_Data/Managed", extraManaged: new[] { "UnityEngine.PhysicsModule" });
        File.Delete(Path.Combine(game, "valheim_Data", "Managed", "assembly_utils.dll"));
        _project.Write(
            usesTypesOf: new[] { "assembly_valheim", "UnityEngine.PhysicsModule", "BepInEx" },
            items: "<ValheimReference Include=\"UnityEngine.PhysicsModule\" /><ValheimReference Remove=\"assembly_utils\" />");

        BuildRun run = await _project.Build(new Dictionary<string, string?> { ["VALHEIM_PATH"] = null }, "-p:ValheimPath=" + game);

        run.AssertSucceeded();
    }

    [Fact]
    public async Task ReferencesAnotherFileByName()
    {
        // A publicized copy keeps the assembly's name under another file name; the original is gone to prove which is used.
        string game = _project.FakeGame("game", "valheim_Data/Managed");
        string managed = Path.Combine(game, "valheim_Data", "Managed");
        File.Delete(Path.Combine(managed, "assembly_valheim.dll"));
        GameReferencesProject.WriteFakeAssembly(Path.Combine(managed, "publicized_assemblies", "assembly_valheim_publicized.dll"), "assembly_valheim");
        File.Delete(Path.Combine(game, "BepInEx", "core", "0Harmony.dll"));
        GameReferencesProject.WriteFakeAssembly(Path.Combine(game, "BepInEx", "core", "harmony", "0Harmony.dll"), "0Harmony");
        _project.Write(
            usesTypesOf: GameReferencesProject.DefaultAssemblies,
            items: "<ValheimReference Update=\"assembly_valheim\" File=\"publicized_assemblies/assembly_valheim_publicized.dll\" />" +
                   "<BepInExReference Update=\"0Harmony\" File=\"harmony/0Harmony.dll\" />");

        BuildRun run = await _project.Build(new Dictionary<string, string?> { ["VALHEIM_PATH"] = null }, "-p:ValheimPath=" + game);

        run.AssertSucceeded();
    }

    [Fact]
    public async Task CompilesAgainstValheimCliOnlyWhenAsked()
    {
        string game = _project.FakeGame("game", "valheim_Data/Managed");
        string cli = _project.PathOf("runtime/valheimCLI.dll");
        GameReferencesProject.WriteFakeAssembly(cli, "valheimCLI");
        _project.Write(usesTypesOf: new[] { "assembly_valheim", "valheimCLI" }, properties: "<UseValheimCli>true</UseValheimCli>");

        BuildRun run = await _project.Build(new Dictionary<string, string?> { ["VALHEIM_PATH"] = null }, "-p:ValheimPath=" + game, "-p:CliDll=" + cli);

        run.AssertSucceeded();
        Assert.Empty(Directory.GetFiles(_project.PathOf("bin/Debug/net48"), "valheimCLI.dll"));
    }
}

/// <summary>
/// Each missing part of a game install stops the build with an error naming it. The install checks run only the
/// target that holds them (no restore, no compile); the successful builds in <see cref="GameReferencesBuildTests"/> and
/// <see cref="PropsWithoutTargets"/> run the real build, through the hook into reference resolution.
/// </summary>
public sealed class GameReferencesErrorTests : IDisposable
{
    private readonly GameReferencesProject _project = new();

    public void Dispose() => _project.Dispose();

    private const string AddReferences = "AddValheimGameReferences";

    private static readonly Dictionary<string, string?> NoEnvironmentPath = new() { ["VALHEIM_PATH"] = null };

    [Fact]
    public async Task NoGameFolder()
    {
        string missing = _project.PathOf("no-game-here");
        _project.Write(usesTypesOf: GameReferencesProject.DefaultAssemblies);

        BuildRun run = await _project.Check(AddReferences, NoEnvironmentPath, "-p:ValheimPath=" + missing);

        run.AssertFailedWith("Valheim is not installed at '" + missing + "' (ValheimPath)");
    }

    [UnixFact]
    public async Task NoGameInTheDefaultSteamFolder()
    {
        string home = _project.Folder("home");
        _project.Write(usesTypesOf: GameReferencesProject.DefaultAssemblies);

        BuildRun run = await _project.Check(AddReferences, GameReferencesProject.WithHome(home));

        string expected = Path.Combine(new[] { home }.Concat(GameReferencesProject.DefaultSteamGame().Game.Split('/')).ToArray());
        run.AssertFailedWith("Valheim is not installed at '" + expected + "' (the default Steam folder; neither ValheimPath nor VALHEIM_PATH is set)");
    }

    [Fact]
    public async Task AFolderThatIsNotTheGame()
    {
        string folder = _project.Folder("not-a-game");
        _project.Write(usesTypesOf: GameReferencesProject.DefaultAssemblies);

        BuildRun run = await _project.Check(AddReferences, new Dictionary<string, string?> { ["VALHEIM_PATH"] = folder });

        run.AssertFailedWith("'" + folder + "' (the VALHEIM_PATH environment variable) has no valheim_Data/Managed");
    }

    [Fact]
    public async Task NamesEachMissingGameAssembly()
    {
        string game = _project.FakeGame("game", "valheim_Data/Managed");
        File.Delete(Path.Combine(game, "valheim_Data", "Managed", "assembly_utils.dll"));
        _project.Write(usesTypesOf: GameReferencesProject.DefaultAssemblies);

        BuildRun run = await _project.Check(AddReferences, NoEnvironmentPath, "-p:ValheimPath=" + game);

        run.AssertFailedWith("Game assemblies not found in");
        Assert.Contains("assembly_utils (", run.Output);
        Assert.DoesNotContain("assembly_valheim (", run.Output);
    }

    [Fact]
    public async Task NoBepInEx()
    {
        string game = _project.FakeGame("game", "valheim_Data/Managed");
        Directory.Delete(Path.Combine(game, "BepInEx"), recursive: true);
        _project.Write(usesTypesOf: GameReferencesProject.DefaultAssemblies);

        BuildRun run = await _project.Check(AddReferences, NoEnvironmentPath, "-p:ValheimPath=" + game);

        run.AssertFailedWith("BepInEx assemblies not found in");
        Assert.Contains("BepInEx.dll", run.Output);
    }

    [Fact]
    public async Task ValheimCliWithoutCliDll()
    {
        string game = _project.FakeGame("game", "valheim_Data/Managed");
        _project.Write(usesTypesOf: new[] { "assembly_valheim" }, properties: "<UseValheimCli>true</UseValheimCli>");

        BuildRun run = await _project.Check(AddReferences, NoEnvironmentPath, "-p:ValheimPath=" + game);

        run.AssertFailedWith("sets UseValheimCli: pass -p:CliDll=");
    }

    [Fact]
    public async Task ValheimCliAtAMissingPath()
    {
        string game = _project.FakeGame("game", "valheim_Data/Managed");
        string cli = _project.PathOf("runtime/valheimCLI.dll");
        _project.Write(usesTypesOf: new[] { "assembly_valheim" }, properties: "<UseValheimCli>true</UseValheimCli>");

        BuildRun run = await _project.Check(AddReferences, NoEnvironmentPath, "-p:ValheimPath=" + game, "-p:CliDll=" + cli);

        run.AssertFailedWith("CliDll does not exist: '" + cli + "'");
    }

    [Fact]
    public async Task PropsWithoutTargets()
    {
        string game = _project.FakeGame("game", "valheim_Data/Managed");
        _project.Write(usesTypesOf: GameReferencesProject.DefaultAssemblies, importTargets: false);

        // A real build: the refusal hooks into reference resolution, so this also proves the hook fires before compiling.
        BuildRun run = await _project.Build(NoEnvironmentPath, "-p:ValheimPath=" + game);

        run.AssertFailedWith("imports Valheim.GameReferences.props but not Valheim.GameReferences.targets");
    }

    [Fact]
    public async Task GameReferencesWithDoubles()
    {
        string game = _project.FakeGame("game", "valheim_Data/Managed");
        _project.Write(usesTypesOf: GameReferencesProject.DefaultAssemblies,
            items: "<PackageReference Include=\"Valheim.Testing.Doubles\" Version=\"[0.1.0-preview.4]\" PrivateAssets=\"all\" />");

        BuildRun run = await _project.Check(AddReferences, NoEnvironmentPath, "-p:ValheimPath=" + game);

        run.AssertFailedWith("references both the game's assemblies (Valheim.GameReferences) and Valheim.Testing.Doubles");
    }
}

/// <summary>A net48 fixture project in a temporary directory that imports tools/game-references from this repository.</summary>
internal sealed class GameReferencesProject : IDisposable
{
    public static readonly string[] DefaultAssemblies = { "assembly_valheim", "assembly_utils", "UnityEngine", "UnityEngine.CoreModule", "BepInEx", "0Harmony" };

    private static readonly string[] BepInExAssemblies = { "BepInEx", "0Harmony" };

    public GameReferencesProject() => Root = Directory.CreateTempSubdirectory("game-references-").FullName;

    public string Root { get; }

    public string PathOf(string relative) => Path.Combine(new[] { Root }.Concat(relative.Split('/')).ToArray());

    public string Folder(string relative)
    {
        string path = PathOf(relative);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Where the targets look without ValheimPath, relative to HOME on macOS and Linux, and its Managed folder.</summary>
    public static (string Game, string Managed) DefaultSteamGame() => OperatingSystem.IsMacOS()
        ? ("Library/Application Support/Steam/steamapps/common/Valheim", "Valheim.app/Contents/Resources/Data/Managed")
        : (".steam/steam/steamapps/common/Valheim", "valheim_Data/Managed");

    /// <summary>
    /// An environment with HOME at <paramref name="home"/> and no VALHEIM_PATH. The package cache and the SDK's own
    /// home stay where they were, so nothing is downloaded again.
    /// </summary>
    public static Dictionary<string, string?> WithHome(string home)
    {
        string realHome = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new Dictionary<string, string?>
        {
            ["HOME"] = home,
            ["VALHEIM_PATH"] = null,
            ["NUGET_PACKAGES"] = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } packages
                ? packages
                : Path.Combine(realHome, ".nuget", "packages"),
            ["DOTNET_CLI_HOME"] = Environment.GetEnvironmentVariable("DOTNET_CLI_HOME") is { Length: > 0 } cliHome ? cliHome : realHome,
        };
    }

    /// <summary>A game folder with stand-ins for the default assemblies: <paramref name="managed"/> and BepInEx/core.</summary>
    public string FakeGame(string relative, string managed, IEnumerable<string>? extraManaged = null)
    {
        string game = Folder(relative);
        foreach (string name in DefaultAssemblies.Concat(extraManaged ?? Array.Empty<string>()))
        {
            string folder = BepInExAssemblies.Contains(name) ? "BepInEx/core" : managed;
            WriteFakeAssembly(Path.Combine(new[] { game }.Concat(folder.Split('/')).Append(name + ".dll").ToArray()), name);
        }
        return game;
    }

    /// <summary>Writes Fixture.csproj, and Uses.cs naming the stand-in type of each of <paramref name="usesTypesOf"/>, so the build compiles only when each resolved.</summary>
    public void Write(IEnumerable<string> usesTypesOf, string properties = "", string items = "", bool importTargets = true)
    {
        string tools = Path.Combine(FixtureProjects.RepositoryRoot(), "tools", "game-references");
        File.WriteAllText(PathOf("Fixture.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            $"  <Import Project=\"{Path.Combine(tools, "Valheim.GameReferences.props")}\" />\n" +
            $"  <PropertyGroup><TargetFramework>net48</TargetFramework><LangVersion>10</LangVersion>{properties}</PropertyGroup>\n" +
            $"  <ItemGroup>{items}</ItemGroup>\n" +
            (importTargets ? $"  <Import Project=\"{Path.Combine(tools, "Valheim.GameReferences.targets")}\" />\n" : "") +
            "</Project>\n");
        File.WriteAllText(PathOf("Uses.cs"),
            "namespace Fixture { public static class Uses { public static readonly System.Type[] Types = { " +
            string.Join(", ", usesTypesOf.Select(name => "typeof(FakeGame." + TypeName(name) + ")")) +
            " }; } }\n");
    }

    /// <summary>Runs <c>dotnet build Fixture.csproj</c> with this process's environment plus <paramref name="environment"/> (a null value removes the variable).</summary>
    public Task<BuildRun> Build(IReadOnlyDictionary<string, string?> environment, params string[] arguments) =>
        FixtureProjects.Dotnet(Root, new[] { "build", PathOf("Fixture.csproj"), "-nologo", "-tl:off", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-warnaserror:MSB3245,MSB3246" }.Concat(arguments), environment);

    /// <summary>
    /// Runs only <paramref name="target"/> of Fixture.csproj with <c>dotnet msbuild</c>: no restore and no compile. The
    /// checks of a missing install all stop the build inside the targets that run before reference resolution.
    /// </summary>
    public Task<BuildRun> Check(string target, IReadOnlyDictionary<string, string?> environment, params string[] arguments) =>
        FixtureProjects.Dotnet(Root, new[] { "msbuild", PathOf("Fixture.csproj"), "-nologo", "-tl:off", "-nodeReuse:false", "-t:" + target }.Concat(arguments), environment);

    /// <summary>The one public type in a stand-in assembly: FakeGame.A_&lt;name with dots as underscores&gt;.</summary>
    public static string TypeName(string assembly) => "A_" + assembly.Replace('.', '_');

    /// <summary>
    /// Writes a minimal .NET Framework class library named <paramref name="assembly"/> holding one empty public static
    /// class, <c>FakeGame.</c><see cref="TypeName"/>. It stands in for a game assembly by name only.
    /// </summary>
    public static void WriteFakeAssembly(string path, string assembly)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString(assembly + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(assembly), new Version(1, 0, 0, 0), default, default, default, AssemblyHashAlgorithm.Sha1);
        AssemblyReferenceHandle mscorlib = metadata.AddAssemblyReference(metadata.GetOrAddString("mscorlib"), new Version(4, 0, 0, 0), default,
            metadata.GetOrAddBlob(new byte[] { 0xB7, 0x7A, 0x5C, 0x56, 0x19, 0x34, 0xE0, 0x89 }), default, default);
        TypeReferenceHandle systemObject = metadata.AddTypeReference(mscorlib, metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));
        metadata.AddTypeDefinition(default, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        metadata.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            metadata.GetOrAddString("FakeGame"), metadata.GetOrAddString(TypeName(assembly)), systemObject,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var image = new BlobBuilder();
        new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(metadata), new BlobBuilder()).Serialize(image);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, image.ToArray());
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
