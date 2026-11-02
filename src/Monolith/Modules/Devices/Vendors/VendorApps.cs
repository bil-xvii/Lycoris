using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Monolith.Core;
using Monolith.Modules.Apps;
using Monolith.Modules.Drivers;
using Monolith.Modules.Drivers.Hardware;
using Monolith.UI;

namespace Monolith.Modules.Devices.Vendors;

/// <summary>
/// A vendor program that configures devices (devices_vendors.md §4–5). Launchable entries have a fixed exe under Program Files,
/// fixed arguments and the expected Authenticode signers — nothing is taken from the registry or a shortcut. Detection-only
/// entries (no exe) are only recognised. <see cref="PublisherContains"/> empty = publisher not verified on a real install.
/// </summary>
internal sealed record VendorApp(
    string Key,
    string Name,
    IReadOnlyList<string> Vids,
    string? NameFilter,
    string PublisherContains,
    string DisplayNameContains,
    string? ExeUnderProgramFiles,
    string Args,
    IReadOnlyList<string> Signers,
    IReadOnlyList<string> Processes,
    VendorSoftware? Install)
{
    /// <summary>Lighting from many brands through reverse-engineered protocols (OpenRGB, SignalRGB): never launched, never suggested.</summary>
    public bool IsRgbUnifier { get; init; }
    /// <summary>The vendor's own lighting control (G HUB, Synapse, iCUE, MSI Center, GG) — it conflicts with a unifier.</summary>
    public bool ControlsLighting { get; init; }

    /// <summary>Started from Lycoris only through a fixed, signed exe.</summary>
    public bool IsLaunchable => ExeUnderProgramFiles is not null && Signers.Count > 0 && !IsRgbUnifier;

    /// <summary>The installed-program rule shared with Drivers: display name AND (verified) publisher.</summary>
    public bool Matches(UninstallEntry entry) =>
        VendorSoftware.Matches(entry, [DisplayNameContains], PublisherContains.Length == 0 ? null : PublisherContains);

    /// <summary>The program configures this device: its vendor id, and the name filter (Sony: INZONE devices only).</summary>
    public bool AppliesTo(string? vid, IEnumerable<string?> deviceNames) =>
        vid is not null && Vids.Contains(vid, StringComparer.OrdinalIgnoreCase)
        && (NameFilter is null || deviceNames.Any(n => n?.Contains(NameFilter, StringComparison.OrdinalIgnoreCase) == true));
}

/// <summary>What the scan found for one program.</summary>
internal sealed record VendorAppState(VendorApp App, bool Installed, bool Launchable, bool Running, string? Version);

/// <summary>The vendor programs of this PC at scan time.</summary>
internal sealed class VendorAppSnapshot(IReadOnlyList<VendorAppState> states)
{
    public static VendorAppSnapshot Empty { get; } = new([]);
    public IReadOnlyList<VendorAppState> States { get; } = states;
    public VendorAppState? Get(string key) => States.FirstOrDefault(s => s.App.Key == key);
    public bool IsInstalled(string key) => Get(key)?.Installed == true;
    public IEnumerable<VendorApp> Installed => States.Where(s => s.Installed).Select(s => s.App);
}

/// <summary>
/// Detecting vendor programs and opening them as the signed-in user (CLAUDE.md rule 8). Never uses the vendors' URI handlers
/// (lghubapp:, RazerAppEngine.synapse: — HKCU\Software\Classes is user-writable), never reads or writes vendor databases,
/// never starts an unsigned exe (OpenRGB).
/// </summary>
internal static class VendorApps
{
    public static readonly VendorApp GHub = new("ghub", "G HUB", ["046D"], null, "Logitech", "Logitech G HUB",
        @"LGHUB\system_tray\lghub_system_tray.exe", "", ["Logitech Inc"], ["lghub_agent", "lghub_system_tray", "lghub"],
        HardwareCatalog.GHub) { ControlsLighting = true };

    public static readonly VendorApp Synapse = new("synapse", "Razer Synapse", ["1532"], null, "Razer", "Razer Synapse",
        @"Razer\RazerAppEngine\RazerAppEngine.exe", "--url-params=apps=synapse", ["Razer USA Ltd."], ["RazerAppEngine"],
        HardwareCatalog.Synapse) { ControlsLighting = true };

    public static readonly VendorApp InzoneHub = new("inzone", "INZONE Hub", ["054C"], "INZONE", "Sony", "INZONE Hub",
        @"Sony\INZONE Hub\INZONEHub.exe", "", ["Sony Corporation"], ["INZONEHub"], HardwareCatalog.InzoneHub);

    // Detection only: the launch target and signer are not verified on a PC with the program yet.
    public static readonly VendorApp OptionsPlus = new("optionsplus", "Logi Options+", ["046D"], null, "", "Logi Options+",
        null, "", [], ["logioptionsplus_agent", "logioptionsplus"], HardwareCatalog.OptionsPlus);

    public static readonly VendorApp SteelSeriesGg = new("gg", "SteelSeries GG", ["1038"], null, "", "SteelSeries GG",
        null, "", [], ["SteelSeriesGG"], HardwareCatalog.SteelSeriesGgApp) { ControlsLighting = true };

    public static readonly VendorApp Icue = new("icue", "iCUE", ["1B1C"], null, "", "iCUE",
        null, "", [], ["iCUE"], HardwareCatalog.Icue) { ControlsLighting = true };

    public static readonly VendorApp MsiCenter = new("msicenter", "MSI Center", ["0DB0"], null, "", "MSI Center",
        null, "", [], ["MSI.CentralServer", "MSI Center"], HardwareCatalog.MsiCenter) { ControlsLighting = true };

    public static readonly VendorApp OpenRgb = new("openrgb", "OpenRGB", [], null, "OpenRGB", "OpenRGB",
        null, "", [], ["OpenRGB"], null) { IsRgbUnifier = true }; // unsigned exe: NEVER launchable

    public static readonly VendorApp SignalRgb = new("signalrgb", "SignalRGB", [], null, "", "SignalRGB",
        null, "", [], ["SignalRgb"], null) { IsRgbUnifier = true };

    public static IReadOnlyList<VendorApp> All { get; } = [GHub, Synapse, InzoneHub, OptionsPlus, SteelSeriesGg, Icue, MsiCenter, OpenRgb, SignalRgb];

    // ───────────────────────────── Detection ─────────────────────────────

    /// <summary>
    /// Pure detection: installed = an "Apps &amp; features" entry by the shared rule (HKLM, HKLM\WOW6432Node, HKCU — Razer lives
    /// in WOW6432Node); launchable = a launchable entry whose exe exists; running = one of its processes.
    /// </summary>
    internal static VendorAppSnapshot Detect(IEnumerable<UninstallEntry> entries, Func<VendorApp, bool> exeExists, Func<string, bool> isRunning)
    {
        var list = entries.ToList();
        var states = new List<VendorAppState>();
        foreach (var app in All)
        {
            var entry = list.FirstOrDefault(app.Matches);
            var installed = entry is not null;
            bool launchable;
            try { launchable = installed && app.IsLaunchable && exeExists(app); }
            catch { launchable = false; }
            bool running;
            try { running = installed && app.Processes.Any(isRunning); }
            catch { running = false; }
            states.Add(new VendorAppState(app, installed, launchable, running, entry?.Version));
        }
        return new VendorAppSnapshot(states);
    }

    /// <summary>This PC, now (worker thread; read-only).</summary>
    public static VendorAppSnapshot DetectNow()
    {
        List<UninstallEntry> entries;
        try { entries = DriverInventory.ReadUninstallEntries(); }
        catch (Exception ex)
        {
            Log.Warn("Программы производителей: список программ не прочитан ({0}).", ex.Message);
            entries = [];
        }
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return Detect(entries, app => app.ExeUnderProgramFiles is { } rel && pf.Length > 0 && File.Exists(Path.Combine(pf, rel)), IsRunning);
    }

    private static bool IsRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try { return processes.Length > 0; }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    // ───────────────────────────── Launch ─────────────────────────────

    /// <summary>Everything the launch touches, injectable for tests.</summary>
    internal sealed class LaunchEnvironment
    {
        public required string ProgramFiles { get; init; }
        public required Func<string, bool> FileExists { get; init; }
        public required Func<string, DateTime?> LastWriteUtc { get; init; }
        /// <summary>True when the file or directory is a reparse point (symlink, junction) — or cannot be checked.</summary>
        public required Func<string, bool> IsReparsePoint { get; init; }
        public required Func<string, IReadOnlyList<string>, Task<(bool Ok, string Signer)>> VerifySignature { get; init; }
        /// <summary>(exe, arguments, working directory) → started.</summary>
        public required Func<string, string, string?, Task<bool>> Start { get; init; }

        public static LaunchEnvironment System { get; } = new()
        {
            ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            FileExists = File.Exists,
            LastWriteUtc = p =>
            {
                try { return File.GetLastWriteTimeUtc(p); }
                catch { return null; }
            },
            IsReparsePoint = p =>
            {
                try { return (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0; }
                catch { return true; } // cannot check = not trusted
            },
            VerifySignature = VendorDownloads.VerifySignatureAsync,
            Start = UnelevatedShell.StartAsUserAsync,
        };
    }

    /// <summary>Verified signatures by exe path; an entry counts only while the file's LastWriteTimeUtc is unchanged.</summary>
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, bool Ok, string Signer)> SignatureCache = new(StringComparer.OrdinalIgnoreCase);

    internal static void ClearSignatureCacheForTests() => SignatureCache.Clear();

    /// <summary>
    /// The exe of a launchable program, or why there is none: inside Program Files after normalisation, present, and no reparse
    /// point on the file or any folder between it and Program Files.
    /// </summary>
    internal static string? ResolveExe(VendorApp app, LaunchEnvironment env, out string? problem)
    {
        problem = null;
        if (!app.IsLaunchable || app.ExeUnderProgramFiles is not { Length: > 0 } rel)
        {
            problem = app.IsRgbUnifier ? Loc.T("программа без цифровой подписи — Lycoris её не запускает") : Loc.T("нет проверенного способа запуска");
            return null;
        }
        if (string.IsNullOrWhiteSpace(env.ProgramFiles))
        {
            problem = Loc.T("папка Program Files не найдена");
            return null;
        }
        if (Path.IsPathRooted(rel))
        {
            problem = Loc.T("путь ведёт за пределы Program Files");
            return null;
        }
        string full, root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(env.ProgramFiles)) + Path.DirectorySeparatorChar;
            full = Path.GetFullPath(Path.Combine(root, rel));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            problem = Loc.T("неверный путь");
            return null;
        }
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            problem = Loc.T("путь ведёт за пределы Program Files");
            return null;
        }
        if (!env.FileExists(full))
        {
            problem = Loc.T("программа не найдена в Program Files");
            return null;
        }
        // The file and every folder up to Program Files: a link could point the fixed path somewhere else.
        for (var p = full; p.Length > root.Length && p.StartsWith(root, StringComparison.OrdinalIgnoreCase); p = Path.GetDirectoryName(p) ?? "")
        {
            if (env.IsReparsePoint(p))
            {
                problem = Loc.T("в пути есть ссылка на другую папку");
                return null;
            }
        }
        return full;
    }

    /// <summary>
    /// Starts a vendor program as the signed-in user: fixed exe under Program Files, Authenticode signer checked (cached per
    /// path + LastWriteTimeUtc), then <see cref="UnelevatedShell.StartAsUserAsync"/> with the fixed arguments. Never throws.
    /// </summary>
    internal static async Task<(bool Ok, string? Problem)> TryLaunchAsync(VendorApp app, LaunchEnvironment env)
    {
        try
        {
            if (ResolveExe(app, env, out var problem) is not { } exe) return (false, problem);
            if (env.LastWriteUtc(exe) is not { } stamp) return (false, Loc.T("файл программы недоступен"));

            (bool Ok, string Signer) signature;
            if (SignatureCache.TryGetValue(exe, out var cached) && cached.Stamp == stamp) signature = (cached.Ok, cached.Signer);
            else
            {
                signature = await env.VerifySignature(exe, app.Signers).ConfigureAwait(false);
                SignatureCache[exe] = (stamp, signature.Ok, signature.Signer);
            }
            if (!signature.Ok) return (false, Loc.Tf("цифровая подпись не подтверждена ({0})", signature.Signer));

            var started = await env.Start(exe, app.Args, Path.GetDirectoryName(exe)).ConfigureAwait(false);
            return started ? (true, null) : (false, Loc.T("Windows не запустила программу без прав администратора"));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>The «ОТКРЫТЬ …» button: false (with a Warn and a toast that says why) when the program could not be opened.</summary>
    public static async Task<bool> LaunchAsync(VendorApp app)
    {
        var (ok, problem) = await TryLaunchAsync(app, LaunchEnvironment.System).ConfigureAwait(false);
        if (ok)
        {
            Log.Info("Открыт {0} (подпись проверена, без прав администратора).", app.Name);
            return true;
        }
        var text = Loc.Tf("Не удалось открыть {0}: {1}.", app.Name, problem);
        Log.Warn("Не удалось открыть {0}: {1}.", app.Name, problem);
        Ui.Toast(text, ToastKind.Warning, app.Name.ToUpperInvariant());
        return false;
    }

    // ───────────────────────────── Windows HDR Calibration (Microsoft Store) ─────────────────────────────

    /// <summary>Microsoft «Калибровка HDR в Windows» (devices_displays.md display-hdr-calibration).</summary>
    public const string HdrCalibrationStoreId = "9N7F2SM5D1LR";
    public const string HdrCalibrationStorePage = "ms-windows-store://pdp/?productid=" + HdrCalibrationStoreId;
    private const string HdrCalibrationFamilyPrefix = "Microsoft.Windows.HDRCalibration_";

    /// <summary>The Apps catalog can install it (then a link to «Приложения» is not a dead end).</summary>
    public static bool HdrCalibrationInAppsCatalog =>
        AppCatalog.All.Any(e => string.Equals(e.Id, HdrCalibrationStoreId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Installed for this user (AppX repository, read-only); false when it cannot be read.</summary>
    public static bool IsHdrCalibrationInstalled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages", false);
            return key?.GetSubKeyNames().Any(n => n.StartsWith(HdrCalibrationFamilyPrefix, StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Its Microsoft Store page (install or open it there), never with Lycoris's admin token.</summary>
    public static Task<bool> OpenHdrCalibrationAsync() => UnelevatedShell.OpenAsync(HdrCalibrationStorePage);
}
