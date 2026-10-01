using System.Diagnostics;
using Valheim.Testing.Game;

// This first slice is a client-hosted smoke. A dedicated server and exact multi-mod arms follow in #156/#158.
if (!Arguments.TryRead(args, out var options, out var mods, out var error))
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine("Usage: native-smoke --game DIR --mod DLL [--mod DLL ...] --source COMMIT --cli-manifest FILE --cli-files DIR --steam-userdata DIR --output NEW_DIR [--search-root DIR] [--loader-package FILE] [--port 9500] [--expected-log-error EXACT_HEADER --expected-log-reason REASON]");
    return 2;
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, press) => { press.Cancel = true; cancel.Cancel(); };
var elapsed = Stopwatch.StartNew();
RegressionEnvironment? environment = null;
try
{
    string output = Path.GetFullPath(options!["--output"]);
    if (Path.Exists(output)) throw new IOException("--output must be a new directory; an earlier run or personal files will not be changed: " + output);
    string game = Path.GetFullPath(options["--game"]);
    var selectedMods = mods!.Select(Path.GetFullPath).ToList();
    string mod = selectedMods[0];
    string cliManifest = Path.GetFullPath(options["--cli-manifest"]);
    string cliFiles = Path.GetFullPath(options["--cli-files"]);
    string steamUserdata = Path.GetFullPath(options["--steam-userdata"]);
    string? loader = options.TryGetValue("--loader-package", out string? loaderFile) ? Path.GetFullPath(loaderFile) : null;
    foreach (var (name, path) in new[] { ("--game", game), ("--cli-files", cliFiles), ("--steam-userdata", steamUserdata) })
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(name + " directory does not exist: " + path);
    foreach (var (name, path) in selectedMods.Select(path => ("--mod", path)).Append(("--cli-manifest", cliManifest)))
        if (!File.Exists(path)) throw new FileNotFoundException(name + " file does not exist: " + path, path);
    if (loader != null && !File.Exists(loader)) throw new FileNotFoundException("--loader-package file does not exist: " + loader, loader);
    string core = loader == null ? Path.Combine(game, InstallPins.CoreDirectory)
        : Path.Combine(BepInExLoaderPackage.Read(loader).Root, InstallPins.CoreDirectory);
    var request = new NativeDependencyRequest
    {
        Mods = selectedMods, GameManaged = Path.GetDirectoryName(InstallPins.GameAssembly(game))!,
        BepInExCore = core, CliManifest = cliManifest, CliFiles = cliFiles,
        SearchRoots = options.TryGetValue("--search-root", out string? search) ? [Path.GetFullPath(search)] : [],
        Capabilities = [.. CliCapabilities.HostedRounds],
    };
    var dependencies = NativeDependencyResolver.Resolve(request);
    Directory.CreateDirectory(output);
    dependencies.Write(Path.Combine(output, "dependencies.lock.json"));
    if (!dependencies.Ready)
        throw new InvalidDataException("Dependency choices remain: " + string.Join("; ", dependencies.Gaps.Select(gap => gap.Kind + " " + gap.Name + ": " + gap.Reason)));

    var world = DefaultSmokeWorld.Prepare(Path.Combine(output, "world-source"));
    var character = DefaultSmokeCharacter.Prepare(Path.Combine(output, "character-source"));
    int port = options.TryGetValue("--port", out string? specifiedPort) ? int.Parse(specifiedPort) : 9500;
    environment = new RegressionEnvironment
    {
        Name = "native-smoke", Game = game, Install = Path.Combine(output, "install"),
        Fixture = new RegressionFixture { Root = Path.Combine(output, "world-source"), WorldUid = world.UidText },
        Client = new RegressionClient { Port = port, Character = DefaultSmokeCharacter.Name,
            CharacterStore = character.Root, SteamUserDataDirectory = steamUserdata },
        Mod = new RegressionMod { InstallAs = Path.GetFileName(mod), Arms = new Dictionary<string, RegressionArm>
            { ["smoke"] = new() { File = mod, Sha256 = WorldFixture.Hash(mod), Commit = options["--source"] } } },
        LoaderPackage = loader,
    };
    if (options.TryGetValue("--expected-log-error", out string? expectedError))
        environment.LogScan[LogScanner.UnknownError] = new LogClassification
        { Expected = [expectedError], Reason = options["--expected-log-reason"] };
    dependencies.ApplyTo(environment, Path.Combine(output, "cli-capabilities.json"));
    // TargetedRegression calls one build its arm; other deliberately selected mods are fixed plugins in that same arm.
    // The resolver checks their combined dependency closure, duplicate GUIDs and declared incompatibilities first.
    environment.Plugins.AddRange(dependencies.Mods.Skip(1).Select(file => new RegressionFile { File = file.File, Sha256 = file.Sha256 }));
    environment.Write(Path.Combine(output, "environment.json"));
    var runner = new TargetedRegression(environment);
    var report = runner.Run("smoke", Path.Combine(output, "evidence"), "selected plugin loads in a hosted fixture",
        ["first"], _ => { }, cancel.Token);
    Console.WriteLine((report.Passed ? "PASS" : "FAIL") + $": hosted fixture and {selectedMods.Count} selected mod(s); {elapsed.Elapsed.TotalSeconds:F1}s; private evidence in {output}");
    return report.Passed ? 0 : 1;
}
catch (Exception failure) when (failure is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException or FormatException)
{
    Console.Error.WriteLine("REFUSED: " + failure.Message);
    return 3;
}
finally
{
    if (environment != null)
        try { TargetedRegression.Remove(environment); }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // A partially copied install may not yet have its ownership marker. Never delete it by path alone.
            Console.Error.WriteLine("CLEANUP REFUSED: " + cleanup.Message + "; inspect the disposable install at " + environment.Install);
        }
}

file static class Arguments
{
    private static readonly HashSet<string> Required = ["--game", "--mod", "--source", "--cli-manifest", "--cli-files", "--steam-userdata", "--output"];
    private static readonly HashSet<string> Allowed = [.. Required, "--search-root", "--loader-package", "--port", "--expected-log-error", "--expected-log-reason"];

    public static bool TryRead(string[] args, out Dictionary<string, string>? result, out List<string>? mods, out string error)
    {
        result = null;
        mods = null;
        error = "";
        if (args.Length % 2 != 0) { error = "Every option needs one value."; return false; }
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var selected = new List<string>();
        for (int i = 0; i < args.Length; i += 2)
        {
            if (args[i] == "--mod" && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                selected.Add(args[i + 1]);
                found.TryAdd("--mod", args[i + 1]);
                continue;
            }
            if (!Allowed.Contains(args[i]) || !found.TryAdd(args[i], args[i + 1]) || string.IsNullOrWhiteSpace(args[i + 1]))
            { error = "Unknown, repeated or empty option: " + args[i]; return false; }
        }
        var missing = Required.Where(key => !found.ContainsKey(key)).ToList();
        if (missing.Count != 0) { error = "Missing: " + string.Join(", ", missing); return false; }
        if (found.ContainsKey("--expected-log-error") != found.ContainsKey("--expected-log-reason"))
        { error = "--expected-log-error needs --expected-log-reason (and vice versa); an unexplained error is never ignored."; return false; }
        result = found;
        mods = selected;
        return true;
    }
}
