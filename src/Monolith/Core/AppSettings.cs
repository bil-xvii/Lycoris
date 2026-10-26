using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Monolith.Core;

/// <summary>User preferences, stored in %LocalAppData%\MONOLITH\settings.json.</summary>
public sealed partial class AppSettings : ObservableObject
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Current { get; private set; } = new();

    /// <summary>Create a System Restore point before every batch of changes.</summary>
    [ObservableProperty] private bool _createRestorePoints = true;

    /// <summary>Show a confirmation with the list of changes before applying them.</summary>
    [ObservableProperty] private bool _confirmBeforeApply = true;

    /// <summary>
    /// Expert mode of the ВСЕ СЛУЖБЫ tab: allows changing services outside MONOLITH's curated allowlist
    /// (non-curated Microsoft and third-party services). Off by default — research services.md §0.4/§2B.
    /// </summary>
    [ObservableProperty] private bool _servicesExpertMode;

    /// <summary>Show the operations console at the bottom of the window (collapsed by default to leave room for content).</summary>
    [ObservableProperty] private bool _showConsole;

    /// <summary>Dashboard refresh interval in milliseconds.</summary>
    [ObservableProperty] private int _monitorIntervalMs = 1000;

    /// <summary>Last chosen optimization profile.</summary>
    [ObservableProperty] private string _lastProfile = "Recommended";

    /// <summary>Processes that Game Boost may pause/close (user-chosen, by process name).</summary>
    [ObservableProperty] private List<string> _gameBoostCloseList = [];

    /// <summary>Game Boost: switch to the high-performance power plan while a game runs.</summary>
    [ObservableProperty] private bool _gameBoostPowerPlan = true;

    /// <summary>Game Boost: raise the game's process priority to High.</summary>
    [ObservableProperty] private bool _gameBoostPriority = true;

    /// <summary>Game Boost: clear the memory standby list when a session starts.</summary>
    [ObservableProperty] private bool _gameBoostPurgeStandby;

    /// <summary>Extra folders with games the user added manually.</summary>
    [ObservableProperty] private List<string> _customGameFolders = [];

    [ObservableProperty] private bool _firstRunCompleted;

    public static void Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Настройки повреждены, используются значения по умолчанию: {ex.Message}");
            Current = new AppSettings();
        }
        Current.PropertyChanged += (_, _) => Current.Save();
    }

    public void Save()
    {
        try
        {
            if (!AppPaths.TryEnsureDir(AppPaths.UserDir)) return;
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось сохранить настройки: {ex.Message}");
        }
    }
}
