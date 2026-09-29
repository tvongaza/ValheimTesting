// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// BepInEx 5.4.23's configuration (ConfigFile, ConfigEntry<T>, definitions, descriptions, acceptable values and the TOML
// value converter) over an in-memory disk, the plugin base class and attributes a plugin class compiles against, and
// the rest of the logging API. Parts are adapted from BepInEx 5.4.23 (MIT, Copyright (c) 2018 Bepis; see
// THIRD-PARTY-NOTICES.md): ConfigFile.Save, the description lines, SetSerializedValue/ClampValue and TomlTypeConverter.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace BepInEx.Logging
{
    [Flags]
    public enum LogLevel { None = 0, Fatal = 1, Error = 2, Warning = 4, Message = 8, Info = 16, Debug = 32, All = Fatal | Error | Warning | Message | Info | Debug }

    public partial class ManualLogSource
    {
        public string SourceName { get; } = "";
        public ManualLogSource() { }
        public ManualLogSource(string sourceName) { SourceName = sourceName; }
        public void LogFatal(object data) => Write("FATAL", data);
        public void LogMessage(object data) => Write("MSG  ", data);
        public void Dispose() { }
        public void Log(LogLevel level, object data)
        {
            if ((level & LogLevel.Fatal) != 0) LogFatal(data);
            else if ((level & LogLevel.Error) != 0) LogError(data);
            else if ((level & LogLevel.Warning) != 0) LogWarning(data);
            else if ((level & LogLevel.Message) != 0) LogMessage(data);
            else if ((level & LogLevel.Info) != 0) LogInfo(data);
            else LogDebug(data);
        }
    }

    /// <summary>BepInEx's log hub; sources it creates write to the log capture like any other.</summary>
    public static partial class Logger
    {
        public static ManualLogSource CreateLogSource(string sourceName) => new(sourceName);
        internal static readonly ManualLogSource Internal = new("BepInEx");
    }
}

namespace BepInEx
{
    /// <summary>A plugin's identity. A version BepInEx cannot parse is null, as in BepInEx.</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public partial class BepInPlugin : Attribute
    {
        public string GUID { get; protected set; }
        public string Name { get; protected set; }
        public Version? Version { get; protected set; }
        public BepInPlugin(string GUID, string Name, string Version)
        {
            this.GUID = GUID; this.Name = Name;
            try { this.Version = new Version(Version); } catch { this.Version = null; }
        }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public partial class BepInDependency : Attribute
    {
        [Flags] public enum DependencyFlags { HardDependency = 1, SoftDependency = 2 }
        public string DependencyGUID { get; protected set; }
        public DependencyFlags Flags { get; protected set; }
        public BepInDependency(string DependencyGUID, DependencyFlags Flags = DependencyFlags.HardDependency) { this.DependencyGUID = DependencyGUID; this.Flags = Flags; }
        public BepInDependency(string DependencyGUID, string MinimumDependencyVersion) : this(DependencyGUID) { }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public partial class BepInIncompatibility : Attribute
    {
        public string IncompatibilityGUID { get; protected set; }
        public BepInIncompatibility(string IncompatibilityGUID) { this.IncompatibilityGUID = IncompatibilityGUID; }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public partial class BepInProcess : Attribute
    {
        public string ProcessName { get; protected set; }
        public BepInProcess(string ProcessName) { this.ProcessName = ProcessName; }
    }

    /// <summary>BepInEx's folders, relative to the game root; only <see cref="ConfigPath"/> is used (for plugin config files, which live on the in-memory disk).</summary>
    public static partial class Paths
    {
        public static string GameRootPath = ".";
        public static string BepInExRootPath = "BepInEx";
        public static string ConfigPath = Path.Combine("BepInEx", "config");
        public static string PluginPath = Path.Combine("BepInEx", "plugins");
    }

    public partial class PluginInfo
    {
        public BepInPlugin Metadata { get; internal set; } = null!;
        public BaseUnityPlugin? Instance { get; internal set; }
        public string Location { get; internal set; } = "";
        public IEnumerable<BepInDependency> Dependencies { get; internal set; } = new BepInDependency[0];
        public IEnumerable<BepInProcess> Processes { get; internal set; } = new BepInProcess[0];
    }

    /// <summary>
    /// The base of a BepInEx plugin. As in BepInEx 5.4.23, constructing one reads its <see cref="BepInPlugin"/> attribute
    /// (and throws without it), makes its <see cref="Logger"/> and opens its <see cref="Config"/> at
    /// <c>BepInEx/config/&lt;GUID&gt;.cfg</c> (on the in-memory disk). <c>ValheimWorldScope.LoadPlugin&lt;T&gt;()</c> adds it
    /// to an object as the chainloader does, which runs its Awake.
    /// </summary>
    public abstract partial class BaseUnityPlugin : UnityEngine.MonoBehaviour
    {
        protected BaseUnityPlugin()
        {
            var metadata = (BepInPlugin?)Attribute.GetCustomAttribute(GetType(), typeof(BepInPlugin))
                ?? throw new InvalidOperationException($"Can't create an instance of {GetType().FullName} because it inherits from BaseUnityPlugin and the BepInPlugin attribute is missing.");
            Info = new PluginInfo
            {
                Metadata = metadata, Instance = this, Location = GetType().Assembly.Location,
                Dependencies = GetType().GetCustomAttributes(typeof(BepInDependency), true).Cast<BepInDependency>().ToArray(),
                Processes = GetType().GetCustomAttributes(typeof(BepInProcess), true).Cast<BepInProcess>().ToArray(),
            };
            Logger = BepInEx.Logging.Logger.CreateLogSource(metadata.Name);
            Config = new ConfigFile(Path.Combine(Paths.ConfigPath, metadata.GUID + ".cfg"), false, metadata);
        }
        public PluginInfo Info { get; }
        protected ManualLogSource Logger { get; }
        public ConfigFile Config { get; }
    }
}

namespace BepInEx.Configuration
{
    /// <summary>Section and key of a setting, case-sensitive; characters BepInEx refuses throw, as in BepInEx.</summary>
    public partial class ConfigDefinition : IEquatable<ConfigDefinition>
    {
        private static readonly char[] s_invalid = { '=', '\n', '\t', '\\', '"', '\'', '[', ']' };
        public string Section { get; }
        public string Key { get; }
        public ConfigDefinition(string section, string key)
        {
            Check(section, nameof(section)); Check(key, nameof(key));
            Section = section; Key = key;
        }
        private static void Check(string value, string name)
        {
            if (value == null) throw new ArgumentNullException(name);
            if (value != value.Trim()) throw new ArgumentException("Cannot use whitespace characters at start or end of section and key names", name);
            if (value.IndexOfAny(s_invalid) >= 0) throw new ArgumentException(@"Cannot use any of the following characters in section and key names: = \n \t \ "" ' [ ]", name);
        }
        public bool Equals(ConfigDefinition? other) => other is not null && string.Equals(Key, other.Key) && string.Equals(Section, other.Section);
        public override bool Equals(object? obj) => ReferenceEquals(this, obj) || Equals(obj as ConfigDefinition);
        public override int GetHashCode() { unchecked { return ((Key?.GetHashCode() ?? 0) * 397) ^ (Section?.GetHashCode() ?? 0); } }
        public static bool operator ==(ConfigDefinition? left, ConfigDefinition? right) => Equals(left, right);
        public static bool operator !=(ConfigDefinition? left, ConfigDefinition? right) => !Equals(left, right);
        public override string ToString() => Section + "." + Key;
    }

    public partial class ConfigDescription
    {
        public ConfigDescription(string description, AcceptableValueBase? acceptableValues = null, params object[] tags)
        {
            Description = description ?? throw new ArgumentNullException(nameof(description));
            AcceptableValues = acceptableValues; Tags = tags;
        }
        public string Description { get; }
        public AcceptableValueBase? AcceptableValues { get; }
        public object[] Tags { get; }
        public static ConfigDescription Empty { get; } = new("");
    }

    public sealed partial class SettingChangedEventArgs : EventArgs
    {
        public SettingChangedEventArgs(ConfigEntryBase changedSetting) { ChangedSetting = changedSetting; }
        public ConfigEntryBase ChangedSetting { get; }
    }

    public abstract partial class AcceptableValueBase
    {
        protected AcceptableValueBase(Type valueType) { ValueType = valueType; }
        public abstract object Clamp(object value);
        public abstract bool IsValid(object value);
        public Type ValueType { get; }
        public abstract string ToDescriptionString();
    }

    /// <summary>A value is clamped into the range.</summary>
    public partial class AcceptableValueRange<T> : AcceptableValueBase where T : IComparable
    {
        public AcceptableValueRange(T minValue, T maxValue) : base(typeof(T))
        {
            if (maxValue == null) throw new ArgumentNullException(nameof(maxValue));
            if (minValue == null) throw new ArgumentNullException(nameof(minValue));
            if (minValue.CompareTo(maxValue) >= 0) throw new ArgumentException("minValue has to be lower than maxValue");
            MinValue = minValue; MaxValue = maxValue;
        }
        public virtual T MinValue { get; }
        public virtual T MaxValue { get; }
        public override object Clamp(object value) => MinValue.CompareTo(value) > 0 ? MinValue : MaxValue.CompareTo(value) < 0 ? MaxValue : value;
        public override bool IsValid(object value) => MinValue.CompareTo(value) <= 0 && MaxValue.CompareTo(value) >= 0;
        public override string ToDescriptionString() => $"# Acceptable value range: From {MinValue} to {MaxValue}";
    }

    /// <summary>A value not in the list becomes the list's first value.</summary>
    public partial class AcceptableValueList<T> : AcceptableValueBase where T : IEquatable<T>
    {
        public AcceptableValueList(params T[] acceptableValues) : base(typeof(T))
        {
            if (acceptableValues == null) throw new ArgumentNullException(nameof(acceptableValues));
            if (acceptableValues.Length == 0) throw new ArgumentException("At least one acceptable value is needed", nameof(acceptableValues));
            AcceptableValues = acceptableValues;
        }
        public virtual T[] AcceptableValues { get; }
        public override object Clamp(object value) => IsValid(value) ? value : AcceptableValues[0];
        public override bool IsValid(object value) => value is T v && AcceptableValues.Any(x => x.Equals(v));
        public override string ToDescriptionString() => "# Acceptable values: " + string.Join(", ", AcceptableValues.Select(x => x.ToString()).ToArray());
    }

    /// <summary>
    /// One setting. Setting <see cref="Value"/> to a different value (after clamping) raises the file's
    /// <c>SettingChanged</c> and then this entry's, once, and saves first when <c>SaveOnConfigSet</c> is on; setting an
    /// equal value does nothing. As in BepInEx, binding with a default other than the type's default already counts as a
    /// change: the file's <c>SettingChanged</c> fires for the new entry (its own event is not subscribed yet).
    /// </summary>
    public sealed partial class ConfigEntry<T> : ConfigEntryBase
    {
        public event EventHandler? SettingChanged;
        private T m_value = default!;
        public T Value
        {
            get => m_value;
            set
            {
                value = ClampValue(value);
                if (Equals(m_value, value)) return;
                m_value = value;
                OnSettingChanged(this);
            }
        }
        public override object BoxedValue { get => Value!; set => Value = (T)value; }
        internal ConfigEntry(ConfigFile configFile, ConfigDefinition definition, T defaultValue, ConfigDescription? configDescription)
            : base(configFile, definition, typeof(T), defaultValue, configDescription)
        {
            configFile.SettingChanged += (sender, args) => { if (args.ChangedSetting == this) SettingChanged?.Invoke(sender, args); };
        }
    }

    public abstract partial class ConfigEntryBase
    {
        internal ConfigEntryBase(ConfigFile configFile, ConfigDefinition definition, Type settingType, object? defaultValue, ConfigDescription? configDescription)
        {
            ConfigFile = configFile ?? throw new ArgumentNullException(nameof(configFile));
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            SettingType = settingType ?? throw new ArgumentNullException(nameof(settingType));
            Description = configDescription ?? ConfigDescription.Empty;
            if (Description.AcceptableValues != null && !SettingType.IsAssignableFrom(Description.AcceptableValues.ValueType))
                throw new ArgumentException("configDescription.AcceptableValues is for a different type than the type of this setting");
            DefaultValue = defaultValue!;
            BoxedValue = defaultValue!;
        }
        public ConfigFile ConfigFile { get; }
        public ConfigDefinition Definition { get; }
        public ConfigDescription Description { get; }
        public Type SettingType { get; }
        public object DefaultValue { get; }
        public abstract object BoxedValue { get; set; }
        public string GetSerializedValue() => TomlTypeConverter.ConvertToString(BoxedValue, SettingType);
        /// <summary>Sets the value from its text; text that does not parse is logged and ignored, as in BepInEx.</summary>
        public void SetSerializedValue(string value)
        {
            try { BoxedValue = TomlTypeConverter.ConvertToValue(value, SettingType); }
            catch (Exception e) { BepInEx.Logging.Logger.Internal.LogWarning($"Config value of setting \"{Definition}\" could not be parsed and will be ignored. Reason: {e.Message}; Value: {value}"); }
        }
        protected T ClampValue<T>(T value) => Description.AcceptableValues != null ? (T)Description.AcceptableValues.Clamp(value!) : value;
        protected void OnSettingChanged(object sender) => ConfigFile.OnSettingChanged(sender, this);
        public void WriteDescription(StreamWriter writer) { foreach (var line in DescriptionLines()) writer.WriteLine(line); }
        internal IEnumerable<string> DescriptionLines()
        {
            if (!string.IsNullOrEmpty(Description.Description)) yield return "## " + Description.Description.Replace("\n", "\n## ");
            yield return "# Setting type: " + SettingType.Name;
            yield return "# Default value: " + TomlTypeConverter.ConvertToString(DefaultValue, SettingType);
            if (Description.AcceptableValues != null) yield return Description.AcceptableValues.ToDescriptionString();
            else if (SettingType.IsEnum)
            {
                yield return "# Acceptable values: " + string.Join(", ", Enum.GetNames(SettingType));
                if (SettingType.GetCustomAttributes(typeof(FlagsAttribute), true).Any())
                    yield return "# Multiple values can be set at the same time by separating them with , (e.g. Debug, Warning)";
            }
        }
    }

    /// <summary>
    /// A plugin's config file, as BepInEx 5.4.23's, on an in-memory disk: <see cref="Save"/> writes the file BepInEx would
    /// write (header, sections in order, each setting's description, type, default and acceptable values, then
    /// <c>Key = value</c>) and <see cref="Reload"/> reads it back. Values in the file for settings not bound yet are kept
    /// and applied when they are bound, and written back on save. <see cref="FileText"/> reads or replaces the file, as an
    /// edit outside the game would (no events until <see cref="Reload"/>). <c>ValheimWorldScope.WithConfigFiles</c> gives a
    /// test its own disk.
    /// </summary>
    public partial class ConfigFile : IDictionary<ConfigDefinition, ConfigEntryBase>
    {
        internal static Dictionary<string, string> s_files = new(StringComparer.Ordinal);
        private readonly BepInPlugin? m_owner;
        private readonly object m_ioLock = new();
        protected Dictionary<ConfigDefinition, ConfigEntryBase> Entries { get; } = new();
        private Dictionary<ConfigDefinition, string> OrphanedEntries { get; } = new();

        public string ConfigFilePath { get; }
        /// <summary>Save after every change and every new binding (on by default, as in BepInEx).</summary>
        public bool SaveOnConfigSet { get; set; } = true;
        /// <summary>How many times this file was saved. Not a BepInEx member.</summary>
        public int SaveCount { get; private set; }

        public ConfigFile(string configPath, bool saveOnInit) : this(configPath, saveOnInit, null) { }
        public ConfigFile(string configPath, bool saveOnInit, BepInPlugin? ownerMetadata)
        {
            m_owner = ownerMetadata;
            if (configPath == null) throw new ArgumentNullException(nameof(configPath));
            ConfigFilePath = Path.GetFullPath(configPath);
            if (s_files.ContainsKey(ConfigFilePath)) Reload();
            else if (saveOnInit) Save();
        }

        /// <summary>The file on the in-memory disk at a path (null when there is none).</summary>
        public static string? ReadFile(string configPath) => s_files.TryGetValue(Path.GetFullPath(configPath), out var text) ? text : null;
        /// <summary>Writes (or, with null, deletes) a file on the in-memory disk, as an edit outside the game would.</summary>
        public static void WriteFile(string configPath, string? text) { if (text == null) s_files.Remove(Path.GetFullPath(configPath)); else s_files[Path.GetFullPath(configPath)] = text; }
        /// <summary>This file's text on the in-memory disk; null before it is first saved.</summary>
        public string? FileText { get => ReadFile(ConfigFilePath); set => WriteFile(ConfigFilePath, value); }

        /// <summary>Reads the file again: unsaved changes are lost, changed values raise <c>SettingChanged</c>, then <see cref="ConfigReloaded"/> fires. A missing file throws, as reading it would.</summary>
        public void Reload()
        {
            lock (m_ioLock)
            {
                if (!s_files.TryGetValue(ConfigFilePath, out var text)) throw new FileNotFoundException("Could not find file '" + ConfigFilePath + "'.", ConfigFilePath);
                OrphanedEntries.Clear();
                foreach (var (definition, value) in Settings(text))
                {
                    if (Entries.TryGetValue(definition, out var entry)) entry.SetSerializedValue(value);
                    else OrphanedEntries[definition] = value;
                }
            }
            OnConfigReloaded();
        }

        // The settings a file holds, in order: "[Section]" lines open a section, "#" lines are comments, and every other
        // line with an '=' is "key = value" (trimmed, split at the first '='). Anything else is skipped.
        private static IEnumerable<(ConfigDefinition Definition, string Value)> Settings(string text)
        {
            string section = string.Empty;
            foreach (var line in text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None).Select(l => l.Trim()))
            {
                if (line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2); continue; }
                int equals = line.IndexOf('=');
                if (equals < 0) continue;
                yield return (new ConfigDefinition(section, line.Substring(0, equals).Trim()), line.Substring(equals + 1).Trim());
            }
        }

        /// <summary>Writes the file as BepInEx does.</summary>
        public void Save()
        {
            lock (m_ioLock)
            {
                var text = new StringBuilder();
                if (m_owner != null)
                {
                    text.AppendLine($"## Settings file was created by plugin {m_owner.Name} v{m_owner.Version}");
                    text.AppendLine($"## Plugin GUID: {m_owner.GUID}");
                    text.AppendLine();
                }
                var all = Entries.Select(x => (x.Key, Entry: (ConfigEntryBase?)x.Value, Value: x.Value.GetSerializedValue()))
                    .Concat(OrphanedEntries.Select(x => (x.Key, Entry: (ConfigEntryBase?)null, x.Value)));
                foreach (var section in all.GroupBy(x => x.Key.Section).OrderBy(x => x.Key))
                {
                    text.AppendLine($"[{section.Key}]");
                    foreach (var setting in section)
                    {
                        text.AppendLine();
                        if (setting.Entry != null) foreach (var line in setting.Entry.DescriptionLines()) text.AppendLine(line);
                        text.AppendLine($"{setting.Key.Key} = {setting.Value}");
                    }
                    text.AppendLine();
                }
                s_files[ConfigFilePath] = text.ToString();
                SaveCount++;
            }
        }

        public bool TryGetEntry<T>(ConfigDefinition configDefinition, out ConfigEntry<T> entry)
        {
            lock (m_ioLock)
            {
                if (Entries.TryGetValue(configDefinition, out var raw)) { entry = (ConfigEntry<T>)raw; return true; }
                entry = null!; return false;
            }
        }
        public bool TryGetEntry<T>(string section, string key, out ConfigEntry<T> entry) => TryGetEntry(new ConfigDefinition(section, key), out entry);

        /// <summary>
        /// The setting for the definition: a new one with the default (or the value the file already holds for it), or the
        /// one already bound, as BepInEx does; a second bind with another type throws <see cref="InvalidCastException"/>
        /// and an unsupported type <see cref="ArgumentException"/>.
        /// </summary>
        public ConfigEntry<T> Bind<T>(ConfigDefinition configDefinition, T defaultValue, ConfigDescription? configDescription = null)
        {
            if (!TomlTypeConverter.CanConvert(typeof(T)))
                throw new ArgumentException($"Type {typeof(T)} is not supported by the config system. Supported types: {string.Join(", ", TomlTypeConverter.GetSupportedTypes().Select(x => x.Name).ToArray())}");
            lock (m_ioLock)
            {
                if (Entries.TryGetValue(configDefinition, out var raw)) return (ConfigEntry<T>)raw;
                var entry = new ConfigEntry<T>(this, configDefinition, defaultValue, configDescription);
                Entries[configDefinition] = entry;
                if (OrphanedEntries.TryGetValue(configDefinition, out var fileValue)) { entry.SetSerializedValue(fileValue); OrphanedEntries.Remove(configDefinition); }
                if (SaveOnConfigSet) Save();
                return entry;
            }
        }
        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, ConfigDescription? configDescription = null) => Bind(new ConfigDefinition(section, key), defaultValue, configDescription);
        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description) => Bind(new ConfigDefinition(section, key), defaultValue, new ConfigDescription(description));

        public event EventHandler? ConfigReloaded;
        public event EventHandler<SettingChangedEventArgs>? SettingChanged;

        // As BepInEx: save first, then call every handler; one that throws is logged and the rest still run.
        internal void OnSettingChanged(object sender, ConfigEntryBase changedEntryBase)
        {
            if (changedEntryBase == null) throw new ArgumentNullException(nameof(changedEntryBase));
            if (SaveOnConfigSet) Save();
            var handlers = SettingChanged;
            if (handlers == null) return;
            var args = new SettingChangedEventArgs(changedEntryBase);
            foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<SettingChangedEventArgs>>())
            {
                try { handler(sender, args); }
                catch (Exception e) { BepInEx.Logging.Logger.Internal.LogError(e); }
            }
        }
        private void OnConfigReloaded()
        {
            var handlers = ConfigReloaded;
            if (handlers == null) return;
            foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
            {
                try { handler(this, EventArgs.Empty); }
                catch (Exception e) { BepInEx.Logging.Logger.Internal.LogError(e); }
            }
        }

        public IEnumerator<KeyValuePair<ConfigDefinition, ConfigEntryBase>> GetEnumerator() => Entries.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        void ICollection<KeyValuePair<ConfigDefinition, ConfigEntryBase>>.Add(KeyValuePair<ConfigDefinition, ConfigEntryBase> item) { lock (m_ioLock) Entries.Add(item.Key, item.Value); }
        public bool Contains(KeyValuePair<ConfigDefinition, ConfigEntryBase> item) { lock (m_ioLock) return ((ICollection<KeyValuePair<ConfigDefinition, ConfigEntryBase>>)Entries).Contains(item); }
        void ICollection<KeyValuePair<ConfigDefinition, ConfigEntryBase>>.CopyTo(KeyValuePair<ConfigDefinition, ConfigEntryBase>[] array, int arrayIndex) { lock (m_ioLock) ((ICollection<KeyValuePair<ConfigDefinition, ConfigEntryBase>>)Entries).CopyTo(array, arrayIndex); }
        bool ICollection<KeyValuePair<ConfigDefinition, ConfigEntryBase>>.Remove(KeyValuePair<ConfigDefinition, ConfigEntryBase> item) { lock (m_ioLock) return Entries.Remove(item.Key); }
        public int Count { get { lock (m_ioLock) return Entries.Count; } }
        public bool IsReadOnly => false;
        public bool ContainsKey(ConfigDefinition key) { lock (m_ioLock) return Entries.ContainsKey(key); }
        public void Add(ConfigDefinition key, ConfigEntryBase value) => throw new InvalidOperationException("Directly adding a config entry is not supported");
        public bool Remove(ConfigDefinition key) { lock (m_ioLock) return Entries.Remove(key); }
        public void Clear() { lock (m_ioLock) Entries.Clear(); }
        bool IDictionary<ConfigDefinition, ConfigEntryBase>.TryGetValue(ConfigDefinition key, out ConfigEntryBase value) { lock (m_ioLock) { bool found = Entries.TryGetValue(key, out var entry); value = entry!; return found; } }
        ConfigEntryBase IDictionary<ConfigDefinition, ConfigEntryBase>.this[ConfigDefinition key]
        {
            get { lock (m_ioLock) return Entries[key]; }
            set => throw new InvalidOperationException("Directly setting a config entry is not supported");
        }
        public ConfigEntryBase this[ConfigDefinition key] { get { lock (m_ioLock) return Entries[key]; } }
        public ConfigEntryBase this[string section, string key] => this[new ConfigDefinition(section, key)];
        public ICollection<ConfigDefinition> Keys { get { lock (m_ioLock) return Entries.Keys.ToArray(); } }
        ICollection<ConfigEntryBase> IDictionary<ConfigDefinition, ConfigEntryBase>.Values { get { lock (m_ioLock) return Entries.Values.ToArray(); } }
    }

    public partial class TypeConverter
    {
        public Func<object, Type, string> ConvertToString { get; set; } = null!;
        public Func<string, Type, object> ConvertToObject { get; set; } = null!;
    }

    /// <summary>
    /// How config values are written and read, as BepInEx's: strings escaped (a backslash is written as is, and text that
    /// looks like a Windows path is read unescaped), bool lower-case, whole numbers in the current culture, float, double
    /// and decimal in the invariant culture, enums by name (read case-insensitively), and UnityEngine.Color as RRGGBBAA hex.
    /// BepInEx also converts Vector2, Vector3, Vector4, Quaternion (with Unity's JsonUtility) and Rect; those are not here,
    /// so binding one throws where BepInEx would not: register a converter with <see cref="AddConverter"/> if a test needs it.
    /// </summary>
    public static partial class TomlTypeConverter
    {
        private static TypeConverter Plain(Func<string, object> parse) => new() { ConvertToString = (obj, _) => obj.ToString()!, ConvertToObject = (str, _) => parse(str) };
        private static readonly Dictionary<Type, TypeConverter> s_converters = new()
        {
            [typeof(string)] = new TypeConverter
            {
                ConvertToString = (obj, _) => Escape((string)obj),
                ConvertToObject = (str, _) => Regex.IsMatch(str, @"^""?\w:\\(?!\\)(?!.+\\\\)") ? str : Unescape(str),
            },
            [typeof(bool)] = new TypeConverter { ConvertToString = (obj, _) => obj.ToString()!.ToLowerInvariant(), ConvertToObject = (str, _) => bool.Parse(str) },
            [typeof(byte)] = Plain(s => byte.Parse(s)),
            [typeof(sbyte)] = Plain(s => sbyte.Parse(s)),
            [typeof(short)] = Plain(s => short.Parse(s)),
            [typeof(ushort)] = Plain(s => ushort.Parse(s)),
            [typeof(int)] = Plain(s => int.Parse(s)),
            [typeof(uint)] = Plain(s => uint.Parse(s)),
            [typeof(long)] = Plain(s => long.Parse(s)),
            [typeof(ulong)] = Plain(s => ulong.Parse(s)),
            [typeof(float)] = new TypeConverter { ConvertToString = (obj, _) => ((float)obj).ToString(NumberFormatInfo.InvariantInfo), ConvertToObject = (str, _) => float.Parse(str, NumberFormatInfo.InvariantInfo) },
            [typeof(double)] = new TypeConverter { ConvertToString = (obj, _) => ((double)obj).ToString(NumberFormatInfo.InvariantInfo), ConvertToObject = (str, _) => double.Parse(str, NumberFormatInfo.InvariantInfo) },
            [typeof(decimal)] = new TypeConverter { ConvertToString = (obj, _) => ((decimal)obj).ToString(NumberFormatInfo.InvariantInfo), ConvertToObject = (str, _) => decimal.Parse(str, NumberFormatInfo.InvariantInfo) },
            [typeof(Enum)] = new TypeConverter { ConvertToString = (obj, _) => obj.ToString()!, ConvertToObject = (str, type) => Enum.Parse(type, str, true) },
            [typeof(UnityEngine.Color)] = new TypeConverter { ConvertToString = (obj, _) => ColorToHex((UnityEngine.Color)obj), ConvertToObject = (str, _) => HexToColor(str) },
        };

        public static string ConvertToString(object value, Type valueType) =>
            (GetConverter(valueType) ?? throw new InvalidOperationException($"Cannot convert from type {valueType}")).ConvertToString(value, valueType);
        public static T ConvertToValue<T>(string value) => (T)ConvertToValue(value, typeof(T));
        public static object ConvertToValue(string value, Type valueType) =>
            (GetConverter(valueType) ?? throw new InvalidOperationException($"Cannot convert to type {valueType.Name}")).ConvertToObject(value, valueType);
        public static TypeConverter? GetConverter(Type valueType)
        {
            if (valueType == null) throw new ArgumentNullException(nameof(valueType));
            if (valueType.IsEnum) return s_converters[typeof(Enum)];
            return s_converters.TryGetValue(valueType, out var converter) ? converter : null;
        }
        /// <summary>Adds a converter; a type that already has one keeps it (logged, false), as in BepInEx.</summary>
        public static bool AddConverter(Type type, TypeConverter converter)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (converter == null) throw new ArgumentNullException(nameof(converter));
            if (CanConvert(type)) { BepInEx.Logging.Logger.Internal.LogWarning("Tried to add a TomlConverter when one already exists for type " + type.FullName); return false; }
            s_converters.Add(type, converter);
            return true;
        }
        public static bool CanConvert(Type type) => GetConverter(type) != null;
        public static IEnumerable<Type> GetSupportedTypes() => s_converters.Keys;

        private static string ColorToHex(UnityEngine.Color c)
        {
            // As Unity's conversion to Color32: clamp, scale to 255 and round half to even.
            static string Channel(float v) => ((int)Math.Round(Math.Max(0f, Math.Min(1f, v)) * 255f)).ToString("X2");
            return Channel(c.r) + Channel(c.g) + Channel(c.b) + Channel(c.a);
        }
        private static UnityEngine.Color HexToColor(string text)
        {
            string hex = text.Trim('#', ' ');
            if ((hex.Length != 6 && hex.Length != 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint bits))
                throw new FormatException("Invalid color string, expected hex #RRGGBBAA");
            if (hex.Length == 6) bits = (bits << 8) | 0xFF;
            return new UnityEngine.Color(((bits >> 24) & 0xFF) / 255f, ((bits >> 16) & 0xFF) / 255f, ((bits >> 8) & 0xFF) / 255f, (bits & 0xFF) / 255f);
        }

        // The characters written as a backslash sequence, and their letters. A backslash itself is written as it is (as
        // BepInEx does), so reading "\\" back gives one backslash and an unknown sequence keeps its backslash.
        private static readonly (char Raw, char Code)[] s_escapes =
            { ('\0', '0'), ('\a', 'a'), ('\b', 'b'), ('\t', 't'), ('\n', 'n'), ('\v', 'v'), ('\f', 'f'), ('\r', 'r'), ('\'', '\''), ('"', '"') };
        private static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var escaped = new StringBuilder(text.Length + 2);
            foreach (char c in text)
            {
                int i = Array.FindIndex(s_escapes, e => e.Raw == c);
                if (i >= 0) escaped.Append('\\').Append(s_escapes[i].Code); else escaped.Append(c);
            }
            return escaped.ToString();
        }
        private static string Unescape(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var result = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\\' || i == text.Length - 1) { result.Append(text[i]); continue; }
                char code = text[++i];
                int known = code == '\\' ? -2 : Array.FindIndex(s_escapes, e => e.Code == code);
                if (known == -2) result.Append('\\');
                else if (known >= 0) result.Append(s_escapes[known].Raw);
                else result.Append('\\').Append(code);
            }
            return result.ToString();
        }
    }
}
