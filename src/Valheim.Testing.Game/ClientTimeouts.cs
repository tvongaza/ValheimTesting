namespace Valheim.Testing.Game;

internal static class ClientTimeouts
{
    internal const int DefaultStartSeconds = 300;
    internal const int DefaultJoinSeconds = 180;

    internal static void RequireStartAndJoin(int startSeconds, int joinSeconds)
    {
        if (startSeconds is < 10 or > 1800 || joinSeconds is < 10 or > 900)
            throw new ArgumentException("Client start/join timeouts are out of range.");
    }
}
