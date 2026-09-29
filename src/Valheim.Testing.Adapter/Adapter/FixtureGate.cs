// Source from the Valheim.Testing.Adapter package: compiled into a mod's game-side test adapter. No Unity or game
// types, so it also compiles into ordinary test projects.
#nullable enable
using System;

namespace Valheim.Testing.Adapter
{
    /// <summary>
    /// The opt-in gate for commands that build fixtures in the world (spawn terrain, write saved objects): they run only in
    /// a process whose runner turned them on. A test adapter can be installed in a runtime that somebody else uses; its
    /// read-only observations are harmless there, but a fixture command must refuse unless the owned test session asked
    /// for it. Check at the start of the command:
    /// <code>
    /// if (FixtureGate.Refusal("MYMOD_TEST_FIXTURES", "MYMOD_TEST_SESSION_TOKEN") is string refused)
    /// { context.Fail("fixture_disabled", refused); yield break; }
    /// </code>
    /// </summary>
    public static class FixtureGate
    {
        /// <summary>
        /// Null when the process was started with <paramref name="enableVariable"/> set to exactly <c>1</c> and a nonempty
        /// session token in <paramref name="tokenVariable"/> (the one <c>TestExtension</c> reports, which only an owned test
        /// session sets); otherwise why the fixture command must refuse.
        /// </summary>
        public static string? Refusal(string enableVariable, string tokenVariable)
        {
            if (string.IsNullOrEmpty(enableVariable)) throw new ArgumentException("Name the enabling variable.", nameof(enableVariable));
            if (string.IsNullOrEmpty(tokenVariable)) throw new ArgumentException("Name the session token variable.", nameof(tokenVariable));
            if (Environment.GetEnvironmentVariable(enableVariable) != "1")
                return "Fixture commands are off: start the owned test process with " + enableVariable + "=1.";
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(tokenVariable)))
                return "Fixture commands need an owned test session: " + tokenVariable + " is not set.";
            return null;
        }
    }
}
