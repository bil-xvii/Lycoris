using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Monolith.Core;

public sealed record WingetResult(int ExitCode, string Output, bool Cancelled = false)
{
    public bool Success => ExitCode is 0 || WingetClient.IsBenign(ExitCode);
    public string Message => Cancelled ? "Отменено" : WingetClient.Describe(ExitCode);
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

    private static IEnumerable<string> Common => ["--accept-source-agreements", "--disable-interactivity"];

    public static Task<WingetResult> InstallAsync(string id, string source = "winget", Action<string>? output = null, CancellationToken ct = default)
    {
        var args = new List<string> { "install", "--id", id, "--exact", "--source", source, "--accept-package-agreements" };
        if (source == "winget") args.Add("--silent");
        args.AddRange(Common);
        return RunAsync(args, output, ct);
    }

    public static Task<WingetResult> UpgradeAsync(string id, string source = "winget", Action<string>? output = null, CancellationToken ct = default)
    {
        var args = new List<string> { "upgrade", "--id", id, "--exact", "--source", source, "--accept-package-agreements" };
        if (source == "winget") args.Add("--silent");
        args.AddRange(Common);
        return RunAsync(args, output, ct);
    }

    public static Task<WingetResult> UninstallAsync(string id, Action<string>? output = null, CancellationToken ct = default)
    {
        var args = new List<string> { "uninstall", "--id", id, "--exact", "--silent" };
        args.AddRange(Common);
        return RunAsync(args, output, ct);
    }

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
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("Sources", out var sources)) return result;
            foreach (var s in sources.EnumerateArray())
            {
                if (!s.TryGetProperty("Packages", out var pkgs)) continue;
                foreach (var p in pkgs.EnumerateArray())
                {
                    var id = p.TryGetProperty("PackageIdentifier", out var i) ? i.GetString() : null;
                    var ver = p.TryGetProperty("Version", out var v) ? v.GetString() : "";
                    if (!string.IsNullOrEmpty(id)) result[id] = ver ?? "";
                }
            }
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

    /// <summary>Runs winget with the given arguments (serialized; retried once on internal error).</summary>
    public static async Task<WingetResult> RunAsync(IEnumerable<string> args, Action<string>? output, CancellationToken ct, bool log = true)
    {
        var exe = ExePath;
        if (exe is null) return new WingetResult(-1, "winget не найден. Установите «Установщик приложений» из Microsoft Store.");
        var list = args.ToList();

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
                    OnOutput = line =>
                    {
                        if (IsNoise(line)) return;
                        output?.Invoke(line);
                    },
                }, ct).ConfigureAwait(false);

                var text = string.Join(Environment.NewLine, r.Combined.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !IsNoise(l)));
                if (r.Cancelled) return new WingetResult(r.ExitCode, text, Cancelled: true);
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

    public static string Describe(int code) => code switch
    {
        0 => "готово",
        UpdateNotApplicable => "уже последняя версия",
        AlreadyInstalled => "уже установлено",
        _ when code == unchecked((int)0x8A15010D) => "установлена другая версия",
        _ when code == unchecked((int)0x8A15010E) => "установлена более новая версия",
        NoApplicationsFound => "пакет не найден",
        _ when code == unchecked((int)0x8A150016) => "найдено несколько пакетов",
        _ when code == unchecked((int)0x8A150010) => "нет подходящего установщика для этой системы",
        ElevationProhibited => "установщик запрещает запуск от администратора",
        _ when code == unchecked((int)0x8A150019) => "нужны права администратора",
        _ when code == unchecked((int)0x8A150006) => "установщик завершился с ошибкой",
        _ when code == unchecked((int)0x8A150008) || code == unchecked((int)0x8A150107) => "ошибка загрузки — проверьте интернет",
        _ when code == unchecked((int)0x8A150011) => "хэш установщика не совпал — установка заблокирована ради безопасности",
        _ when code == unchecked((int)0x8A150045) => "не удалось открыть источник winget",
        _ when code == unchecked((int)0x8A150007) => "нужно обновить «Установщик приложений»",
        _ when code == unchecked((int)0x8A150101) || code == unchecked((int)0x8A150103) || code == unchecked((int)0x8A150111) => "программа сейчас запущена — закройте её",
        _ when code == unchecked((int)0x8A150102) => "уже идёт другая установка",
        RebootRequiredToFinish => "готово, нужна перезагрузка",
        RebootRequiredToInstall => "нужна перезагрузка перед установкой",
        _ when code == unchecked((int)0x8A15010C) => "отменено пользователем",
        _ when code == unchecked((int)0x8A15010F) || code == unchecked((int)0x8A15003A) => "заблокировано политикой",
        _ when code == unchecked((int)0x8A150105) => "недостаточно места на диске",
        _ when code == unchecked((int)0x8A150113) => "система не поддерживается",
        InternalError => "внутренняя ошибка winget",
        -1 => "winget недоступен",
        _ => $"код {code} (0x{unchecked((uint)code):X8})",
    };
}
