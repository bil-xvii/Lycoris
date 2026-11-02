using System.IO;

namespace Monolith.Modules.Startup;

/// <summary>How careful the user should be with an autostart entry (docs/research/general_tweaks.md §1, "Risk").</summary>
public enum StartupRisk
{
    /// <summary>Ordinary application.</summary>
    Normal,
    /// <summary>Signed by Microsoft: disabling may remove a tray icon or background feature.</summary>
    Microsoft,
    /// <summary>Audio / touchpad / GPU-panel helper of a hardware vendor: disabling may lose functionality.</summary>
    Driver,
    /// <summary>Security agent (Windows Security, antivirus). MONOLITH never disables these.</summary>
    Security,
    /// <summary>Program updater task. MONOLITH never disables these (security updates).</summary>
    Updater,
}

/// <summary>Classifies autostart entries and logon tasks by file name, publisher and task name.</summary>
public static class StartupClassifier
{
    /// <summary>Tray / UI processes of security products, matched by file name.</summary>
    private static readonly HashSet<string> SecurityExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "SecurityHealthSystray.exe", "MSASCuiL.exe", "msseces.exe",          // Windows Security / Defender / MSE
        "avpui.exe", "avp.exe",                                             // Kaspersky
        "egui.exe", "ekrn.exe",                                             // ESET
        "bdagent.exe", "seccenter.exe",                                     // Bitdefender
        "AvastUI.exe", "avgui.exe",                                         // Avast / AVG
        "mcuicnt.exe", "McUICnt.exe",                                       // McAfee
        "NortonUI.exe",                                                     // Norton
        "avgnt.exe", "Avira.Systray.exe",                                   // Avira
        "mbamtray.exe",                                                     // Malwarebytes
        "SophosUI.exe", "uiSeAgnt.exe", "spideragent.exe",                  // Sophos / Trend Micro / Dr.Web
    };

    /// <summary>Companies whose autostart entries are always security agents.</summary>
    private static readonly string[] SecurityVendors =
    [
        "Kaspersky", "ESET", "Bitdefender", "Malwarebytes", "Sophos", "Trend Micro", "Doctor Web", "F-Secure",
        "WithSecure", "Emsisoft", "G DATA", "Avira",
    ];

    /// <summary>Hardware vendors whose tray helpers drive audio, touchpads and GPU panels.</summary>
    private static readonly string[] DriverVendors =
    [
        "Realtek", "NVIDIA", "Advanced Micro Devices", "AMD", "Intel", "Synaptics", "ELAN Microelectronics", "Dolby",
        "Waves Audio", "Conexant", "Cirrus Logic", "Alps", "Qualcomm", "MediaTek", "Creative Technology",
    ];

    private static readonly string[] MicrosoftNames = ["Microsoft", Loc.Keep("Майкрософт")];

    /// <summary>Risk of a Run / Startup-folder entry.</summary>
    public static StartupRisk ClassifyEntry(string? targetPath, string? publisher)
    {
        var file = FileName(targetPath);
        if (file is not null && SecurityExecutables.Contains(file)) return StartupRisk.Security;
        if (MatchesVendor(publisher, SecurityVendors)) return StartupRisk.Security;
        if (MatchesVendor(publisher, DriverVendors)) return StartupRisk.Driver;
        if (MatchesVendor(publisher, MicrosoftNames)) return StartupRisk.Microsoft;
        return StartupRisk.Normal;
    }

    /// <summary>
    /// Risk of a packaged app's startup task. Besides the executable and publisher, the package family / task id
    /// names a security app whose package name is opaque (Microsoft Defender: <c>Microsoft.6365217CE6EB4</c> / <c>microsoftdefender</c>).
    /// </summary>
    public static StartupRisk ClassifyPackagedTask(string? familyName, string? taskId, string? targetPath, string? publisher)
    {
        var risk = ClassifyEntry(targetPath, publisher);
        if (risk == StartupRisk.Security) return risk;
        if (Mentions(familyName, "Defender") || Mentions(taskId, "Defender") || MatchesVendor(familyName?.Replace('.', ' '), SecurityVendors))
            return StartupRisk.Security;
        return risk;

        static bool Mentions(string? s, string word) => s?.Contains(word, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>Risk of a third-party logon / boot task. Updaters are locked like security agents.</summary>
    public static StartupRisk ClassifyTask(string? taskName, string? targetPath, string? publisher)
    {
        var risk = ClassifyEntry(targetPath, publisher);
        if (risk == StartupRisk.Security) return risk;
        if (IsUpdater(taskName) || IsUpdater(FileName(targetPath))) return StartupRisk.Updater;
        return risk;
    }

    /// <summary>True for tasks that belong to Windows itself (hidden from the list).</summary>
    public static bool IsSystemTask(string? taskPath, string? author)
    {
        var path = taskPath ?? "";
        if (path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) return true;

        var name = path[(path.LastIndexOf('\\') + 1)..];
        // Edge / WebView2 updater tasks live in the root folder; research: never touch (security).
        if (name.StartsWith("MicrosoftEdgeUpdate", StringComparison.OrdinalIgnoreCase)) return true;

        var a = author?.Trim() ?? "";
        if (a.StartsWith("$(@", StringComparison.Ordinal)) return true; // localized resource string of a Windows component
        return MicrosoftNames.Any(m => a.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the security lock allows turning the entry OFF (turning on is always allowed).</summary>
    public static bool CanDisable(StartupRisk risk) => risk is not (StartupRisk.Security or StartupRisk.Updater);

    internal static bool MatchesVendor(string? publisher, IEnumerable<string> vendors)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return false;
        foreach (var vendor in vendors)
        {
            var idx = publisher.IndexOf(vendor, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                var before = idx == 0 || !char.IsLetterOrDigit(publisher[idx - 1]);
                var endIdx = idx + vendor.Length;
                var after = endIdx >= publisher.Length || !char.IsLetterOrDigit(publisher[endIdx]);
                if (before && after) return true;
                idx = publisher.IndexOf(vendor, idx + 1, StringComparison.OrdinalIgnoreCase);
            }
        }
        return false;
    }

    private static bool IsUpdater(string? text) =>
        !string.IsNullOrEmpty(text) &&
        (text.Contains("update", StringComparison.OrdinalIgnoreCase) || text.Contains(Loc.Keep("обновл"), StringComparison.OrdinalIgnoreCase));

    private static string? FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFileName(path.Trim().Trim('"')); }
        catch (ArgumentException) { return null; }
    }
}
