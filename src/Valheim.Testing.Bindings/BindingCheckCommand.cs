using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Valheim.Testing.Bindings;

/// <summary>
/// The <c>valheim-bindings</c> command (Valheim.Testing.Bindings.Tool), callable from a test too. Exit codes:
/// <see cref="Binds"/> when every checked reference binds, <see cref="Fails"/> when any is missing (or, with
/// <c>--fail-on-access</c>, when a non-accessible member is used without <c>IgnoresAccessChecksTo</c>), and
/// <see cref="Incomplete"/> for bad arguments, an unreadable file or a required assembly that was not supplied.
/// </summary>
public static class BindingCheckCommand
{
    public const int Binds = 0, Fails = 1, Incomplete = 2;

    public static readonly string Usage = string.Join(Environment.NewLine, new[]
    {
        "Usage: valheim-bindings <mod.dll>... (--game-dir <dir> | --game-file <file>)... [options]",
        "",
        "Checks offline that each mod's references into the supplied game assemblies still bind.",
        "",
        "  --game-dir <dir>     Look up referenced assemblies here as <name>.dll, e.g. valheim_Data/Managed. Repeatable.",
        "  --game-file <file>   Check against this assembly file whatever its file name, e.g. a pinned assembly_valheim.dll. Repeatable.",
        "  --only <name>        Check only these assemblies from --game-dir directories (files are always checked). Repeatable.",
        "  --require <name>     Also require this assembly to be supplied if the mod references it (assembly_valheim always is). Repeatable.",
        "  --fail-on-access     Also fail when a non-accessible member is used without IgnoresAccessChecksTo for its assembly.",
        "  --max-users <n>      List at most n users per finding (default 10).",
        "",
        "Exit codes: 0 every checked reference binds; 1 missing references (or access findings with --fail-on-access);",
        "2 bad arguments, unreadable input or a required assembly not supplied.",
        "",
    });

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var options = new BindingCheckOptions();
        var mods = new List<string>();
        bool failOnAccess = false;
        int maxUsers = 10;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-h" or "--help") { output.Write(Usage); return Binds; }
            if (arg == "--fail-on-access") { failOnAccess = true; continue; }
            if (arg is "--game-dir" or "--game-file" or "--only" or "--require" or "--max-users")
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) return Refuse(error, arg + " needs a value.");
                string value = args[++i];
                switch (arg)
                {
                    case "--game-dir": options.GameDirectories.Add(value); break;
                    case "--game-file": options.GameFiles.Add(value); break;
                    case "--only": options.OnlyAssemblies.Add(value); break;
                    case "--require": options.RequiredAssemblies.Add(value); break;
                    default:
                        if (!int.TryParse(value, out maxUsers) || maxUsers < 0) return Refuse(error, "--max-users needs a number of 0 or more.");
                        break;
                }
                continue;
            }
            if (arg.StartsWith("-", StringComparison.Ordinal)) return Refuse(error, "Unknown option " + arg + ".");
            mods.Add(arg);
        }
        if (mods.Count == 0) return Refuse(error, "Name at least one mod assembly.");
        if (options.GameDirectories.Count == 0 && options.GameFiles.Count == 0) return Refuse(error, "Supply the game assemblies with --game-dir or --game-file.");

        int result = Binds;
        foreach (string mod in mods)
        {
            BindingReport report;
            try { report = BindingCheck.Check(mod, options); }
            catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException or ArgumentException)
            {
                error.WriteLine($"valheim-bindings: cannot check {mod}: {e.Message}");
                return Incomplete;
            }
            report.WriteText(output, maxUsers);
            output.WriteLine();
            int outcome = report.MissingRequired.Count > 0 ? Incomplete
                : !report.Binds || (failOnAccess && report.UndeclaredAccess.Any()) ? Fails
                : Binds;
            result = Math.Max(result, outcome);
        }
        return result;
    }

    private static int Refuse(TextWriter error, string message)
    {
        error.WriteLine("valheim-bindings: " + message);
        error.WriteLine("Run valheim-bindings --help for usage.");
        return Incomplete;
    }
}
