using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>
/// Whether a Linux runtime can load crossplay. The game's PlayFab Party library (<c>libparty.so</c> in the runtime's
/// <c>Plugins</c> folder) needs system libraries a bare Linux host may lack, <c>libpulse0</c> and <c>libpulse-mainloop-glib0</c>
/// among them. Without one, Unity logs <c>Failed to open plugin</c>, the game's first PlayFab call fails with
/// <c>DllNotFoundException: libParty.so</c>, and a <c>-crossplay</c> server retries its network every 30 s and never opens a
/// lobby. The check asks the host's own loader (<c>ldd</c>, with the launch's library path) before anything starts, and a
/// refusal names each missing library and, where known, the Debian or Ubuntu package that provides it.
/// </summary>
public static class CrossplayLibraries
{
    /// <summary>Where the game keeps the library, relative to the runtime: the dedicated server's, then the client's.</summary>
    public static IReadOnlyList<string> PartyLibraries { get; } = ["valheim_server_Data/Plugins/libparty.so", "valheim_Data/Plugins/libparty.so"];

    /// <summary>The Debian and Ubuntu package for each library <c>libparty.so</c> needs that a minimal install may lack.</summary>
    public static IReadOnlyDictionary<string, string> Packages { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["libpulse.so.0"] = "libpulse0",
        ["libpulse-simple.so.0"] = "libpulse0",
        ["libpulse-mainloop-glib.so.0"] = "libpulse-mainloop-glib0",
        ["libatomic.so.1"] = "libatomic1",
    };

    private static readonly Regex NotFound = new(@"^\s*(?<name>\S+)\s+=>\s+not found\s*$", RegexOptions.CultureInvariant);

    /// <summary>The libraries <c>ldd</c> reports as <c>not found</c>, in its order, each once.</summary>
    public static IReadOnlyList<string> Missing(string lddOutput)
    {
        ArgumentNullException.ThrowIfNull(lddOutput);
        return lddOutput.Split('\n').Select(line => NotFound.Match(line.TrimEnd('\r'))).Where(match => match.Success)
            .Select(match => match.Groups["name"].Value).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The refusal for <paramref name="missing"/> libraries of <paramref name="library"/> on <paramref name="where"/>.</summary>
    public static string Refusal(string where, string library, IReadOnlyList<string> missing)
    {
        var packages = missing.Where(Packages.ContainsKey).Select(name => Packages[name]).Distinct(StringComparer.Ordinal).ToList();
        var unknown = missing.Where(name => !Packages.ContainsKey(name)).ToList();
        var text = new StringBuilder($"Crossplay cannot start on {where}: the game's {library} cannot load, because ");
        text.Append(string.Join(", ", missing.Select(name => Packages.TryGetValue(name, out string? package) ? $"{name} (package {package})" : name)));
        text.Append(missing.Count == 1 ? " is" : " are").Append(" missing. Nothing was started.");
        if (packages.Count != 0) text.Append(" Install ").Append(string.Join(" and ", packages)).Append(" (apt-get install ").Append(string.Join(' ', packages)).Append(")");
        if (unknown.Count != 0) text.Append(packages.Count != 0 ? "; also provide " : " Provide ").Append(string.Join(", ", unknown)).Append(" from the host's distribution");
        return text.Append(", or use the docker/linux-server image, which has them.").ToString();
    }

    /// <summary>
    /// Refuses a Linux <paramref name="runtime"/> on <paramref name="host"/> (a bash host) whose <c>libparty.so</c> is absent or
    /// cannot load, naming what is missing; returns the library it checked. A host without <c>ldd</c> is refused too: nothing
    /// else there can prove the library loads.
    /// </summary>
    public static async Task<string> RequireAsync(IGameHost host, string runtime, TimeSpan timeout, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtime);
        if (host.Shell.Kind != HostShellKind.Bash)
            throw new PlatformNotSupportedException($"A crossplay library check runs through a bash host on Linux; {host.Name} runs {host.Shell}.");
        var result = (await host.RunAsync(CrossplayLibraryScripts.Check, new Dictionary<string, string>
        {
            ["runtime"] = runtime.Length > 1 ? runtime.TrimEnd('/') : runtime, ["libraries"] = string.Join('\n', PartyLibraries),
        }, timeout, cancellation).ConfigureAwait(false)).EnsureSuccess($"Checking crossplay's libraries on {host.Name}");
        return Verdict(host.Name, result.Stdout) ?? throw new HostOperationException($"Unexpected reply while checking crossplay's libraries on {host.Name}", result);
    }

    // The check's reply (CrossplayLibraryScripts.Body): the library checked, or an exception naming the problem; null for a
    // reply that is none of these.
    internal static string? Verdict(string where, string stdout)
    {
        string? verdict = InteractiveClient.Line(stdout, "VT-PARTY ");
        if (verdict == null) return null;
        int space = verdict.IndexOf(' ');
        string word = space < 0 ? verdict : verdict[..space], detail = space < 0 ? "" : verdict[(space + 1)..];
        var ldd = new StringBuilder();
        foreach (string line in stdout.Split('\n'))
            if (line.StartsWith("VT-LDD ", StringComparison.Ordinal)) ldd.Append(line["VT-LDD ".Length..].TrimEnd('\r')).Append('\n');
        switch (word)
        {
            case "absent":
                throw new FileNotFoundException($"Crossplay cannot start on {where}: the runtime has no {string.Join(" or ", PartyLibraries)}, the game's PlayFab library. Nothing was started.");
            case "unsupported":
                throw new PlatformNotSupportedException($"Crossplay's libraries are checked on a Linux host; {where} runs {detail}.");
            case "noldd":
                throw new PlatformNotSupportedException($"Cannot check crossplay's libraries on {where}: it has no ldd (package libc-bin). Nothing was started.");
            case "checked":
            {
                int last = detail.LastIndexOf(' ');
                if (last < 0 || !int.TryParse(detail[(last + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int code)) return null;
                string library = detail[..last];
                var missing = Missing(ldd.ToString());
                if (missing.Count != 0) throw new InvalidOperationException(Refusal(where, library, missing));
                // ldd fails on a library the loader cannot read at all (another architecture, a truncated copy).
                if (code != 0) throw new InvalidOperationException($"Crossplay cannot start on {where}: ldd could not read the game's {library} (exit {code}): {ldd.ToString().Trim()}. Nothing was started.");
                return library;
            }
            default: return null;
        }
    }
}

// The fixed check script. Values arrive as variables (ScriptedGameHost.Compose); it ends with one verdict line.
internal static class CrossplayLibraryScripts
{
    // Variables: runtime, libraries (candidates relative to the runtime, one per line). ldd runs with the launch's
    // LD_LIBRARY_PATH order (linux64, doorstop_libs, then the session's), as the game resolves the library's dependencies; its
    // output comes back line by line.
    public const string Body = """
        vt_party_check() {
            if [ "$(uname -s)" != Linux ]; then echo "VT-PARTY unsupported $(uname -s)"; return 0; fi
            local lib= f out code
            while IFS= read -r f; do
                if [ -n "$f" ] && [ -f "$runtime/$f" ]; then lib=$f; break; fi
            done <<< "$libraries"
            if [ -z "$lib" ]; then echo "VT-PARTY absent"; return 0; fi
            if ! command -v ldd > /dev/null 2>&1; then echo "VT-PARTY noldd"; return 0; fi
            out=$(LD_LIBRARY_PATH="$runtime/linux64:$runtime/doorstop_libs${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" ldd "$runtime/$lib" 2>&1); code=$?
            while IFS= read -r f; do printf 'VT-LDD %s\n' "$f"; done <<< "$out"
            echo "VT-PARTY checked $lib $code"
        }
        """;

    public static readonly string Check = ("set -u\n" + Body + "\nvt_party_check\n").ReplaceLineEndings("\n");
}
