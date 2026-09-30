// Compile-only declarations of the members the adapter sources use: Unity, Valheim 1.0.16 (assembly_valheim and
// assembly_utils), BepInEx 5 and the pinned ValheimCLI. Signatures only, written from the public API; no behaviour, and
// nothing here runs. The game's assemblies carry no nullable annotations, so neither do their declarations here;
// ValheimCLI's are annotated as its source is. Add a member here when the adapter sources start using it, with the
// signature the shipped assembly has (a member the shipped game keeps private is reached by reflection, not declared).
#nullable disable
#pragma warning disable CS0649, CS0169, CS8618

namespace UnityEngine
{
    public class Object
    {
        public string name { get => throw null; set => throw null; }
        public static T Instantiate<T>(T original, Vector3 position, Quaternion rotation) where T : Object => throw null;
        public static void DestroyImmediate(Object obj) => throw null;
        public static bool operator ==(Object x, Object y) => throw null;
        public static bool operator !=(Object x, Object y) => throw null;
        public override bool Equals(object other) => throw null;
        public override int GetHashCode() => throw null;
    }
    public class Component : Object
    {
        public Transform transform => throw null;
        public GameObject gameObject => throw null;
        public T GetComponent<T>() => throw null;
        public T GetComponentInChildren<T>() => throw null;
    }
    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { }
    public sealed class GameObject : Object
    {
        public Transform transform => throw null;
        public T GetComponent<T>() => throw null;
        public T GetComponentInChildren<T>() => throw null;
    }
    public class Transform : Component { public Vector3 position { get => throw null; set => throw null; } }
    public class Collider : Component { public bool Raycast(Ray ray, out RaycastHit hitInfo, float maxDistance) => throw null; }
    public sealed class MeshCollider : Collider { }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 down => throw null;
    }
    public struct Quaternion
    {
        public static Quaternion identity => throw null;
        public Vector3 eulerAngles => throw null;
    }
    public struct Ray { public Ray(Vector3 origin, Vector3 direction) => throw null; }
    public struct RaycastHit { public Vector3 point => throw null; }
    public sealed class Time { public static float realtimeSinceStartup => throw null; }
    public sealed class Application { public static event System.Action quitting { add => throw null; remove => throw null; } }
}

namespace BepInEx
{
    public abstract class BaseUnityPlugin : UnityEngine.MonoBehaviour { public Configuration.ConfigFile Config => throw null; }
    public class BepInPlugin : System.Attribute { public System.Version Version => throw null; }
    public class PluginInfo
    {
        public BepInPlugin Metadata => throw null;
        public BaseUnityPlugin Instance => throw null;
    }
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

namespace BepInEx.Configuration
{
    // The real ConfigFile is an IDictionary<ConfigDefinition, ConfigEntryBase>; the adapter only enumerates it.
    public class ConfigFile : System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<ConfigDefinition, ConfigEntryBase>>
    {
        public System.Collections.Generic.IEnumerator<System.Collections.Generic.KeyValuePair<ConfigDefinition, ConfigEntryBase>> GetEnumerator() => throw null;
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => throw null;
    }
    public class ConfigDefinition
    {
        public string Section => throw null;
        public string Key => throw null;
    }
    public abstract class ConfigEntryBase
    {
        public System.Type SettingType => throw null;
        public object DefaultValue => throw null;
        public string GetSerializedValue() => throw null;
    }
    public static class TomlTypeConverter { public static string ConvertToString(object value, System.Type valueType) => throw null; }
}

namespace BepInEx.Bootstrap
{
    public static class Chainloader { public static System.Collections.Generic.Dictionary<string, PluginInfo> PluginInfos => throw null; }
}

public struct Vector2s
{
    public short x, y;
    public Vector2s(int _x, int _y) => throw null;
}
public static class StringExtensionMethods { public static int GetStableHashCode(this string str) => throw null; }
public class Utils { public static string GetSaveDataPath(FileHelpers.FileSource fileSource) => throw null; }
public class FileHelpers { public enum FileSource { Auto = 1, Local = 2, Cloud = 4, Legacy = 8 } }
public class ZNet : UnityEngine.MonoBehaviour
{
    public static ZNet instance => throw null;
    public bool IsServer() => throw null;
    public bool IsDedicated() => throw null;
    public UnityEngine.Vector3 GetReferencePosition() => throw null;
    public SimulationDistance GetSyncedSimulationDistance() => throw null;
}
public struct SimulationDistance
{
    public SimulationDistance(int nearSimulationDistance, int farSimulationDistance, bool classic = false) => throw null;
    public int NearSimulationDistance => throw null;
    public int FarSimulationDistance => throw null;
    public bool IsClassic => throw null;
}
public class DungeonGenerator : UnityEngine.MonoBehaviour
{
    public UnityEngine.Vector3 m_zoneSize;
    public bool m_useCustomInteriorTransform;
}
public class ZoneSystem : UnityEngine.MonoBehaviour
{
    public struct SectorIndex { public uint Sector; }
    public class ZoneLocation { public string m_prefabName; }
    public struct LocationInstance
    {
        public ZoneLocation m_location;
        public UnityEngine.Vector3 m_position;
    }
    public static ZoneSystem instance => throw null;
    public UnityEngine.GameObject m_zonePrefab;
    public float m_zoneSize = 64f;
    public System.Collections.Generic.Dictionary<Vector2s, LocationInstance> m_locationInstances;
    public static UnityEngine.Vector3 GetZonePos(Vector2s id) => throw null;
    public static Vector2s GetZone(UnityEngine.Vector3 point) => throw null;
    public bool IsZoneLoaded(Vector2s zoneID) => throw null;
    public System.Collections.Generic.List<string> GetGlobalKeys() => throw null;
    public void SetGlobalKey(string name) => throw null;
    public void RemoveGlobalKey(string name) => throw null;
}
public class ZNetScene : UnityEngine.MonoBehaviour
{
    public static ZNetScene instance => throw null;
    public bool HasPrefab(int hash) => throw null;
    public UnityEngine.GameObject GetPrefab(int hash) => throw null;
    public ZNetView FindInstance(ZDO zdo) => throw null;
}
public struct ZDOID { }
public class ZDO
{
    public ZDOID m_uid;
    public bool IsOwner() => throw null;
    public int GetPrefab() => throw null;
    public UnityEngine.Vector3 GetPosition() => throw null;
    public int GetInt(string name, int defaultValue = 0) => throw null;
    public bool GetInt(string name, out int value) => throw null;
    public UnityEngine.Vector3 GetVec3(string name, UnityEngine.Vector3 defaultValue) => throw null;
    public UnityEngine.Quaternion GetQuaternion(string name, UnityEngine.Quaternion defaultValue) => throw null;
    public bool GetByteArray(string name, out byte[] value) => throw null;
}
public class ZDOMan
{
    public static ZDOMan instance => throw null;
    public void DestroyZDO(ZDO zdo) => throw null;
    public void FindSectorObjects(Vector2s sector, SimulationDistance simulationDistance, System.Collections.Generic.List<ZDO> sectorObjects, System.Collections.Generic.List<ZDO> distantSectorObjects = null) => throw null;
}
public class ZNetView : UnityEngine.MonoBehaviour
{
    public bool m_distant;
    public ZDO GetZDO() => throw null;
    public bool IsValid() => throw null;
    public bool IsOwner() => throw null;
    public static void StartGhostInit() => throw null;
    public static void FinishGhostInit() => throw null;
}
public class Player : UnityEngine.MonoBehaviour
{
    public static Player m_localPlayer;
    public System.Collections.Generic.Dictionary<string, string> m_customData;
}
public class PlayerProfile
{
    public FileHelpers.FileSource m_fileSource;
    public string GetName() => throw null;
    public string GetFilename() => throw null;
    public string GetPath() => throw null;
}
public class Game : UnityEngine.MonoBehaviour
{
    public static Game instance => throw null;
    public PlayerProfile GetPlayerProfile() => throw null;
}
public class WorldGenerator { public static WorldGenerator instance => throw null; }
public class HeightmapBuilder
{
    public static HeightmapBuilder instance => throw null;
    public bool IsTerrainReady(UnityEngine.Vector3 center, int width, float scale, bool distantLod, WorldGenerator worldGen) => throw null;
}
public class Heightmap : UnityEngine.MonoBehaviour
{
    public UnityEngine.GameObject m_terrainCompilerPrefab;
    public int m_width = 32;
    public float m_scale = 1f;
    public bool IsDistantLod => throw null;
    public void Regenerate() => throw null;
    public bool GetWorldHeight(UnityEngine.Vector3 worldPos, out float height) => throw null;
    public static Heightmap FindHeightmap(UnityEngine.Vector3 point) => throw null;
}
public class TerrainComp : UnityEngine.MonoBehaviour { public static TerrainComp FindTerrainCompiler(UnityEngine.Vector3 pos) => throw null; }
public abstract class Terminal : UnityEngine.MonoBehaviour
{
    public class ConsoleEventArgs
    {
        public string[] Args;
        public Terminal Context;
        public int Length => throw null;
        public string this[int i] => throw null;
    }
    public delegate object ConsoleEventFailable(ConsoleEventArgs args);
    public delegate void ConsoleEvent(ConsoleEventArgs args);
    public delegate System.Collections.Generic.List<string> ConsoleOptionsFetcher();
    public class ConsoleCommand
    {
        public ConsoleCommand(string command, string description, ConsoleEventFailable action, bool isCheat = false, bool isNetwork = false, bool onlyServer = false, bool isSecret = false, bool allowInDevBuild = false, bool hideBehindDevCommands = false, ConsoleOptionsFetcher optionsFetcher = null, bool alwaysRefreshTabOptions = false, bool remoteCommand = false, bool onlyAdmin = false) => throw null;
        public ConsoleCommand(string command, string description, ConsoleEvent action, bool isCheat = false, bool isNetwork = false, bool onlyServer = false, bool isSecret = false, bool allowInDevBuild = false, bool hideBehindDevCommands = false, ConsoleOptionsFetcher optionsFetcher = null, bool alwaysRefreshTabOptions = false, bool remoteCommand = false, bool onlyAdmin = false) => throw null;
    }
    public void AddString(string text) => throw null;
}
public class Console : Terminal { public static Console instance => throw null; }

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
        public string Name => throw null!;
        public ExtensionCommand(string name, string help, System.Func<ExtensionContext, System.Collections.IEnumerator> execute,
            bool readOnly = false, ExtensionRole role = ExtensionRole.Any, bool needsWorld = false, int resultVersion = 1) => throw null!;
    }
    public sealed class ExtensionContext
    {
        public System.Collections.Generic.IReadOnlyList<string> Arguments => throw null!;
        public bool Cancelled => throw null!;
        public void Succeed(System.Collections.Generic.IDictionary<string, object?>? data = null) => throw null!;
        public void Fail(string code, string message) => throw null!;
    }
    public sealed class ExtensionRegistration : System.IDisposable
    {
        public string Id => throw null!;
        public string Version => throw null!;
        public string Instance => throw null!;
        public bool IsClosing => throw null!;
        public void OnDispose(System.Action cleanup) => throw null!;
        public void Dispose() => throw null!;
    }
    public sealed class ExtensionRegistry
    {
        public ExtensionRegistration Register(string id, string version, int apiVersion, params ExtensionCommand[] commands) => throw null!;
        public System.Collections.Generic.IReadOnlyList<ExtensionCommand> Commands(ExtensionRegistration owner) => throw null!;
    }
    public static class ExtensionHost
    {
        public static void Execute(ExtensionRegistry registry, string path, string[] arguments, System.Action<string> output) => throw null!;
    }
    public sealed class OwnedCommandSet<T> : System.IDisposable where T : class
    {
        public static OwnedCommandSet<T> Register(System.Collections.Generic.IDictionary<string, T> table, System.Action register) => throw null!;
        public void Dispose() => throw null!;
    }
}
