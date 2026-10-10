using System.Globalization;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// A hosted fixture world on a campaign client's own host (#258 step 8b, part b): placed into that host user's
/// <c>worlds_local</c> before the client hosts it, and moved out again into the run's directory on that host (then fetched
/// into the evidence) once no client can still host it. Only entries named for the world (<c>&lt;name&gt;</c>,
/// <c>&lt;name&gt;.*</c>, <c>&lt;name&gt;_*</c>, in any case) are ever touched, and the placement refuses when one exists: a
/// user's world is never overwritten or used. The placement is journalled on the host before the ship (a
/// <see cref="JournalEntry.CopyIntended"/> that <c>holds</c> <c>hosted-world</c>), so <c>env recover</c> moves a world a killed run
/// left behind out of the user's worlds in the same way (<see cref="RunRecovery"/>).
/// </summary>
internal static class HostedWorldOnHost
{
    internal const string Holds = "hosted-world";

    /// <summary>
    /// Where a placed world goes on its host (<paramref name="WorldsDirectory"/>), the run's folder it is shipped to first
    /// (<paramref name="Stage"/>) and moved out to after (<paramref name="KeepIn"/>), and how its moves are journalled.
    /// </summary>
    /// <param name="Hold">Before anything in the host's worlds is read or changed: the host's lock is this run's, and no game runs there.</param>
    internal sealed record Site(IGameHost Host, string HostName, string WorldsDirectory, string Stage, string KeepIn,
        Func<JournalEntry, CancellationToken, Task> Journal, Func<CancellationToken, Task> Hold) : IHostedWorldSite
    {
        public HostedWorld Place(HostWorldPlan plan, string output, bool pinned, CancellationToken cancellation) =>
            HostedWorldOnHost.Place(this, plan, output, pinned, cancellation);
    }

    /// <summary>
    /// The client's <c>worlds_local</c> on its host, beside the <c>characters_local</c> the campaign staged its character in
    /// (both are the host user's Valheim data directory's).
    /// </summary>
    internal static string WorldsBeside(string charactersLocal)
    {
        string path = charactersLocal.TrimEnd('/', '\\');
        int at = path.LastIndexOfAny(['/', '\\']);
        if (at <= 0 || !path[(at + 1)..].Equals("characters_local", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"The client's characters folder {charactersLocal} is not a Valheim characters_local, so its worlds_local is unknown.", nameof(charactersLocal));
        return HostPath.Join(path[..at], "worlds_local");
    }

    /// <summary>
    /// Copies <paramref name="plan"/>'s fixture into <paramref name="output"/> (verified, the input evidence, as
    /// <see cref="HostedWorld.Place"/> does), refuses when the host's worlds already hold an entry named for the world, journals the
    /// placement, ships the fixture into the run's stage folder on the host and verifies every file there, then moves its entries
    /// (all named for the world) into the host's worlds, refusing one that appeared meanwhile.
    /// </summary>
    internal static HostedWorld Place(Site site, HostWorldPlan plan, string output, bool pinned, CancellationToken cancellation)
    {
        bool verified = pinned || plan.World.Sha256.Count != 0;
        var copy = verified ? WorldFixture.Copy(plan.World.Source, output, plan.World.Sha256) : WorldFixture.CopyAsFound(plan.World.Source, output);
        copy.Preserve = true;
        try
        {
            string name = HostedWorld.NameOf(copy.SourceHashes.Keys);
            string world = HostPath.Join(site.WorldsDirectory, name);
            site.Hold(cancellation).GetAwaiter().GetResult();
            var existing = Entries(site, name, cancellation);
            if (existing.Count != 0)
                throw new InvalidOperationException($"The client's worlds on {site.HostName} already hold {string.Join(", ", existing)} in {site.WorldsDirectory}: a fixture world is never copied over a world. Remove or rename that one yourself, or rename the fixture world.");
            // Journalled before the ship: no journal line, no world in the user's worlds.
            site.Journal(JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", world), ("stage", ""), ("parent", site.WorldsDirectory), ("holds", Holds),
                ("world", name), ("keepIn", site.KeepIn)), cancellation).GetAwaiter().GetResult();
            // Shipped into the run's stage first (with the ship's own SOURCE.txt, which never reaches the user's worlds), verified there.
            site.Host.ShipFilesAsync(copy.DirectoryPath, site.Stage, HostedTimeouts.Long, cancellation).GetAwaiter().GetResult();
            var listing = HostInstall.ListAsync(site.Host, site.Stage, HostedTimeouts.Long, null, cancellation).GetAwaiter().GetResult();
            HostInstall.RequireSame(copy.SourceHashes, listing, "hosted world", ["SOURCE.txt", WorldFixture.ProvenanceFile]);
            var expected = copy.SourceHashes.Keys.Select(path => path.Split('/', '\\')[0]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            try
            {
                var moved = MoveOut(site.Host, site.Stage, name, site.WorldsDirectory, HostedTimeouts.Long, cancellation).GetAwaiter().GetResult();
                if (!moved.Order(StringComparer.Ordinal).SequenceEqual(expected, StringComparer.Ordinal))
                    throw new IOException($"Moving the hosted world into the client's worlds on {site.HostName} moved {string.Join(", ", moved)}, not {string.Join(", ", expected)}.");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Whatever reached the user's worlds goes out again into the run's folder, as at the end of a run; the failure is the one reported.
                try
                {
                    MoveOut(site.Host, site.WorldsDirectory, name, site.KeepIn, HostedTimeouts.Long, CancellationToken.None).GetAwaiter().GetResult();
                    site.Journal(JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", world), ("handedOver", "true"), ("keptIn", site.KeepIn)), CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Exception cleanup) { Console.Error.WriteLine($"Warning: the hosted world may be left in {site.WorldsDirectory} on {site.HostName}: {cleanup.Message}; env recover --run moves it out."); }
                throw;
            }
            site.Journal(JournalEntry.Of(JournalEntry.CopyDone, ("runtime", world), ("files", listing.Files.Count.ToString(CultureInfo.InvariantCulture)), ("verified", "true")),
                cancellation).GetAwaiter().GetResult();
            return HostedWorld.OnHost(name, plan.WorldUid, site.WorldsDirectory, copy.DirectoryPath, output, site.HostName, target => Collect(site, name, target));
        }
        finally { copy.Dispose(); } // kept (Preserve) as evidence; nothing uses it after placement
    }

    // Moves every entry named for the world out of the user's worlds into the run's directory on the host (the run's save, handed
    // over there), then fetches that into the evidence. Returns where the evidence is.
    private static string Collect(Site site, string name, string target)
    {
        string world = HostPath.Join(site.WorldsDirectory, name);
        MoveOut(site.Host, site.WorldsDirectory, name, site.KeepIn, HostedTimeouts.Long, CancellationToken.None).GetAwaiter().GetResult();
        site.Journal(JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", world), ("handedOver", "true"), ("keptIn", site.KeepIn)), CancellationToken.None).GetAwaiter().GetResult();
        site.Host.FetchDirectoryAsync(site.KeepIn, target, HostedTimeouts.Long).GetAwaiter().GetResult();
        return target;
    }

    private static List<string> Entries(Site site, string name, CancellationToken cancellation)
    {
        var result = site.Host.RunAsync(site.Host.Shell.Kind == HostShellKind.PowerShell ? WindowsEntries : BashEntries,
            new Dictionary<string, string> { ["worlds"] = site.WorldsDirectory, ["name"] = name }, HostedTimeouts.Quick, cancellation).GetAwaiter().GetResult()
            .EnsureSuccess($"Listing the client's worlds on {site.HostName}");
        if (InteractiveClient.Line(result.Stdout, "VT-WORLD-ENTRIES") == null) throw new HostOperationException($"Unexpected reply while listing the client's worlds on {site.HostName}", result);
        return result.Stdout.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.StartsWith("VT-WORLD-ENTRY ", StringComparison.Ordinal))
            .Select(line => line["VT-WORLD-ENTRY ".Length..]).ToList();
    }

    /// <summary>Moves every entry named for <paramref name="name"/> in <paramref name="worlds"/> into <paramref name="keepIn"/> (created); returns the moved entries.</summary>
    internal static async Task<IReadOnlyList<string>> MoveOut(IGameHost host, string worlds, string name, string keepIn, TimeSpan timeout, CancellationToken cancellation)
    {
        var result = (await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsMoveOut : BashMoveOut,
            new Dictionary<string, string> { ["worlds"] = worlds, ["name"] = name, ["to"] = keepIn }, timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Moving the hosted world out of the client's worlds on {host.Name}");
        if (InteractiveClient.Line(result.Stdout, "VT-WORLD-MOVE") == null) throw new HostOperationException($"Unexpected reply while moving the hosted world on {host.Name}", result);
        return result.Stdout.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.StartsWith("VT-WORLD-MOVED ", StringComparison.Ordinal))
            .Select(line => line["VT-WORLD-MOVED ".Length..]).ToList();
    }

    /// <summary>
    /// What env recover requires of a journalled hosted world before it moves anything: the folder is a <c>worlds_local</c>, the
    /// world name is a fixture world's, the copy is that folder's entry of that name, and the folder it moves to is in this run's
    /// directory. Returns the world's name.
    /// </summary>
    internal static string RequireJournalled(IReadOnlyDictionary<string, string> fields, string runtime, string runId)
    {
        string worlds = fields.GetValueOrDefault("parent") ?? "", name = fields.GetValueOrDefault("world") ?? "", keepIn = fields.GetValueOrDefault("keepIn") ?? "";
        string Leaf(string path) => path.Replace('\\', '/').TrimEnd('/').Split('/').Last();
        if (!Leaf(worlds).Equals("worlds_local", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"its journal names {worlds}, which is not a client's worlds_local");
        _ = HostedWorld.NameOf([name + "/_main.0.fwl2"]); // a fixture world's name
        string separator = worlds.Contains('\\') ? "\\" : "/";
        if (!runtime.Equals(worlds.TrimEnd('/', '\\') + separator + name, StringComparison.Ordinal))
            throw new InvalidDataException($"its journal names {runtime}, which is not {name} in {worlds}");
        // <runtime>/<runId>/host-world, with no relative segments: the run's own folder on that host.
        var segments = keepIn.Replace('\\', '/').TrimEnd('/').Split('/');
        if (!RunJournal.SafeName(runId) || segments.Length < 3 || segments[^1] != "host-world" || segments[^2] != runId || segments.Any(segment => segment is "." or ".."))
            throw new InvalidDataException($"its journal names {keepIn}, which is not this run's host-world folder (<runtime>/{runId}/host-world)");
        return name;
    }

    // Entries named for the world: <name>, <name>.*, <name>_* (any case): the placement refuses any of them, so a user's world is
    // never used. worlds_local is created when missing. Lower-casing is culture-free (C locale, invariant culture).
    internal static readonly string WindowsEntries = """
        $ErrorActionPreference = 'Stop'
        if (-not [IO.Directory]::Exists($worlds)) { [void][IO.Directory]::CreateDirectory($worlds) }
        $lower = $name.ToLowerInvariant()
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($worlds)) {
            $leaf = [IO.Path]::GetFileName($entry); $l = $leaf.ToLowerInvariant()
            if ($l -ceq $lower -or $l.StartsWith($lower + '.', [StringComparison]::Ordinal) -or $l.StartsWith($lower + '_', [StringComparison]::Ordinal)) { 'VT-WORLD-ENTRY ' + $leaf }
        }
        'VT-WORLD-ENTRIES done'
        """;
    internal static readonly string BashEntries = """
        set -eu
        mkdir -p -- "$worlds"
        lower=$(printf '%s' "$name" | LC_ALL=C tr '[:upper:]' '[:lower:]')
        for entry in "$worlds"/* "$worlds"/.[!.]*; do
            [ -e "$entry" ] || [ -L "$entry" ] || continue
            leaf=${entry##*/}; l=$(printf '%s' "$leaf" | LC_ALL=C tr '[:upper:]' '[:lower:]')
            case "$l" in "$lower"|"$lower".*|"$lower"_*) printf 'VT-WORLD-ENTRY %s\n' "$leaf" ;; esac
        done
        echo 'VT-WORLD-ENTRIES done'
        """;
    // Moves the world's own entries from $worlds into $to (created): <name>, <name>.* and the game's <name>_backup* (the name's own
    // case; any case on Windows, whose folders ignore it). Every destination is checked first, and none may exist: nothing is
    // ever overwritten, merged or deleted. Across volumes (Windows) an entry is copied, then removed from where it was.
    internal static readonly string WindowsMoveOut = """
        $ErrorActionPreference = 'Stop'
        $lower = $name.ToLowerInvariant()
        [void][IO.Directory]::CreateDirectory($to)
        $moves = @()
        if ([IO.Directory]::Exists($worlds)) {
            foreach ($entry in @([IO.Directory]::EnumerateFileSystemEntries($worlds))) {
                $leaf = [IO.Path]::GetFileName($entry); $l = $leaf.ToLowerInvariant()
                if (-not ($l -ceq $lower -or $l.StartsWith($lower + '.', [StringComparison]::Ordinal) -or $l.StartsWith($lower + '_backup', [StringComparison]::Ordinal))) { continue }
                $dest = Join-Path $to $leaf
                if ([IO.File]::Exists($dest) -or [IO.Directory]::Exists($dest)) { 'VT-WORLD-EXISTS ' + $leaf; exit 4 }
                $moves += ,@($entry, $dest, $leaf)
            }
        }
        foreach ($move in $moves) {
            $entry = $move[0]; $dest = $move[1]
            if ([IO.Path]::GetPathRoot($entry) -ne [IO.Path]::GetPathRoot($dest)) {
                Copy-Item -LiteralPath $entry -Destination $dest -Recurse
                Remove-Item -LiteralPath $entry -Recurse -Force
            }
            elseif ([IO.Directory]::Exists($entry)) { [IO.Directory]::Move($entry, $dest) }
            else { [IO.File]::Move($entry, $dest) }
            'VT-WORLD-MOVED ' + $move[2]
        }
        'VT-WORLD-MOVE done'
        """;
    internal static readonly string BashMoveOut = """
        set -eu
        mkdir -p -- "$to"
        moves=""
        if [ -d "$worlds" ]; then
            for entry in "$worlds"/* "$worlds"/.[!.]*; do
                [ -e "$entry" ] || [ -L "$entry" ] || continue
                leaf=${entry##*/}
                case "$leaf" in "$name"|"$name".*|"$name"_backup*) ;; *) continue ;; esac
                if [ -e "$to/$leaf" ] || [ -L "$to/$leaf" ]; then printf 'VT-WORLD-EXISTS %s\n' "$leaf"; exit 4; fi
                moves="$moves$leaf
        "
            done
        fi
        printf '%s' "$moves" | while IFS= read -r leaf; do
            [ -n "$leaf" ] || continue
            mv -n -- "$worlds/$leaf" "$to/$leaf"
            if [ -e "$worlds/$leaf" ] || [ -L "$worlds/$leaf" ]; then printf 'VT-WORLD-EXISTS %s\n' "$leaf"; exit 4; fi
            printf 'VT-WORLD-MOVED %s\n' "$leaf"
        done
        echo 'VT-WORLD-MOVE done'
        """;
}
