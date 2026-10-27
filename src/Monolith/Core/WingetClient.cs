using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Monolith.Core;

public sealed record WingetResult(int ExitCode, string Output, bool Cancelled = false)
{
    /// <summary>
    /// A modifying run (install/upgrade/uninstall) exceeded the timeout and was left running, not killed:
    /// the result is unknown and has to be re-detected later.
    /// </summary>
    public bool StillRunning { get; init; }
    public bool Success => !StillRunning && (ExitCode is 0 || WingetClient.IsBenign(ExitCode));
    public string Message => Cancelled ? "Отменено" : StillRunning ? "ещё выполняется — результат проверим заново" : WingetClient.Describe(ExitCode);
}

/// <summary>One package to install or upgrade, plus the optional installer filters.</summary>
public sealed record WingetPackageRequest(string Id, string Source = "winget")
{
    /// <summary>--installer-type (e.g. "exe" for Microsoft.DirectX, so winget runs dxwebsetup.exe instead of the MSIX framework).</summary>
    public string? InstallerType { get; init; }

    /// <summary>--architecture (x86 / x64 / arm64).</summary>
    public string? Architecture { get; init; }

    /// <summary>
    /// --location as a template with one known root ("%ProgramFiles(x86)%\Battle.net", "%LOCALAPPDATA%\Programs\X"):
    /// required by manifests with InstallLocationRequired, which winget cannot ask for with --disable-interactivity
    /// (0x8A15005F). Expanded by MONOLITH, or by the de-elevated cmd for the signed-in user (<see cref="WingetClient.IsValidLocationTemplate"/>).
    /// </summary>
    public string? Location { get; init; }
}

/// <summary>
/// Thin wrapper over the winget CLI (Windows Package Manager).
/// Calls are serialized (parallel winget runs produce spurious internal errors) and retried once on 0x8A150001.
/// Never passes --ignore-security-hash, --allow-reboot or --force.
/// </summary>
public static partial class WingetClient
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public const int UpdateNotApplicable = unchecked((int)0x8A15002B);
    public const int AlreadyInstalled = unchecked((int)0x8A150061);
    public const int NoApplicationsFound = unchecked((int)0x8A150014);
    public const int InternalError = unchecked((int)0x8A150001);
    public const int ElevationProhibited = unchecked((int)0x8A150056);
    public const int RebootRequiredToFinish = unchecked((int)0x8A150109);
    public const int RebootRequiredToInstall = unchecked((int)0x8A15010A);
    public const int RebootInitiated = unchecked((int)0x8A15010B);
    public const int NoApplicableInstaller = unchecked((int)0x8A150010);
    public const int UpgradeVersionNotNewer = unchecked((int)0x8A15004F);
    public const int UpgradeVersionUnknown = unchecked((int)0x8A150050);
    public const int PackageIsPinned = unchecked((int)0x8A150068);
    /// <summary>APPINSTALLER_CLI_ERROR_INSTALL_LOCATION_REQUIRED: the manifest needs --location and winget may not ask.</summary>
    public const int InstallLocationRequired = unchecked((int)0x8A15005F);
    /// <summary>APPINSTALLER_CLI_ERROR_INSTALLER_HASH_MISMATCH: the downloaded installer is not the file the manifest describes.</summary>
    public const int InstallerHashMismatch = unchecked((int)0x8A150011);
    public const int DownloadFailed = unchecked((int)0x8A150008);
    public const int NoNetwork = unchecked((int)0x8A150107);
    /// <summary>HTTP_E_STATUS_NOT_FOUND: the installer link in the manifest answers 404.</summary>
    public const int HttpNotFound = unchecked((int)0x80190194);

    /// <summary>Oldest winget client MONOLITH relies on (export --include-versions, --disable-interactivity).</summary>
    public static readonly Version MinimumClientVersion = new(1, 6);

    public static string? ExePath
    {
        get
        {
            var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
            return File.Exists(alias) ? alias : null;
        }
    }

    public static async Task<string?> GetVersionAsync()
    {
        if (ExePath is null) return null;
        var r = await RunAsync(["--version"], null, CancellationToken.None, log: false).ConfigureAwait(false);
        return r.ExitCode == 0 ? r.Output.Trim().TrimStart('v') : null;
    }

    public static async Task<bool> IsAvailableAsync() => await GetVersionAsync().ConfigureAwait(false) is not null;

    /// <summary>True when the reported client version ("1.11.510" / "v1.29.380") is older than <see cref="MinimumClientVersion"/>.</summary>
    public static bool IsClientOutdated(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        var v = version.Trim().TrimStart('v', 'V');
        var cut = v.IndexOfAny(['-', '+', ' ']);
        if (cut > 0) v = v[..cut];
        return Version.TryParse(v, out var parsed) && parsed < MinimumClientVersion;
    }

    private static IEnumerable<string> Common => ["--accept-source-agreements", "--disable-interactivity"];

    public static Task<WingetResult> InstallAsync(string id, string source = "winget", Action<string>? output = null, CancellationToken ct = default)
        => InstallPackageAsync(new WingetPackageRequest(id, source), output, ct);

    public static Task<WingetResult> UpgradeAsync(string id, string source = "winget", Action<string>? output = null, CancellationToken ct = default)
        => UpgradePackageAsync(new WingetPackageRequest(id, source), output, ct);

    /// <summary>Installs a package with optional installer filters (architecture, installer type, install location).</summary>
    public static Task<WingetResult> InstallPackageAsync(WingetPackageRequest request, Action<string>? output = null, CancellationToken ct = default)
        => RunBuiltAsync(() => BuildInstallArgs(request), output, ct);

    /// <summary>Upgrades one package (UpdateNotApplicable = already the newest version).</summary>
    public static Task<WingetResult> UpgradePackageAsync(WingetPackageRequest request, Action<string>? output = null, CancellationToken ct = default)
        => RunBuiltAsync(() => BuildUpgradeArgs(request), output, ct);

    /// <summary>Invalid requests become a failed result instead of an exception (the caller always gets a WingetResult).</summary>
    private static Task<WingetResult> RunBuiltAsync(Func<IReadOnlyList<string>> build, Action<string>? output, CancellationToken ct)
    {
        IReadOnlyList<string> args;
        try
        {
            args = build();
        }
        catch (ArgumentException ex)
        {
            Log.Warn($"winget: {ex.Message}");
            return Task.FromResult(new WingetResult(NoApplicationsFound, ex.Message));
        }
        return RunAsync(args, output, ct);
    }

    public static Task<WingetResult> UninstallAsync(string id, Action<string>? output = null, CancellationToken ct = default)
    {
        var args = new List<string> { "uninstall", "--id", id, "--exact", "--silent" };
        args.AddRange(Common);
        return RunAsync(args, output, ct);
    }

    /// <summary>
    /// Arguments for "winget install". Exact id match with an explicit source; --silent for the winget source only
    /// (Store installs have no silent switch). Never adds --scope (about 15 manifests declare none and would fail),
    /// --force, --ignore-security-hash or --allow-reboot.
    /// </summary>
    /// <param name="keepLocationTemplate">
    /// true for the de-elevated runner: --location stays "%LOCALAPPDATA%\…" and the signed-in user's cmd expands it
    /// (an elevated MONOLITH may run under another administrator's profile). Otherwise MONOLITH expands it.
    /// </param>
    public static IReadOnlyList<string> BuildInstallArgs(WingetPackageRequest request, bool keepLocationTemplate = false)
    {
        Validate(request);
        var args = new List<string> { "install", "--id", request.Id, "--exact", "--source", request.Source, "--accept-package-agreements" };
        if (request.Source == "winget") args.Add("--silent");
        AddFilters(args, request, keepLocationTemplate);
        args.AddRange(Common);
        return args;
    }

    /// <summary>Arguments for "winget upgrade" of one package (same rules as <see cref="BuildInstallArgs"/>).</summary>
    public static IReadOnlyList<string> BuildUpgradeArgs(WingetPackageRequest request, bool keepLocationTemplate = false)
    {
        Validate(request);
        var args = new List<string> { "upgrade", "--id", request.Id, "--exact", "--source", request.Source, "--accept-package-agreements" };
        if (request.Source == "winget") args.Add("--silent");
        AddFilters(args, request, keepLocationTemplate);
        args.AddRange(Common);
        return args;
    }

    private static void AddFilters(List<string> args, WingetPackageRequest request, bool keepLocationTemplate)
    {
        if (!string.IsNullOrEmpty(request.Architecture))
        {
            args.Add("--architecture");
            args.Add(request.Architecture);
        }
        if (!string.IsNullOrEmpty(request.InstallerType))
        {
            args.Add("--installer-type");
            args.Add(request.InstallerType);
        }
        if (!string.IsNullOrEmpty(request.Location))
        {
            args.Add("--location");
            args.Add(keepLocationTemplate
                ? request.Location
                : ExpandLocation(request.Location) ?? throw new ArgumentException($"Недопустимая папка установки: «{request.Location}»"));
        }
    }

    /// <summary>
    /// Install-location templates MONOLITH may pass: one known root (%ProgramFiles%, %ProgramFiles(x86)%, %LOCALAPPDATA%)
    /// followed by 1–4 plain folder names (letters, digits, space . _ + ( ) -; no "..", no trailing dot or space).
    /// Nothing else can reach winget's --location or the de-elevated cmd line.
    /// </summary>
    public static bool IsValidLocationTemplate(string? template)
    {
        if (LocationRoot(template) is not { } root) return false;
        var tail = template![root.Variable.Length..];
        if (!LocationTailRegex().IsMatch(tail)) return false;
        foreach (var segment in tail.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (segment.EndsWith('.') || segment.EndsWith(' ')) return false;
        return true;
    }

    private static readonly (string Variable, Environment.SpecialFolder Folder)[] LocationRoots =
    [
        ("%ProgramFiles%", Environment.SpecialFolder.ProgramFiles),
        ("%ProgramFiles(x86)%", Environment.SpecialFolder.ProgramFilesX86),
        ("%LOCALAPPDATA%", Environment.SpecialFolder.LocalApplicationData),
    ];

    private static (string Variable, Environment.SpecialFolder Folder)? LocationRoot(string? template) =>
        template is null ? null
            : LocationRoots.Where(r => template.StartsWith(r.Variable + @"\", StringComparison.OrdinalIgnoreCase))
                .Select(r => ((string, Environment.SpecialFolder)?)r).FirstOrDefault();

    /// <summary>"\Folder\Sub": 1–4 plain ASCII folder names (case-sensitive class: no Unicode case-folding surprises).</summary>
    [GeneratedRegex(@"^(\\[A-Za-z0-9][A-Za-z0-9 ._+()\-]{0,63}){1,4}$")]
    private static partial Regex LocationTailRegex();

    /// <summary>
    /// Expands a location template for this process ("%ProgramFiles(x86)%\Battle.net" → "C:\Program Files (x86)\Battle.net").
    /// Null when the template is invalid or the result would leave its root folder.
    /// </summary>
    public static string? ExpandLocation(string? template)
    {
        if (!IsValidLocationTemplate(template) || LocationRoot(template) is not { } r) return null;
        var root = Environment.GetFolderPath(r.Folder);
        if (string.IsNullOrEmpty(root) || !Path.IsPathFullyQualified(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        try
        {
            var rootDir = Path.TrimEndingDirectorySeparator(root);
            var full = Path.GetFullPath(rootDir + template![r.Variable.Length..]);
            return full.StartsWith(rootDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static readonly HashSet<string> KnownSources = new(StringComparer.Ordinal) { "winget", "msstore" };
    private static readonly HashSet<string> KnownArchitectures = new(StringComparer.Ordinal) { "x86", "x64", "arm64" };
    private static readonly HashSet<string> KnownInstallerTypes = new(StringComparer.Ordinal)
        { "exe", "msi", "msix", "inno", "nullsoft", "wix", "burn", "portable", "zip", "appx" };

    private static void Validate(WingetPackageRequest request)
    {
        if (!IsValidPackageId(request.Id)) throw new ArgumentException($"Недопустимый идентификатор пакета: «{request.Id}»");
        if (!KnownSources.Contains(request.Source)) throw new ArgumentException($"Неизвестный источник winget: «{request.Source}»");
        if (request.Architecture is { Length: > 0 } a && !KnownArchitectures.Contains(a)) throw new ArgumentException($"Неизвестная архитектура: «{a}»");
        if (request.InstallerType is { Length: > 0 } t && !KnownInstallerTypes.Contains(t)) throw new ArgumentException($"Неизвестный тип установщика: «{t}»");
        if (request.Location is { Length: > 0 } l && !IsValidLocationTemplate(l)) throw new ArgumentException($"Недопустимая папка установки: «{l}»");
    }

    /// <summary>
    /// Package identifiers are plain tokens ("Valve.Steam", "Microsoft.VCRedist.2015+.x64", "9MV0B5HZVK9Z").
    /// Anything with spaces, quotes or shell metacharacters is rejected.
    /// </summary>
    public static bool IsValidPackageId(string? id) => id is not null && PackageIdRegex().IsMatch(id);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.+_\-]{0,127}$")]
    private static partial Regex PackageIdRegex();

    /// <summary>Exit code of "winget list --id X -e": 0 = installed.</summary>
    public static async Task<bool> IsInstalledAsync(string id, CancellationToken ct = default)
    {
        var args = new List<string> { "list", "--id", id, "--exact" };
        args.AddRange(Common);
        var r = await RunAsync(args, null, ct, log: false).ConfigureAwait(false);
        return r.ExitCode == 0;
    }

    /// <summary>
    /// Installed packages that winget can match to a source: id → version.
    /// Uses "winget export" (structured JSON) — never parses the localized table output.
    /// </summary>
    public static async Task<Dictionary<string, string>> ExportInstalledAsync(CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var file = Path.Combine(Path.GetTempPath(), $"monolith_wg_{Guid.NewGuid():N}.json");
        try
        {
            var args = new List<string> { "export", "-o", file, "--include-versions" };
            args.AddRange(Common);
            await RunAsync(args, null, ct, log: false).ConfigureAwait(false);
            if (!File.Exists(file)) return result;
            return ParseExport(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"winget export: {ex.Message}");
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
        return result;
    }

    /// <summary>
    /// Parses a "winget export" file (schema https://aka.ms/winget-packages.schema.2.0.json):
    /// Sources[].Packages[].PackageIdentifier / Version. Malformed input gives an empty map.
    /// </summary>
    public static Dictionary<string, string> ParseExport(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("Sources", out var sources)
                || sources.ValueKind != JsonValueKind.Array) return result;
            foreach (var s in sources.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object || !s.TryGetProperty("Packages", out var pkgs) || pkgs.ValueKind != JsonValueKind.Array) continue;
                foreach (var p in pkgs.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Object) continue;
                    var id = p.TryGetProperty("PackageIdentifier", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                    var ver = p.TryGetProperty("Version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                    if (!string.IsNullOrEmpty(id)) result[id] = ver ?? "";
                }
            }
        }
        catch (JsonException ex)
        {
            Log.Warn($"winget export: повреждённый JSON ({ex.Message})");
        }
        return result;
    }

    /// <summary>
    /// Runs <paramref name="work"/> while holding the winget gate, so a winget process started some other way
    /// (e.g. de-elevated through the shell) never overlaps with MONOLITH's own winget calls.
    /// </summary>
    public static async Task<T> RunExclusiveAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await work(ct).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>install / upgrade / uninstall / import / repair change the system; list / show / export / search do not.</summary>
    public static bool IsModifying(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].ToLowerInvariant() is "install" or "add" or "upgrade" or "update" or "uninstall" or "remove" or "rm"
            or "import" or "repair" or "configure" or "pin" or "source";

    /// <summary>Runs winget with the given arguments (serialized; retried once on internal error).</summary>
    public static async Task<WingetResult> RunAsync(IEnumerable<string> args, Action<string>? output, CancellationToken ct, bool log = true)
    {
        var exe = ExePath;
        if (exe is null) return new WingetResult(-1, "winget не найден. Установите «Установщик приложений» из Microsoft Store.");
        var list = args.ToList();
        if (SafetyLock.IsLocked && IsModifying(list))
            return new WingetResult(-1, "Установка программ заблокирована: режим самопроверки.");

        // Killing winget mid-install would kill the installer it started and can leave a half-installed program:
        // a modifying run that exceeds the timeout is left running and reported as "still running".
        var modifying = IsModifying(list);
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var r = await ProcessRunner.RunAsync(exe, list, new ProcessRunOptions
                {
                    Encoding = OutputEncoding.Utf8,
                    LogCommand = log,
                    Timeout = TimeSpan.FromMinutes(30),
                    KillOnTimeout = !modifying,
                    OnOutput = line =>
                    {
                        if (IsNoise(line)) return;
                        output?.Invoke(line);
                    },
                }, ct).ConfigureAwait(false);

                var text = string.Join(Environment.NewLine, r.Combined.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !IsNoise(l)));
                if (r.Cancelled) return new WingetResult(r.ExitCode, text, Cancelled: true);
                if (r.StillRunning)
                {
                    Log.Warn($"winget {list.FirstOrDefault()} {ArgId(list)}: идёт дольше 30 минут — MONOLITH перестал ждать, процесс не прерывался.");
                    return new WingetResult(-1, text) { StillRunning = true };
                }
                if (r.ExitCode == InternalError && attempt == 0)
                {
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                    continue;
                }
                if (log)
                {
                    if (r.ExitCode == 0 || IsBenign(r.ExitCode)) Log.Success($"winget {list.FirstOrDefault()} {ArgId(list)}: {Describe(r.ExitCode)}");
                    else Log.Warn($"winget {list.FirstOrDefault()} {ArgId(list)}: {Describe(r.ExitCode)}");
                }
                return new WingetResult(r.ExitCode, text);
            }
            return new WingetResult(InternalError, "");
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string ArgId(List<string> args)
    {
        var i = args.IndexOf("--id");
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : "";
    }

    /// <summary>Spinner characters and progress bars that winget prints even when redirected.</summary>
    public static bool IsNoise(string line)
    {
        var t = line.Trim();
        if (t.Length == 0) return true;
        if (SpinnerRegex().IsMatch(t)) return true;
        if (t.Contains('█') || t.Contains('▒')) return true;
        return false;
    }

    [GeneratedRegex(@"^[-\\|/]+$")]
    private static partial Regex SpinnerRegex();

    /// <summary>Non-zero codes that still mean "the package is there and fine".</summary>
    public static bool IsBenign(int code) => code is UpdateNotApplicable or AlreadyInstalled
        || code == unchecked((int)0x8A15010D) || code == unchecked((int)0x8A15010E)
        || code == RebootRequiredToFinish;

    /// <summary>Upgrade was deliberately not attempted: version not newer, unknown installed version or a pinned package.</summary>
    public static bool IsSkipped(int code) => code is UpgradeVersionNotNewer or UpgradeVersionUnknown or PackageIsPinned;

    /// <summary>HTTP_E_STATUS_* (0x8019xxxx): the download server answered with an HTTP error; low word = status code.</summary>
    public static bool IsHttpStatus(int code) => (unchecked((uint)code) & 0xFFFF0000u) == 0x80190000u && (code & 0xFFFF) is >= 300 and <= 599;

    /// <summary>The installer could not be downloaded (network, server error, dead link).</summary>
    public static bool IsDownloadProblem(int code) => code is DownloadFailed or NoNetwork || IsHttpStatus(code);

    /// <summary>The installer needs (or started) a reboot. MONOLITH never passes --allow-reboot.</summary>
    public static bool IsRebootRequired(int code) => code is RebootRequiredToFinish or RebootRequiredToInstall or RebootInitiated;

    public static string Describe(int code) => code switch
    {
        0 => "готово",
        UpdateNotApplicable => "уже последняя версия",
        AlreadyInstalled => "уже установлено",
        _ when code == unchecked((int)0x8A15010D) => "установлена другая версия",
        _ when code == unchecked((int)0x8A15010E) => "установлена более новая версия",
        NoApplicationsFound => "пакет не найден",
        _ when code == unchecked((int)0x8A150016) => "найдено несколько пакетов",
        NoApplicableInstaller => "нет подходящего установщика для этой системы",
        ElevationProhibited => "установщик запрещает запуск от администратора",
        _ when code == unchecked((int)0x8A150019) => "нужны права администратора",
        _ when code == unchecked((int)0x8A150006) => "установщик завершился с ошибкой",
        DownloadFailed or NoNetwork => "ошибка загрузки — проверьте интернет",
        InstallerHashMismatch => "хэш установщика не совпал с каталогом winget — установка заблокирована ради безопасности",
        InstallLocationRequired => "нужно указать папку установки",
        HttpNotFound => "файл установщика не найден на сервере (ошибка 404)",
        _ when IsHttpStatus(code) => $"сервер установщика ответил ошибкой HTTP {code & 0xFFFF}",
        _ when code == unchecked((int)0x8A150049) => "установщик MSI завершился с ошибкой",
        _ when code == unchecked((int)0x8A150052) => "не удалось установить портативный пакет",
        _ when code == unchecked((int)0x8A15005E) => "сертификат сервера не совпал — соединение перехвачено",
        _ when code == unchecked((int)0x8A150060) => "архив не прошёл проверку антивирусом",
        _ when code == unchecked((int)0x8A150104) => "в системе не хватает зависимости пакета",
        _ when code == unchecked((int)0x8A150106) => "не хватает оперативной памяти",
        _ when code == unchecked((int)0x8A150108) => "установщик завершился с ошибкой и просит обратиться к производителю",
        _ when code == unchecked((int)0x8A150110) => "не удалось установить зависимости пакета",
        _ when code == unchecked((int)0x8A150112) => "установщик не принял параметры",
        _ when code == unchecked((int)0x8A150114) => "установщик не умеет обновлять установленную версию",
        _ when code == unchecked((int)0x8A150115) => "установщик завершился с собственной ошибкой",
        _ when code == unchecked((int)0x8A15004B) => "не удалось открыть источники winget",
        _ when code == unchecked((int)0x8A150002) => "winget не принял параметры команды",
        _ when code == unchecked((int)0x8A150005) || code == unchecked((int)0x8A15006A) => "winget прерван",
        _ when code == unchecked((int)0x8A150041) => "не приняты условия пакета",
        _ when code == unchecked((int)0x8A150046) => "не приняты условия источника",
        _ when code == unchecked((int)0x8A150045) => "не удалось открыть источник winget",
        _ when code == unchecked((int)0x8A150007) => "нужно обновить «Установщик приложений»",
        UpgradeVersionNotNewer => "доступная версия не новее установленной",
        UpgradeVersionUnknown => "версия установленной программы неизвестна — обновление пропущено",
        PackageIsPinned => "пакет закреплён — обновление пропущено",
        _ when code == unchecked((int)0x8A150101) || code == unchecked((int)0x8A150103) || code == unchecked((int)0x8A150111) => "программа сейчас запущена — закройте её",
        _ when code == unchecked((int)0x8A150102) => "уже идёт другая установка",
        RebootRequiredToFinish => "готово, нужна перезагрузка",
        RebootRequiredToInstall => "нужна перезагрузка перед установкой",
        RebootInitiated => "установщик начал перезагрузку",
        _ when code == unchecked((int)0x8A15010C) => "отменено пользователем",
        _ when code == unchecked((int)0x8A15010F) || code == unchecked((int)0x8A15003A) => "заблокировано политикой",
        _ when code == unchecked((int)0x8A15001B) => "Microsoft Store заблокирован политикой",
        _ when code == unchecked((int)0x8A15001C) => "приложение Microsoft Store заблокировано политикой",
        _ when code == unchecked((int)0x8A150083) => "не удалось получить лицензию Microsoft Store",
        _ when code == unchecked((int)0x8A150105) => "недостаточно места на диске",
        _ when code == unchecked((int)0x8A150113) => "система не поддерживается",
        InternalError => "внутренняя ошибка winget",
        -1 => "winget недоступен",
        _ => $"код {code} (0x{unchecked((uint)code):X8})",
    };
}
