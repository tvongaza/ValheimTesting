// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// Localization as the game (1.0.16) translates: $words, $KEY_<binding> through ZInput, $1.. insertion and the
// 100-entry cache; ZInput's key-binding strings; and PlatformPrefs, which can be made unavailable, as it is while
// plugins load on a dedicated server.
using System;
using System.Collections.Generic;

/// <summary>
/// The game's translator. <see cref="Localize(string)"/> replaces each <c>$word</c> (up to a space or one of
/// <c>(){}[]+-!?/\&amp;%,.:=&lt;&gt;</c> or a newline) with its translation, or <c>[word]</c> when there is none;
/// <c>$KEY_Use</c> becomes the key bound to "Use" (<see cref="ZInput"/>, the "Joy" binding first while a gamepad is
/// active). Results are cached (100 entries, least recently used first out) except those that are empty or report a
/// missing key or button, and <see cref="AddWord"/> does not clear the cache, as in the game: a text localized before its
/// word was added keeps its "[word]". <see cref="instance"/> is made on first use and reads the saved language from
/// <see cref="PlatformPrefs"/>, so it throws where PlatformPrefs does. There are no language files: translations are the
/// words added.
/// </summary>
public partial class Localization
{
    private static Localization? m_instance;
    public static Action? OnLanguageChange;
    private static readonly char[] s_endChars = " (){}[]+-!?/\\\\&%,.:-=<>\n".ToCharArray();
    private readonly Dictionary<string, string> m_translations = new();
    private readonly Dictionary<string, LinkedListNode<(string Text, string Translated)>> m_cache = new();
    private readonly LinkedList<(string Text, string Translated)> m_recent = new();
    private const int CacheSize = 100;
    private string m_language;

    public static Localization instance => m_instance ??= new Localization();
    /// <summary>The instance without making one; <c>ValheimWorldScope</c> saves and restores it.</summary>
    internal static Localization? Current { get => m_instance; set => m_instance = value; }

    private Localization()
    {
        m_language = "English";
        string saved = PlatformPrefs.GetString("language");
        if (!string.IsNullOrEmpty(saved)) m_language = saved;
    }

    public string GetSelectedLanguage() => m_language;
    /// <summary>Switches language as the game does: the saved language changes, every translation (added words included) and the cache are dropped, and <see cref="OnLanguageChange"/> runs.</summary>
    public void SetLanguage(string language)
    {
        if (PlatformPrefs.GetString("language") == language) return;
        PlatformPrefs.SetString("language", language);
        Clear();
        m_language = language;
        OnLanguageChange?.Invoke();
    }

    /// <summary>Adds or replaces a translation (private in the game; mods reach it through publicized assemblies or Jotunn).</summary>
    public void AddWord(string key, string text) { m_translations.Remove(key); m_translations.Add(key, text); }
    private void Clear() { m_translations.Clear(); m_cache.Clear(); m_recent.Clear(); }

    /// <summary>Localizes, then puts <paramref name="words"/> in for $1, $2, ...</summary>
    public string Localize(string text, params string[] words)
    {
        string result = Localize(text);
        for (int i = 0; i < words.Length; i++) result = result.Replace("$" + (i + 1), words[i]);
        return result;
    }

    public string Localize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (m_cache.TryGetValue(text, out var hit)) { m_recent.Remove(hit); m_recent.AddFirst(hit); return hit.Value.Translated; }
        var result = new System.Text.StringBuilder();
        int at = 0;
        while (FindNextWord(text, at, out string word, out int wordStart, out int wordEnd))
        {
            result.Append(text, at, wordStart - at);
            result.Append(Translate(word));
            at = wordEnd;
        }
        result.Append(text.Substring(at));
        string translated = result.ToString();
        if (translated == "" || translated.Contains("MISSING KEY") || translated.Contains("MISSING BUTTON")) return translated;
        if (m_cache.Count >= CacheSize) { var oldest = m_recent.Last!; m_recent.RemoveLast(); m_cache.Remove(oldest.Value.Text); }
        m_cache[text] = m_recent.AddFirst((text, translated));
        return translated;
    }

    private static bool FindNextWord(string text, int startIndex, out string word, out int wordStart, out int wordEnd)
    {
        word = ""; wordStart = -1; wordEnd = -1;
        if (startIndex >= text.Length - 1) return false;
        wordStart = text.IndexOf('$', startIndex);
        if (wordStart == -1) return false;
        int end = text.IndexOfAny(s_endChars, wordStart);
        if (end != -1) { word = text.Substring(wordStart + 1, end - wordStart - 1); wordEnd = end; }
        else { word = text.Substring(wordStart + 1); wordEnd = text.Length; }
        return true;
    }

    private string Translate(string word)
    {
        if (word.CustomStartsWith("KEY_"))
        {
            string binding = word.Substring(4);
            if (ZInput.IsGamepadActive())
            {
                string joy = GetBoundKeyString("Joy" + binding, emptyStringOnMissing: true);
                if (joy.Length > 0) return joy;
            }
            return GetBoundKeyString(binding);
        }
        return m_translations.TryGetValue(word, out var value) ? value : "[" + word + "]";
    }

    /// <summary>The key bound to a button; a key string that is itself a <c>$word</c> is translated.</summary>
    public string GetBoundKeyString(string bindingName, bool emptyStringOnMissing = false)
    {
        string key = ZInput.instance.GetBoundKeyString(bindingName, emptyStringOnMissing);
        return key.Length > 0 && key[0] == '$' && m_translations.TryGetValue(key.Substring(1), out var value) ? value : key;
    }
}

/// <summary>
/// The game's input bindings, as text: a test declares a button and the key shown for it (<see cref="SetBinding"/>). An
/// unknown button reads "MISSING BUTTON DEF", a button with no key "MISSING KEY BINDING", as in the game (or an empty
/// string when asked to). <see cref="GamepadActive"/> stands for a gamepad being in use.
/// </summary>
public partial class ZInput
{
    private static ZInput? m_instance;
    public static ZInput instance => m_instance ??= new ZInput();
    internal static ZInput? Current { get => m_instance; set => m_instance = value; }
    private readonly Dictionary<string, string?> m_buttons = new();
    /// <summary>Whether a gamepad is the active input. A test switch.</summary>
    public bool GamepadActive;

    /// <summary>Declares a button and the key text shown for it; null or empty declares it unbound.</summary>
    public void SetBinding(string name, string? key) => m_buttons[name] = key;
    public string GetBoundKeyString(string name, bool emptyStringOnMissing = false)
    {
        if (!m_buttons.TryGetValue(name, out var key)) return emptyStringOnMissing ? "" : "MISSING BUTTON DEF \"" + name + "\"";
        if (string.IsNullOrEmpty(key)) return emptyStringOnMissing ? "" : "MISSING KEY BINDING \"" + name + "\"";
        return key!;
    }
    public static bool IsGamepadActive() => m_instance?.GamepadActive ?? false;
}

/// <summary>
/// The game's saved preferences, in memory. While <see cref="Unavailable"/> is set every call throws
/// <see cref="InvalidOperationException"/> with that reason: <c>ValheimWorldScope.AsDedicatedServer</c> sets it while
/// plugins load, because on a dedicated server the platform (Steamworks) is not initialized yet. A value read with another
/// type's getter gives the default, as PlayerPrefs does.
/// </summary>
public static partial class PlatformPrefs
{
    internal static Dictionary<string, object> s_values = new();
    /// <summary>Why the preferences cannot be used right now, or null when they can.</summary>
    public static string? Unavailable;

    private static void Check(string name)
    {
        if (Unavailable != null) throw new InvalidOperationException($"PlatformPrefs was used for \"{name}\" before the platform was initialized: {Unavailable}");
    }
    private static T Get<T>(string name, T defaultValue) { Check(name); return s_values.TryGetValue(name, out var value) && value is T typed ? typed : defaultValue; }
    private static void Set(string name, object value) { Check(name); s_values[name] = value; }

    public const string c_PreferencesPath = "Preferences";
    public static float GetFloat(string name, float defaultValue = 0f) => Get(name, defaultValue);
    public static void SetFloat(string name, float value) => Set(name, value);
    public static int GetInt(string name, int defaultValue = 0) => Get(name, defaultValue);
    public static void SetInt(string name, int value) => Set(name, value);
    public static string GetString(string name, string defaultValue = "") => Get(name, defaultValue);
    public static void SetString(string name, string value) => Set(name, value);
    public static bool GetBool(string name, bool defaultValue = false) => GetInt(name, defaultValue ? 1 : 0) == 1;
    public static void SetBool(string name, bool value) => SetInt(name, value ? 1 : 0);
    public static bool HasKey(string name) { Check(name); return s_values.ContainsKey(name); }
    public static void DeleteKey(string name) { Check(name); s_values.Remove(name); }
    public static void DeleteAll() { Check("*"); s_values.Clear(); }
    public static void Save() => Check("*");
}
