using System.IO;
using System.Net.Http;
using Monolith.Core;
using Monolith.Core.Updates;
using Monolith.Modules.Settings;
using Monolith.Shell;

namespace Monolith.Tests.Updates;

public sealed class UpdateUiStateTests
{
    private static (UpdateController Controller, UpdateService Service, FakeStore Store) Make(string tag = "v1.0.1")
    {
        var store = new FakeStore { CachedRelease = ReleaseParser.Serialize(Kit.Release(tag)) };
        var service = new UpdateService(new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("no network"))),
            store, () => new SemVer(1, 0, 0, 0, ""), () => new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc));
        return (new UpdateController(service), service, store);
    }

    [Fact]
    public void The_button_says_the_version_and_is_shown_only_when_clickable_state_applies()
    {
        var (c, s, _) = Make();
        Assert.True(c.ButtonVisible);
        Assert.Equal("ОБНОВЛЕНИЕ 1.0.1", c.ButtonText);
        Assert.Contains("1.0.1", c.VersionLine);
        Assert.True(c.CanStart);

        c.SkipVersionCommand.Execute(null);
        Assert.False(c.ButtonVisible);
        Assert.True(c.HasUpdate);   // still listed in Settings → О программе
        Assert.True(c.IsSkipped);
        Assert.NotNull(s.Available);
    }

    [Fact]
    public void Remind_later_hides_the_button_but_keeps_the_settings_block()
    {
        var (c, _, store) = Make();
        c.RemindLaterCommand.Execute(null);
        Assert.False(c.ButtonVisible);
        Assert.True(c.HasUpdate);
        Assert.NotNull(store.SnoozedUntilUtc);
    }

    [Fact]
    public void The_button_and_lines_are_english_in_the_english_interface()
    {
        var (c, _, _) = Make();
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("UPDATE 1.0.1", c.ButtonText);
        Assert.StartsWith("INSTALLED " + AppVersion.CurrentText, c.VersionLine);
        Assert.DoesNotMatch("[а-яА-Я]", c.VersionLine + c.StatusText);
    }

    [Fact]
    public void Demo_mode_shows_an_update_without_a_service_answer_and_cannot_install()
    {
        var store = new FakeStore();
        var c = new UpdateController(new UpdateService(new HttpClient(new FakeHandler(_ => throw new InvalidOperationException())), store, () => new SemVer(1, 0, 0, 0, ""), () => DateTime.UtcNow));
        Assert.False(c.ButtonVisible);
        c.EnableDemo();
        Assert.True(c.ButtonVisible);
        Assert.NotNull(c.Notes);
        Assert.Equal("", store.CachedRelease); // nothing was stored
    }

    [Fact]
    public void Settings_reset_turns_automatic_checks_back_on()
    {
        var s = new AppSettings { AutoCheckUpdates = false };
        SettingsDefaults.Apply(s);
        Assert.True(s.AutoCheckUpdates);
        Assert.True(new AppSettings().AutoCheckUpdates);
        Assert.Contains(SettingsDefaults.Describe(), l => l.Contains("обновлений"));
    }

    [Fact]
    public void The_settings_store_roundtrips_times_in_utc()
    {
        var old = AppSettings.Current;
        try
        {
            // A private instance: nothing subscribed to its changes, so nothing is written to the real settings file.
            typeof(AppSettings).GetProperty(nameof(AppSettings.Current))!.SetValue(null, new AppSettings());
            var store = new SettingsUpdateStore();
            var t = new DateTime(2026, 11, 2, 12, 30, 0, DateTimeKind.Utc);
            store.LastCheckUtc = t;
            store.SnoozedUntilUtc = t.AddDays(1);
            Assert.Equal(t, store.LastCheckUtc);
            Assert.Equal(t.AddDays(1), store.SnoozedUntilUtc);
            store.LastCheckUtc = null;
            Assert.Null(store.LastCheckUtc);
            Assert.Null(SettingsUpdateStore.ParseUtc("garbage"));
        }
        finally
        {
            typeof(AppSettings).GetProperty(nameof(AppSettings.Current))!.SetValue(null, old);
        }
    }

    [Fact]
    public void The_updates_area_is_translated_and_every_visible_label_has_an_english_text()
    {
        Loc.LoadEmbeddedAreas();
        Assert.Contains(Loc.Areas, a => a.Name == "Updates" && a.Translated);
        using var _ = Loc.UseLanguage("en");
        foreach (var ru in new[]
        {
            "ОБНОВИТЬ СЕЙЧАС", "ПРОВЕРИТЬ ОБНОВЛЕНИЯ", "НАПОМНИТЬ ПОЗЖЕ", "ПРОПУСТИТЬ ЭТУ ВЕРСИЮ", "Проверять обновления автоматически",
            "ОБНОВЛЕНИЕ {0}", "ОБНОВИТЬ LYCORIS?", "СКАЧАТЬ И ОБНОВИТЬ",
        })
            Assert.DoesNotMatch("[а-яА-Я]", Loc.T(ru));
    }
}
