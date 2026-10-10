using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;

/// <summary>Prepares private compile references from an installed game and builds a mod without deploying it.</summary>
internal static partial class ModBuild
{
    internal const string Usage = "valheim-test build --project MOD.csproj [--role client|server] [--game DIR] [--dependency NAME=DLL ...] [--output NEW_DIR]";
    private const string PublicizerVersion = "BepInEx.AssemblyPublicizer/0.4.3";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    internal sealed record FilePin(string File, string Sha256);
    internal sealed record BuildRecord(string Game, string GameSha256, string Managed, string Mod, string ModSha256, string Tool,
        IReadOnlyList<FilePin> Sources, IReadOnlyList<FilePin> Publicized, IReadOnlyList<FilePin> Dependencies);
    internal sealed record Inputs(string Project, string Game, string Managed, string Core,
        string Output, IReadOnlyList<string> Publicize, IReadOnlyDictionary<string, string> Dependencies);

    internal static Dictionary<string, string> ParseDependencies(IEnumerable<string> values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string value in values)
        {
            int equal = value.IndexOf('=');
            if (equal < 1 || equal == value.Length - 1 || !result.TryAdd(value[..equal], value[(equal + 1)..]))
                throw new ArgumentException("--dependency must be a unique NAME=DLL pair.");
        }
        return result;
    }

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellation = default)
    {
        try
        {
            var (project, game, dependencies, output) = Parse(args);
            var input = Plan(project, game, dependencies, output);
            var record = await BuildAsync(input, cancellation).ConfigureAwait(false);
            Console.WriteLine("Built mod: " + record.Mod);
            Console.WriteLine("Build inputs: " + Path.Combine(input.Output, "build-inputs.json"));
            Console.WriteLine("Use --mod " + record.Mod + " --build-inputs " + Path.Combine(input.Output, "build-inputs.json") + " for the native run.");
            return 0;
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
        {
            Console.Error.WriteLine("REFUSED: " + error.Message);
            Console.Error.WriteLine("Usage: " + Usage);
            return 3;
        }
    }

    private static (string Project, string Game, Dictionary<string, string> Dependencies, string Output) Parse(string[] args)
    {
        string? project = null, game = null, output = null, role = null;
        var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            if (i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(key + " needs a value.");
            string value = args[++i];
            switch (key)
            {
                case "--project" when project == null: project = value; break;
                case "--game" when game == null: game = value; break;
                case "--role" when role == null: role = value; break;
                case "--output" when output == null: output = value; break;
                case "--dependency":
                    int equal = value.IndexOf('=');
                    if (equal < 1 || equal == value.Length - 1 || !dependencies.TryAdd(value[..equal], value[(equal + 1)..]))
                        throw new ArgumentException("--dependency must be a unique NAME=DLL pair.");
                    break;
                default: throw new ArgumentException("Unknown or repeated option: " + key);
            }
        }
        if (project == null) throw new ArgumentException("Give --project MOD.csproj.");
        if (role is not (null or "client" or "server")) throw new ArgumentException("--role must be client or server.");
        if (game == null)
        {
            var inventory = EnvironmentInventory.Read(null);
            string chosenRole = role ?? "client";
            game = inventory.Environments.FirstOrDefault(recipe => recipe.Roles.Contains(chosenRole))?.Install
                ?? throw new ArgumentException($"Valheim {chosenRole} install was not found in Steam libraries; give --game DIR.");
        }
        output = SmokeCommandOptions.Output(output == null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["--output"] = output });
        return (project, game, dependencies, output);
    }

    internal static Inputs Plan(string project, string game, IReadOnlyDictionary<string, string> dependencies, string output,
        string? loaderRoot = null)
    {
        project = Path.GetFullPath(project); game = Path.GetFullPath(game); output = Path.GetFullPath(output);
        if (!File.Exists(project) || Path.GetExtension(project) != ".csproj")
            throw new FileNotFoundException("--project must name an existing .csproj: " + project, project);
        if (File.Exists(Path.Combine(game, GameLaunch.ServerWindowsExecutable)) ||
            File.Exists(Path.Combine(game, GameLaunch.ServerLinuxExecutable)) ||
            File.Exists(Path.Combine(game, GameLaunch.ServerMacExecutable))) GameLaunch.DetectServer(game);
        else GameLaunch.DetectClient(game);
        string managed = new[] { Path.Combine(game, "valheim_Data", "Managed"),
            Path.Combine(game, "Valheim.app", "Contents", "Resources", "Data", "Managed"),
            Path.Combine(game, "valheim_server_Data", "Managed"), Path.Combine(game, "valheim_server", "Data", "Managed") }
            .FirstOrDefault(Directory.Exists) ?? throw new DirectoryNotFoundException("No Valheim Managed folder under " + game);
        string source = Path.Combine(managed, "assembly_valheim.dll");
        if (!File.Exists(source)) throw new FileNotFoundException("The selected game lacks assembly_valheim.dll: " + source, source);
        string core = Path.Combine(loaderRoot ?? game, "BepInEx", "core");
        foreach (string name in new[] { "BepInEx.dll", "0Harmony.dll" })
            if (!File.Exists(Path.Combine(core, name)))
                throw new FileNotFoundException("The selected loader lacks BepInEx core " + name + "; install a coherent BepInExPack or select a reviewed loader package.");
        if (ProtectedPaths.Contains(game, output) || ProtectedPaths.Contains(Path.GetDirectoryName(project)!, output))
            throw new ArgumentException("--output must be outside the game install and mod project: " + output);
        if (Path.Exists(output)) throw new IOException("--output must be new: " + output);

        string projectText = File.ReadAllText(project);
        string projectDirectory = Path.GetDirectoryName(project)!;
        string text = projectText + "\n" + string.Join("\n", Directory.GetFiles(projectDirectory, "*.props").Select(File.ReadAllText));
        var names = PublicizedName().Matches(text).Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        if (names.Length == 0) names = ["assembly_valheim"];
        foreach (string name in names)
            if (!File.Exists(Path.Combine(managed, name + ".dll")))
                throw new FileNotFoundException($"The selected game is missing {name}.dll required by {Path.GetFileName(project)}.");
        var selected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string dependency in DependencyName().Matches(text).Select(match => match.Groups[1].Value)
            .Where(name => name.Equals("Jotunn", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (dependencies.TryGetValue(dependency, out string? explicitFile))
                selected[dependency] = Path.GetFullPath(explicitFile);
            else
            {
                string[] matches = Directory.GetFiles(Path.Combine(game, "BepInEx"), dependency + ".dll", SearchOption.AllDirectories);
                if (matches.Length == 0 || matches.Select(FileHash.Sha256).Distinct(StringComparer.Ordinal).Count() != 1)
                    throw new InvalidDataException($"{dependency} is a required third-party build dependency: found {matches.Length} missing or different copies in {game}. Install one coherent copy or give --dependency {dependency}=DLL; nothing is downloaded.");
                selected[dependency] = matches.Order(StringComparer.Ordinal).First();
            }
        }
        foreach (var (name, file) in dependencies)
        {
            if (!Name().IsMatch(name)) throw new ArgumentException("Invalid dependency name: " + name);
            selected[name] = Path.GetFullPath(file);
        }
        foreach (var (name, file) in selected)
            if (!File.Exists(file) || !Path.GetFileName(file).Equals(name + ".dll", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException($"Dependency {name} must name an existing {name}.dll: {file}");
        return new Inputs(project, game, managed, core, output, names, selected);
    }

    internal static async Task<BuildRecord> BuildAsync(Inputs input, CancellationToken cancellation)
    {
        // All checks above run before this first write. Source installs and their signed macOS app bundles stay untouched.
        string refs = Path.Combine(input.Output, "references"), publicized = Path.Combine(refs, "publicized_assemblies");
        string core = Path.Combine(refs, "core"), deployment = Path.Combine(input.Output, "private-deployment");
        Directory.CreateDirectory(publicized); Directory.CreateDirectory(core); Directory.CreateDirectory(deployment);
        var sources = new List<FilePin>(); var outputs = new List<FilePin>(); var deps = new List<FilePin>();
        foreach (string name in input.Publicize)
        {
            string source = Path.Combine(input.Managed, name + ".dll");
            string target = Path.Combine(publicized, name + "_publicized.dll");
            sources.Add(Pin(source));
            BepInEx.AssemblyPublicizer.AssemblyPublicizer.Publicize(source, target);
            outputs.Add(Pin(target));
        }
        foreach (string file in new[] { "BepInEx.dll", "0Harmony.dll" }.Select(name => Path.Combine(input.Core, name))
            .Concat(input.Dependencies.Values))
        {
            string target = Path.Combine(core, Path.GetFileName(file));
            if (File.Exists(target)) throw new InvalidDataException("Duplicate private build reference " + target);
            File.Copy(file, target);
            deps.Add(Pin(file));
        }
        // Reuse an owned writable cache. A per-run cache would accumulate gigabytes in retained evidence.
        string packages = WritableCache("NUGET_PACKAGES", "packages"), http = WritableCache("NUGET_HTTP_CACHE_PATH", "http");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.GetDirectoryName(input.Project)!,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.Environment["NUGET_PACKAGES"] = packages; start.Environment["NUGET_HTTP_CACHE_PATH"] = http;
        string[] buildArguments = BuildArguments(input, core, publicized, deployment);
        start.ArgumentList.Add("build");
        foreach (string argument in buildArguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the .NET SDK for the mod build.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellation), stderr = process.StandardError.ReadToEndAsync(cancellation);
        try { await process.WaitForExitAsync(cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        string log = (await stdout.ConfigureAwait(false)) + "\n" + (await stderr.ConfigureAwait(false));
        File.WriteAllText(Path.Combine(input.Output, "build.log"), log);
        if (process.ExitCode != 0) throw new InvalidOperationException($"The mod build failed (exit {process.ExitCode}); see {Path.Combine(input.Output, "build.log")}.");
        // Ask MSBuild for the target path rather than guessing the DLL name or picking up an older build.
        // A project may set AssemblyName and output paths in imported properties.
        var targetQuery = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.GetDirectoryName(input.Project)!,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        targetQuery.Environment["NUGET_PACKAGES"] = packages; targetQuery.Environment["NUGET_HTTP_CACHE_PATH"] = http;
        targetQuery.ArgumentList.Add("msbuild");
        foreach (string argument in buildArguments) targetQuery.ArgumentList.Add(argument);
        targetQuery.ArgumentList.Add("-getProperty:TargetPath");
        using var query = Process.Start(targetQuery) ?? throw new InvalidOperationException("Could not inspect the built mod's target path.");
        Task<string> queryOutput = query.StandardOutput.ReadToEndAsync(cancellation);
        Task<string> queryError = query.StandardError.ReadToEndAsync(cancellation);
        await query.WaitForExitAsync(cancellation).ConfigureAwait(false);
        string built = (await queryOutput.ConfigureAwait(false)).Trim();
        if (query.ExitCode != 0 || !Path.IsPathFullyQualified(built) || !File.Exists(built))
            throw new InvalidDataException("The mod build did not produce an inspectable target DLL: " +
                (await queryError.ConfigureAwait(false)).Trim());
        if (PluginMetadata.Read(built).Plugins.Count == 0) throw new InvalidDataException("The built DLL declares no BepInEx plugin: " + built);
        string mod = Path.Combine(input.Output, "mod", Path.GetFileName(built));
        Directory.CreateDirectory(Path.GetDirectoryName(mod)!); File.Copy(built, mod);
        var record = new BuildRecord(input.Game, InstallPins.GameHash(input.Game), input.Managed, mod, FileHash.Sha256(mod), PublicizerVersion, sources, outputs, deps);
        File.WriteAllText(Path.Combine(input.Output, "build-inputs.json"), JsonSerializer.Serialize(record, Json) + "\n");
        return record;
    }

    internal static string[] BuildArguments(Inputs input, string core, string publicized, string deployment) =>
        [input.Project, "-p:Configuration=Debug",
            "-p:OutputPath=" + Path.Combine(input.Output, "private-build"),
            "-p:VALHEIM_INSTALL=" + input.Game, "-p:VALHEIM_MANAGED=" + input.Managed,
            "-p:ValheimPath=" + input.Game, "-p:ValheimManaged=" + input.Managed,
            "-p:BEPINEX_CORE=" + core, "-p:BepInExCore=" + core,
            "-p:PUBLICIZED_PATH=" + publicized, "-p:PublicizedAssembliesPath=" + publicized,
            "-p:CopyOutputDLLPath=" + deployment, "-p:CopyOutputDLLPath2=", "-p:CopyOutputDLLPath3=" ];

    internal static BuildRecord Verify(string file, string selectedGame, string selectedMod)
    {
        var record = JsonSerializer.Deserialize<BuildRecord>(File.ReadAllText(file), Json)
            ?? throw new InvalidDataException("Empty build-inputs manifest: " + file);
        if (string.IsNullOrWhiteSpace(record.Game) || string.IsNullOrWhiteSpace(record.Mod) ||
            string.IsNullOrWhiteSpace(record.GameSha256) || record.Sources is not { Count: > 0 } ||
            record.Publicized is not { Count: > 0 } || record.Dependencies == null)
            throw new InvalidDataException("Incomplete build-inputs manifest; rebuild the mod before launch: " + file);
        if (!Path.GetFullPath(record.Mod).Equals(Path.GetFullPath(selectedMod), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Build inputs select a different mod DLL; rebuild the selected mod before the native run.");
        if (record.Tool != PublicizerVersion) throw new InvalidDataException("Build inputs used a different publicizer version; rebuild the mod.");
        if (InstallPins.GameHash(record.Game) != record.GameSha256 || InstallPins.GameHash(selectedGame) != record.GameSha256)
            throw new InvalidDataException("Selected native game build differs from the mod's compile game SHA256; rebuild the mod against this game version.");
        foreach (var pin in record.Sources.Concat(record.Publicized).Concat(record.Dependencies).Append(new FilePin(record.Mod, record.ModSha256)))
        {
            if (!File.Exists(pin.File) || FileHash.Sha256(pin.File) != pin.Sha256)
                throw new InvalidDataException("Build input changed since preparation: " + pin.File + ". Rebuild the mod before launch.");
        }
        // A dedicated server is a different Steam install from the client used to build the mod.
        // Its managed game assemblies must still be byte-identical before the run can use this build.
        string? selectedManaged = new[] { "valheim_Data/Managed", "Valheim.app/Contents/Resources/Data/Managed",
            "valheim_server_Data/Managed", "valheim_server/Data/Managed" }
            .Select(relative => Path.Combine(selectedGame, relative.Replace('/', Path.DirectorySeparatorChar))).FirstOrDefault(Directory.Exists);
        if (selectedManaged == null) throw new DirectoryNotFoundException("Selected native game has no Managed folder: " + selectedGame);
        foreach (var source in record.Sources)
        {
            string counterpart = Path.Combine(selectedManaged, Path.GetFileName(source.File));
            if (!File.Exists(counterpart) || FileHash.Sha256(counterpart) != source.Sha256)
                throw new InvalidDataException("Selected native game build differs from the mod's compile reference: " + counterpart + ". Build the mod against this game version.");
        }
        return record;
    }

    internal static IEnumerable<string> ThirdPartyRoots(BuildRecord record)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pin in record.Dependencies.Where(pin => Path.GetFileName(pin.File) is not ("BepInEx.dll" or "0Harmony.dll")))
            roots.Add(pin.File);
        // A pinned file is an exact resolver choice. Scanning even its parent folder can rediscover
        // another build of the same assembly. Other transitive dependencies use explicit roots.
        return roots.Order(StringComparer.Ordinal);
    }

    internal static void RequireLoaderMatches(BuildRecord record, string selectedCore)
    {
        foreach (string name in new[] { "BepInEx.dll", "0Harmony.dll" })
        {
            var compiled = record.Dependencies.SingleOrDefault(pin =>
                Path.GetFileName(pin.File).Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"Build inputs do not pin {name}; rebuild the mod before launch.");
            string selected = Path.Combine(selectedCore, name);
            if (!File.Exists(selected) || FileHash.Sha256(selected) != compiled.Sha256)
                throw new InvalidDataException($"The selected native loader has a different {name} from the mod's compile input; rebuild with this loader or select the loader used for the build.");
        }
    }

    internal static void RequireResolvedDependencies(BuildRecord record, NativeDependencyLock resolved)
    {
        foreach (var pin in record.Dependencies.Where(pin => Path.GetFileName(pin.File) is not ("BepInEx.dll" or "0Harmony.dll")))
        {
            string name = Path.GetFileName(pin.File);
            if (!resolved.Mods.Concat(resolved.Plugins).Any(file => Path.GetFileName(file.File).Equals(name, StringComparison.OrdinalIgnoreCase)
                && file.Sha256 == pin.Sha256))
                throw new InvalidDataException($"Native dependency {name} differs from the mod's compile input; select the same DLL before launch.");
        }
    }

    private static FilePin Pin(string file) => new(Path.GetFullPath(file), FileHash.Sha256(file));
    internal static string WritableCache(string variable, string leaf)
    {
        string? chosen = Environment.GetEnvironmentVariable(variable);
        if (chosen != null && CanWrite(chosen)) return Path.GetFullPath(chosen);
        string owned = Path.Combine(CliBundle.DataRoot, "nuget", leaf);
        if (CanWrite(owned)) return owned;
        throw new IOException($"Neither {variable} nor the ValheimTesting-owned NuGet cache is writable: {owned}.");
    }

    private static bool CanWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string probe = Path.Combine(directory, ".valheim-test-write-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
    [GeneratedRegex(@"([A-Za-z0-9_.-]+)_publicized\.dll", RegexOptions.IgnoreCase)] private static partial Regex PublicizedName();
    [GeneratedRegex("<Reference\\s+Include=\"([A-Za-z0-9_.-]+)\"", RegexOptions.IgnoreCase)] private static partial Regex DependencyName();
    [GeneratedRegex(@"^[A-Za-z0-9_.-]+$")] private static partial Regex Name();
}
