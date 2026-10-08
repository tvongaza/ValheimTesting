// Compile-only declarations used by the small source adapter. These are signatures, not game behaviour.
// The ValheimCLI Observe pack compiles the generic game observations against real game assemblies.
#nullable disable
#pragma warning disable CS0649, CS0169, CS8618

namespace UnityEngine
{
    public class MonoBehaviour { }
    public sealed class Time { public static float realtimeSinceStartup => throw null; }
    public sealed class Application { public static event System.Action quitting { add => throw null; remove => throw null; } }
}

namespace BepInEx
{
    public abstract class BaseUnityPlugin : UnityEngine.MonoBehaviour { }
}

namespace BepInEx.Logging
{
    public interface ILogListener : System.IDisposable { void LogEvent(object sender, LogEventArgs eventArgs); }
    public class LogEventArgs : System.EventArgs { }
    public class DiskLogListener : ILogListener
    {
        public System.IO.TextWriter LogWriter { get => throw null; protected set => throw null; }
        public void LogEvent(object sender, LogEventArgs eventArgs) => throw null;
        public void Dispose() => throw null;
    }
    public class ManualLogSource { public void LogInfo(object data) => throw null; }
    public static class Logger
    {
        public static System.Collections.Generic.ICollection<ILogListener> Listeners => throw null;
        public static ManualLogSource CreateLogSource(string sourceName) => throw null;
    }
}

public struct Vector2s
{
    public short x, y;
    public Vector2s(int x, int y) => throw null;
}
public static class Utils { public static string GetSaveDataPath(FileHelpers.FileSource source) => throw null; }
public static class FileHelpers { public enum FileSource { Local = 2 } }
public class ZNet
{
    public static ZNet instance => throw null;
    public bool IsServer() => throw null;
    public bool IsDedicated() => throw null;
}
public class ZoneSystem
{
    public struct SectorIndex { public uint Sector; }
    public static ZoneSystem instance => throw null;
    public System.Collections.Generic.List<string> GetGlobalKeys() => throw null;
    public void SetGlobalKey(string name) => throw null;
    public void RemoveGlobalKey(string name) => throw null;
}
public class ZDO { }
public class ZDOMan { public static ZDOMan instance => throw null; }

namespace valheimCLI
{
    #nullable enable
    public class valheimCLIPlugin : BepInEx.BaseUnityPlugin
    {
        public static valheimCLIPlugin? Instance => throw null!;
        public Extensions.ExtensionRegistry? Extensions => throw null!;
    }
}
namespace valheimCLI.Extensions
{
    #nullable enable
    public enum ExtensionRole { Any, Server, Client }
    public sealed class ExtensionCommand
    {
        public ExtensionCommand(string name, string help, System.Func<ExtensionContext, System.Collections.IEnumerator> execute,
            bool readOnly = false, ExtensionRole role = ExtensionRole.Any, bool needsWorld = false, int resultVersion = 1) => throw null!;
    }
    public sealed class ExtensionContext
    {
        public System.Collections.Generic.IReadOnlyList<string> Arguments => throw null!;
        public void Succeed(System.Collections.Generic.IDictionary<string, object?>? data = null) => throw null!;
        public void Fail(string code, string message) => throw null!;
    }
    public sealed class ExtensionRegistration : System.IDisposable { public void Dispose() => throw null!; }
    public sealed class ExtensionRegistry
    {
        public ExtensionRegistration Register(string id, string version, int apiVersion, params ExtensionCommand[] commands) => throw null!;
    }
}
