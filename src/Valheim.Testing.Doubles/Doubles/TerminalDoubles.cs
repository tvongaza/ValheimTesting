// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// Console commands: a mod registers them with new Terminal.ConsoleCommand(...) as in the game; a test runs one by its
// command line on a Terminal and reads what it printed.
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// The game's console. Constructing a <see cref="ConsoleCommand"/> registers it in <see cref="commands"/> under its
/// lower-case name, as in the game. <see cref="TryRunCommand"/> runs a registered command with this terminal as its
/// context, and <see cref="Output"/> holds every line the command printed. Cheat, server-only and admin gating is not
/// modelled: a test decides which checks it exercises, and the mod's own checks (for example ZNet.IsServer) still run.
/// </summary>
public partial class Terminal
{
    public delegate void ConsoleEvent(ConsoleEventArgs args);
    public delegate object? ConsoleEventFailable(ConsoleEventArgs args);
    public delegate List<string> ConsoleOptionsFetcher();

    /// <summary>Every registered command by lower-case name; <c>ValheimWorldScope.WithCommands</c> gives a test its own.</summary>
    public static Dictionary<string, ConsoleCommand> commands = new();

    /// <summary>Every line printed to this terminal, in order.</summary>
    public readonly List<string> Output = new();

    public void AddString(string text) => Output.Add(text);

    /// <summary>Runs the command named by the line's first word, or prints that it is unknown, as the game does.</summary>
    public void TryRunCommand(string text, bool silentFail = false, bool skipAllowedCheck = false)
    {
        var args = new ConsoleEventArgs(text, this);
        if (args.Length == 0) return;
        if (commands.TryGetValue(args[0].ToLowerInvariant(), out var command))
        {
            if (command.RunAction(args) is string failure) AddString("Error executing command: " + failure);
        }
        else if (!silentFail) AddString("Unknown command: " + args[0]);
    }

    public partial class ConsoleEventArgs
    {
        public string[] Args;
        /// <summary>Everything after the command name.</summary>
        public string ArgsAll;
        public string FullLine;
        public Terminal Context;
        public int Length => Args.Length;
        public string this[int i] => Args[i];

        public ConsoleEventArgs(string line, Terminal context)
        {
            FullLine = line;
            Context = context;
            Args = line.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            int space = line.TrimStart().IndexOf(' ');
            ArgsAll = space < 0 ? "" : line.TrimStart().Substring(space + 1).Trim();
        }

        public int TryParameterInt(int index, int defaultValue = 1) =>
            index < Args.Length && int.TryParse(Args[index], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value) ? value : defaultValue;
        public float TryParameterFloat(int index, float defaultValue = 1f) =>
            index < Args.Length && float.TryParse(Args[index], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value) ? value : defaultValue;
        public bool HasArgumentAnywhere(string value, int firstIndexToCheck = 0, bool toLower = true) =>
            Args.Skip(firstIndexToCheck).Any(a => (toLower ? a.ToLowerInvariant() : a) == value);
    }

    public partial class ConsoleCommand
    {
        public string Command;
        public string Description;
        public bool IsCheat, IsNetwork, OnlyServer, IsSecret, AllowInDevBuild, RemoteCommand, OnlyAdmin;
        private readonly ConsoleEvent? _action;
        private readonly ConsoleEventFailable? _actionFailable;

        public ConsoleCommand(string command, string description, ConsoleEvent action, bool isCheat = false, bool isNetwork = false,
            bool onlyServer = false, bool isSecret = false, bool allowInDevBuild = false, ConsoleOptionsFetcher? optionsFetcher = null,
            bool alwaysRefreshTabOptions = false, bool remoteCommand = false, bool onlyAdmin = false)
            : this(command, description, isCheat, isNetwork, onlyServer, isSecret, allowInDevBuild, remoteCommand, onlyAdmin) => _action = action;

        public ConsoleCommand(string command, string description, ConsoleEventFailable action, bool isCheat = false, bool isNetwork = false,
            bool onlyServer = false, bool isSecret = false, bool allowInDevBuild = false, ConsoleOptionsFetcher? optionsFetcher = null,
            bool alwaysRefreshTabOptions = false, bool remoteCommand = false, bool onlyAdmin = false)
            : this(command, description, isCheat, isNetwork, onlyServer, isSecret, allowInDevBuild, remoteCommand, onlyAdmin) => _actionFailable = action;

        private ConsoleCommand(string command, string description, bool isCheat, bool isNetwork, bool onlyServer, bool isSecret,
            bool allowInDevBuild, bool remoteCommand, bool onlyAdmin)
        {
            Command = command; Description = description;
            IsCheat = isCheat; IsNetwork = isNetwork; OnlyServer = onlyServer; IsSecret = isSecret;
            AllowInDevBuild = allowInDevBuild; RemoteCommand = remoteCommand; OnlyAdmin = onlyAdmin;
            commands[command.ToLowerInvariant()] = this;
        }

        /// <summary>Runs the action; a failable action's non-true result is its failure message.</summary>
        public object? RunAction(ConsoleEventArgs args)
        {
            if (_action != null) { _action(args); return true; }
            object? result = _actionFailable!(args);
            return result is true ? (object)true : result?.ToString();
        }
    }
}
