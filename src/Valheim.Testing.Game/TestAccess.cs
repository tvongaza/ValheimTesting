using System.Text.Json;
using valheim_cli.Testing;

namespace Valheim.Testing.Game;

/// <summary>The owned process state in which test access is requested.</summary>
public enum TestActorRole { DedicatedServer, ClientMenu, ClientInWorld }

/// <summary>Observed access facts, not a grant of permission or proof a scenario action succeeded.</summary>
public sealed record TestAccessState(bool Devcommands, bool CheatsAcknowledged, bool AllowOnServerClients,
    bool Server, bool Dedicated, bool JoinedClient, bool LocalPlayer, bool ProfileAvailable);

/// <summary>Establishes explicitly requested test access on owned disposable actors only.</summary>
public static class TestAccess
{
    /// <summary>Reads the pinned core's structured observation. Missing or older core commands refuse setup.</summary>
    public static TestAccessState Read(GameActor actor)
    {
        using var json = GameActor.ParseLine(actor.Execute("cli_access"), "ACCESS ");
        var value = json.RootElement;
        if (value.GetProperty("schemaVersion").GetInt32() != 1 || !value.GetProperty("complete").GetBoolean())
            throw new InvalidOperationException("ValheimCLI access observation is unsupported or incomplete.");
        bool Get(string name) => value.GetProperty(name).GetBoolean();
        return new(Get("devcommands"), Get("cheatsAcknowledged"), Get("allowOnServerClients"),
            Get("server"), Get("dedicated"), Get("joinedClient"), Get("localPlayer"), Get("profileAvailable"));
    }

    /// <summary>
    /// Enables devcommands and, when needed, acknowledges cheats on this owned disposable profile.
    /// Menu setup enables devcommands only: the character does not exist yet. Call again after joining.
    /// Does not change AllowOnServerClients or bypass a role refusal; stage that setting when client mutations are needed.
    /// </summary>
    public static TestAccessState Ensure(GameActor actor, TestActorRole role, bool clientMutations = false)
    {
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        var state = Read(actor);
        RequireRole(state, role);
        if (clientMutations && role != TestActorRole.DedicatedServer && !state.AllowOnServerClients)
            throw new InvalidOperationException("This scenario needs client mutations: stage AllowOnServerClients=true before launch.");
        if (!state.Devcommands)
        {
            RequireAccepted(actor.Execute("devcommands"), "devcommands"); // One toggle; verify the resulting state.
            state = Read(actor);
            RequireRole(state, role);
            if (!state.Devcommands) throw new InvalidOperationException("The actor did not enable devcommands.");
        }
        if (role == TestActorRole.ClientMenu) return state;
        if (!state.CheatsAcknowledged)
        {
            if (!state.ProfileAvailable) throw new InvalidOperationException("No disposable profile is loaded to acknowledge cheats.");
            string command = role == TestActorRole.DedicatedServer ? "confirmcheats" : "cli_acknowledge_local_cheats";
            RequireAccepted(actor.Execute(command), command);
            state = Read(actor);
        }
        RequireRole(state, role);
        if (!state.Devcommands || !state.CheatsAcknowledged)
            throw new InvalidOperationException("Test access was not established: devcommands and cheat acknowledgement are separate gates.");
        return state;
    }

    private static void RequireRole(TestAccessState state, TestActorRole role)
    {
        bool matches = role switch
        {
            TestActorRole.DedicatedServer => state.Server && state.Dedicated,
            TestActorRole.ClientMenu => !state.Dedicated && !state.Server && !state.JoinedClient && !state.LocalPlayer,
            TestActorRole.ClientInWorld => !state.Dedicated && state.LocalPlayer,
            _ => false,
        };
        if (!matches) throw new InvalidOperationException("The actor is not in the requested test-access role: " + role);
    }

    /// <summary>
    /// Recognises known console refusals for the three access setup commands only. Raw output remains in the trace.
    /// The caller must still verify state after acceptance; this is not a general parser for arbitrary mod replies.
    /// </summary>
    public static void RequireAccepted(CommandResult result, string command)
    {
        if (command is not ("devcommands" or "confirmcheats" or "cli_acknowledge_local_cheats"))
            throw new ArgumentException("Use the command's own structured result or semantic assertion for " + command, nameof(command));
        if (!result.Ok || result.Output.Any(line => line.Contains("That command is a cheat", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Error executing command", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Unknown command", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(command + " was refused: " + string.Join(" | ", result.Output));
    }
}
