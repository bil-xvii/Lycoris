using System.IO;
using Lycoris.Core;

namespace Lycoris.Tests.Core;

public sealed class AppPathsMigrationTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lycoris-path-migration-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Missing_destination_moves_legacy_directory()
    {
        var oldPath = Path.Combine(_root, "old");
        var newPath = Path.Combine(_root, "new");
        Directory.CreateDirectory(oldPath);
        File.WriteAllText(Path.Combine(oldPath, "journal.json"), "safe test data");

        AppPaths.MigrateDirectory(oldPath, newPath);

        Assert.False(Directory.Exists(oldPath));
        Assert.Equal("safe test data", File.ReadAllText(Path.Combine(newPath, "journal.json")));
        Directory.Delete(_root, true);
    }

    [Fact]
    public void Existing_destination_has_priority_and_legacy_is_untouched()
    {
        var oldPath = Path.Combine(_root, "old");
        var newPath = Path.Combine(_root, "new");
        Directory.CreateDirectory(oldPath);
        Directory.CreateDirectory(newPath);
        File.WriteAllText(Path.Combine(oldPath, "state"), "old");
        File.WriteAllText(Path.Combine(newPath, "state"), "new");

        AppPaths.MigrateDirectory(oldPath, newPath);

        Assert.Equal(newPath, AppPaths.ReadDirectory(newPath, oldPath));
        Assert.Equal("old", File.ReadAllText(Path.Combine(oldPath, "state")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(newPath, "state")));
        Directory.Delete(_root, true);
    }

    [Fact]
    public void Legacy_reparse_directory_is_refused()
    {
        var target = Path.Combine(_root, "target");
        var link = Path.Combine(_root, "old-link");
        Directory.CreateDirectory(target);
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Directory.Delete(_root, true);
            return; // Symlink creation is unavailable on this test host.
        }

        Assert.Throws<IOException>(() => AppPaths.MigrateDirectory(link, Path.Combine(_root, "new")));
        Assert.True(Directory.Exists(link));
        Directory.Delete(link);
        Directory.Delete(_root, true);
    }

    [Fact]
    public void Read_only_selection_uses_legacy_directory_when_new_is_missing()
    {
        var oldPath = Path.Combine(_root, "old");
        var newPath = Path.Combine(_root, "new");
        Directory.CreateDirectory(oldPath);
        File.WriteAllText(Path.Combine(oldPath, "journal.json"), "legacy");

        Assert.Equal(oldPath, AppPaths.ReadDirectory(newPath, oldPath));
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(AppPaths.ReadDirectory(newPath, oldPath), "journal.json")));
        Assert.False(Directory.Exists(newPath));
        Directory.Delete(_root, true);
    }

    [Fact]
    public void Journal_path_from_legacy_machine_folder_maps_to_moved_backup()
    {
        var oldPath = Path.Combine(_root, "old");
        var newPath = Path.Combine(_root, "new");
        Directory.CreateDirectory(Path.Combine(newPath, "Backups"));
        File.WriteAllText(Path.Combine(newPath, "Backups", "state.reg"), "backup");
        var oldBackup = Path.Combine(oldPath, "Backups", "state.reg");

        Assert.Equal(Path.Combine(newPath, "Backups", "state.reg"), AppPaths.MapLegacyMachinePath(oldBackup, oldPath, newPath));
        Assert.Equal(oldBackup, AppPaths.MapLegacyMachinePath(oldBackup, oldPath, Path.Combine(_root, "missing")));
        Directory.Delete(_root, true);
    }
}
