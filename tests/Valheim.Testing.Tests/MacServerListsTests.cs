using Valheim.Testing.GameSessions;
using Xunit;

public sealed class MacServerListsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mac-server-lists-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact] public void ExistingListsAreIsolatedAndRestoredWhileRunVersionsAreKept()
    {
        string save = Path.Combine(_root, "save"), backup = Path.Combine(_root, "run", "server-lists");
        Directory.CreateDirectory(save);
        File.WriteAllText(Path.Combine(save, "adminlist.txt"), "user admin\n");
        File.WriteAllText(Path.Combine(save, "permittedlist.txt"), "user permit\n");
        var lists = MacServerLists.Capture(save, backup);
        Assert.Equal("mac-lists-captured", lists.Captured().Kind);
        lists.Isolate();
        Assert.DoesNotContain(MacServerLists.Names, name => File.Exists(Path.Combine(save, name)));
        File.WriteAllText(Path.Combine(save, "adminlist.txt"), "game admin\n");
        File.WriteAllText(Path.Combine(save, "bannedlist.txt"), "game ban\n");
        lists.Restore();
        Assert.Equal("user admin\n", File.ReadAllText(Path.Combine(save, "adminlist.txt")));
        Assert.Equal("user permit\n", File.ReadAllText(Path.Combine(save, "permittedlist.txt")));
        Assert.False(File.Exists(Path.Combine(save, "bannedlist.txt")));
        Assert.Equal("game admin\n", File.ReadAllText(Path.Combine(backup, "after", "adminlist.txt")));
        Assert.Equal("game ban\n", File.ReadAllText(Path.Combine(backup, "after", "bannedlist.txt")));
        lists.Restore(); // recovery after a lost "restored" journal line is harmless
    }

    [Fact] public void ChangedSourceRefusesIsolationAndChangedBackupRefusesRestore()
    {
        string save = Path.Combine(_root, "save"), backup = Path.Combine(_root, "run", "server-lists");
        Directory.CreateDirectory(save);
        string admin = Path.Combine(save, "adminlist.txt");
        File.WriteAllText(admin, "user\n");
        var lists = MacServerLists.Capture(save, backup);
        File.WriteAllText(admin, "changed\n");
        Assert.Throws<IOException>(lists.Isolate);
        Assert.Equal("changed\n", File.ReadAllText(admin));
        File.WriteAllText(Path.Combine(backup, "adminlist.txt"), "bad backup\n");
        Assert.Throws<IOException>(lists.Restore);
        Assert.Equal("changed\n", File.ReadAllText(admin));
    }

    [Fact] public void ACorruptLaterBackupDoesNotPartlyRestoreEarlierLists()
    {
        string save = Path.Combine(_root, "save"), backup = Path.Combine(_root, "run", "server-lists");
        Directory.CreateDirectory(save);
        File.WriteAllText(Path.Combine(save, "adminlist.txt"), "original admin\n");
        File.WriteAllText(Path.Combine(save, "permittedlist.txt"), "original permit\n");
        var lists = MacServerLists.Capture(save, backup);
        lists.Isolate();
        File.WriteAllText(Path.Combine(save, "adminlist.txt"), "game admin\n");
        File.WriteAllText(Path.Combine(backup, "permittedlist.txt"), "corrupt\n");
        Assert.Throws<IOException>(lists.Restore);
        Assert.Equal("game admin\n", File.ReadAllText(Path.Combine(save, "adminlist.txt")));
        Assert.False(File.Exists(Path.Combine(save, "permittedlist.txt")));
    }

    [Fact] public void ASecondRunVersionCannotBeLostOnRecoveryRetry()
    {
        string save = Path.Combine(_root, "save"), backup = Path.Combine(_root, "run", "server-lists");
        Directory.CreateDirectory(save);
        var lists = MacServerLists.Capture(save, backup);
        lists.Isolate();
        string current = Path.Combine(save, "adminlist.txt");
        File.WriteAllText(current, "first run version\n");
        lists.Restore();
        File.WriteAllText(current, "second run version\n");
        Assert.Throws<IOException>(lists.Restore);
        Assert.Equal("second run version\n", File.ReadAllText(current));
        Assert.Equal("first run version\n", File.ReadAllText(Path.Combine(backup, "after", "adminlist.txt")));
    }
}
