namespace Valheim.Testing.Game;

/// <summary>
/// An install's Valheim build as its <c>assembly_valheim.dll</c> declares it. <see cref="NetworkVersion"/> is the only thing the game
/// compares when a client joins (<c>ZNet.RPC_PeerInfo</c> refuses any other); <see cref="GameVersion"/> is shown beside it and
/// decides nothing. Null <see cref="NetworkVersion"/> means unknown, and <see cref="Unknown"/> says why.
/// </summary>
internal sealed record GameBuild(uint? NetworkVersion, string? GameVersion, string? Unknown)
{
    internal WorldSupport? WorldSupport { get; init; }
    internal string? WorldUnknown { get; init; }

    public override string ToString() => NetworkVersion is { } network
        ? $"Valheim {GameVersion ?? "(game version unread)"} (network version {network})"
        : "an unknown Valheim build (" + Unknown + ")";
}

/// <summary>A verified predicate from the actual host assembly, bound to its bytes.</summary>
internal sealed record WorldSupport(int Minimum, int Maximum, int ChunkedSave, string Source, string AssemblySha256);
