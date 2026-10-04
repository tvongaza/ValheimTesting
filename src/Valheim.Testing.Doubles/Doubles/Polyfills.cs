// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
// Compiler support types .NET Framework lacks, so a net48 test project can use them as a net10.0 one does. Define
// VALHEIM_TESTING_NO_POLYFILLS in a project that already declares its own.
#if !NET5_0_OR_GREATER && !VALHEIM_TESTING_NO_POLYFILLS
namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// .NET Framework has no ModuleInitializerAttribute; the C# compiler only needs a type of this name to run a
    /// <c>[ModuleInitializer]</c> method when the test assembly loads (for example to set a mod's switches once).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}
#endif

namespace Valheim.Testing.Doubles
{
    /// <summary>
    /// Marks a doubles type or member the game does not have: a test switch, a recorder or a fixture shorthand. Mod code
    /// that calls one compiles against the doubles and fails against the game. The doubles' tests refuse an unmarked
    /// member the game lacks and a marked one it has (docs/packages/Valheim.Testing.Doubles.members.txt lists both).
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.All, Inherited = false)]
    public sealed class TestOnlyAttribute : System.Attribute { }
}
