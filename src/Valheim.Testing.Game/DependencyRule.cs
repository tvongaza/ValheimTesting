namespace Valheim.Testing.Game;

/// <summary>One unmet declaration in a plugin set: its kind (the resolver's gap kinds), the GUID or assembly it names, why, and the files involved.</summary>
internal sealed record DependencyProblem(string Kind, string Name, string Message, IReadOnlyList<string> Paths);

/// <summary>
/// The declared-dependency rule over one set of BepInEx plugins (#296): what BepInEx itself would do with the set after the
/// game starts, decided before anything launches. A GUID declared twice (one is skipped), a hard <c>[BepInDependency]</c>
/// nothing in the set declares or declares at a lower version, a <c>[BepInIncompatibility]</c> with a member of the set, a
/// <c>[BepInProcess]</c> that excludes the process (when one is named), and an assembly reference that neither the game,
/// BepInEx's core, the set nor the confirmed optional references provide. <see cref="NativeDependencyResolver.Resolve"/>
/// turns the problems into gaps; <see cref="TargetedRegression"/> refuses its staged set on them. Messages name a file as
/// <c>show</c> does; a problem's paths are the given ones.
/// </summary>
internal static class DependencyRule
{
    internal static List<DependencyProblem> Check(IReadOnlyList<(string Path, PluginAssembly Metadata)> plugins, IEnumerable<string> providedAssemblies,
        IEnumerable<string> optionalReferences, string? process, Func<string, string>? show = null)
    {
        show ??= path => path;
        var problems = new List<DependencyProblem>();
        var providers = new Dictionary<string, (string Path, PluginDeclaration Plugin)>(StringComparer.Ordinal);
        foreach (var group in plugins.SelectMany(entry => entry.Metadata.Plugins.Select(plugin => (entry.Path, Plugin: plugin))).GroupBy(entry => entry.Plugin.Guid, StringComparer.Ordinal))
        {
            var declaring = group.ToList();
            providers[group.Key] = (declaring[0].Path, declaring[0].Plugin);
            if (declaring.Count == 1) continue;
            var names = declaring.Select(entry => show(entry.Path)).ToList();
            string all = names.Count == 2 ? $"both {names[0]} and {names[1]}" : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
            problems.Add(new("duplicate-plugin", group.Key, $"{group.Key} is declared by {all}; BepInEx loads one and skips the others. Keep one", declaring.Select(entry => entry.Path).ToList()));
        }
        foreach (var (path, metadata) in plugins)
            foreach (var plugin in metadata.Plugins)
            {
                foreach (var dependency in plugin.Dependencies.Where(dependency => dependency.Hard))
                {
                    if (!providers.TryGetValue(dependency.Guid, out var provider))
                        problems.Add(new("plugin", dependency.Guid, $"{plugin.Guid} ({show(path)}) has a hard [BepInDependency(\"{dependency.Guid}\"{(dependency.MinimumVersion == null ? "" : $", \"{dependency.MinimumVersion}\"")})] that nothing staged declares, so BepInEx would skip it after the game starts. " +
                            $"Add the DLL that declares [BepInPlugin(\"{dependency.Guid}\")] to plugins in the manifest, with its SHA256 (a file name alone does not count)", [path]));
                    else if (!AtLeast(provider.Plugin.Version, dependency.MinimumVersion))
                        problems.Add(new("plugin", dependency.Guid, $"{plugin.Guid} ({show(path)}) needs {dependency.Guid} {dependency.MinimumVersion} or newer, and the staged {show(provider.Path)} declares {provider.Plugin.Version}. Stage a newer {dependency.Guid}", [path, provider.Path]));
                }
                foreach (string incompatible in plugin.Incompatibilities)
                    if (providers.TryGetValue(incompatible, out var provider))
                        problems.Add(new("incompatible-plugin", plugin.Guid, $"{plugin.Guid} ({show(path)}) declares [BepInIncompatibility(\"{incompatible}\")] and {show(provider.Path)} declares it, so BepInEx would skip {plugin.Guid}. Remove one of them from the manifest", [path, provider.Path]));
                if (process != null && plugin.Processes.Count != 0 && !plugin.Processes.Any(name => Path.GetFileNameWithoutExtension(name).Equals(process, StringComparison.OrdinalIgnoreCase)))
                    problems.Add(new("process", plugin.Guid, $"{plugin.Guid} ({show(path)}) loads only in {string.Join(", ", plugin.Processes)} ([BepInProcess]), and the process is {process}, so it would never load. Stage the mod's build for {process}, or remove it from the manifest", [path]));
            }
        var provided = new HashSet<string>(providedAssemblies, StringComparer.OrdinalIgnoreCase);
        provided.UnionWith(optionalReferences);
        provided.UnionWith(plugins.Select(entry => entry.Metadata.AssemblyName));
        foreach (var (path, metadata) in plugins)
            foreach (string reference in metadata.References.Where(reference => !provided.Contains(reference)))
                problems.Add(new("assembly", reference, $"{show(path)} references assembly {reference}, which is not in the game's Managed folder, BepInEx/core or the set. " +
                    "Add the DLL that provides it to plugins in the manifest (a library whose assembly name it is), or list the name in optionalReferences if the mod only uses it when present", [path]));
        return problems;
    }

    /// <summary>Whether <paramref name="version"/> is at least <paramref name="minimum"/> (no minimum: always); an unparsable one is not.</summary>
    internal static bool AtLeast(string version, string? minimum) => minimum == null ||
        Version.TryParse(version, out var have) && Version.TryParse(minimum, out var need) && have >= need;
}
