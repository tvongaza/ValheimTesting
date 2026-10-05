namespace Valheim.Testing.Game;

/// <summary>
/// The static checks of an owned client install that need no game: every pinned plugin build is installed exactly once,
/// a plugin in <c>BepInEx/scripts</c> will load at start, and ValheimCLI's standing expectations file, when set, is
/// readable and cannot refuse the run. Each fact found here would otherwise surface as a client that never answers, or
/// one that refuses every command, after the launch.
/// </summary>
internal static class OwnedClientPreflight
{
    internal static readonly string Plugins = Path.Combine("BepInEx", "plugins"), Scripts = Path.Combine("BepInEx", "scripts"), Config = Path.Combine("BepInEx", "config");
    internal const string ScriptEngine = "com.bepis.bepinex.scriptengine";
    internal const string ScriptEngineConfig = ScriptEngine + ".cfg";
    internal const string CliConfig = "valheimCLI.valheimCLI.cfg";

    internal static Dictionary<string, List<string>> Check(string install, IReadOnlyDictionary<string, string> pins, bool pinned, HostWorldPlan? hostWorld, string? hostWorldName)
    {
        var located = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (pinned)
        {
            located = RequireInstalled(install, pins);
            RequireScriptsLoad(install, pins, located);
        }
        RequireStandingFile(install, pins, hostWorld?.WorldUid, hostWorldName);
        return located;
    }

    /// <summary>Where each pinned plugin MD5 is installed (relative paths); refuses one installed nowhere or more than once.</summary>
    internal static Dictionary<string, List<string>> RequireInstalled(string install, IReadOnlyDictionary<string, string> pins)
    {
        var installed = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string dll in InstalledDlls(install))
        {
            string md5 = FileHash.Md5(dll);
            if (!installed.TryGetValue(md5, out var paths)) installed[md5] = paths = [];
            paths.Add(Path.GetRelativePath(install, dll).Replace('\\', '/'));
        }
        var located = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var (plugin, value) in pins)
        {
            if (value == "absent") continue;
            string md5 = value.ToLowerInvariant();
            if (!installed.TryGetValue(md5, out var paths))
            {
                string hint = string.Join(", ", installed.SelectMany(entry => entry.Value.Select(path => (Path: path, Md5: entry.Key)))
                    .Where(file => Resembles(file.Path, plugin)).OrderBy(file => file.Path, StringComparer.Ordinal).Select(file => $"{file.Path} is {file.Md5}"));
                problems.Add($"{plugin}={md5} is in neither {Slash(Plugins)} nor {Slash(Scripts)}" + (hint.Length == 0 ? "" : $" ({hint})"));
            }
            else if (paths.Count > 1) problems.Add($"{plugin}={md5} is installed {paths.Count} times ({string.Join(", ", paths)}); BepInEx loads one and skips the rest, so keep one");
            else located[plugin] = paths;
        }
        if (problems.Count != 0)
            throw new InvalidOperationException($"The client install does not hold the plugin builds the plan pins: {string.Join("; ", problems)}. " +
                "Stage the pinned builds, or derive the pins from the staged files (InstallPins.Plugins) rather than typing names or hashes.");
        return located;
    }

    /// <summary>Every DLL BepInEx can load from the install: <c>BepInEx/plugins</c> and <c>BepInEx/scripts</c>, any subfolder (macOS metadata files skipped).</summary>
    internal static IEnumerable<string> InstalledDlls(string install)
    {
        foreach (string folder in new[] { Plugins, Scripts })
        {
            string root = Path.Combine(install, folder);
            if (!Directory.Exists(root)) continue;
            foreach (string dll in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !FileHash.IsMacMetadata(path)).Order(StringComparer.Ordinal))
                yield return dll;
        }
    }

    // A file whose name holds the pin's last dotted part (com.jotunn.jotunn: Jotunn.dll), for the hint.
    private static bool Resembles(string path, string plugin)
    {
        string last = plugin.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? plugin;
        return last.Length >= 3 && Path.GetFileNameWithoutExtension(path).Contains(last, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A pinned plugin in <c>BepInEx/scripts</c> loads only through ScriptEngine, and at start only with its
    /// <c>[General] LoadOnStart = true</c> (ScriptEngine's default is false): otherwise ValheimCLI there never opens its port.
    /// </summary>
    internal static void RequireScriptsLoad(string install, IReadOnlyDictionary<string, string> pins, Dictionary<string, List<string>> located)
    {
        string scripts = Slash(Scripts) + "/";
        var inScripts = located.Where(entry => entry.Value[0].StartsWith(scripts, StringComparison.OrdinalIgnoreCase)).Select(entry => $"{entry.Key} ({entry.Value[0]})").Order(StringComparer.Ordinal).ToList();
        RequireScriptsLoad(install, pins, located, inScripts);
    }

    /// <summary>Apply the same startup-loader check to manifest files even when the plan does not pin each pack separately.</summary>
    internal static void RequireManifestScriptsLoad(string install, IReadOnlyDictionary<string, string> pins,
        Dictionary<string, List<string>> located, IReadOnlyList<string> manifestFiles)
    {
        string scripts = Slash(Scripts) + "/";
        var inScripts = manifestFiles.Where(path => path.StartsWith(scripts, StringComparison.OrdinalIgnoreCase))
            .Select(path => $"ValheimCLI manifest file ({path})").ToList();
        RequireScriptsLoad(install, pins, located, inScripts);
    }

    private static void RequireScriptsLoad(string install, IReadOnlyDictionary<string, string> pins,
        Dictionary<string, List<string>> located, List<string> inScripts)
    {
        if (inScripts.Count == 0) return;
        string listed = string.Join(", ", inScripts);
        string remedy = "or install the current ValheimCLI core and its packs in BepInEx/plugins, which needs no ScriptEngine.";
        if (!pins.Any(pin => pin.Key.Equals(ScriptEngine, StringComparison.OrdinalIgnoreCase) && pin.Value != "absent"))
            throw new InvalidOperationException($"The plan pins {listed} in {Slash(Scripts)}, which only ScriptEngine loads, but does not pin ScriptEngine ({ScriptEngine}) as loaded. Install and pin ScriptEngine, " + remedy);
        var enginePaths = located.Where(entry => entry.Key.Equals(ScriptEngine, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Value).FirstOrDefault();
        if (enginePaths == null ||
            !enginePaths[0].StartsWith(Slash(Plugins) + "/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The plan pins {listed} in {Slash(Scripts)}, but ScriptEngine ({ScriptEngine}) must itself be installed in {Slash(Plugins)}. " +
                "A ScriptEngine DLL in scripts cannot load itself or the other scripts; move and pin it in plugins, " + remedy);
        string config = Path.Combine(install, Config, ScriptEngineConfig);
        string? loadOnStart = File.Exists(config) ? IniValue(config, "General", "LoadOnStart") : null;
        if (!string.Equals(loadOnStart, "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The plan pins {listed} in {Slash(Scripts)}, but ScriptEngine will not load them at start: " +
                (File.Exists(config) ? $"{Slash(Config)}/{ScriptEngineConfig} has [General] LoadOnStart = {loadOnStart ?? "(not set)"}" : $"{Slash(Config)}/{ScriptEngineConfig} does not exist yet, and LoadOnStart defaults to false") +
                ", so ScriptEngine loads scripts only on its reload key and the client never answers. Set [General] LoadOnStart = true, " + remedy);
    }

    /// <summary>
    /// ValheimCLI's <c>[Expectations] File</c>, when set in <c>BepInEx/config/valheimCLI.valheimCLI.cfg</c>, is checked before
    /// every command but the diagnostics. Refuses one that is missing or malformed (<see cref="StandingPins.Parse"/>), a strict
    /// one that names no world (every command is refused once a world loads; <c>world=any</c> lets a host or join through
    /// while the actor still pins the exact UID), and one that contradicts the plan's pins or hosted world.
    /// </summary>
    internal static void RequireStandingFile(string install, IReadOnlyDictionary<string, string> pins, string? worldUid, string? worldName)
    {
        string config = Path.Combine(install, Config, CliConfig);
        if (!File.Exists(config)) return;
        string configured = IniValue(config, "Expectations", "File")?.Trim() ?? "";
        if (configured.Length == 0) return;
        bool strict = string.Equals(IniValue(config, "Expectations", "Strict"), "true", StringComparison.OrdinalIgnoreCase);
        // As ValheimCLI resolves it (Expectations.ResolvePath): a relative path is relative to BepInEx/config.
        string path = Path.IsPathRooted(configured) ? configured : Path.Combine(install, Config, configured);
        string where = $"ValheimCLI's standing expectations file {path} ([Expectations] File in {Slash(Config)}/{CliConfig})";
        string relative = Path.GetRelativePath(install, Path.GetFullPath(path));
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"{where} is outside this disposable client install. Use a per-run file under BepInEx/config derived from the staged plugins and world UID; a host-global standing file can require unrelated mods.");
        if (!File.Exists(path))
            throw new InvalidOperationException($"{where} does not exist, and ValheimCLI refuses every command but its diagnostics until it does. Write it (StandingPins.Write) or clear the setting.");
        IReadOnlyList<KeyValuePair<string, string>> standing;
        try { standing = StandingPins.Read(path); }
        catch (ArgumentException error) { throw new InvalidOperationException($"{where} is malformed, so the game would refuse every command: {error.Message}", error); }
        RequireStandingPins(standing, pins, worldUid, worldName, strict, where);
    }

    internal static void RequireStandingPins(IReadOnlyList<KeyValuePair<string, string>> standing, IReadOnlyDictionary<string, string> pins,
        string? worldUid, string? worldName, bool strict, string where)
    {
        var problems = new List<string>();
        if (strict && !standing.Any(pin => pin.Key is "world" or "worlduid"))
            problems.Add("it is strict (Strict = true) but names no world, so every command is refused once a world loads; add world=any (the toolkit's actor still pins the exact world UID) or the exact worlduid=");
        foreach (var (key, value) in standing)
        {
            if (key == "worlduid" && worldUid != null && value != worldUid) problems.Add($"worlduid={value}, but the plan hosts world UID {worldUid}");
            else if (key == "world" && value != "any" && worldName != null && !Same(value, worldName)) problems.Add($"world={value}, but the plan hosts {worldName}");
            else if (pins.FirstOrDefault(p => Same(p.Key, key)) is { Key: not null } planned && Contradicts(value, planned.Value.ToLowerInvariant()))
                problems.Add($"{key}={value}, but the plan pins {planned.Key}={planned.Value}");
            else if (key is not ("world" or "worlduid" or "seed" or "worldfiles") && value != "absent" && !pins.Keys.Any(p => Same(p, key)))
                problems.Add($"{key}={value} requires a plugin the plan does not pin; make the per-run standing file from the staged plugins");
        }
        if (problems.Count != 0)
            throw new InvalidOperationException($"{where} would refuse this run: {string.Join("; ", problems)}.");
    }

    // ValheimCLI compares names case-insensitively, with a space written as _.
    private static bool Same(string a, string b) => string.Equals(a.Replace(' ', '_'), b.Replace(' ', '_'), StringComparison.OrdinalIgnoreCase);
    // A standing value no game can meet together with the plan's exact pin: absent against a build, or another hash (prefix).
    private static bool Contradicts(string standing, string planned) =>
        standing == "any" ? planned == "absent" : standing == "absent" ? planned != "absent" : planned == "absent" || !planned.StartsWith(standing, StringComparison.Ordinal);

    private static string Slash(string relative) => relative.Replace('\\', '/');

    /// <summary>A BepInEx .cfg / .ini value: <c>key = value</c> in <c>[section]</c>, comments (<c>#</c>, <c>;</c>) skipped; null when absent.</summary>
    internal static string? IniValue(string path, string section, string key) => IniValue(File.ReadLines(path), section, key);
    internal static string? IniValue(IEnumerable<string> lines, string section, string key)
    {
        string? current = null, found = null;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[') { current = line.Trim('[', ']').Trim(); continue; }
            int equals = line.IndexOf('=');
            if (equals > 0 && string.Equals(current, section, StringComparison.OrdinalIgnoreCase) && line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                found = line[(equals + 1)..].Trim();
        }
        return found;
    }
}
