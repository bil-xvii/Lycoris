using System.Text.RegularExpressions;

namespace Lycoris.Modules.Drivers;

/// <summary>
/// Pure helpers for the Windows Update Agent driver search: criteria, result codes, HRESULT messages
/// (docs/research/drivers.md §1).
/// </summary>
internal static partial class WuText
{
    /// <summary>Not-installed, not-hidden driver updates (recommended and optional).</summary>
    public const string DriverCriteria = "IsInstalled=0 and Type='Driver' and IsHidden=0";

    /// <summary>IUpdateSearcher.ServerSelection: follow WSUS / WUfB policy.</summary>
    public const int ServerDefault = 0;
    /// <summary>IUpdateSearcher.ServerSelection: Microsoft's servers directly (advanced choice only).</summary>
    public const int ServerWindowsUpdate = 2;

    // OperationResultCode
    public const int OrcNotStarted = 0;
    public const int OrcInProgress = 1;
    public const int OrcSucceeded = 2;
    public const int OrcSucceededWithErrors = 3;
    public const int OrcFailed = 4;
    public const int OrcAborted = 5;

    // HRESULTs
    public const int ServiceDisabled = unchecked((int)0x80070422);
    public const int AccessDenied = unchecked((int)0x80070005);
    public const int OperationCancelled = unchecked((int)0x8024000B);
    public const int NotAllowed = unchecked((int)0x80240016);
    public const int OperationInProgress = unchecked((int)0x80240009);
    public const int ExclusiveInstallConflict = unchecked((int)0x80240019);
    public const int EulasDeclined = unchecked((int)0x80240023);

    private static readonly int[] NetworkErrors =
    [
        unchecked((int)0x8024001F), // WU_E_NO_CONNECTION
        unchecked((int)0x8024402C), // WU_E_PT_WINHTTP_NAME_NOT_RESOLVED
        unchecked((int)0x80072EE7), // ERROR_INTERNET_NAME_NOT_RESOLVED
        unchecked((int)0x80072EE2), // ERROR_INTERNET_TIMEOUT
        unchecked((int)0x80072EFD), // ERROR_INTERNET_CANNOT_CONNECT
        unchecked((int)0x80072EFE), // ERROR_INTERNET_CONNECTION_ABORTED
        unchecked((int)0x8024401C), // WU_E_PT_HTTP_STATUS_REQUEST_TIMEOUT
        unchecked((int)0x80244022), // WU_E_PT_HTTP_STATUS_SERVICE_UNAVAIL
        unchecked((int)0x80240438), // WU_E_PT_ENDPOINT_UNREACHABLE
        unchecked((int)0x80072EFF), // ERROR_INTERNET_CONNECTION_RESET
        unchecked((int)0x80246005), // WU_E_DM_NONETWORK
        unchecked((int)0x80D03805), // DO_E_BLOCKED_BY_NO_NETWORK
    ];

    public static bool IsNetworkError(int hresult) => NetworkErrors.Contains(hresult);

    // Microsoft's side is busy or broken (not the user's network): wait a little, then retry once.
    private static readonly int[] ServerBusyErrors =
    [
        unchecked((int)0x8024401F), // WU_E_PT_HTTP_STATUS_SERVER_ERROR (500)
        unchecked((int)0x80244021), // WU_E_PT_HTTP_STATUS_BAD_GATEWAY (502)
        unchecked((int)0x80244023), // WU_E_PT_HTTP_STATUS_GATEWAY_TIMEOUT (504)
        unchecked((int)0x8024402D), // WU_E_PT_LOAD_SHEDDING (Microsoft: transient, retry later)
    ];

    public static bool IsServerBusy(int hresult) => ServerBusyErrors.Contains(hresult);

    public static bool IsSuccess(int resultCode) => resultCode is OrcSucceeded or OrcSucceededWithErrors;

    public const int DownloadFailed = unchecked((int)0x80240034);   // WU_E_DOWNLOAD_FAILED
    public const int FileNotFound = unchecked((int)0x80070002);     // ERROR_FILE_NOT_FOUND (the payload vanished from the cache)

    // Download failures that a second try usually fixes (owner's PC 10.11.2026: three Realtek drivers failed with 0x80240034 /
    // 0x80070002 and installed fine one minute later). Codes checked against Microsoft Learn "Windows Update error codes by
    // component" and "Delivery Optimization return values". Not retried: cancel, policy, rights, EULA, service disabled, no disk space.
    private static readonly int[] TransientDownloadErrors =
    [
        DownloadFailed, FileNotFound,
        unchecked((int)0x80246002), // WU_E_DM_INCORRECTFILEHASH (corrupt download — fetch again)
        unchecked((int)0x80246007), // WU_E_DM_NOTDOWNLOADED
        unchecked((int)0x80246008), // WU_E_DM_FAILTOCONNECTTOBITS
        unchecked((int)0x80246009), // WU_E_DM_BITSTRANSFERERROR
        unchecked((int)0x8024600A), // WU_E_DM_DOWNLOADLOCATIONCHANGED ("download must be restarted")
        unchecked((int)0x8024600B), // WU_E_DM_CONTENTCHANGED ("download must be restarted")
        unchecked((int)0x80244025), // WU_E_PT_FILE_LOCATIONS_CHANGED ("refresh and resend")
        unchecked((int)0x80D02002), // DO_E_DOWNLOAD_NO_PROGRESS (Delivery Optimization stalled)
        unchecked((int)0x80240021), // WU_E_TIME_OUT
    ];

    /// <summary>Pause before the one retry: a network failure needs time to recover, a dropped file does not.</summary>
    public static TimeSpan RetryDelayFor(IEnumerable<int> hresults) =>
        hresults.Any(h => IsNetworkError(h) || IsServerBusy(h)) ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(5);

    /// <summary>
    /// True when one automatic retry of the download is worth it: the item ended as failed (not aborted, not still running)
    /// with a network or transient download error, or with no error code at all.
    /// </summary>
    public static bool IsRetryableDownload(int resultCode, int hresult) =>
        resultCode == OrcFailed
        && (hresult == 0 || IsNetworkError(hresult) || IsServerBusy(hresult) || TransientDownloadErrors.Contains(hresult));

    /// <summary>OperationResultCode → short Russian status.</summary>
    public static string ResultText(int resultCode) => resultCode switch
    {
        OrcNotStarted => Loc.T("НЕ НАЧАТО"),
        OrcInProgress => Loc.T("ВЫПОЛНЯЕТСЯ"),
        OrcSucceeded => Loc.T("УСТАНОВЛЕНО"),
        OrcSucceededWithErrors => Loc.T("УСТАНОВЛЕНО С ОШИБКАМИ"),
        OrcFailed => Loc.T("ОШИБКА"),
        OrcAborted => Loc.T("ПРЕРВАНО"),
        _ => Loc.Tf("КОД {0}", resultCode),
    };

    /// <summary>InstallationBehavior.RebootBehavior → text.</summary>
    public static string RebootText(int rebootBehavior) => rebootBehavior switch
    {
        0 => Loc.T("без перезагрузки"),
        1 => Loc.T("нужна перезагрузка"),
        2 => Loc.T("может потребоваться перезагрузка"),
        _ => "—",
    };

    /// <summary>Windows Update HRESULT → message the user can act on.</summary>
    public static string DescribeHResult(int hresult)
    {
        if (IsNetworkError(hresult))
            return Loc.T("Нет подключения к Windows Update: проверьте интернет и DNS, затем повторите поиск.");
        return hresult switch
        {
            ServiceDisabled => Loc.T("Служба Windows Update отключена. Включите службу «Центр обновления Windows» (wuauserv) — без неё Windows не получает исправления драйверов и безопасности."),
            AccessDenied => Loc.T("Недостаточно прав: загрузка и установка драйверов требуют прав администратора."),
            OperationCancelled => Loc.T("Операция отменена."),
            NotAllowed => Loc.T("Установка сейчас невозможна: Windows уже выполняет другую установку или ожидает перезагрузки."),
            OperationInProgress or ExclusiveInstallConflict => Loc.T("Windows Update уже выполняет другую операцию. Повторите позже."),
            EulasDeclined => Loc.T("Лицензионное соглашение драйвера не принято — установка пропущена."),
            unchecked((int)0x80240021) => Loc.T("Windows Update не ответил вовремя. Повторите позже."),
            DownloadFailed or FileNotFound => Loc.T("Файл драйвера не скачался с серверов Windows Update (Lycoris уже повторил загрузку). Повторите позже; если ошибка не уходит — «Сброс Центра обновления Windows» в «Обслуживании»."),
            unchecked((int)0x8024001E) => Loc.T("Операция прервана: служба Windows Update останавливается или компьютер завершает работу."),
            unchecked((int)0x8024002E) => Loc.T("Доступ к серверам Windows Update запрещён политикой организации."),
            unchecked((int)0x80240044) => Loc.T("Недостаточно прав для операции с обновлениями всего компьютера."),
            _ => Loc.Tf("Ошибка Windows Update 0x{0:X8}.", unchecked((uint)hresult)),
        };
    }

    /// <summary>Windows Update (0x8024xxxx) and Win32 (0x8007xxxx) HRESULTs get a mapped message.</summary>
    public static bool IsMappedHResult(int hresult) => (unchecked((uint)hresult) >> 16) is 0x8024 or 0x8007;

    /// <summary>The HRESULT behind an exception (COM errors may arrive wrapped).</summary>
    public static int HResultOf(Exception ex)
    {
        if (ex is System.Reflection.TargetInvocationException { InnerException: { } inner }) ex = inner;
        return ex is WuException w ? w.Code : ex.HResult;
    }

    /// <summary>Any failure of a Windows Update operation → one Russian sentence for the UI.</summary>
    public static string DescribeException(Exception ex)
    {
        if (ex is System.Reflection.TargetInvocationException { InnerException: { } inner }) ex = inner;
        return ex switch
        {
            OperationCanceledException => DescribeHResult(OperationCancelled),
            TimeoutException => Loc.T("Windows Update не ответил вовремя. Повторите позже."),
            WuException { Code: 0 } w => w.Message,
            WuException w => DescribeHResult(w.Code),
            _ when IsMappedHResult(ex.HResult) => DescribeHResult(ex.HResult),
            _ => ex.Message,
        };
    }

    [GeneratedRegex(@"(\d+(?:\.\d+){1,3})\)?\s*$")]
    private static partial Regex TitleVersionRegex();

    /// <summary>
    /// WUA has no version property: the version is the tail of the title,
    /// e.g. "NVIDIA - Display - 32.0.15.6094" or "Logitech USB Driver Update (1.1.84.7627)".
    /// </summary>
    public static string? VersionFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var m = TitleVersionRegex().Match(title.Trim());
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Firmware (UEFI/BIOS capsules) can arrive through Windows Update as a "driver". Lycoris never flashes
    /// firmware itself (research §6) — such updates are shown but left to Windows Settings / the vendor.
    /// </summary>
    public static bool IsFirmware(string? driverClass) =>
        string.Equals(driverClass?.Trim(), "Firmware", StringComparison.OrdinalIgnoreCase);

    /// <summary>MaxDownloadSize is a DECIMAL in bytes.</summary>
    public static double SizeBytes(object? maxDownloadSize)
    {
        try
        {
            return maxDownloadSize is null ? 0 : Convert.ToDouble(maxDownloadSize, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }
}
