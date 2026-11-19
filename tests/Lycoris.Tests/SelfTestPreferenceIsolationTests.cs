using System.IO;
using Lycoris.Core;
using Lycoris.Modules.Optimize;

namespace Lycoris.Tests;

[CollectionDefinition("SelfTestPreferenceIsolation", DisableParallelization = true)]
public sealed class SelfTestPreferenceIsolationCollection { }

[Collection("SelfTestPreferenceIsolation")]
public sealed class SelfTestPreferenceIsolationTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("lycoris-preferences-");
    private readonly bool _priorSaveSuspended = AppSettings.SaveSuspended;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Suspended_custom_selection_does_not_create_or_replace_saved_preferences(bool hasSavedSelection)
    {
        var folder = Path.Combine(_temp.FullName, "preferences");
        var path = Path.Combine(folder, "optimize-custom.json");
        if (hasSavedSelection)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, "[\"game-mode\"]");
        }

        AppSettings.SaveSuspended = true;
        CustomSelectionStore.Save(["hags"], path);

        Assert.Equal(hasSavedSelection, Directory.Exists(folder));
        Assert.Equal(hasSavedSelection, File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
        if (hasSavedSelection) Assert.Equal("[\"game-mode\"]", File.ReadAllText(path));
    }

    [Fact]
    public void Ordinary_custom_selection_saves_and_replaces_preferences()
    {
        var path = Path.Combine(_temp.FullName, "preferences", "optimize-custom.json");
        AppSettings.SaveSuspended = false;

        CustomSelectionStore.Save(["game-mode"], path);
        Assert.Equal("[\"game-mode\"]", File.ReadAllText(path));

        CustomSelectionStore.Save(["hags", "gamedvr-off", "hags"], path);
        Assert.Equal("[\"gamedvr-off\",\"hags\"]", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Suspended_invalid_settings_backup_does_not_create_or_replace_a_copy(bool hasBackup)
    {
        var path = Path.Combine(_temp.FullName, "settings.json");
        var backup = path + ".bad";
        File.WriteAllText(path, "{invalid");
        if (hasBackup) File.WriteAllText(backup, "previous backup");

        AppSettings.SaveSuspended = true;
        AppSettings.BackupInvalidSettings(path);

        Assert.Equal("{invalid", File.ReadAllText(path));
        Assert.Equal(hasBackup, File.Exists(backup));
        if (hasBackup) Assert.Equal("previous backup", File.ReadAllText(backup));
    }

    [Fact]
    public void Ordinary_invalid_settings_backup_preserves_the_original()
    {
        var path = Path.Combine(_temp.FullName, "settings.json");
        File.WriteAllText(path, "{invalid");
        File.WriteAllText(path + ".bad", "previous backup");
        AppSettings.SaveSuspended = false;

        AppSettings.BackupInvalidSettings(path);

        Assert.Equal("{invalid", File.ReadAllText(path));
        Assert.Equal("{invalid", File.ReadAllText(path + ".bad"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Suspended_legacy_migration_leaves_both_directories_untouched(bool hasCurrentDirectory)
    {
        var legacy = Path.Combine(_temp.FullName, "legacy");
        var current = Path.Combine(_temp.FullName, "current");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "legacy settings");
        if (hasCurrentDirectory) Directory.CreateDirectory(current);

        AppSettings.SaveSuspended = true;
        AppPaths.MigrateDirectory(legacy, current);

        Assert.Equal("legacy settings", File.ReadAllText(Path.Combine(legacy, "settings.json")));
        Assert.Equal(hasCurrentDirectory, Directory.Exists(current));
        if (hasCurrentDirectory) Assert.Empty(Directory.EnumerateFileSystemEntries(current));
        Assert.Equal(hasCurrentDirectory ? current : legacy, AppPaths.ReadDirectory(current, legacy));
    }

    public void Dispose()
    {
        AppSettings.SaveSuspended = _priorSaveSuspended;
        _temp.Delete(recursive: true);
    }
}
