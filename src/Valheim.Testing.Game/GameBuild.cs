namespace Valheim.Testing.Game;

/// <summary>
/// An install's Valheim build as its <c>assembly_valheim.dll</c> declares it. <see cref="NetworkVersion"/> is the only thing the game
/// compares when a client joins (<c>ZNet.RPC_PeerInfo</c> refuses any other); <see cref="GameVersion"/> is shown beside it and
/// decides nothing. Null <see cref="NetworkVersion"/> means unknown, and <see cref="Unknown"/> says why.
/// </summary>
internal sealed record GameBuild(uint? NetworkVersion, string? GameVersion, string? Unknown)
{
    public override string ToString() => NetworkVersion is { } network
        ? $"Valheim {GameVersion ?? "(game version unread)"} (network version {network})"
        : "an unknown Valheim build (" + Unknown + ")";
}
