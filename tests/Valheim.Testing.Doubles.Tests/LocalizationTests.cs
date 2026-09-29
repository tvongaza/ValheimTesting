using System;
using System.Collections.Generic;
using Valheim.Testing.Doubles;
using Xunit;

public sealed class LocalizationTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new ValheimWorldScope().AsHost();
    public void Dispose() => _scope.Dispose();

    [Fact] public void WordsAreTranslatedAndAMissingOneIsBracketed()
    {
        var l = Localization.instance;
        l.AddWord("item_coins", "Coins");
        Assert.Equal("5 Coins (stack)", l.Localize("5 $item_coins (stack)"));
        Assert.Equal("[item_ruby]", l.Localize("$item_ruby"));
        l.AddWord("msg_picked", "Picked $1 of $2");
        Assert.Equal("Picked 3 of Coins", l.Localize("$msg_picked", "3", "Coins")); // the placeholders come from the translation
        Assert.Equal("Picked [1]", l.Localize("Picked $1", "3")); // in the text itself, $1 is a word, as in the game
        Assert.Equal("", l.Localize("")); Assert.Equal("English", l.GetSelectedLanguage());
        Assert.Equal("cost []", l.Localize("cost $")); Assert.Equal("$", l.Localize("$")); // a trailing '$' is an empty word; a lone '$' is left
    }

    [Fact] public void ATextLocalizedBeforeItsWordWasAddedKeepsItsBracketsFromTheCache()
    {
        var l = Localization.instance;
        Assert.Equal("[mod_greeting]", l.Localize("$mod_greeting"));
        l.AddWord("mod_greeting", "Hello");
        Assert.Equal("[mod_greeting]", l.Localize("$mod_greeting")); // cached, as in the game
        Assert.Equal("Hello!", l.Localize("$mod_greeting!")); // another text is looked up afresh
        for (int i = 0; i < 100; i++) l.Localize("$filler" + i + " "); // 100 newer texts push it out of the cache
        Assert.Equal("Hello", l.Localize("$mod_greeting"));
    }

    [Fact] public void KeyBindingsExpandAndMissingOnesAreReportedWithoutCaching()
    {
        var l = Localization.instance;
        Assert.Equal("Press MISSING BUTTON DEF \"Use\"", l.Localize("Press $KEY_Use"));
        ZInput.instance.SetBinding("Use", "E");
        Assert.Equal("Press E", l.Localize("Press $KEY_Use")); // the missing-button text was not cached
        ZInput.instance.SetBinding("Block", null);
        Assert.Equal("MISSING KEY BINDING \"Block\"", l.Localize("$KEY_Block"));
        ZInput.instance.SetBinding("JoyUse", "$button_a"); l.AddWord("button_a", "A");
        ZInput.instance.GamepadActive = true;
        Assert.Equal("[A] take", l.Localize("[$KEY_Use] take")); // the gamepad binding first, its $word translated
    }

    [Fact] public void SwitchingLanguageDropsAddedWordsAndTellsListeners()
    {
        var l = Localization.instance;
        l.AddWord("mod_name", "Roads"); int told = 0;
        Localization.OnLanguageChange += () => told++;
        l.SetLanguage("English"); // the saved language is empty, so this is a switch
        l.SetLanguage("English");
        Assert.Equal(1, told); Assert.Equal("English", PlatformPrefs.GetString("language"));
        Assert.Equal("[mod_name]", l.Localize("$mod_name")); // mods add their words again on the change
    }

    [Fact] public void PlatformPrefsKeepTypedValues()
    {
        PlatformPrefs.SetInt("volume", 3); PlatformPrefs.SetString("name", "tester"); PlatformPrefs.SetBool("flag", true);
        Assert.Equal(3, PlatformPrefs.GetInt("volume")); Assert.Equal(0f, PlatformPrefs.GetFloat("volume")); // another type's getter gives the default
        Assert.Equal("tester", PlatformPrefs.GetString("name")); Assert.True(PlatformPrefs.GetBool("flag"));
        Assert.True(PlatformPrefs.HasKey("name")); PlatformPrefs.DeleteKey("name"); Assert.False(PlatformPrefs.HasKey("name"));
    }
}
