using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Lycoris.Core;

public enum UserGoal { Recommended, Games, Work, LaptopBattery, Quiet }

/// <summary>User preferences, stored in %LocalAppData%\Lycoris\settings.json.</summary>
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
    /// Expert mode of the ВСЕ СЛУЖБЫ tab: allows changing services outside Lycoris's curated allowlist
    /// (non-curated Microsoft and third-party services). Off by default — research services.md §0.4/§2B.
    /// </summary>
    [ObservableProperty] private bool _servicesExpertMode;

    /// <summary>Global detail mode; simple mode is the default for new installations.</summary>
    [ObservableProperty] private bool _expertMode;

    [ObservableProperty] private UserGoal _userGoal = UserGoal.Recommended;

    /// <summary>Show the operations console at the bottom of the window (collapsed by default to leave room for content).</summary>
    [ObservableProperty] private bool _showConsole;

    /// <summary>Dashboard refresh interval in milliseconds.</summary>
    [ObservableProperty] private int _monitorIntervalMs = 1000;

    /// <summary>Last chosen optimization profile.</summary>
    [ObservableProperty] private string _lastProfile = "Recommended";

    /// <summary>Processes that Game Boost may pause/close (user-chosen, by process name).</summary>
    [ObservableProperty] private List<string> _gameBoostCloseList = [];

    /// <summary>
    /// What Game Boost does with the apps of <see cref="GameBoostCloseList"/>: "Close" (default) closes them; "EcoQos"
    /// (Windows 11 only) puts them into efficiency mode (EcoQoS, like Task Manager's «Режим эффективности» without the priority
    /// drop) for the session and restores each process's original state afterwards. Stored as a string so an unknown value
    /// from a newer build falls back to closing instead of discarding the whole settings file.
    /// </summary>
    [ObservableProperty] private string _gameBoostCloseMode = CloseModeClose;

    public const string CloseModeClose = "Close";
    public const string CloseModeEcoQos = "EcoQos";

    /// <summary>True when the close list is handled with efficiency mode instead of closing (the caller still checks Windows 11).</summary>
    [JsonIgnore]
    public bool GameBoostUsesEcoQos => string.Equals(GameBoostCloseMode, CloseModeEcoQos, StringComparison.OrdinalIgnoreCase);

    /// <summary>Game Boost: switch to the high-performance power plan while a game runs.</summary>
    [ObservableProperty] private bool _gameBoostPowerPlan = true;

    /// <summary>Game Boost: raise the game's process priority to High.</summary>
    [ObservableProperty] private bool _gameBoostPriority = true;

    /// <summary>Game Boost: clear the memory standby list when a session starts.</summary>
    [ObservableProperty] private bool _gameBoostPurgeStandby;

    /// <summary>Extra folders with games the user added manually.</summary>
    [ObservableProperty] private List<string> _customGameFolders = [];

    [ObservableProperty] private bool _firstRunCompleted;
    [ObservableProperty] private bool _firstRunWizardInProgress;

    /// <summary>
    /// Ask for admin rights (UAC) right at startup, as before. Off by default (owner: «чтобы приложение открывалось
    /// сразу»): Lycoris opens instantly in read-only mode and asks for rights the first time a change needs them,
    /// then reopens on the same page.
    /// </summary>
    [ObservableProperty] private bool _elevateAtStartup;

    /// <summary>Windows build ("26100.2314") seen at the last start — drift notice names a build change.</summary>
    [ObservableProperty] private string _lastWindowsBuild = "";

    /// <summary>
    /// Drift the user accepted («ЗАБЫТЬ» on the Restore page): "recordId|live value". Not reported again until the
    /// value changes once more. Journal records stay untouched (still revertable to the original).
    /// </summary>
    [ObservableProperty] private List<string> _driftAccepted = [];

    /// <summary>The last tab of «Устройства и сеть»: "auto" (АВТОНАСТРОЙКА, default) or "manual" (ВРУЧНУЮ).</summary>
    [ObservableProperty] private string _devicesTab = "auto";

    /// <summary>
    /// Interface language: "auto" (default: the Windows display language, Russian → ru, anything else → en), "ru" or "en".
    /// Read once at startup (<c>Loc.Initialize</c>); a change takes effect after Lycoris restarts.
    /// </summary>
    [ObservableProperty] private string _language = "auto";

    // ───────── Regular maintenance (roadmap item 7) ─────────

    /// <summary>Show «ПОРА ОБСЛУЖИТЬ ПК» in the checks when the last cleanup is older than <see cref="MaintenanceEveryDays"/>. Default on.</summary>
    [ObservableProperty] private bool _maintenanceReminders = true;

    /// <summary>UTC time (ISO 8601) of the last finished cleanup, or of the first run (the baseline); "" = not set yet.</summary>
    [ObservableProperty] private string _lastMaintenanceUtc = "";

    public const int MaintenanceEveryDays = 14;

    // ───────── Updates (Core/Updates) ─────────

    /// <summary>Look for a new Lycoris version in the background (at most every 12 hours). Default on.</summary>
    [ObservableProperty] private bool _autoCheckUpdates = true;

    /// <summary>UTC time (ISO 8601) of the last successful check against the public releases page; "" = never.</summary>
    [ObservableProperty] private string _updateLastCheckUtc = "";

    /// <summary>ETag of the last release answer (If-None-Match saves GitHub's anonymous request quota).</summary>
    [ObservableProperty] private string _updateETag = "";

    /// <summary>Version the user chose to skip («ПРОПУСТИТЬ ЭТУ ВЕРСИЮ»); a newer one is announced again.</summary>
    [ObservableProperty] private string _updateDismissedVersion = "";

    /// <summary>UTC time (ISO 8601) until which «НАПОМНИТЬ ПОЗЖЕ» hides the title-bar button.</summary>
    [ObservableProperty] private string _updateSnoozedUntilUtc = "";

    /// <summary>The latest release as the last check saw it (JSON), so the button shows at once, also offline.</summary>
    [ObservableProperty] private string _updateCachedRelease = "";

    public static void Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsReadFile))
            {
                // The file is user-writable: bounded read (no links, no huge file), then every value is clamped to what the app could have written.
                var raw = Security.SafeIo.ReadText(AppPaths.SettingsReadFile);
                Current = JsonSerializer.Deserialize<AppSettings>(raw, JsonOptions) ?? new AppSettings();
                using var document = JsonDocument.Parse(raw);
                MigrateLegacyPreferences(Current, document.RootElement.TryGetProperty(nameof(ExpertMode), out _));
                var fixes = Security.SettingsValidator.Sanitize(Current);
                if (fixes > 0) Log.Warn("В настройках исправлено значений: {0} (недопустимые значения заменены безопасными).", fixes);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Настройки повреждены, используются значения по умолчанию: {0}", ex.Message);
            // The next change saves over the file: keep what was there (language, accepted drift, first-run flag) for the user.
            try { if (File.Exists(AppPaths.SettingsFile)) File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".bad", overwrite: true); }
            catch { /* best effort */ }
            Current = new AppSettings();
        }
        Current.PropertyChanged += (_, _) => Current.Save();
    }

    internal static void MigrateLegacyPreferences(AppSettings settings, bool hasExpertMode)
    {
        // Existing users who explicitly enabled the former expert-services option keep expert detail mode.
        if (!hasExpertMode && settings.ServicesExpertMode) settings.ExpertMode = true;
    }

    /// <summary>Every property change saves, from the UI thread and from background tasks (update check): one writer at a time, they share one .tmp file.</summary>
    private static readonly object SaveLock = new();

    public void Save()
    {
        try
        {
            lock (SaveLock)
            {
                if (!AppPaths.TryEnsureDir(AppPaths.UserDir)) return;
                var tmp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
                File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось сохранить настройки: {0}", ex.Message);
        }
    }
}
