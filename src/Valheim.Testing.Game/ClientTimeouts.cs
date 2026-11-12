namespace Valheim.Testing.Game;

/// <summary>The client's start and world-entry limits; the join limits are the one home for a plan's <c>joinSeconds</c> and for <c>--join-seconds</c>.</summary>
internal static class ClientTimeouts
{
    internal const int DefaultStartSeconds = 300;
    internal const int DefaultJoinSeconds = 180;
    internal const int MinimumJoinSeconds = 10;
    // A world that generates its content on load (a roads network over a whole map) can take 20 to 40 minutes to become
    // joinable; the hour covers that and still ends a game that never gets there.
    internal const int MaximumJoinSeconds = 3600;

    internal static bool IsJoinSeconds(int seconds) => seconds is >= MinimumJoinSeconds and <= MaximumJoinSeconds;

    internal static void RequireStartAndJoin(int startSeconds, int joinSeconds)
    {
        if (startSeconds is < 10 or > 1800 || !IsJoinSeconds(joinSeconds))
            throw new ArgumentException("Client start/join timeouts are out of range.");
    }
}
