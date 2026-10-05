using System.Security.Cryptography;

namespace Valheim.Testing.Game;

/// <summary>The file hashes the toolkit pins by, as lower-case hex.</summary>
public static class FileHash
{
    /// <summary>
    /// A plugin file's MD5 as ValheimCLI reports and compares it (<c>cli_manifest</c>, <c>cli_expect</c>): 32 lower-case hex
    /// characters. MD5 stays for plugin pins because the game side computes and compares MD5; a plan's plugin pin must be the
    /// value the game reports. It identifies a build; it is not a security check.
    /// </summary>
    public static string Md5(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(MD5.HashData(stream));
    }
}
