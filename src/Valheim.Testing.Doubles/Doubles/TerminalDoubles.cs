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
using Valheim.Testing.Doubles;

/// <summary>
/// The game's console. Constructing a <see cref="ConsoleCommand"/> registers it in <see cref="commands"/> under its
/// lower-case name, as in the game. <see cref="TryRunCommand"/> runs a registered command with this terminal as its
/// context, and <see cref="Output"/> holds every line the command printed. It gates commands as Valheim 1.0.16 does
/// (<c>Terminal.TryRunCommand</c>, <c>ConsoleCommand.IsValid</c> and <c>RunAction</c>): a cheat command runs only with
/// devcommands on (<see cref="m_cheat"/>) on the server, and then only once cheats are acknowledged
/// (<see cref="Achievements.IsCheatedAtAll"/>, the game's <c>confirmcheats</c>); a server-only command only on the server;
/// a network command only with a <c>ZNet</c>; an invalid remote command on a client is sent to the server
/// (<see cref="ZNet.RemoteCommands"/>). Both cheat gates start closed, as on a fresh dedicated server: a test of a cheat
/// command opens them (<c>ValheimWorldScope.WithCheats</c>). Admin checks are not modelled.
/// </summary>
public partial class Terminal
{
    /// <summary>The game's text when cheats are not acknowledged (<c>$achievements_confirm_cheat</c>, English, 1.0.16).</summary>
    [TestOnly] public const string ConfirmCheat = "That command is a cheat, please enter 'confirmcheats' in the console to use cheats. " +
        "<color=red>Using cheats will permanently disable the ability to unlock achievements for this character and this world.</color>";

    /// <summary>Devcommands (the game's first cheat gate), static as in the game; the <c>devcommands</c> command toggles it there.</summary>
    public static bool m_cheat;

    /// <summary>As in the game: devcommands on, and this side is the server.</summary>
    public bool IsCheatsEnabled() => m_cheat && ZNet.instance != null && ZNet.instance.IsServer();

    public delegate void ConsoleEvent(ConsoleEventArgs args);
    public delegate object? ConsoleEventFailable(ConsoleEventArgs args);
    public delegate List<string> ConsoleOptionsFetcher();

    /// <summary>Every registered command by lower-case name; <c>ValheimWorldScope.WithCommands</c> gives a test its own.</summary>
    public static Dictionary<string, ConsoleCommand> commands = new();

    /// <summary>Every line printed to this terminal, in order.</summary>
    [TestOnly] public readonly List<string> Output = new();

    public void AddString(string text) => Output.Add(text);

    /// <summary>
    /// Runs the command named by the line's first word if it is valid here, sends an invalid remote command from a client
    /// to the server, or prints why not, as the game does; an unknown command prints that it is unknown.
    /// </summary>
    public void TryRunCommand(string text, bool silentFail = false, bool skipAllowedCheck = false)
    {
        var args = new ConsoleEventArgs(text, this);
        if (args.Length == 0) return;
        if (commands.TryGetValue(args[0].ToLowerInvariant(), out var command))
        {
            if (command.IsValid(this, skipAllowedCheck))
            {
                command.RunAction(args);
            }
            else if (command.RemoteCommand && ZNet.instance != null && !ZNet.instance.IsServer()) ZNet.instance.RemoteCommand(text);
            else if (!silentFail) AddString("'" + args[0] + "' is not valid in the current context.");
            else if (!IsCheatsEnabled() && command.IsCheat) AddString(ConfirmCheat);
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

        [TestOnly] public ConsoleEventArgs(string line, Terminal context)
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
        public bool IsCheat, IsNetwork, OnlyServer, IsSecret, AllowInDevBuild, RemoteCommand, OnlyAdmin, HideBehindDevCommands;
        private readonly ConsoleEvent? _action;
        private readonly ConsoleEventFailable? _actionFailable;

        public ConsoleCommand(string command, string description, ConsoleEvent action, bool isCheat = false, bool isNetwork = false,
            bool onlyServer = false, bool isSecret = false, bool allowInDevBuild = false, bool hideBehindDevCommands = false, ConsoleOptionsFetcher? optionsFetcher = null,
            bool alwaysRefreshTabOptions = false, bool remoteCommand = false, bool onlyAdmin = false)
            : this(command, description, isCheat, isNetwork, onlyServer, isSecret, allowInDevBuild, remoteCommand, onlyAdmin, hideBehindDevCommands) => _action = action;

        public ConsoleCommand(string command, string description, ConsoleEventFailable action, bool isCheat = false, bool isNetwork = false,
            bool onlyServer = false, bool isSecret = false, bool allowInDevBuild = false, bool hideBehindDevCommands = false, ConsoleOptionsFetcher? optionsFetcher = null,
            bool alwaysRefreshTabOptions = false, bool remoteCommand = false, bool onlyAdmin = false)
            : this(command, description, isCheat, isNetwork, onlyServer, isSecret, allowInDevBuild, remoteCommand, onlyAdmin, hideBehindDevCommands) => _actionFailable = action;

        private ConsoleCommand(string command, string description, bool isCheat, bool isNetwork, bool onlyServer, bool isSecret,
            bool allowInDevBuild, bool remoteCommand, bool onlyAdmin, bool hideBehindDevCommands)
        {
            Command = command; Description = description;
            IsCheat = isCheat; IsNetwork = isNetwork; OnlyServer = onlyServer; IsSecret = isSecret;
            AllowInDevBuild = allowInDevBuild; RemoteCommand = remoteCommand; OnlyAdmin = onlyAdmin; HideBehindDevCommands = hideBehindDevCommands;
            commands[command.ToLowerInvariant()] = this;
        }

        /// <summary>
        /// As in the game: a cheat command needs devcommands on the server (or the caller skipped that check), a network
        /// command a <c>ZNet</c>, a server-only command the server.
        /// </summary>
        public bool IsValid(Terminal context, bool skipAllowedCheck = false) =>
            (!IsCheat || context.IsCheatsEnabled()) && (!IsNetwork || ZNet.instance != null)
            && (!OnlyServer || (ZNet.instance != null && ZNet.instance.IsServer()));

        /// <summary>
        /// Runs the action, as the game does: a failable action that returns false prints the game's "Check parameters and
        /// context" error, and one that returns a string prints it as the error (both without the game's colour tags and
        /// its command-and-description line); any other result prints nothing. A cheat command (other than
        /// <c>confirmcheats</c>) prints <see cref="ConfirmCheat"/> and does nothing until cheats are acknowledged.
        /// </summary>
        public void RunAction(ConsoleEventArgs args)
        {
            if (IsCheat && !Achievements.IsCheatedAtAll() && args[0].ToLowerInvariant() != "confirmcheats") { args.Context.AddString(ConfirmCheat); return; }
            if (_action != null) { _action(args); return; }
            object? result = _actionFailable!(args);
            if (result is false) args.Context.AddString("Error executing command. Check parameters and context.");
            if (result is string failure) args.Context.AddString("Error executing command: " + failure);
        }
    }
}

/// <summary>
/// The game's achievement state, as far as console commands see it: <see cref="IsCheatedAtAll"/> is the second cheat gate,
/// which the game's <c>confirmcheats</c> opens for the world and character. Closed by default, as on a fresh server.
/// </summary>
public partial class Achievements : UnityEngine.MonoBehaviour
{
    /// <summary>Whether cheats were acknowledged; a test sets it (or uses <c>ValheimWorldScope.WithCheats</c>).</summary>
    [TestOnly] public static bool CheatedAtAll;
    public static bool IsCheatedAtAll() => CheatedAtAll;
}
