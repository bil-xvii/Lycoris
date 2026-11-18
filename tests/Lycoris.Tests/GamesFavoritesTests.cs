using System.Reflection;
using System.Text.Json;
using Lycoris.Core;
using Lycoris.Core.Security;
using Lycoris.Modules.Games;

namespace Lycoris.Tests;

[CollectionDefinition("GamesFavorites", DisableParallelization = true)]
public sealed class GamesFavoritesCollection { }

[Collection("GamesFavorites")]
public sealed class GamesFavoritesTests : IDisposable
{
    private readonly AppSettings _previousSettings = AppSettings.Current;
    private readonly string? _previousTestRoot = Environment.GetEnvironmentVariable("LYCORIS_TEST_ROOT");
    private static readonly PropertyInfo CurrentSettings = typeof(AppSettings).GetProperty(nameof(AppSettings.Current))!;
    private readonly AppSettings _settings = new(); // Never Load(): no autosave subscription or real settings writes.

    public GamesFavoritesTests()
    {
        Environment.SetEnvironmentVariable("LYCORIS_TEST_ROOT", System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "Lycoris-Favorites-Tests", Guid.NewGuid().ToString("N")));
        CurrentSettings.SetValue(null, _settings);
    }

    public void Dispose()
    {
        CurrentSettings.SetValue(null, _previousSettings);
        Environment.SetEnvironmentVariable("LYCORIS_TEST_ROOT", _previousTestRoot);
    }

    [Fact]
    public void Preferences_from_older_versions_default_to_empty_and_new_favorites_roundtrip()
    {
        var old = JsonSerializer.Deserialize<AppSettings>("{\"CustomGameFolders\":[\"C:\\\\Games\"]}")!;
        Assert.Empty(old.FavoriteGameIds);
        Assert.Equal(0, SettingsValidator.Sanitize(old));

        _settings.FavoriteGameIds = ["steam:570", @"custom:D:\Games\Game.exe"];
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings))!;
        Assert.Equal(_settings.FavoriteGameIds, restored.FavoriteGameIds);
        Assert.Equal(0, SettingsValidator.Sanitize(restored));
    }

    [Fact]
    public void Favorites_drop_null_empty_control_overlong_and_duplicate_ids_but_keep_absent_games()
    {
        var nullList = JsonSerializer.Deserialize<AppSettings>("{\"FavoriteGameIds\":null}")!;
        Assert.True(SettingsValidator.Sanitize(nullList) > 0);
        Assert.Empty(nullList.FavoriteGameIds);

        var longestId = new string('x', 700);
        _settings.FavoriteGameIds = [null!, "", "  ", "steam:1\t", "steam:2\r", "steam:3\u007f",
            new string('x', 701), "steam:570", "STEAM:570", "epic:absent", longestId];
        Assert.True(SettingsValidator.Sanitize(_settings) > 0);
        Assert.Equal(["steam:570", "epic:absent", longestId], _settings.FavoriteGameIds);
        Assert.Equal(0, SettingsValidator.Sanitize(_settings));
    }

    [Fact]
    public void Favorites_are_bounded_to_500_unique_ids()
    {
        _settings.FavoriteGameIds = Enumerable.Range(0, 510).Select(i => $"steam:{i}").ToList();
        Assert.Equal(10, SettingsValidator.Sanitize(_settings));
        Assert.Equal(SettingsValidator.MaxListItems, _settings.FavoriteGameIds.Count);
        Assert.Equal("steam:499", _settings.FavoriteGameIds[^1]);
    }

    [Fact]
    public void Toggle_assigns_a_new_settings_list_and_survives_a_new_library_model_and_missing_games()
    {
        _settings.FavoriteGameIds = ["epic:absent"];
        var previousList = _settings.FavoriteGameIds;
        var vm = Model(Game("steam:570", "Dota 2"));
        var changes = 0;
        _settings.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppSettings.FavoriteGameIds)) changes++; };
        var card = Assert.Single(vm.VisibleGames);

        vm.ToggleFavoriteCommand.Execute(card);
        Assert.True(card.IsFavorite);
        Assert.NotSame(previousList, _settings.FavoriteGameIds);
        Assert.Equal(["epic:absent"], previousList);
        Assert.Equal(1, changes); // This is the event used by AppSettings.Load's autosave.

        ApplyScan(vm); // A disconnected disk / launcher must not erase preferences.
        Assert.Equal(["epic:absent", "steam:570"], _settings.FavoriteGameIds);
        var persisted = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings))!;
        CurrentSettings.SetValue(null, persisted);
        var restored = Model(Game("steam:570", "Dota 2") with { IsAvailable = false });
        Assert.True(Assert.Single(restored.VisibleGames).IsFavorite);
        restored.ToggleFavoriteCommand.Execute(Assert.Single(restored.VisibleGames));
        Assert.Equal(["epic:absent"], persisted.FavoriteGameIds);
    }

    [Fact]
    public void Search_platform_and_favorites_combine_and_reset_restores_favorite_first_name_order()
    {
        _settings.FavoriteGameIds = ["steam:z", "epic:m"];
        var vm = Model(Game("steam:z", "Zulu"), Game("steam:a", "Alpha"),
            Game("epic:m", "Moon", GamePlatform.Epic), Game("steam:m", "Moon Siege"));
        Assert.Equal(["Moon", "Zulu", "Alpha", "Moon Siege"], vm.VisibleGames.Select(c => c.Name));

        vm.SearchText = " moon ";
        Assert.Equal(["Moon", "Moon Siege"], vm.VisibleGames.Select(c => c.Name));
        vm.FavoritesOnly = true;
        Assert.Equal("epic:m", Assert.Single(vm.VisibleGames).Entry.Id);
        vm.Platforms.Single(p => p.Platform == GamePlatform.Steam).IsSelected = true;
        Assert.Empty(vm.VisibleGames);
        Assert.True(vm.IsFilterEmpty);
        Assert.False(vm.IsLibraryEmpty);

        vm.ResetFiltersCommand.Execute(null);
        Assert.Equal("", vm.SearchText);
        Assert.False(vm.FavoritesOnly);
        Assert.True(vm.Platforms.Single(p => p.Platform is null).IsSelected);
        Assert.Equal(["Moon", "Zulu", "Alpha", "Moon Siege"], vm.VisibleGames.Select(c => c.Name));
        Assert.False(vm.IsFilterEmpty);
    }

    [Fact]
    public void Settings_changes_sync_cards_and_unfavoriting_removes_the_last_filtered_card()
    {
        var vm = Model(Game("steam:z", "Zulu"), Game("steam:a", "Alpha"));
        var zulu = vm.VisibleGames.Single(c => c.Entry.Id == "steam:z");
        var changed = new List<string?>();
        zulu.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        _settings.FavoriteGameIds = ["STEAM:Z"];
        Assert.True(zulu.IsFavorite);
        Assert.Same(zulu, vm.VisibleGames[0]);
        Assert.Contains(nameof(GameCardViewModel.FavoriteActionText), changed);

        vm.FavoritesOnly = true;
        vm.ToggleFavoriteCommand.Execute(Assert.Single(vm.VisibleGames));
        Assert.Empty(vm.VisibleGames);
        Assert.True(vm.IsFilterEmpty);
        Assert.False(zulu.IsFavorite);
        Assert.Empty(_settings.FavoriteGameIds);
        Assert.True(vm.HasGames);
        vm.ResetFiltersCommand.Execute(null);
        Assert.Equal(["Alpha", "Zulu"], vm.VisibleGames.Select(c => c.Name));
    }

    [Theory]
    [InlineData("ru", "В ИЗБРАННОЕ", "УБРАТЬ ИЗ ИЗБРАННОГО", "ИЗБРАННОЕ", "СБРОСИТЬ ФИЛЬТРЫ")]
    [InlineData("en", "ADD TO FAVORITES", "REMOVE FROM FAVORITES", "FAVORITES", "RESET FILTERS")]
    public void Favorite_actions_filters_and_empty_state_are_translated(string language, string add, string remove, string favorites, string reset)
    {
        using var _ = Loc.UseLanguage(language);
        var card = new GameCardViewModel(Game("steam:570", "Dota 2"));
        Assert.Equal(add, card.FavoriteActionText);
        card.IsFavorite = true;
        Assert.Equal(remove, card.FavoriteActionText);
        Assert.Equal(favorites, Loc.T("ИЗБРАННОЕ"));
        Assert.Equal(reset, Loc.T("СБРОСИТЬ ФИЛЬТРЫ"));
        if (language == "en")
        {
            Assert.Equal("No games match these filters. Change the search, launcher or favorites filter.",
                Loc.T("Нет игр по выбранным фильтрам. Измените запрос, лаунчер или фильтр избранного."));
            Assert.Equal("You already have 500 favorite games. Remove one to add another.",
                Loc.T("В избранном уже 500 игр. Уберите одну, чтобы добавить другую."));
        }
    }

    private static GameEntry Game(string id, string name, GamePlatform platform = GamePlatform.Steam) =>
        new() { Id = id, Name = name, Platform = platform };

    private static GamesViewModel Model(params GameEntry[] games)
    {
        var vm = new GamesViewModel();
        ApplyScan(vm, games);
        return vm;
    }

    private static void ApplyScan(GamesViewModel vm, params GameEntry[] games) =>
        typeof(GamesViewModel).GetMethod("ApplyScan", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [new LibraryScan(games, [], new DateTime(2026, 11, 18, 12, 0, 0))]);
}
