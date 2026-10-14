using System.Globalization;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// A hosted fixture world on a campaign client's own host (#258 step 8b, part b), this machine included (#807): placed into that
/// host user's <c>worlds_local</c> before the client hosts it, and moved out again into the run's directory on that host (then
/// fetched into the evidence) once no client can still host it. Only entries named for the world (<c>&lt;name&gt;</c>,
/// <c>&lt;name&gt;.*</c>, <c>&lt;name&gt;_*</c>, in any case) are ever touched, and the placement refuses when one exists: a
/// user's world is never overwritten or used. The placement is journalled on the host before the ship (a
/// <see cref="JournalEntry.CopyIntended"/> that <c>holds</c> <c>hosted-world</c>), so <c>env recover</c> moves a world a killed run
/// left behind out of the user's worlds in the same way (<see cref="RunRecovery"/>), once the world there states the journalled
/// world UID (<see cref="RequireOwnWorldAsync"/>).
/// </summary>
internal static class HostedWorldOnHost
{
    internal const string Holds = "hosted-world";

    /// <summary>The game's user-data root on an SSH Mac, from that host's own home rather than the runner's home.</summary>
    internal static async Task<string> MacSaveRootAsync(IGameHost host, TimeSpan timeout, CancellationToken cancellation)
    {
        var result = (await host.RunAsync("""
            set -eu
            [ "$(uname -s)" = Darwin ] && [ -d "$HOME" ] || exit 4
            printf 'VT-MAC-HOME %s\n' "$HOME"
            """, null, timeout, cancellation).ConfigureAwait(false)).EnsureSuccess($"Finding the Mac save root on {host.Name}");
        string home = InteractiveClient.Line(result.Stdout, "VT-MAC-HOME ")
            ?? throw new HostOperationException($"The Mac on {host.Name} did not name its home directory", result);
        if (!home.StartsWith('/') || home.Any(char.IsControl) || home.Split('/').Any(part => part is "." or ".."))
            throw new InvalidDataException($"The Mac on {host.Name} reported an unsafe home directory.");
        return HostPath.Join(home, HostedWorld.MacSaveRelative);
    }

    /// <summary>
    /// Where a placed world goes on its host (<paramref name="WorldsDirectory"/>), the run's folder it is shipped to first
    /// (<paramref name="Stage"/>) and moved out to after (<paramref name="KeepIn"/>), and how its moves are journalled.
    /// </summary>
    /// <param name="Hold">Before anything in the host's worlds is read or changed: the host's lock is this run's, and no game runs there.</param>
    internal sealed record Site(IGameHost Host, string HostName, string WorldsDirectory, string Stage, string KeepIn,
        Func<JournalEntry, CancellationToken, Task> Journal, Func<CancellationToken, Task> Hold) : IHostedWorldSite
    {
        public HostedWorld Place(HostWorldPlan plan, string output, CancellationToken cancellation) =>
            HostedWorldOnHost.Place(this, plan, output, cancellation);
    }

    /// <summary>
    /// Where a campaign's hosting client <paramref name="client"/> places its world: its host user's own <c>worlds_local</c>, beside
    /// the <c>characters_local</c> its character was staged in, shipped through and moved out into the run's folder in its role's
    /// runtime there. The same on every host, this machine included: a local hosting client's world is journalled as a remote one's
    /// is, so <c>env status</c> lists it and <c>env recover</c> moves it out after a killed run (#807).
    /// </summary>
    internal static Site ForCampaignClient(ResolvedEnvironment environment, string client, string runId, HostedCampaignCharacter character,
        IGameHost clientHost, Func<JournalEntry, CancellationToken, Task> journal, Func<CancellationToken, Task> hold)
    {
        var role = environment.Clients[client];
        return new Site(clientHost, role.Host, WorldsBeside(character.CharactersLocalDirectory),
            HostPath.Join(role.Runtime, runId, "host-world-stage"), HostPath.Join(role.Runtime, runId, "host-world"), journal, hold);
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
    internal static HostedWorld Place(Site site, HostWorldPlan plan, string output, CancellationToken cancellation)
    {
        var copy = WorldFixture.Copy(plan.World.Source, output, plan.World.Sha256);
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
                ("world", name), ("worldUid", plan.WorldUid), ("keepIn", site.KeepIn)), cancellation).GetAwaiter().GetResult();
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
    /// directory, and the world UID placed is named. Returns the world's name and UID.
    /// </summary>
    internal static (string Name, string WorldUid) RequireJournalled(IReadOnlyDictionary<string, string> fields, string runtime, string runId)
    {
        string worlds = fields.GetValueOrDefault("parent") ?? "", name = fields.GetValueOrDefault("world") ?? "", keepIn = fields.GetValueOrDefault("keepIn") ?? "";
        string uid = fields.GetValueOrDefault("worldUid") ?? "";
        if (!long.TryParse(uid, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
            throw new InvalidDataException("its journal names no world UID, so the world in the client's worlds cannot be proven the run's own");
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
        return (name, uid);
    }

    /// <summary>
    /// Before env recover moves a journalled world out of a client's worlds: every metadata file of the world there
    /// (<c>&lt;name&gt;/_main.&lt;n&gt;.fwl2</c>, Valheim 1.0's chunked save folder, or <c>&lt;name&gt;.fwl</c>) must state
    /// <paramref name="worldUid"/>, the fixture's UID the run journalled before placing it. A world the user made under that name has
    /// another UID and is refused, as is one whose identity cannot be read (entries named for the world but no readable metadata).
    /// The game rewrites the files once it hosted the world, so the UID, not the bytes, is what stays the run's. Returns false when
    /// nothing named for the world is there.
    /// </summary>
    internal static async Task<bool> RequireOwnWorldAsync(IGameHost host, string worlds, string name, string worldUid, TimeSpan timeout, CancellationToken cancellation)
    {
        var result = (await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsMetadata : BashMetadata,
            new Dictionary<string, string> { ["worlds"] = worlds, ["name"] = name }, timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Reading the hosted world's identity in the client's worlds on {host.Name}");
        string? done = InteractiveClient.Line(result.Stdout, "VT-WORLD-METAS ");
        if (done is not ("present" or "absent")) throw new HostOperationException($"Unexpected reply while reading the hosted world's identity on {host.Name}", result);
        var lines = result.Stdout.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        if (lines.FirstOrDefault(line => line.StartsWith("VT-WORLD-META-LARGE ", StringComparison.Ordinal)) is { } large)
            throw new InvalidDataException($"{large["VT-WORLD-META-LARGE ".Length..]} in {worlds} is too large to be world metadata, so the world there cannot be proven the run's");
        var read = lines.Where(line => line.StartsWith("VT-WORLD-META ", StringComparison.Ordinal)).Select(line =>
        {
            string[] parts = line["VT-WORLD-META ".Length..].Split(' ');
            if (parts.Length != 2) throw new HostOperationException($"Unexpected world metadata line from {host.Name}", result);
            return WorldIdentity.Parse(Convert.FromBase64String(parts[1]), parts[0]);
        }).ToList();
        if (read.Count == 0)
        {
            if (done == "absent") return false;
            throw new InvalidDataException($"{name} in {worlds} holds no world metadata ({name}/_main.<n>.fwl2 or {name}.fwl), so it cannot be proven the run's world UID {worldUid}; inspect it and move it out yourself");
        }
        var other = read.FirstOrDefault(identity => identity.UidText != worldUid);
        if (other != null)
            throw new InvalidDataException($"{other.File} in {worlds} states world {other.Name} with UID {other.UidText}, not the fixture's {worldUid} the run placed: a world of the same name that is not the run's stays where it is");
        return true;
    }

    // The world's metadata files, base64: <name>.fwl and <name>/_main.<n>.fwl2 (the name's own case; any case on Windows, whose
    // folders ignore it). VT-WORLD-METAS says whether any entry named for the world (as MoveOut moves them) is there at all.
    // Metadata is a few hundred bytes; a file over 1 MiB is named, not read.
    internal static readonly string WindowsMetadata = """
        $ErrorActionPreference = 'Stop'
        $lower = $name.ToLowerInvariant(); $present = $false
        if ([IO.Directory]::Exists($worlds)) {
            foreach ($entry in @([IO.Directory]::EnumerateFileSystemEntries($worlds))) {
                $l = [IO.Path]::GetFileName($entry).ToLowerInvariant()
                if ($l -ceq $lower -or $l.StartsWith($lower + '.', [StringComparison]::Ordinal) -or $l.StartsWith($lower + '_backup', [StringComparison]::Ordinal)) { $present = $true }
            }
            $files = @()
            $flat = Join-Path $worlds ($name + '.fwl')
            if ([IO.File]::Exists($flat)) { $files += ,@($flat, ($name + '.fwl')) }
            $folder = Join-Path $worlds $name
            if ([IO.Directory]::Exists($folder)) {
                foreach ($file in @([IO.Directory]::EnumerateFiles($folder))) {
                    $leaf = [IO.Path]::GetFileName($file)
                    if ($leaf -match '^_main\.[0-9]+\.fwl2$') { $files += ,@($file, ($name + '/' + $leaf)) }
                }
            }
            foreach ($file in $files) {
                if ((Get-Item -LiteralPath $file[0]).Length -gt 1048576) { 'VT-WORLD-META-LARGE ' + $file[1]; continue }
                'VT-WORLD-META ' + $file[1] + ' ' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($file[0]))
            }
        }
        if ($present) { 'VT-WORLD-METAS present' } else { 'VT-WORLD-METAS absent' }
        """;
    internal static readonly string BashMetadata = """
        set -eu
        present=absent
        if [ -d "$worlds" ]; then
            for entry in "$worlds"/* "$worlds"/.[!.]*; do
                [ -e "$entry" ] || [ -L "$entry" ] || continue
                leaf=${entry##*/}
                case "$leaf" in "$name"|"$name".*|"$name"_backup*) present=present ;; esac
            done
            meta() {
                size=$(wc -c < "$1" | tr -d ' ')
                if [ "$size" -gt 1048576 ]; then printf 'VT-WORLD-META-LARGE %s\n' "$2"; return; fi
                printf 'VT-WORLD-META %s %s\n' "$2" "$(base64 < "$1" | tr -d '\n\r')"
            }
            if [ -f "$worlds/$name.fwl" ]; then meta "$worlds/$name.fwl" "$name.fwl"; fi
            if [ -d "$worlds/$name" ]; then
                for file in "$worlds/$name"/_main.*.fwl2; do
                    [ -f "$file" ] || continue
                    leaf=${file##*/}
                    case "$leaf" in _main.*[!0-9]*.fwl2|_main..fwl2) continue ;; esac
                    meta "$file" "$name/$leaf"
                done
            fi
        fi
        printf 'VT-WORLD-METAS %s\n' "$present"
        """;

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
    internal static readonly string WindowsMoveOut = ProtectedPaths.PowerShellInside + ProtectedPaths.PowerShellRemove + """
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
                Remove-VtTree $entry $worlds
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
