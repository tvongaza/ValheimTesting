namespace Valheim.Testing.Game;

// The plan-validation idioms mod runners repeat: a known scenario, an environment flag that must be exactly "1" and
// settings that belong to another scenario. Each refusal is an ArgumentException whose message names the plan field and
// the fix. A runner refuses a mode and plan that do not belong together with PinnedServerRunOptions.CheckMode.
public partial class ServerRunPlan
{
    /// <summary>Refuses a <see cref="Scenario"/> that is not one of <paramref name="known"/>.</summary>
    public void RequireScenario(params string[] known)
    {
        if (known.Length == 0) throw new ArgumentException("Name at least one scenario.", nameof(known));
        if (!known.Contains(Scenario, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown scenario \"{Scenario}\": set scenario to {Or(known)}.");
    }

    /// <summary>
    /// Requires <paramref name="variable"/> in <see cref="Environment"/> set to exactly <c>1</c>, the explicit opt-in a test
    /// fixture in the mod needs (for example one that writes terrain when the world loads). Anything else is refused:
    /// missing, another value such as <c>true</c>, or a key that differs only in case, which the server's platform may
    /// or may not treat as the same variable. <paramref name="purpose"/> completes "set it to 1 to ...".
    /// </summary>
    public void RequireEnvironmentFlag(string variable, string purpose)
    {
        var entry = SingleEnvironmentEntry(variable);
        if (entry?.Value != "1")
            throw new ArgumentException($"Set environment.{variable} to \"1\" to {purpose}; the {Scenario} scenario needs it" +
                (entry is { } found ? $", and it is \"{found.Value}\"." : ", and the plan does not set it."));
    }

    /// <summary>
    /// Refuses <paramref name="settings"/> (the plan fields, as the plan spells them) when <paramref name="supplied"/> is
    /// true and the plan's <see cref="Scenario"/> is not one of <paramref name="scenarios"/>: a setting another scenario
    /// reads would otherwise be ignored silently, and the plan would not test what its author thinks.
    /// </summary>
    public void OnlyForScenario(string settings, bool supplied, params string[] scenarios)
    {
        if (scenarios.Length == 0) throw new ArgumentException("Name at least one scenario.", nameof(scenarios));
        if (supplied && !scenarios.Contains(Scenario, StringComparer.Ordinal))
            throw new ArgumentException($"{settings} are for the {Or(scenarios)} scenario, not {Scenario}: remove them from this plan, or set scenario to {Or(scenarios)}.");
    }

    // The one entry whose key matches ignoring case, or null; a key that differs only in case is refused by name.
    private KeyValuePair<string, string>? SingleEnvironmentEntry(string variable)
    {
        var entries = Environment.Where(entry => string.Equals(entry.Key, variable, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.FirstOrDefault(entry => entry.Key != variable).Key is { } stray)
            throw new ArgumentException($"environment.{stray} differs from {variable} only in case: spell it {variable}, once.");
        return entries.Length == 0 ? null : entries[0];
    }

    private static string Or(string[] values) => values.Length == 1 ? values[0] : string.Join(", ", values[..^1]) + " or " + values[^1];
}
