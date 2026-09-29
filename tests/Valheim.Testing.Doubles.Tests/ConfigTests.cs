using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using Valheim.Testing.Doubles;
using Xunit;

public enum Mode { Off, Roads, Everything }

[BepInPlugin("example.configured", "Configured Mod", "1.2.3")]
public sealed class ConfiguredPlugin : BaseUnityPlugin
{
    public ConfigEntry<int> Radius = null!;
    public ConfigEntry<Mode> Mode = null!;
    private void Awake()
    {
        Radius = Config.Bind("General", "Radius", 40, new ConfigDescription("How far roads reach.\nIn metres.", new AcceptableValueRange<int>(10, 100)));
        Mode = Config.Bind("General", "Mode", global::Mode.Roads, "What to build.");
        Logger.LogInfo("configured " + Radius.Value);
    }
}

public sealed class UnattributedPlugin : BaseUnityPlugin { }

public sealed class ConfigTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new ValheimWorldScope().WithScene().WithConfigFiles();
    private static string Path(string name) => System.IO.Path.Combine("BepInEx", "config", name);
    private static string[] Lines(string? text) => (text ?? "").Replace("\r\n", "\n").Split('\n');
    public void Dispose() { UnityEngine.Object.EndOfFrame(); _scope.Dispose(); }

    [Fact] public void APluginsConfigIsBoundInAwakeAndWrittenInBepInExsFormat()
    {
        var log = _scope.CaptureLog();
        var plugin = _scope.LoadPlugin<ConfiguredPlugin>();
        Assert.Equal(40, plugin.Radius.Value); Assert.Contains("configured 40", log);
        Assert.Equal(System.IO.Path.GetFullPath(Path("example.configured.cfg")), plugin.Config.ConfigFilePath);
        Assert.Equal(new[]
        {
            "## Settings file was created by plugin Configured Mod v1.2.3",
            "## Plugin GUID: example.configured",
            "",
            "[General]",
            "",
            "## How far roads reach.",
            "## In metres.",
            "# Setting type: Int32",
            "# Default value: 40",
            "# Acceptable value range: From 10 to 100",
            "Radius = 40",
            "",
            "## What to build.",
            "# Setting type: Mode",
            "# Default value: Roads",
            "# Acceptable values: Off, Roads, Everything",
            "Mode = Roads",
            "",
            "",
        }, Lines(plugin.Config.FileText));
        Assert.Equal("example.configured", plugin.Info.Metadata.GUID);
        Assert.Throws<InvalidOperationException>(() => _scope.LoadPlugin<UnattributedPlugin>());
    }

    [Fact] public void SettingChangedFiresOncePerChangeAndNotForAnEqualValue()
    {
        var config = new ConfigFile(Path("changes.cfg"), false);
        var entry = config.Bind("General", "Enabled", false, "Switch.");
        int fileEvents = 0, entryEvents = 0; ConfigEntryBase? changed = null;
        config.SettingChanged += (_, e) => { fileEvents++; changed = e.ChangedSetting; };
        entry.SettingChanged += (_, _) => entryEvents++;
        int saves = config.SaveCount;
        entry.Value = true; entry.Value = true;
        Assert.Equal(1, fileEvents); Assert.Equal(1, entryEvents); Assert.Same(entry, changed);
        Assert.Equal(saves + 1, config.SaveCount); // SaveOnConfigSet saves before the handlers run
        entry.BoxedValue = false;
        Assert.Equal(2, fileEvents); Assert.Equal(2, entryEvents);
        Assert.Same(entry, config.Bind("General", "Enabled", true, "Ignored: already bound.")); Assert.False(entry.Value);
        Assert.Throws<InvalidCastException>(() => config.Bind("General", "Enabled", 1, "another type"));
    }

    [Fact] public void BindingWithANonDefaultValueAlreadyRaisesTheFilesSettingChanged()
    {
        // BepInEx sets the default through the value setter, before the entry's own event is subscribed.
        var config = new ConfigFile(Path("bind.cfg"), false);
        var seen = new List<string>();
        config.SettingChanged += (_, e) => seen.Add(e.ChangedSetting.Definition.ToString());
        var entry = config.Bind("A", "Count", 3, "");
        config.Bind("A", "Zero", 0, "");
        Assert.Equal(new[] { "A.Count" }, seen);
        int entryEvents = 0; entry.SettingChanged += (_, _) => entryEvents++;
        Assert.Equal(0, entryEvents);
    }

    [Fact] public void WithoutSaveOnConfigSetNothingIsWrittenUntilSave()
    {
        var config = new ConfigFile(Path("manual.cfg"), false) { SaveOnConfigSet = false };
        var entry = config.Bind("S", "K", 1, "");
        Assert.Null(config.FileText);
        entry.Value = 2; Assert.Null(config.FileText);
        config.Save();
        Assert.Contains("K = 2", Lines(config.FileText));
        var created = new ConfigFile(Path("created.cfg"), saveOnInit: true);
        Assert.Equal("", created.FileText);
    }

    [Fact] public void ReloadAppliesAnEditedFileAndKeepsValuesForSettingsNotBoundYet()
    {
        var config = new ConfigFile(Path("reload.cfg"), false);
        var radius = config.Bind("General", "Radius", 40, new ConfigDescription("", new AcceptableValueRange<int>(10, 100)));
        var name = config.Bind("General", "Name", "road", "");
        var changes = new List<string>(); int reloads = 0;
        config.SettingChanged += (_, e) => changes.Add(e.ChangedSetting.Definition.Key);
        config.ConfigReloaded += (_, _) => reloads++;
        var log = _scope.CaptureLog();
        config.FileText = "[General]\nRadius = 500\nName = paved\\nway\n# comment\nBroken line\n[Later]\nColor = FF000080\nSpeed = fast\n";
        Assert.Equal(40, radius.Value); // an edit outside the game changes nothing until a reload
        config.Reload();
        Assert.Equal(100, radius.Value); // clamped to the acceptable range
        Assert.Equal("paved\nway", name.Value);
        Assert.Equal(new[] { "Radius", "Name" }, changes); Assert.Equal(1, reloads);
        var color = config.Bind("Later", "Color", new UnityEngine.Color(1, 1, 1, 1), "");
        Assert.Equal(1f, color.Value.r); Assert.Equal(0f, color.Value.g); Assert.Equal(128f / 255f, color.Value.a, 5);
        var speed = config.Bind("Later", "Speed", 2.5f, "");
        Assert.Equal(2.5f, speed.Value); // "fast" does not parse: logged and ignored
        Assert.Contains(log, line => line.Contains("Config value of setting \"Later.Speed\" could not be parsed and will be ignored."));
        config.Save();
        Assert.Contains("Color = FF000080", Lines(config.FileText)); Assert.Contains("Name = paved\\nway", Lines(config.FileText));
        config.FileText = null;
        Assert.Throws<FileNotFoundException>(() => config.Reload());
    }

    [Fact] public void AValueAlreadyInTheFileIsUsedWhenTheSettingIsBound()
    {
        ConfigFile.WriteFile(Path("existing.cfg"), "[General]\nMode = everything\nKept = yes\n");
        var config = new ConfigFile(Path("existing.cfg"), false);
        Assert.Equal(Mode.Everything, config.Bind("General", "Mode", Mode.Off, "").Value); // enums read case-insensitively
        Assert.Contains("Kept = yes", Lines(config.FileText)); // an unbound value is written back
        Assert.True(config.TryGetEntry<Mode>("General", "Mode", out var mode)); Assert.Equal(Mode.Everything, mode.Value);
        Assert.False(config.TryGetEntry<Mode>("General", "Kept", out _));
        Assert.Equal(1, config.Count); Assert.Same(mode, config["General", "Mode"]);
    }

    [Fact] public void DefinitionsRefuseWhatBepInExRefusesAndValuesConvertAsItDoes()
    {
        Assert.Throws<ArgumentException>(() => new ConfigDefinition("General", "Bad=Key"));
        Assert.Throws<ArgumentException>(() => new ConfigDefinition(" General", "Key"));
        Assert.Throws<ArgumentException>(() => new ConfigDefinition("General", "Key[1]"));
        Assert.Equal(new ConfigDefinition("A", "B"), new ConfigDefinition("A", "B")); Assert.NotEqual(new ConfigDefinition("A", "B"), new ConfigDefinition("a", "B"));
        Assert.Equal("true", TomlTypeConverter.ConvertToString(true, typeof(bool)));
        Assert.Equal("0.5", TomlTypeConverter.ConvertToString(0.5f, typeof(float)));
        Assert.Equal("say \\\"hi\\\"\\tnow", TomlTypeConverter.ConvertToString("say \"hi\"\tnow", typeof(string)));
        Assert.Equal(@"C:\mods\x", TomlTypeConverter.ConvertToValue<string>(@"C:\mods\x")); // a Windows path is read as it is
        Assert.Equal("a\\b", TomlTypeConverter.ConvertToString("a\\b", typeof(string))); // a backslash is written unescaped
        Assert.Throws<ArgumentException>(() => new ConfigFile(Path("types.cfg"), false).Bind("A", "V", DateTime.Now, "")); // BepInEx has no converter for it
        Assert.Equal(new[] { 1 }, new[] { new AcceptableValueList<int>(1, 2).Clamp(3) }.Cast<int>());
    }
}
