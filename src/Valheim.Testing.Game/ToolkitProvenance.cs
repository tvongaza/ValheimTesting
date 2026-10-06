using System.Reflection;

namespace Valheim.Testing.Game;

/// <summary>
/// One package of this toolkit in the running process: its NuGet id, the version it was built as, the source commit its build
/// recorded (for the ValheimCLI transport, the valheimCLI commit a candidate version names; empty when none), its state and the
/// SHA-256 of the assembly that ran. A <c>Valheim.Testing*</c> package is <c>released</c> when the release workflow built it, a
/// <c>candidate</c> when its version carries <c>-candidate</c> (a local build identity, never published), and <c>unreleased</c>
/// otherwise (a source build). The transport (<c>Valheim.Testing.Cli</c>, built from the valheimCLI fork) is a <c>candidate</c>
/// or <c>released</c>: a version without <c>-candidate</c> is one the release refuses unless NuGet.org serves it, whose nuspec
/// names its commit; its SHA-256 tells a local build of that version apart.
/// </summary>
[ResultShape]
public sealed record PackageProvenance(string Id, string Version, string Commit, string State, string Sha256);

/// <summary>
/// What ran, written into every <c>result.json</c> as <c>Toolkit</c> (schema 3): each toolkit package in the run's process
/// (<see cref="PackageProvenance"/>), the runner (the entry assembly) and its SHA-256, and, when the provenance itself could
/// not be read, why (<see cref="Error"/>; the result is written regardless). Each actor's in-game plugin pins are
/// <see cref="ScenarioReport.Plugins"/>.
/// </summary>
[ResultShape]
public sealed record ToolkitProvenance(IReadOnlyList<PackageProvenance> Packages, string Runner, string RunnerSha256, string? Error = null)
{
    /// <summary>Built by the release workflow (<c>Valheim.Testing*</c>); for the transport, any version that is not a candidate.</summary>
    public const string Released = "released";
    /// <summary>A local build identity (<c>-candidate</c>), never published.</summary>
    public const string Candidate = "candidate";
    /// <summary>A source build that is neither.</summary>
    public const string Unreleased = "unreleased";

    // The assemblies of the toolkit's packages (Doubles and Adapter are source-only), the transport's, and the metadata the
    // release workflow builds with (-p:ValheimTestingRelease=true, src/Directory.Build.props).
    internal static readonly string[] PackageAssemblies = ["Valheim.Testing", "Valheim.Testing.Game", "Valheim.Testing.GameSessions", "Valheim.Testing.Bindings", "Valheim.Testing.Bindings.Tool", "Valheim.Testing.NativeSmoke"];
    internal const string CliAssembly = "Valheim.Cli.Testing", CliPackage = "Valheim.Testing.Cli", ReleaseMetadata = "ValheimTesting.Release";

    /// <summary>
    /// This process's toolkit now: the Game assembly, the packages it references (loaded if they were not yet) and any other toolkit
    /// package already loaded. Never throws: a failure is recorded in <see cref="Error"/>.
    /// </summary>
    internal static ToolkitProvenance Capture()
    {
        try
        {
            var game = typeof(ToolkitProvenance).Assembly;
            var referenced = game.GetReferencedAssemblies().Where(name => name.Name == CliAssembly || PackageAssemblies.Contains(name.Name)).Select(Assembly.Load);
            return Of(AppDomain.CurrentDomain.GetAssemblies().Append(game).Concat(referenced), Assembly.GetEntryAssembly());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or FileLoadException or BadImageFormatException)
        {
            return new([], Assembly.GetEntryAssembly()?.GetName().Name ?? "unknown", "", "The toolkit's provenance could not be read: " + error.Message);
        }
    }

    internal static string StateOf(string version, bool releaseBuild) =>
        version.Contains("-candidate", StringComparison.OrdinalIgnoreCase) ? Candidate : releaseBuild ? Released : Unreleased;

    internal static ToolkitProvenance Of(IEnumerable<Assembly> loaded, Assembly? entry)
    {
        var packages = new List<PackageProvenance>();
        foreach (var assembly in loaded.Where(assembly => !assembly.IsDynamic).DistinctBy(assembly => assembly.GetName().Name).OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal))
        {
            string name = assembly.GetName().Name ?? "";
            var (version, commit) = Built(assembly);
            if (name == CliAssembly)
            {
                var candidate = System.Text.RegularExpressions.Regex.Match(version, "-candidate\\.([0-9a-f]{7,40})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                packages.Add(new(CliPackage, version, commit.Length != 0 ? commit : candidate.Success ? candidate.Groups[1].Value : "", StateOf(version, releaseBuild: true), Hash(assembly)));
            }
            else if (PackageAssemblies.Contains(name))
                packages.Add(new(name, version, commit, StateOf(version, assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .Any(attribute => attribute.Key == ReleaseMetadata && attribute.Value == "true")), Hash(assembly)));
            // Anything else (tests, examples, a mod's assemblies) is not the toolkit's.
        }
        return new(packages, entry?.GetName().Name ?? "unknown", entry == null ? "" : Hash(entry));
    }

    // The informational version split at its source revision (version+commit), as the package was versioned and built.
    private static (string Version, string Commit) Built(Assembly assembly)
    {
        string informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "unknown";
        int plus = informational.IndexOf('+');
        return plus < 0 ? (informational, "") : (informational[..plus], informational[(plus + 1)..]);
    }

    private static string Hash(Assembly assembly) => assembly.Location is { Length: > 0 } file && File.Exists(file) ? FileHash.Sha256(file) : "";
}
