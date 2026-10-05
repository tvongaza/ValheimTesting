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
        public static void Destroy(Object obj) => throw null;
        public static T[] FindObjectsByType<T>(FindObjectsInactive inactive, FindObjectsSortMode sort) where T : Object => throw null;
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
    public class ScriptableObject : Object { }
    public sealed class GameObject : Object
    {
        public GameObject(string name) { }
        public Transform transform => throw null;
        public int layer => throw null;
        public bool activeSelf => throw null;
        public T AddComponent<T>() where T : Component => throw null;
        public void SetActive(bool active) => throw null;
        public T GetComponent<T>() => throw null;
        public T GetComponentInChildren<T>() => throw null;
    }
    public class Transform : Component
    {
        public Vector3 position { get => throw null; set => throw null; }
        public Quaternion rotation { get => throw null; set => throw null; }
    }
    public class Camera : Behaviour
    {
        public bool enabled { get => throw null; set => throw null; }
        public int cullingMask { get => throw null; set => throw null; }
        public RenderTexture targetTexture { get => throw null; set => throw null; }
        public void CopyFrom(Camera other) => throw null;
        public void Render() => throw null;
    }
    public class Canvas : Behaviour { public RenderMode renderMode => throw null; }
    public enum RenderMode { ScreenSpaceOverlay, ScreenSpaceCamera, WorldSpace }
    public static class LayerMask { public static int GetMask(params string[] layerNames) => throw null; }
    public class Texture2D : Object
    {
        public Texture2D(int width, int height, TextureFormat format, bool mipChain) { }
        public void ReadPixels(Rect source, int x, int y) => throw null;
        public void Apply(bool updateMipmaps) => throw null;
        public byte[] EncodeToPNG() => throw null;
    }
    public enum TextureFormat { RGB24 }
    public class RenderTexture : Object
    {
        public RenderTexture(int width, int height, int depth) { }
        public static RenderTexture active { get => throw null; set => throw null; }
    }
    public struct Rect { public Rect(float x, float y, float width, float height) { } }
    public sealed class WaitForSecondsRealtime { public WaitForSecondsRealtime(float time) { } }
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
    public sealed class Time { public static float realtimeSinceStartup => throw null; }
    public enum FindObjectsInactive { Include, Exclude }
    public enum FindObjectsSortMode { None, InstanceID }
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
        public string Location => throw null;
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
    public float m_zoneSize = 64f;
    public System.Collections.Generic.Dictionary<Vector2s, LocationInstance> m_locationInstances;
    public static Vector2s GetZone(UnityEngine.Vector3 point) => throw null;
    public bool IsZoneLoaded(Vector2s zoneID) => throw null;
    public System.Collections.Generic.List<string> GetGlobalKeys() => throw null;
    public void SetGlobalKey(string name) => throw null;
    public void RemoveGlobalKey(string name) => throw null;
}
public class ZNetScene : UnityEngine.MonoBehaviour
{
    public static ZNetScene instance => throw null;
    public System.Collections.Generic.List<UnityEngine.GameObject> m_prefabs;
    public System.Collections.Generic.List<UnityEngine.GameObject> m_nonNetViewPrefabs;
    public bool HasPrefab(int hash) => throw null;
    public UnityEngine.GameObject GetPrefab(int hash) => throw null;
    public ZNetView FindInstance(ZDO zdo) => throw null;
}
public class ObjectDB : UnityEngine.MonoBehaviour
{
    public static ObjectDB instance => throw null;
    public System.Collections.Generic.List<UnityEngine.GameObject> m_items;
    public System.Collections.Generic.List<Recipe> m_recipes;
    public System.Collections.Generic.List<StatusEffect> m_StatusEffects;
    public StatusEffect GetStatusEffect(int hash) => throw null;
    public UnityEngine.GameObject GetItemPrefab(string name) => throw null;
    public UnityEngine.GameObject GetItemPrefab(int hash) => throw null;
    public UnityEngine.GameObject GetItemPrefab(ItemDrop.ItemData.SharedData sharedData) => throw null;
}
public class StatusEffect : UnityEngine.ScriptableObject { public int NameHash() => throw null; }
public class Recipe : UnityEngine.ScriptableObject
{
    public ItemDrop m_item;
    public int m_amount = 1;
    public bool m_enabled = true;
    public CraftingStation m_craftingStation;
    public int m_minStationLevel = 1;
    public Piece.Requirement[] m_resources;
}
public class ItemDrop : UnityEngine.MonoBehaviour
{
    public ItemData m_itemData;
    public class ItemData
    {
        public SharedData m_shared;
        public class SharedData { public string m_name; public PieceTable m_buildPieces; }
    }
}
public class Piece : UnityEngine.MonoBehaviour
{
    public bool m_enabled;
    public CraftingStation m_craftingStation;
    public Requirement[] m_resources;
    public class Requirement
    {
        public ItemDrop m_resItem;
        public int m_amount = 1;
    }
}
public class PieceTable : UnityEngine.MonoBehaviour { public System.Collections.Generic.List<UnityEngine.GameObject> m_pieces; }
public class CraftingStation : UnityEngine.MonoBehaviour { public string m_name = ""; }
public struct ZDOID { }
public class ZDO
{
    public ZDOID m_uid;
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
    public void FindSectorObjects(Vector2s sector, SimulationDistance simulationDistance, System.Collections.Generic.List<ZDO> sectorObjects, System.Collections.Generic.List<ZDO> distantSectorObjects = null) => throw null;
}
public class ZNetView : UnityEngine.MonoBehaviour
{
    public bool m_distant;
    public ZDO GetZDO() => throw null;
    public bool IsValid() => throw null;
}
public class Player : UnityEngine.MonoBehaviour
{
    public static Player m_localPlayer;
    public static bool m_debugMode;
    public System.Collections.Generic.Dictionary<string, string> m_customData;
    public bool InGodMode() => throw null;
    public bool InGhostMode() => throw null;
    public bool InDebugFlyMode() => throw null;
    public void SetGodMode(bool value) => throw null;
    public void SetGhostMode(bool value) => throw null;
    public void ToggleDebugFly() => throw null;
}
public class EnvMan : UnityEngine.MonoBehaviour
{
    public static EnvMan instance;
    public bool m_debugTimeOfDay;
    public float m_debugTime;
    public string m_debugEnv;
}
public class GameCamera : UnityEngine.MonoBehaviour
{
    public static GameCamera instance;
    public bool m_freeFly;
    public void ToggleFreeFly() => throw null;
}
public class Mister : UnityEngine.MonoBehaviour { }
public class ClutterSystem : UnityEngine.MonoBehaviour
{
    public static ClutterSystem instance;
    public class Clutter { public bool m_enabled; }
    public System.Collections.Generic.List<Clutter> m_clutter;
    public void ClearAll() => throw null;
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
public abstract class Terminal : UnityEngine.MonoBehaviour
{
}
public class Console : Terminal { public static Console instance => throw null; public void updateCommandList() => throw null; }

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
    public static class ExtensionJson { public static string Write(object? value) => throw null!; }
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
    }
}
