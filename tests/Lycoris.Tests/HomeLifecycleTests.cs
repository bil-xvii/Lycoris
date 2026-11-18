using System.Reflection;
using Lycoris.Core;
using Lycoris.Core.Security;
using Lycoris.Modules.Dashboard;
using Lycoris.Modules.Home;
using Lycoris.Shell;

namespace Lycoris.Tests;

[CollectionDefinition("HomeLifecycle", DisableParallelization = true)]
public sealed class HomeLifecycleCollection { }

[Collection("HomeLifecycle")]
public sealed class HomeLifecycleTests
{
    [Fact]
    public async Task Returning_home_resumes_updates_once_and_leaving_stops_them()
    {
        using var owner = ExpertAccessPolicy.OverrideFingerprintForTests(() => ExpertAccessPolicy.OwnerFingerprint);
        using var language = Loc.UseLanguage("ru");
        var previousSettings = AppSettings.Current;
        var settingsProperty = typeof(AppSettings).GetProperty(nameof(AppSettings.Current))!;
        var settings = new AppSettings();
        settingsProperty.SetValue(null, settings);
        HomeViewModel? home = null;
        try
        {
            var dashboard = new DashboardViewModel { HasResults = true, Score = 95 };
            home = new HomeViewModel(dashboard, null, action => action());
            // Start with a cached first load: navigation is exercised without running hardware checks or WMI.
            typeof(PageViewModel).GetField("_loadTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(home, Task.CompletedTask);
            await home.ActivateAsync();
            Assert.Equal("95 / 100", home.ScoreText);

            home.Leave();
            await home.ActivateAsync();
            var changed = new List<string?>();
            home.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            dashboard.Score = 72;
            dashboard.SafeFixCount = 2;
            dashboard.LastCheckText = "Проверено сейчас";
            Assert.Equal("72 / 100", home.ScoreText);
            Assert.Equal("Проверено сейчас", home.DoctorChecked);
            Assert.True(home.DoctorHasData);
            Assert.True(home.HasSafeFixes);
            Assert.Equal(dashboard.FixSafeText, home.FixSafeText);
            Assert.Contains(nameof(HomeViewModel.HasSafeFixes), changed);
            Assert.Contains(nameof(HomeViewModel.FixSafeText), changed);

            settings.ExpertMode = true;
            Assert.True(home.ExpertMode);
            settings.FirstRunCompleted = true;
            Assert.Contains(nameof(HomeViewModel.ShowWizardPrompt), changed);

            // Repeated activation while already visible must not add another event listener.
            await home.ActivateAsync();
            await home.ActivateAsync();
            changed.Clear();
            dashboard.Score = 61;
            Assert.Equal("61 / 100", home.ScoreText);
            Assert.Single(changed, p => p == nameof(HomeViewModel.HasSafeFixes));
            Assert.Single(changed, p => p == nameof(HomeViewModel.FixSafeText));

            home.Leave();
            changed.Clear();
            dashboard.Score = 40;
            settings.ExpertMode = false;
            settings.FirstRunCompleted = false;
            Assert.Equal("61 / 100", home.ScoreText);
            Assert.True(home.ExpertMode);
            Assert.Empty(changed);

            await home.ActivateAsync();
            Assert.Equal("40 / 100", home.ScoreText);
            Assert.False(home.ExpertMode);
            Assert.Contains(nameof(HomeViewModel.ShowWizardPrompt), changed);
            changed.Clear();
            dashboard.Score = 39;
            Assert.Single(changed, p => p == nameof(HomeViewModel.HasSafeFixes));
        }
        finally
        {
            home?.Deactivate();
            settingsProperty.SetValue(null, previousSettings);
        }
    }
}
