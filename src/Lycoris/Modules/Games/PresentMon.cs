using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Lycoris.Core;
using Lycoris.Core.Security;

namespace Lycoris.Modules.Games;

/// <summary>Where the PresentMon console is and whether it may be run.</summary>
/// <param name="ExePath">The verified console exe, or null.</param>
/// <param name="Problem">Why it cannot be used (not installed, not signed by Intel, folder writable by users); null when ready.</param>
/// <param name="Installed">The Intel PresentMon package is on the PC (even when its console is missing or refused).</param>
public sealed record PresentMonStatus(string? ExePath, string? Problem, bool Installed)
{
    public bool Ready => ExePath is not null;
}

/// <summary>
/// Intel PresentMon console (MIT, signed by Intel). The winget package <c>Intel.PresentMon</c> (catalog entry on the Apps page, machine
/// scope MSI) installs it next to the overlay app: <c>%ProgramFiles%\Intel\PresentMon\PresentMonConsoleApplication\PresentMon-&lt;ver&gt;-x64.exe</c>
/// (source: GameTechDev/PresentMon IntelPresentMon/PMInstaller/PresentMon.wxs + PMPreProcessor.GetConsoleAppFileName).
/// It records frame events through ETW, which needs administrator rights (or the «Performance Log Users» group) — Lycoris
/// runs it only elevated. Before every run: the folder must be writable only by SYSTEM/Administrators (<see cref="PathTrust"/>)
/// and the file must carry a valid Authenticode signature of Intel (<see cref="Authenticode"/>) — Smart App Control runs only
/// signed code anyway, and an elevated process never starts an unverified exe. Nothing is downloaded here: a missing PresentMon
/// is installed through the Apps page (winget, hash-checked).
/// </summary>
public static partial class PresentMon
{
    public const string WingetId = "Intel.PresentMon";
    public const string ExpectedSigner = "Intel";

    [GeneratedRegex(@"^PresentMon-(\d+(?:\.\d+){1,3})-x64\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsoleName();

    public static string InstallRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Intel", "PresentMon");

    public static string ConsoleDir => Path.Combine(InstallRoot, "PresentMonConsoleApplication");

    /// <summary>The newest «PresentMon-x.y.z-x64.exe» among file names (other names are ignored). Pure.</summary>
    public static string? PickConsole(IEnumerable<string> fileNames)
    {
        string? best = null;
        Version? bestVer = null;
        foreach (var f in fileNames)
        {
            var name = Path.GetFileName(f);
            var m = ConsoleName().Match(name);
            if (!m.Success || !Version.TryParse(m.Groups[1].Value, out var v)) continue;
            if (bestVer is null || v > bestVer) (best, bestVer) = (f, v);
        }
        return best;
    }

    /// <summary>Decision on a found console exe, from facts already read. Pure — unit-tested.</summary>
    public static string? Refusal(string? whyUserWritable, SignatureState signature, string? signer)
    {
        if (whyUserWritable is not null)
            return Loc.Tf("Папка PresentMon доступна для записи обычным программам ({0}) — Lycoris не запустит оттуда файл с правами администратора.", whyUserWritable);
        if (signature != SignatureState.Valid)
            return signature switch
            {
                SignatureState.Unsigned => Loc.T("Файл PresentMon не подписан — запуск отклонён."),
                SignatureState.Tampered => Loc.T("Файл PresentMon изменён после подписи — запуск отклонён."),
                _ => Loc.T("Подпись файла PresentMon не проверена — запуск отклонён."),
            };
        if (signer is null || !signer.Contains(ExpectedSigner, StringComparison.OrdinalIgnoreCase))
            return Loc.Tf("PresentMon подписан не Intel ({0}) — запуск отклонён.", signer ?? "?");
        return null;
    }

    /// <summary>Finds and verifies the console. Blocking (file system, WinVerifyTrust) — call from a background thread.</summary>
    public static PresentMonStatus Locate()
    {
        var installed = Directory.Exists(InstallRoot);
        string? exe = null;
        try
        {
            if (Directory.Exists(ConsoleDir)) exe = PickConsole(Directory.EnumerateFiles(ConsoleDir, "PresentMon-*-x64.exe"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("PresentMon: папка не читается — {0}", ex.Message);
        }
        if (exe is null)
            return new(null, installed
                ? Loc.T("Intel PresentMon установлен без консольной программы замера — переустановите его на странице «Приложения».")
                : Loc.T("Для замера FPS нужен Intel PresentMon — бесплатная программа Intel с подписью. Установка — на странице «Приложения»."), installed);

        var refusal = Refusal(PathTrust.WhyUserWritable(exe), Authenticode.Check(exe), Authenticode.SignerName(exe));
        return refusal is null ? new(exe, null, true) : new(null, refusal, true);
    }

    /// <summary>
    /// Captures <paramref name="seconds"/> seconds of frames of <paramref name="processExe"/>. Re-verifies the exe right before
    /// starting it. The CSV goes to the private temp folder (admin-only when elevated) and is deleted afterwards.
    /// Returns the stats, or an error text for the user.
    /// </summary>
    public static async Task<(FpsStats? Stats, string? Error)> CaptureAsync(string processExe, int seconds, CancellationToken ct)
    {
        if (!Elevation.IsElevated) return (null, Loc.T("Замер FPS нужен с правами администратора (PresentMon читает события Windows)."));
        if (FpsMeasure.NormalizeProcess(processExe) is not { } exeName) return (null, Loc.T("Недопустимое имя процесса."));
        var status = await Task.Run(Locate, ct).ConfigureAwait(false);
        if (status.ExePath is not { } exe) return (null, status.Problem);
        if (SafeIo.NewPrivateTempFile(".csv") is not { } csv) return (null, Loc.T("Нет личной временной папки Lycoris для файла замера."));
        try
        {
            var result = await ProcessRunner.RunAsync(exe, FpsMeasure.BuildArguments(exeName, seconds, csv), new ProcessRunOptions
            {
                Encoding = OutputEncoding.Utf8,
                Timeout = TimeSpan.FromSeconds(seconds + 30),
                WorkingDirectory = Path.GetDirectoryName(exe),
            }, ct).ConfigureAwait(false);
            if (result.Cancelled) return (null, Loc.T("Замер отменён."));
            if (!File.Exists(csv))
            {
                var tail = result.Combined.Trim();
                if (tail.Length > 300) tail = tail[^300..];
                Log.Warn("PresentMon: файл замера не создан (код {0}) {1}", result.ExitCode, tail);
                return (null, Loc.Tf("PresentMon не записал кадры {0}: игра не запущена или не рисует кадры (свёрнута, на паузе).", exeName));
            }
            FpsStats? stats;
            await using (var fs = new FileStream(csv, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
                stats = FpsMeasure.ParseCsv(reader);
            return stats is null
                ? (null, Loc.Tf("Слишком мало кадров от {0} — игра должна быть на экране и не на паузе весь замер.", exeName))
                : (stats, null);
        }
        finally
        {
            try { File.Delete(csv); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
