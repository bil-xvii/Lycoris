using System.IO;
using System.Security.Cryptography;

namespace Monolith.Core.Security;

/// <summary>What <see cref="SelfIntegrity.Evaluate"/> decided about relaunching with administrator rights.</summary>
/// <param name="Allowed">False: do not start the elevated copy; <paramref name="Reason"/> says why (Russian, ready to show).</param>
/// <param name="Warning">Allowed, but the user should know (the program lies in a folder any program can write to).</param>
public sealed record ElevationVerdict(bool Allowed, string? Reason = null, string? Warning = null);

/// <summary>The facts the verdict is made of (collected once at startup and once more right before the UAC prompt).</summary>
public sealed record SelfFacts(
    string? HashAtStart,
    string? HashNow,
    bool HostileLocation,
    SignatureState Signature,
    IReadOnlyList<string> BadNativeLibraries,
    string? WritableReason);

/// <summary>
/// Lycoris's trust in itself. UAC binds to the PATH of the exe, not to its bytes: a program running as the same user can
/// swap a Lycoris.exe that lies in a writable folder (Desktop, Downloads) for its own file before the user presses
/// «Yes». We cannot make that impossible from inside the file — what we can do is (1) record the exe's SHA-256 at start and
/// refuse to ask for rights when the file on disk is no longer the file we started from, (2) refuse to elevate from places
/// anyone controls (network share, Temp, removable media), (3) refuse when a signed exe no longer matches its own
/// signature, or when a native library the single-file runtime extracted into %TEMP% is no longer Microsoft-signed,
/// and (4) tell the user, once and plainly, to keep the program in a protected folder (Program Files).
/// </summary>
public static class SelfIntegrity
{
    private static readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static int _started;

    /// <summary>SHA-256 of the exe at startup (lower-case hex), null until computed or when it cannot be read.</summary>
    public static string? HashAtStart { get; private set; }

    public static SignatureState Signature { get; private set; } = SignatureState.Error;

    public static string? SignerName { get; private set; }

    /// <summary>Why the exe's folder is writable by ordinary programs (null = protected folder).</summary>
    public static string? WritableReason { get; private set; }

    public static bool HostileLocation { get; private set; }

    /// <summary>Native libraries unpacked by the single-file runtime that failed their signature check (empty = fine / not applicable).</summary>
    public static IReadOnlyList<string> BadNativeLibraries { get; private set; } = [];

    /// <summary>True when the exe is in a folder that ordinary programs can write to (Desktop, Downloads, a user profile).</summary>
    public static bool InWritableFolder => WritableReason is not null;

    /// <summary>
    /// Background start-up check: hash, signature, location, extracted libraries. Writes one diagnostics block into the log.
    /// Never throws; the verdict is only ever used to REFUSE or WARN.
    /// </summary>
    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                var exe = AppPaths.ExecutablePath;
                HashAtStart = Sha256OfFile(exe);
                Signature = Authenticode.Check(exe);
                SignerName = Signature is SignatureState.Valid or SignatureState.Untrusted or SignatureState.Tampered ? Authenticode.SignerName(exe) : null;
                HostileLocation = PathTrust.IsHostileLocation(exe);
                WritableReason = PathTrust.WhyUserWritable(exe);
                BadNativeLibraries = BundleGuard.FindUntrustedLibraries();

                Log.Info("Целостность: {0}, SHA-256 {1}", exe, HashAtStart ?? "—");
                Log.Info("Подпись программы: {0}{1}", Signature, SignerName is null ? "" : " (" + SignerName + ")");
                if (Signature == SignatureState.Tampered)
                    Log.Error("Цифровая подпись Lycoris.exe не совпадает с файлом — файл изменён после подписания.");
                if (HostileLocation)
                    Log.Warn("Lycoris запущен из сетевой, временной или съёмной папки — запрос прав администратора будет отклонён.");
                else if (WritableReason is not null)
                    Log.Warn("Lycoris лежит в папке, куда могут писать обычные программы ({0}). Надёжнее держать его в Program Files.", WritableReason);
                foreach (var bad in BadNativeLibraries)
                    Log.Error("Распакованная библиотека не прошла проверку подписи: {0}", bad);
                foreach (var name in EnvironmentGuard.PresentInThisProcess())
                    Log.Warn("В окружении задана переменная, подгружающая код в .NET или дочерние программы: {0}", name);
                var h = Hardening.Current;
                Log.Info("Защита загрузки DLL: каталоги ограничены = {0}, доверенных каталогов = {1}, политика образов = {2}{3}",
                    h.DllDirectoriesRestricted, h.TrustedNativeDirectories, h.ImageLoadPolicy, h.Details.Length > 0 ? " (" + h.Details + ")" : "");
            }
            catch (Exception ex)
            {
                Log.Warn("Проверка целостности не выполнена: {0}", ex.Message);
            }
            finally
            {
                Ready.TrySetResult();
            }
        });
    }

    /// <summary>
    /// The decision (pure; unit-tested). Hostile place, broken signature, a changed file or a bad native library refuse the
    /// elevated start; a merely writable folder only produces a warning.
    /// </summary>
    public static ElevationVerdict Evaluate(SelfFacts f)
    {
        if (f.HostileLocation)
            return new ElevationVerdict(false, Loc.T("Lycoris запущен из сетевой, временной или съёмной папки. Права администратора ему не выдаются: " +
                                                      "скопируйте программу в обычную папку на диске (лучше в C:\\Program Files\\Lycoris) и запустите оттуда."));
        if (f.Signature == SignatureState.Tampered)
            return new ElevationVerdict(false, Loc.T("Цифровая подпись Lycoris.exe не совпадает с файлом: он изменён после подписания. " +
                                                      "Права администратора не запрашиваются — скачайте программу заново из официального источника."));
        if (f.HashAtStart is not null && f.HashNow is not null && !string.Equals(f.HashAtStart, f.HashNow, StringComparison.OrdinalIgnoreCase))
            return new ElevationVerdict(false, Loc.T("Файл Lycoris.exe на диске изменился с момента запуска — возможно, его подменила другая программа. " +
                                                      "Права администратора не запрашиваются. Закройте Lycoris, проверьте файл и запустите его заново."));
        if (f.BadNativeLibraries.Count > 0)
            return new ElevationVerdict(false, Loc.Tf("Библиотека {0}, распакованная Lycoris во временную папку, не прошла проверку подписи Microsoft. " +
                                                      "Права администратора не запрашиваются. Удалите папку %TEMP%\\.net\\Lycoris и запустите программу заново.",
                                                      Path.GetFileName(f.BadNativeLibraries[0])));
        if (f.WritableReason is not null)
            return new ElevationVerdict(true, null, WritableFolderWarning);
        return new ElevationVerdict(true);
    }

    /// <summary>The recommendation shown when the exe lies in a folder ordinary programs can write to.</summary>
    public static string WritableFolderWarning => Loc.T("Lycoris лежит в папке, куда может писать любая программа. Пока вы нажимаете «Да» в окне Windows, " +
                                                         "другая программа теоретически успевает подменить файл. Надёжнее хранить Lycoris в C:\\Program Files\\Lycoris (защищённая папка).");

    /// <summary>
    /// The verdict right before the UAC prompt: re-hashes the exe on disk (the window an attacker needs is between our start and
    /// the user's click) and waits briefly for the start-up check. Call off the UI thread when possible (hashing ~70 MB).
    /// </summary>
    public static ElevationVerdict CheckBeforeElevation()
    {
        try { Ready.Task.Wait(TimeSpan.FromSeconds(15)); } catch { }
        var exe = AppPaths.ExecutablePath;
        string? now = null;
        try { now = Sha256OfFile(exe); } catch { }
        // The exe cannot be read at all: do not ask for rights for a file we cannot vouch for.
        if (now is null && HashAtStart is not null)
            return new ElevationVerdict(false, Loc.T("Файл Lycoris.exe сейчас недоступен для проверки. Права администратора не запрашиваются — повторите позже."));
        return Evaluate(new SelfFacts(HashAtStart, now, HostileLocation, Signature, BadNativeLibraries, WritableReason));
    }

    /// <summary>SHA-256 of a file as lower-case hex (the file may be running and locked for writing: read sharing is requested). Null when unreadable.</summary>
    public static string? Sha256OfFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            return Convert.ToHexStringLower(SHA256.HashData(fs));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// The single-file build unpacks WPF's native libraries into %TEMP%\.net\Lycoris\&lt;id&gt; — a folder the signed-in user (and
/// every program running as that user) can write to, and an elevated Lycoris loads them from there. They are all signed by
/// Microsoft, so a file that is not validly signed is a planted one.
/// </summary>
public static class BundleGuard
{
    /// <summary>Folders of native libraries that the single-file host unpacked (usually none: a normal build loads from the framework folder).</summary>
    public static IEnumerable<string> ExtractionFolders()
    {
        foreach (var dir in Hardening.NativeSearchDirectories())
            if (IsExtractionFolder(dir)) yield return dir;
    }

    /// <summary>True for «…\.net\&lt;app&gt;\&lt;id&gt;» (the extraction layout of the single-file host). Pure — unit-tested.</summary>
    public static bool IsExtractionFolder(string dir) =>
        dir.Replace('/', '\\').Contains(@"\.net\", StringComparison.OrdinalIgnoreCase);

    /// <summary>DLLs in the extraction folders whose Authenticode state is not Valid. Slow-ish (one WinVerifyTrust per file); run in the background.</summary>
    public static IReadOnlyList<string> FindUntrustedLibraries() => FindUntrustedLibraries(ExtractionFolders(), Authenticode.Check);

    public static IReadOnlyList<string> FindUntrustedLibraries(IEnumerable<string> folders, Func<string, SignatureState> check)
    {
        var bad = new List<string>();
        foreach (var folder in folders)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder, "*.dll").ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                try
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) { bad.Add(file); continue; }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { bad.Add(file); continue; }
                if (check(file) != SignatureState.Valid) bad.Add(file);
            }
        }
        return bad;
    }
}
