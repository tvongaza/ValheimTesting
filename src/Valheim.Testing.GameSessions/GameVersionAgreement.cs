using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// The one decision whether a campaign's installs can join each other (#860): Valheim refuses a peer whose network version differs
/// from its own, whatever the Steam app or build IDs say. Session check <c>--hosts</c>, campaign preparation and one-shot
/// server-load all reach it through the campaign's host inspection, before any profile is staged.
/// </summary>
internal static class GameVersionAgreement
{
    /// <summary>The largest game assembly read from another host (Valheim 1.0's is about 2.5 MB).</summary>
    internal const long AssemblyLimit = GameAssemblyVersion.AssemblyLimit;

    /// <summary>One actor's install as the host inspection listed it, with its build; a null build was not read because its install check already failed.</summary>
    internal sealed record Side(string Actor, string Host, string Install, GameBuild? Build);

    /// <summary>
    /// The build of the game assembly <paramref name="listing"/> holds, read on its host: in place on this machine, otherwise through
    /// the host's preflight read. The bytes must hash as the listing says, so the build is the listed file's. Never throws for an
    /// unreadable, missing or changed file: that is an unknown build saying why.
    /// </summary>
    internal static async Task<GameBuild> ReadAsync(IGameHost host, HostListing listing, TimeSpan timeout, CancellationToken cancellation)
    {
        string key, path;
        byte[]? bytes;
        try
        {
            key = HostInstall.GameAssemblyEntry(listing).Key;
            path = HostPath.Join(listing.Root, key);
            if (host.Kind == GameHostKind.Local)
            {
                bytes = await ReadHereAsync(Path.Combine(listing.Root, key.Replace('/', Path.DirectorySeparatorChar)), path, host.Name, cancellation).ConfigureAwait(false);
            }
            else bytes = await HostClientPreflight.Read(host, path, HostedTimeouts.GameAssemblyRead(timeout), cancellation, AssemblyLimit).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or HostOperationException)
        {
            return new(null, null, error.Message);
        }
        if (bytes == null) return new(null, null, $"{path} on {host.Name} is missing");
        if (!FileHash.Sha256(bytes).Equals(listing.Files[key], StringComparison.OrdinalIgnoreCase))
            return new(null, null, $"{path} on {host.Name} changed after the install was listed");
        using var image = new MemoryStream(bytes, writable: false);
        return GameAssemblyVersion.Read(image, $"{path} on {host.Name}");
    }

    /// <summary>The build of the game assembly in a local <paramref name="install"/> that runs in place, never listed: read where it is.</summary>
    internal static Task<GameBuild> ReadInPlaceAsync(string install, CancellationToken cancellation) =>
        GameAssemblyVersion.ReadInstallAsync(install, cancellation);

    /// <summary>
    /// One run's builds (#860): each actor's is admitted where its install is listed or run in place, before it is staged, and an
    /// actor that cannot join the ones already admitted is refused then, by <see cref="Decide"/>, so a standalone run decides as a
    /// campaign does. A campaign's are seeded from its host inspection. Thread-safe: a session opens clients concurrently.
    /// </summary>
    internal sealed class Ledger
    {
        private readonly Dictionary<string, Side> _sides = new(StringComparer.Ordinal);

        /// <summary>Admits <paramref name="side"/>, replacing an earlier admission of the same actor; throws naming every problem.</summary>
        internal GameBuild Admit(Side side)
        {
            ArgumentNullException.ThrowIfNull(side.Build);
            lock (_sides)
            {
                var problems = Decide([.. _sides.Values.Where(other => other.Actor != side.Actor), side]);
                if (problems.Count != 0) throw new InvalidOperationException(string.Join(" ", problems.Select(problem => problem.Message)));
                _sides[side.Actor] = side;
                return side.Build;
            }
        }

        /// <summary>Admits the build of <paramref name="listing"/>, read on <paramref name="host"/>, as <paramref name="actor"/>'s.</summary>
        internal async Task<GameBuild> AdmitAsync(string actor, IGameHost host, HostListing listing, TimeSpan timeout, CancellationToken cancellation) =>
            Admit(new(actor, host.Name, listing.Root, await ReadAsync(host, listing, timeout, cancellation).ConfigureAwait(false)));

        /// <summary>Admits the build of a local <paramref name="install"/> that runs in place as <paramref name="actor"/>'s.</summary>
        internal async Task<GameBuild> AdmitInPlaceAsync(string actor, string install, CancellationToken cancellation) =>
            Admit(new(actor, "this machine", install, await ReadInPlaceAsync(install, cancellation).ConfigureAwait(false)));

        /// <summary>Records builds already decided together (a campaign's inspection), without deciding again.</summary>
        internal void Seed(IEnumerable<Side> sides)
        {
            lock (_sides) foreach (var side in sides.Where(side => side.Build != null)) _sides[side.Actor] = side;
        }

        internal GameBuild? Of(string actor) { lock (_sides) return _sides.GetValueOrDefault(actor)?.Build; }
    }

    // One open file, so its size and bytes are the same file's; null when it is not there.
    private static async Task<byte[]?> ReadHereAsync(string local, string path, string hostName, CancellationToken cancellation)
    {
        FileStream stream;
        try { stream = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, FileOptions.Asynchronous); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        await using (stream.ConfigureAwait(false))
        {
            if (stream.Length > AssemblyLimit) throw HostClientPreflight.TooLarge(path, hostName, AssemblyLimit);
            var bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
            return bytes;
        }
    }

    /// <summary>
    /// The problems that stop these actors joining: an unknown build is refused (compatibility is never assumed), and every actor
    /// whose network version differs from the server's (or, with no server, the first client's by name) is refused naming both.
    /// A single actor joins no one, so it adds nothing here, nor does an actor whose install check already failed.
    /// </summary>
    internal static IReadOnlyList<CampaignPreflightProblem> Decide(IReadOnlyList<Side> actors)
    {
        if (actors.Count < 2) return [];
        var read = actors.Where(side => side.Build != null).OrderBy(side => side.Actor == "server" ? 0 : 1)
            .ThenBy(side => side.Actor, StringComparer.Ordinal).ToList();
        var problems = new List<CampaignPreflightProblem>();
        foreach (var side in read.Where(side => side.Build!.NetworkVersion == null))
            problems.Add(new(side.Actor, "game version", $"The network version of {side.Actor}'s install {side.Install} on {side.Host} is unknown: " +
                $"{side.Build!.Unknown}. Compatibility is never inferred from Steam app or build IDs. Verify the install's files (Steam, or SteamCMD " +
                "for a dedicated server) when its assembly_valheim.dll is missing or unreadable, check the host link when the read timed out, run " +
                "the check again when the file changed while it was read, or choose another environment."));
        if (read.FirstOrDefault(side => side.Build!.NetworkVersion != null) is not { } reference) return problems;
        foreach (var side in read.Where(side => side.Build!.NetworkVersion is { } network && network != reference.Build!.NetworkVersion))
            problems.Add(new(side.Actor, "game version", $"{side.Actor}'s install {side.Install} on {side.Host} is {side.Build}, but {reference.Actor}'s " +
                $"install {reference.Install} on {reference.Host} is {reference.Build}. Valheim refuses a join across network versions, so this is " +
                "refused before anything is staged. Update the older install (Steam, or SteamCMD for a dedicated server), or choose environments " +
                "whose installs share one network version."));
        return problems;
    }
}
