using Valheim.Testing.Game;
using Xunit;

// #1009: what a menu-only run's game adds to the client's characters_local and worlds_local goes into the evidence; a file that
// was there before is never moved, and when one changed or went missing nothing is moved at all.
public sealed class SaveFolderChangesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vt-save-folders-").FullName;
    private string Save => Path.Combine(_root, "save");
    private string Evidence => Path.Combine(_root, "evidence");
    public void Dispose() => Directory.Delete(_root, recursive: true);

    public SaveFolderChangesTests() { Directory.CreateDirectory(Save); Directory.CreateDirectory(Evidence); }

    [Fact] public void FoldersTheGameCreatesAreMovedWholeAndOnlyTopLevelFilesAreRead()
    {
        var changes = SaveFolderChanges.Capture(Save, Evidence); // A user with no characters or worlds yet.
        Directory.CreateDirectory(Path.Combine(Save, "characters_local"));
        File.WriteAllText(Path.Combine(Save, "characters_local", "fresh.fch"), "made at the menu");
        Directory.CreateDirectory(Path.Combine(Save, "worlds_local"));
        File.WriteAllText(Path.Combine(Save, "worlds_local", "Fresh.fwl"), "a world a command made");
        Directory.CreateDirectory(Path.Combine(Save, "characters"));
        File.WriteAllText(Path.Combine(Save, "characters", "cloud.fch"), "a Steam Cloud folder is never read");

        Assert.Equal(["characters_local/fresh.fch", "worlds_local/Fresh.fwl"], changes.MoveAdded(Evidence));
        Assert.Equal("made at the menu", File.ReadAllText(Path.Combine(Evidence, SaveFolderChanges.AddedFolder, "characters_local", "fresh.fch")));
        Assert.Equal("a world a command made", File.ReadAllText(Path.Combine(Evidence, SaveFolderChanges.AddedFolder, "worlds_local", "Fresh.fwl")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Save, "characters_local")));
        Assert.True(File.Exists(Path.Combine(Save, "characters", "cloud.fch")));
        Assert.True(File.Exists(Path.Combine(Evidence, SaveFolderChanges.BeforeFile)));
    }

    [Fact] public void AMissingUserFileMovesNothing()
    {
        string local = Directory.CreateDirectory(Path.Combine(Save, "characters_local")).FullName;
        File.WriteAllText(Path.Combine(local, "mine.fch"), "the user's character");
        var changes = SaveFolderChanges.Capture(Save, Evidence);
        File.Delete(Path.Combine(local, "mine.fch"));
        File.WriteAllText(Path.Combine(local, "fresh.fch"), "made at the menu");

        var error = Assert.Throws<InvalidOperationException>(() => changes.MoveAdded(Evidence));
        Assert.Contains("characters_local/mine.fch is missing", error.Message);
        Assert.True(File.Exists(Path.Combine(local, "fresh.fch")));
        Assert.False(Directory.Exists(Path.Combine(Evidence, SaveFolderChanges.AddedFolder)));
    }
}
