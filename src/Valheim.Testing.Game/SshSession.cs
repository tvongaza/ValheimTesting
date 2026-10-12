namespace Valheim.Testing.Game;

/// <summary>
/// The one place that decides whether this process runs inside an SSH session. OpenSSH's server sets <c>SSH_CONNECTION</c> and, on
/// older builds or other servers, <c>SSH_CLIENT</c>; either, non-blank, counts. Two things depend on it: the Windows quit signal
/// (<c>ProcessStop</c>) and the ssh client a host chooses (GameSessions), so they cannot disagree.
/// </summary>
internal static class SshSession
{
    internal static bool IsActive() => IsActive(Environment.GetEnvironmentVariable);

    /// <param name="getVariable">Reads an environment variable by name; null when unset.</param>
    internal static bool IsActive(Func<string, string?> getVariable) =>
        !string.IsNullOrWhiteSpace(getVariable("SSH_CONNECTION")) || !string.IsNullOrWhiteSpace(getVariable("SSH_CLIENT"));
}
