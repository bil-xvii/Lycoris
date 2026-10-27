using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.UI;

namespace Monolith.Modules.Drivers;

public enum DriversTab { Hardware, Overview, Updates, Problems, Installed, Backup }

public enum UpdatesState { NotSearched, Searching, Ready, Error }

internal static class DriverFormat
{
    public static string Date(DateTime? d) => d is { } v && v.Year > 1900 ? v.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "—";

    public static string Size(double bytes) => bytes <= 0 ? "—" : SystemProfile.FormatBytes(bytes);

    public static string Plural(int n, string one, string few, string many)
    {
        var mod100 = Math.Abs(n) % 100;
        if (mod100 is >= 11 and <= 14) return many;
        return (Math.Abs(n) % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }
}

/// <summary>Matches a Windows Update driver to the devices it would replace (to back up their current package first).</summary>
internal static class DriverMatching
{
    /// <summary>
    /// Devices whose hardware id equals the WU id, or whose instance path starts with it
    /// ("acpi\amdi0030" ↔ "ACPI\AMDI0030\0", "usb\vid_046d&amp;pid_c339" ↔ "USB\VID_046D&amp;PID_C339\…").
    /// </summary>
    public static IEnumerable<SignedDriverInfo> FindAffected(IEnumerable<SignedDriverInfo> drivers, string? wuHardwareId)
    {
        if (string.IsNullOrWhiteSpace(wuHardwareId)) return [];
        var id = wuHardwareId.Trim();
        return drivers.Where(d =>
            string.Equals(d.HardwareId, id, StringComparison.OrdinalIgnoreCase) ||
            (d.DeviceId.StartsWith(id, StringComparison.OrdinalIgnoreCase) &&
             (d.DeviceId.Length == id.Length || d.DeviceId[id.Length] is '\\' or '&')));
    }
}

// ───────────────────────────── GPU ─────────────────────────────

public sealed partial class GpuCardItem : ObservableObject
{
    internal GpuCardItem(GpuInfo gpu, GpuRegistryInfo? reg, bool isPrimary, string? nvidiaPackageVersion,
        IReadOnlyList<UninstallEntry> apps, DateTime now)
    {
        Gpu = gpu;
        Vendor = gpu.Vendor;
        Name = gpu.Name;
        VendorText = gpu.Vendor switch
        {
            GpuVendor.Nvidia => "NVIDIA",
            GpuVendor.Amd => "AMD",
            GpuVendor.Intel => "INTEL",
            GpuVendor.Microsoft => "MICROSOFT",
            _ => "ДРУГОЙ",
        };
        IsPrimary = isPrimary;
        RoleText = isPrimary ? "ОСНОВНАЯ" : gpu.IsIntegrated ? "ВСТРОЕННАЯ" : "ДОПОЛНИТЕЛЬНАЯ";
        IsInformational = !isPrimary && gpu.IsIntegrated;

        IsMissingDriver = gpu.Name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(reg?.InfPath, "display.inf", StringComparison.OrdinalIgnoreCase);

        VersionText = IsMissingDriver ? "НЕТ" : GpuDriverVersions.DisplayVersion(gpu.Vendor, gpu.DriverVersion,
            reg?.RadeonSoftwareVersion, gpu.Vendor == GpuVendor.Nvidia ? nvidiaPackageVersion : null);
        VersionLabel = gpu.Vendor switch
        {
            GpuVendor.Nvidia => "ВЕРСИЯ NVIDIA",
            GpuVendor.Amd when !string.IsNullOrWhiteSpace(reg?.RadeonSoftwareVersion) => "AMD SOFTWARE",
            _ => "ВЕРСИЯ",
        };
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(gpu.DriverVersion)) parts.Add("Windows " + gpu.DriverVersion);
        if (!string.IsNullOrWhiteSpace(reg?.InfPath)) parts.Add(reg!.InfPath!);
        if (!string.IsNullOrWhiteSpace(reg?.Provider)) parts.Add(reg!.Provider!);
        TechLine = parts.Count > 0 ? string.Join("  ·  ", parts) : "—";

        DateText = DriverFormat.Date(gpu.DriverDate);
        var months = DriverAge.Months(gpu.DriverDate, now);
        AgeText = DriverAge.Format(months);
        var verdict = DriverAge.Verdict(months);
        VerdictText = IsMissingDriver ? "НЕТ ДРАЙВЕРА ПРОИЗВОДИТЕЛЯ" : DriverAge.VerdictText(verdict);
        VerdictIsWarning = IsMissingDriver || verdict is DriverAgeVerdict.Aging or DriverAgeVerdict.Old;
        // Research: a non-primary iGPU is informational only — no age nag.
        ShowVerdict = !IsInformational || IsMissingDriver;

        Tool = DriverSources.ToolFor(gpu.Vendor);
        SiteUrl = DriverSources.SiteFor(gpu.Vendor);
        DetectTool(apps);

        Recommendation = BuildRecommendation();
    }

    internal GpuInfo Gpu { get; }
    internal GpuVendor Vendor { get; }
    internal VendorTool? Tool { get; }
    internal string? ToolExePath { get; private set; }

    public string Name { get; }
    public string VendorText { get; }
    public string RoleText { get; }
    public bool IsPrimary { get; }
    public bool IsInformational { get; }
    public bool IsMissingDriver { get; }
    public string VersionLabel { get; }
    public string VersionText { get; }
    public string TechLine { get; }
    public string DateText { get; }
    public string AgeText { get; }
    public string VerdictText { get; }
    public bool VerdictIsWarning { get; }
    public bool ShowVerdict { get; }
    public string Recommendation { get; }
    public string? SiteUrl { get; private set; }

    public bool ToolInstalled { get; private set; }
    public bool HasTool => Tool is not null && (!IsInformational || IsMissingDriver);
    /// <summary>The vendor tool is the main action only while no direct download is offered.</summary>
    public bool ShowToolPrimary => HasTool && !HasDownload;
    /// <summary>With a direct download, NVIDIA App / Intel DSA stay available as a secondary button (AMD's "tool" is just its site).</summary>
    public bool ShowToolSecondary => HasTool && HasDownload && Tool?.WingetId is not null;
    /// <summary>AMD without a download: the "tool" is the official installer page, so a separate site button would duplicate it.</summary>
    public bool ShowSiteButton => SiteUrl is not null && (Tool?.WingetId is not null || !HasTool || HasDownload);
    public bool ToolNeedsAdmin => Tool?.WingetId is not null && !ToolInstalled;
    public bool CanUseTool => !ToolNeedsAdmin || Elevation.IsElevated;

    // ── Direct download of the vendor's installer (DriverDownloads) ──

    /// <summary>Official installer MONOLITH can download for this card (NVIDIA: only when a newer driver exists).</summary>
    internal DriverPackage? Package { get; private set; }

    public bool HasDownload => Package is not null && (!IsInformational || IsMissingDriver);

    public string DownloadButtonText
    {
        get
        {
            if (Package is not { } p) return "";
            var text = Vendor == GpuVendor.Nvidia ? $"СКАЧАТЬ И УСТАНОВИТЬ {p.Version}" : $"СКАЧАТЬ ДРАЙВЕР AMD {p.Version}";
            return p.SizeDisplay is { } size ? $"{text} · {size}" : text;
        }
    }

    public string DownloadHint => Package is not { } p ? "" :
        $"Официальный установщик {p.VendorName} скачивается прямо с {p.Url.Host}, проверяется по цифровой подписи {p.Signer} " +
        "и запускается после точки восстановления." + (Elevation.IsElevated ? "" : " Нужны права администратора.");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelDownloadCommand))]
    private bool _isDownloading;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelDownloadCommand))]
    private bool _canCancelDownload;
    [ObservableProperty] private double _downloadPercent;
    [ObservableProperty] private bool _downloadIndeterminate;
    [ObservableProperty] private string _downloadStage = "";
    [ObservableProperty] private string _downloadDetail = "";
    [ObservableProperty] private string? _downloadResult;
    [ObservableProperty] private bool _downloadResultOk;

    private CancellationTokenSource? _downloadCts;

    internal void SetPackage(DriverPackage? package)
    {
        Package = package;
        OnPropertyChanged(nameof(HasDownload));
        OnPropertyChanged(nameof(DownloadButtonText));
        OnPropertyChanged(nameof(DownloadHint));
        OnPropertyChanged(nameof(ShowToolPrimary));
        OnPropertyChanged(nameof(ShowToolSecondary));
        OnPropertyChanged(nameof(ShowSiteButton));
    }

    /// <summary>Starts the card's download state; the returned token is cancelled by ОТМЕНА or app shutdown.</summary>
    internal CancellationToken BeginDownload(CancellationToken lifetime)
    {
        _downloadCts?.Dispose();
        _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        DownloadResult = null;
        DownloadPercent = 0;
        DownloadIndeterminate = true;
        DownloadStage = "ЗАГРУЗКА";
        DownloadDetail = $"Подключение к {Package?.Url.Host}…";
        IsDownloading = true;
        CanCancelDownload = true;
        return _downloadCts.Token;
    }

    internal void ReportDownload(DownloadProgress p)
    {
        if (!IsDownloading) return;
        if (p.Verifying)
        {
            DownloadStage = "ПРОВЕРКА ПОДПИСИ";
            DownloadIndeterminate = true;
            CanCancelDownload = false;
        }
        else
        {
            DownloadStage = "ЗАГРУЗКА";
            DownloadIndeterminate = p.Total is not > 0;
            DownloadPercent = p.Percent;
        }
        DownloadDetail = DriverDownloads.FormatProgress(p);
    }

    /// <summary>A step after the download (restore point, final check): shown without a cancel button.</summary>
    internal void SetDownloadStep(string stage, string detail)
    {
        DownloadStage = stage;
        DownloadDetail = detail;
        DownloadIndeterminate = true;
        CanCancelDownload = false;
    }

    internal void FinishDownload(bool ok, string message)
    {
        IsDownloading = false;
        CanCancelDownload = false;
        DownloadIndeterminate = false;
        DownloadResultOk = ok;
        DownloadResult = message;
        _downloadCts?.Dispose();
        _downloadCts = null;
    }

    [RelayCommand(CanExecute = nameof(CanCancelDownload))]
    private void CancelDownload()
    {
        if (_downloadCts is not { IsCancellationRequested: false } cts) return;
        DownloadDetail = "Отмена…";
        CanCancelDownload = false;
        cts.Cancel();
    }

    /// <summary>AMD: the official auto-detect installer found on amd.com, plus an "update available" hint when comparable.</summary>
    internal void ApplyAmdPackage(DriverPackage? package, DateTime now)
    {
        SetPackage(package);
        if (package is null || IsMissingDriver || VersionLabel != "AMD SOFTWARE")
        {
            HasLatest = false;
            return;
        }
        var cmp = GpuDriverVersions.CompareAmd(package.Version, VersionText);
        if (cmp is null)
        {
            HasLatest = false;
            return;
        }
        UpdateAvailable = cmp > 0;
        LatestVersion = package.Version;
        LatestText = UpdateAvailable ? $"ДОСТУПЕН ДРАЙВЕР {package.Version}" : $"УСТАНОВЛЕН ПОСЛЕДНИЙ ДРАЙВЕР {package.Version}";
        LatestCaption = $"по данным amd.com, проверено в {now:HH:mm}";
        HasLatest = true;
    }

    public string ToolButtonText => Tool switch
    {
        null => "",
        { WingetId: null } => "ОФИЦИАЛЬНЫЙ УСТАНОВЩИК AMD",
        _ when ToolInstalled => Vendor == GpuVendor.Nvidia ? "ОТКРЫТЬ NVIDIA APP" : "ОТКРЫТЬ INTEL DSA",
        _ => Vendor == GpuVendor.Nvidia ? "УСТАНОВИТЬ NVIDIA APP" : "УСТАНОВИТЬ INTEL DSA",
    };

    public string ToolHint => Tool switch
    {
        null => "",
        { WingetId: null } => Tool.Description,
        _ when ToolInstalled => $"{Tool.Name} уже установлен. {Tool.Description}",
        _ => $"{Tool.Description} Установка через winget ({(Tool.WingetSource == "msstore" ? "Microsoft Store" : "winget")}), издатель: {Tool.Publisher}.",
    };

    // Latest version hint (NVIDIA only)
    [ObservableProperty] private bool _isCheckingLatest;
    [ObservableProperty] private bool _hasLatest;
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _latestText = "";
    [ObservableProperty] private string _latestCaption = "";
    [ObservableProperty] private string _latestVersion = "";

    public bool SupportsLatestLookup => Vendor == GpuVendor.Nvidia && !IsMissingDriver;

    internal void ApplyLatest(NvidiaRelease? release, DateTime now)
    {
        IsCheckingLatest = false;
        if (release is null)
        {
            HasLatest = false;
            return;
        }
        var cmp = GpuDriverVersions.CompareNvidia(release.Version, VersionText);
        if (cmp is null)
        {
            HasLatest = false;
            return;
        }
        UpdateAvailable = cmp > 0;
        LatestVersion = release.Version;
        var date = release.Released is { } d ? d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : null;
        // The size is shown on the download button (exact, from NVIDIA's server), not here in NVIDIA's decimal units.
        var flags = string.Join(" · ", new[] { release.IsWhql ? "WHQL" : null, release.IsBeta ? "BETA" : null, date }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        LatestText = UpdateAvailable ? $"ДОСТУПЕН ДРАЙВЕР {release.Version}" : $"УСТАНОВЛЕН ПОСЛЕДНИЙ ДРАЙВЕР {release.Version}";
        LatestCaption = $"{flags}{(flags.Length > 0 ? " · " : "")}по данным nvidia.com, проверено в {now:HH:mm}";
        if (release.DetailsUrl is not null) SiteUrl = release.DetailsUrl;
        OnPropertyChanged(nameof(SiteUrl));
        HasLatest = true;
        // Direct download only for a newer driver on NVIDIA's own download server.
        SetPackage(UpdateAvailable ? DriverDownloads.NvidiaPackage(release) : null);
    }

    private void DetectTool(IReadOnlyList<UninstallEntry> apps)
    {
        switch (Vendor)
        {
            case GpuVendor.Nvidia:
            {
                var entry = apps.FirstOrDefault(a => a.DisplayName.StartsWith("NVIDIA app", StringComparison.OrdinalIgnoreCase));
                var candidates = new List<string>();
                if (!string.IsNullOrWhiteSpace(entry?.InstallLocation))
                    candidates.Add(Path.Combine(entry!.InstallLocation!.Trim('"'), "CEF", "NVIDIA app.exe"));
                candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVIDIA app", "CEF", "NVIDIA app.exe"));
                ToolExePath = candidates.FirstOrDefault(File.Exists);
                ToolInstalled = entry is not null || ToolExePath is not null;
                break;
            }
            case GpuVendor.Intel:
                ToolInstalled = apps.Any(a => a.DisplayName.Contains("Driver & Support Assistant", StringComparison.OrdinalIgnoreCase));
                break;
        }
    }

    internal void MarkToolInstalled(IReadOnlyList<UninstallEntry> apps)
    {
        DetectTool(apps);
        OnPropertyChanged(nameof(ToolInstalled));
        OnPropertyChanged(nameof(ToolButtonText));
        OnPropertyChanged(nameof(ToolHint));
        OnPropertyChanged(nameof(ToolNeedsAdmin));
        OnPropertyChanged(nameof(CanUseTool));
    }

    private string BuildRecommendation()
    {
        if (IsMissingDriver)
            return "Видеокарта работает на стандартном драйвере Windows: нет ускорения 3D, игр и нормальных частот обновления. Установите драйвер производителя — через его утилиту, сайт или Windows Update.";
        if (IsInformational)
            return "Дополнительный встроенный адаптер. Его драйвер приходит через Windows Update — отдельных действий не нужно.";
        return Vendor switch
        {
            GpuVendor.Nvidia => "Когда выходит новый драйвер, MONOLITH скачивает официальный установщик прямо с сервера NVIDIA. Обновлять можно и через NVIDIA App. Windows Update тоже присылает драйверы NVIDIA, но обычно с опозданием.",
            GpuVendor.Amd => "Свежий драйвер Adrenalin ставит официальный установщик AMD: он сам определит видеокарту. MONOLITH скачивает его прямо с сервера AMD. В winget пакета AMD нет.",
            GpuVendor.Intel => "Драйвер видеоядра Intel проверяет Intel Driver & Support Assistant; Windows Update тоже присылает его, но реже.",
            _ => "Драйвер этого адаптера обновляется через Windows Update.",
        };
    }
}

// ───────────────────────────── Chipset ─────────────────────────────

public sealed class ChipsetComponentItem
{
    internal ChipsetComponentItem(ChipsetComponent c, string vendorLabel)
    {
        Title = c.Title;
        Description = c.Description;
        IsRequired = c.Required;
        (StateText, Glyph, IsOk, IsProblem) = c.State switch
        {
            ComponentState.Installed => ($"ДРАЙВЕР {vendorLabel}", Glyphs.Check, true, false),
            ComponentState.Inbox => ("ДРАЙВЕР WINDOWS", Glyphs.Remove, !c.Required, c.Required),
            ComponentState.Problem => ($"КОД {c.ProblemCode}", Glyphs.Warning, false, true),
            _ => ("НЕ НАЙДЕНО", Glyphs.Remove, !c.Required, c.Required),
        };
        VersionText = c.Version ?? "—";
        DetailText = string.Join(" · ", new[] { c.Date is { } d ? DriverFormat.Date(d) : null, c.InfName }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (c.State == ComponentState.Problem) DetailText = DeviceProblemCodes.Describe(c.ProblemCode ?? 0).Title;
        if (DetailText.Length == 0) DetailText = "—";
    }

    public string Title { get; }
    public string Description { get; }
    public bool IsRequired { get; }
    public string StateText { get; }
    public string Glyph { get; }
    public bool IsOk { get; }
    public bool IsProblem { get; }
    public string VersionText { get; }
    public string DetailText { get; }
}

// ───────────────────────────── Windows Update ─────────────────────────────

public sealed partial class WuUpdateItem : ObservableObject
{
    internal WuUpdateItem(WuDriverUpdate u)
    {
        Update = u;
        IsFirmware = WuText.IsFirmware(u.DriverClass);
        FixesProblem = u.DeviceProblemNumber != 0;
        // Recommended ones are pre-checked; optional ones never are (research drivers.md §1): the user ticks them
        // deliberately — a hint marks those that match a device with a problem.
        _isSelected = !IsFirmware && !u.IsOptional;
    }

    internal WuDriverUpdate Update { get; }

    public string Id => Update.UpdateId;
    public string Title => string.IsNullOrWhiteSpace(Update.Title) ? "Драйвер" : Update.Title;
    public string ModelText => string.Join(" · ", new[] { Update.Model, Update.Manufacturer ?? Update.Provider }
        .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase));
    public string ClassText => DeviceNaming.ClassName(Update.DriverClass);
    public string VersionText => Update.Version ?? "—";
    public string DateText => DriverFormat.Date(Update.DriverDate);
    public string SizeText => DriverFormat.Size(Update.SizeBytes);
    public string RebootText => WuText.RebootText(Update.RebootBehavior);
    public string HardwareId => Update.HardwareId ?? "";
    public bool IsOptional => Update.IsOptional;
    public bool IsFirmware { get; }
    public bool FixesProblem { get; }
    /// <summary>An optional driver for a device with a problem: shown as a hint, never pre-checked.</summary>
    public bool ShowFixesProblemHint => IsOptional && FixesProblem && !IsFirmware;
    /// <summary>A recommended driver (or firmware) for a device with a problem.</summary>
    public bool ShowProblemDeviceTag => FixesProblem && !ShowFixesProblemHint;
    public bool NeedsEula => !Update.EulaAccepted;
    public bool CanSelect => !IsFirmware;
    public string KindText => IsFirmware ? "ПРОШИВКА" : IsOptional ? "НЕОБЯЗАТЕЛЬНОЕ" : "РЕКОМЕНДУЕМОЕ";
    public string KindHint => IsFirmware
        ? "Обновление прошивки (UEFI/BIOS). MONOLITH не прошивает оборудование — установите его в параметрах Windows Update или утилитой производителя."
        : IsOptional
            ? FixesProblem
                ? "Необязательный драйвер для устройства, которое сейчас работает с ошибкой: он может её исправить. Отметьте его, если хотите попробовать."
                : "Необязательный драйвер: ставьте его, только если с устройством есть проблема."
            : "Рекомендуемый драйвер: Windows Update установит его и сама, здесь можно сделать это сразу.";

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string? _resultText;
    [ObservableProperty] private string? _resultMessage;
    [ObservableProperty] private bool _resultOk;

    internal void ApplyResult(WuItemResult r)
    {
        ResultOk = r.Success;
        ResultText = r.Success ? (r.RebootRequired ? "НУЖНА ПЕРЕЗАГРУЗКА" : "УСТАНОВЛЕНО") : WuText.ResultText(r.ResultCode == WuText.OrcSucceeded ? WuText.OrcFailed : r.ResultCode);
        ResultMessage = r.Message;
        if (r.Success) IsSelected = false;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !CanSelect) IsSelected = false;
    }
}

// ───────────────────────────── Problem devices ─────────────────────────────

public sealed class ProblemDeviceItem
{
    internal ProblemDeviceItem(ProblemDeviceInfo d)
    {
        Name = d.Name;
        DeviceId = d.DeviceId;
        Code = d.Code;
        var info = DeviceProblemCodes.Describe(d.Code);
        CodeText = info.CodeText;
        ProblemTitle = info.Title;
        Explanation = info.Explanation;
        Action = info.Action;
        Remedy = info.Remedy;
        ClassText = DeviceNaming.ClassName(d.PnpClass);
        HardwareId = DeviceNaming.PrimaryHardwareId(d.HardwareIds) ?? d.DeviceId;
        Vendor = DeviceNaming.VendorName(HardwareId) ?? (string.IsNullOrWhiteSpace(d.Manufacturer) ? "—" : d.Manufacturer);
        IsSevere = Remedy is ProblemRemedy.InstallDriver or ProblemRemedy.Hardware or ProblemRemedy.DoNotBypass;
    }

    public string Name { get; }
    public string DeviceId { get; }
    public int Code { get; }
    public string CodeText { get; }
    public string ProblemTitle { get; }
    public string Explanation { get; }
    public string Action { get; }
    internal ProblemRemedy Remedy { get; }
    public string ClassText { get; }
    public string HardwareId { get; }
    public string Vendor { get; }
    public bool IsSevere { get; }
    public bool CanEnable => Code == DeviceProblemCodes.Disabled && DeviceId.Length > 0;
    public bool SuggestsWindowsUpdate => Remedy is ProblemRemedy.InstallDriver or ProblemRemedy.UpdateDriver or ProblemRemedy.ReinstallDriver or ProblemRemedy.Hardware;
}

// ───────────────────────────── Installed drivers ─────────────────────────────

public sealed class InstalledDriverItem
{
    internal InstalledDriverItem(SignedDriverInfo d, int count)
    {
        DeviceName = d.DeviceName;
        ClassText = DeviceNaming.ClassName(d.DeviceClass);
        Provider = string.IsNullOrWhiteSpace(d.Provider) ? "—" : d.Provider;
        Version = string.IsNullOrWhiteSpace(d.Version) ? "—" : d.Version;
        DateText = DriverFormat.Date(d.Date);
        IsSigned = d.IsSigned;
        SignedText = d.IsSigned ? "ПОДПИСАН" : "БЕЗ ПОДПИСИ";
        Signer = string.IsNullOrWhiteSpace(d.Signer) ? (d.IsSigned ? "Подпись проверена" : "Цифровая подпись отсутствует") : d.Signer;
        InfName = string.IsNullOrWhiteSpace(d.InfName) ? "—" : d.InfName;
        IsMicrosoft = d.IsMicrosoft;
        IsInbox = !d.IsOem;
        Count = count;
        HardwareId = d.HardwareId;
        DeviceId = d.DeviceId;
        Manufacturer = string.IsNullOrWhiteSpace(d.Manufacturer) ? Provider : d.Manufacturer;
        SearchKey = string.Join('\n', DeviceName, ClassText, d.DeviceClass, Provider, d.Manufacturer, Version, InfName, HardwareId, DeviceId)
            .ToLowerInvariant();
    }

    public string DeviceName { get; }
    public string ClassText { get; }
    public string Provider { get; }
    public string Version { get; }
    public string DateText { get; }
    public bool IsSigned { get; }
    public string SignedText { get; }
    public string Signer { get; }
    public string InfName { get; }
    public bool IsMicrosoft { get; }
    public bool IsInbox { get; }
    public int Count { get; }
    public bool HasDuplicates => Count > 1;
    public string CountText => $"×{Count}";
    public string HardwareId { get; }
    public string DeviceId { get; }
    public string Manufacturer { get; }
    public string Tooltip => $"{DeviceName}\nПроизводитель устройства: {Manufacturer}\nПоставщик драйвера: {Provider}\n{HardwareId}\n{Signer}";
    internal string SearchKey { get; }

    public bool Matches(string[] terms) => terms.All(t => SearchKey.Contains(t, StringComparison.Ordinal));
}

// ───────────────────────────── Backups ─────────────────────────────

public sealed class BackupItem
{
    internal BackupItem(DriverBackupInfo b)
    {
        Info = b;
        Name = b.Name;
        Path = b.Path;
        DateText = b.Created.ToString("dd.MM.yyyy  HH:mm", CultureInfo.InvariantCulture);
        KindText = b.Kind == BackupKind.BeforeUpdate ? "ПЕРЕД ОБНОВЛЕНИЕМ" : "ПОЛНАЯ";
        IsBeforeUpdate = b.Kind == BackupKind.BeforeUpdate;
        SizeText = DriverFormat.Size(b.SizeBytes);
        InfCount = b.InfCount;
        InfCountText = $"{b.InfCount} INF";
        var providers = b.Packages.Select(p => p.Provider).Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Replace(", Inc.", "").Replace(" Inc.", "").Replace(" Corporation", "").Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ProvidersText = providers.Count == 0 ? "Состав не определён" :
            string.Join(" · ", providers.Take(6)) + (providers.Count > 6 ? $" и ещё {providers.Count - 6}" : "");
        Packages = b.Packages.Select(p => new BackupPackageItem(p)).ToList();
        Note = b.Note;
    }

    internal DriverBackupInfo Info { get; }
    public string Name { get; }
    public string Path { get; }
    public string DateText { get; }
    public string KindText { get; }
    public bool IsBeforeUpdate { get; }
    public string SizeText { get; }
    public int InfCount { get; }
    public string InfCountText { get; }
    public string ProvidersText { get; }
    public IReadOnlyList<BackupPackageItem> Packages { get; }
    public string PackagesHeader => $"СОСТАВ КОПИИ · {Packages.Count} {DriverFormat.Plural(Packages.Count, "ПАКЕТ", "ПАКЕТА", "ПАКЕТОВ")}";
    public string? Note { get; }
}

public sealed class BackupPackageItem
{
    internal BackupPackageItem(BackupPackage p)
    {
        Inf = p.InfFile;
        Provider = p.Provider ?? "—";
        ClassText = DeviceNaming.ClassName(p.Class);
        Version = p.Version ?? "—";
        DateText = DriverFormat.Date(p.Date);
    }

    public string Inf { get; }
    public string Provider { get; }
    public string ClassText { get; }
    public string Version { get; }
    public string DateText { get; }
}
