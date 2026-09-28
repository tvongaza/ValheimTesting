using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Valheim.Testing.Game;

public sealed record SessionState(string Phase, string? WorldUid, bool WorldPresent, bool WorldReady,
    bool Server, bool Dedicated, bool LocalPlayer, bool PlayerReady, bool Saving, bool LoadError, string ConnectionStatus)
{
    public static SessionState FromObservation(Observation observation)
    {
        observation.RequireComplete("session-state");
        var d = observation.Data;
        var state = new SessionState(d.GetProperty("phase").GetString()!, d.GetProperty("worldUid").GetString(),
            d.GetProperty("worldPresent").GetBoolean(), d.GetProperty("worldReady").GetBoolean(),
            d.GetProperty("server").GetBoolean(), d.GetProperty("dedicated").GetBoolean(),
            d.GetProperty("localPlayer").GetBoolean(), d.GetProperty("playerReady").GetBoolean(),
            d.GetProperty("saving").GetBoolean(), d.GetProperty("loadError").GetBoolean(), d.GetProperty("connectionStatus").GetString()!);
        if (state.Phase is not ("menu" or "loading" or "world-present" or "leaving" or "failed") || string.IsNullOrWhiteSpace(state.ConnectionStatus) ||
            state.WorldPresent != (state.WorldUid != null) || state.WorldUid != null && !long.TryParse(state.WorldUid, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
            state.PlayerReady && !state.LocalPlayer || state.WorldReady && (!state.WorldPresent || state.LoadError || state.Phase != "world-present" || !state.Server && (!state.PlayerReady || state.ConnectionStatus != "Connected")))
            throw new InvalidOperationException("Inconsistent session observation.");
        return state;
    }
}

/// <summary>Actions affect an attached game, never launch or terminate its process. Mod readiness is separate.</summary>
public sealed class SessionControl(GameActor actor)
{
    public SessionState Read() => Read(actor.RequireCapability("valheim.session/state"));
    private SessionState Read(Capability capability) => SessionState.FromObservation(actor.Observe(capability));

    public SessionState WaitForWorld(string worldUid, TimeSpan timeout, CancellationToken cancellation = default)
    {
        ValidateWorldUid(worldUid);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var capability = actor.RequireCapability("valheim.session/state");
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            cancellation.ThrowIfCancellationRequested();
            var state = Read(capability);
            if (state.LoadError) throw new InvalidOperationException("The game reports a world load error.");
            if (state.WorldPresent && state.WorldUid != worldUid) throw new InvalidOperationException("A different world is loaded.");
            if (state.WorldReady) return state;
            cancellation.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Math.Min(100, Math.Max(0, (timeout - timer.Elapsed).TotalMilliseconds))));
        }
        throw new TimeoutException("World readiness was not established; no action was retried.");
    }

    // Credentials stay in the game host's environment, never in command text or transcripts.
    public void Join(string address, string character, string? passwordEnvironmentVariable = null)
    {
        var args = passwordEnvironmentVariable == null ? new[] { address, character } : new[] { address, character, passwordEnvironmentVariable };
        Transition("join", args);
    }
    public void Leave() => Transition("leave", []);
    private void Transition(string action, string[] arguments)
    {
        var capability = actor.RequireCapability("valheim.session/" + action);
        if (capability.ReadOnly) throw new InvalidOperationException("Session action was incorrectly advertised as read-only.");
        var previous = actor.CommandTimeout;
        try
        {
            actor.CommandTimeout = TimeSpan.FromSeconds(130);
            var result = actor.Invoke(capability, arguments); // Exactly once. A lost reply is an unknown outcome.
            RequireResult(result, "session-" + action);
            if (result.GetProperty("action").GetString() != action) throw new InvalidOperationException("Wrong session action result.");
        }
        finally
        {
            actor.CommandTimeout = previous;
            actor.InvalidateEnvironment(); // Even a failed/ambiguous transition can have changed the world.
        }
    }
    public uint Save(string worldUid, TimeSpan timeout)
    {
        ValidateWorldUid(worldUid);
        if (timeout.TotalSeconds < 1 || timeout.TotalSeconds > 600) throw new ArgumentOutOfRangeException(nameof(timeout));
        var state = Read();
        if (!state.Server || !state.WorldReady || state.WorldUid != worldUid) throw new InvalidOperationException("The requested server world is not ready to save.");
        var capability = actor.RequireCapability("valheim.session/save");
        if (capability.ReadOnly) throw new InvalidOperationException("Save was incorrectly advertised as read-only.");
        var previous = actor.CommandTimeout;
        try
        {
            actor.CommandTimeout = timeout + TimeSpan.FromSeconds(10);
            var data = actor.Invoke(capability, timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture));
            RequireResult(data, "session-save");
            uint before = data.GetProperty("before").GetUInt32(), after = data.GetProperty("after").GetUInt32();
            if (data.GetProperty("worldUid").GetString() != worldUid || !data.GetProperty("saved").GetBoolean() || before == after)
                throw new InvalidOperationException("The game did not confirm a completed save for this world.");
            return after;
        }
        finally { actor.CommandTimeout = previous; }
    }
    private static void ValidateWorldUid(string value)
    { if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) throw new ArgumentException("An exact world UID is required."); }
    private static void RequireResult(JsonElement data, string source)
    { if (data.GetProperty("source").GetString() != source || !data.GetProperty("complete").GetBoolean()) throw new InvalidOperationException("Session result is incomplete or has the wrong source."); }
}
