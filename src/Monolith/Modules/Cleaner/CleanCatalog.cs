using System.IO;
using System.Text.RegularExpressions;
using Monolith.Core;

namespace Monolith.Modules.Cleaner;

public enum CleanCategory { System, Browsers, Games, Apps, Extra }

/// <summary>Research tiers: T0 safe (checked by default), T1 optional, T2 expert (separate confirmation).</summary>
public enum CleanTier { Safe, Optional, Expert }

public enum CleanMethod
{
    /// <summary>Own deletion engine over <see cref="CleanDefinition.Targets"/>.</summary>
    Files,
    /// <summary>SHQueryRecycleBinW / SHEmptyRecycleBinW.</summary>
    RecycleBin,
    /// <summary>Get-DeliveryOptimizationPerfSnap / Delete-DeliveryOptimizationCache.</summary>
    DeliveryOptimization,
    /// <summary>DISM /AnalyzeComponentStore, then /StartComponentCleanup when recommended (never /ResetBase).</summary>
    ComponentStore,
    /// <summary>The tool's own cache command; the targets are only measured, never deleted directly.</summary>
    Tools,
}

/// <summary>An external command of a developer tool ("dotnet nuget locals http-cache --clear").</summary>
public sealed record ToolCommand(string File, string Arguments, string Display, OutputEncoding Encoding = OutputEncoding.Utf8);

/// <summary>One line of the cleanup catalog.</summary>
public sealed class CleanDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>What it is.</summary>
    public required string Description { get; init; }
    /// <summary>What happens after cleaning (side effects, risk) — shown under the description.</summary>
    public required string Effect { get; init; }
    public required CleanCategory Category { get; init; }
    public required CleanTier Tier { get; init; }
    public bool DefaultChecked { get; init; }
    public bool RequiresAdmin { get; init; }
    /// <summary>Runs for minutes (DISM).</summary>
    public bool IsLong { get; init; }
    public CleanMethod Method { get; init; } = CleanMethod.Files;
    /// <summary>Folders (resolved for this PC) — deleted by the engine (Files) or only measured (Tools).</summary>
    public Func<CleanEnvironment, IReadOnlyList<CleanTarget>> Targets { get; init; } = _ => [];
    /// <summary>Reason the item cannot run right now (Russian sentence) or null.</summary>
    public Func<CleanEnvironment, string?>? Blocker { get; init; }
    /// <summary>Tool commands for <see cref="CleanMethod.Tools"/>.</summary>
    public Func<CleanEnvironment, IReadOnlyList<ToolCommand>>? Commands { get; init; }
    /// <summary>Applications that must be closed for <see cref="CleanMethod.Tools"/> items.</summary>
    public IReadOnlyList<AppOwner> ToolOwners { get; init; } = [];
    /// <summary>Shown even when nothing exists (system items). Otherwise hidden when no folder exists.</summary>
    public bool AlwaysShow { get; init; }
    /// <summary>Extra presence condition (tool installed, Steam found…).</summary>
    public Func<CleanEnvironment, bool>? IsAvailable { get; init; }
    /// <summary>Name used in the "не найдено на этом ПК" line of its group; null = not listed there.</summary>
    public string? ProductName { get; init; }

    /// <summary>Per-user items are cleaned in the user's own profile; admin items are machine-wide.</summary>
    public bool IsUserScope => !RequiresAdmin;

    public bool IsPresent(CleanEnvironment env)
    {
        if (IsAvailable is { } available && !available(env)) return false;
        if (AlwaysShow) return true;
        try
        {
            return Targets(env).Any(t => t.SingleFile
                ? t.Patterns is { Count: 1 } p && File.Exists(Path.Combine(t.Base, p[0]))
                : Directory.Exists(t.Base));
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// The cleanup catalog — exactly the locations allowed by docs/research/cleaner.md (§3) with their age filters,
/// owners and tiers. DO-NOT-INCLUDE locations (Prefetch, WinSxS by hand, Installer, DriverStore, browser data stores,
/// shader caches of installed Steam games, Downloads…) are deliberately absent.
/// </summary>
public static partial class CleanCatalog
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    // Lazy: Build() reads the static tables declared further down in this class.
    private static readonly Lazy<IReadOnlyList<CleanDefinition>> Catalog = new(Build);

    public static IReadOnlyList<CleanDefinition> All => Catalog.Value;

    public static string CategoryTitle(CleanCategory c) => c switch
    {
        CleanCategory.System => "СИСТЕМА",
        CleanCategory.Browsers => "БРАУЗЕРЫ",
        CleanCategory.Games => "ИГРЫ И ЛАУНЧЕРЫ",
        CleanCategory.Apps => "ПРИЛОЖЕНИЯ",
        _ => "ДОПОЛНИТЕЛЬНО",
    };

    public static string CategoryHint(CleanCategory c) => c switch
    {
        CleanCategory.System => "Временные файлы, отчёты и кэши Windows",
        CleanCategory.Browsers => "Только кэш — пароли, cookies, история и вкладки не затрагиваются",
        CleanCategory.Games => "Кэши шейдеров и лаунчеров — по желанию, после удаления возможны статтеры",
        CleanCategory.Apps => "Кэши мессенджеров, Office и инструментов разработчика",
        _ => "Корзина, журналы, дампы сбоев и обслуживание Windows — осознанно и по одному",
    };

    // ───────────────────────────── Shared blockers ─────────────────────────────

    private static string? ServicingBlocker(CleanEnvironment env) =>
        env.Servicing.Any ? $"Отложено: {env.Servicing.Describe()}. Перезагрузите ПК или повторите позже." : null;

    private static string? MsiBlocker(CleanEnvironment env) =>
        env.Servicing.MsiRunning ? "Отложено: идёт установка программы (Windows Installer). Повторите после её завершения." : null;

    private static string? WindowsUpdateBlocker(CleanEnvironment env)
    {
        if (env.WindowsUpdateRunning)
            return "Центр обновления сейчас активен — повторите позже. MONOLITH не останавливает его службы.";
        return ServicingBlocker(env);
    }

    // ───────────────────────────── Catalog ─────────────────────────────

    private static List<CleanDefinition> Build()
    {
        var list = new List<CleanDefinition>
        {
            // ═══════════════ СИСТЕМА ═══════════════
            new()
            {
                Id = "temp.user", Name = "Временные файлы пользователя", Category = CleanCategory.System, Tier = CleanTier.Safe,
                DefaultChecked = true, AlwaysShow = true,
                Description = "Папка TEMP вашей учётной записи: распакованные установщики, временные копии документов, остатки обновлений программ. Удаляются только файлы старше 24 часов.",
                Effect = "Незавершённая установка не сможет продолжиться с места остановки. Занятые файлы и файлы, которых ждёт отложенная установка, пропускаются.",
                Blocker = MsiBlocker,
                Targets = env => [new CleanTarget { Base = env.Temp, Label = "TEMP", MinAge = Day, Exclude = env.TempReferences.ToList() }],
            },
            new()
            {
                Id = "temp.windows", Name = "Временные файлы Windows", Category = CleanCategory.System, Tier = CleanTier.Safe,
                DefaultChecked = true, RequiresAdmin = true, AlwaysShow = true,
                Description = "Общая временная папка Windows (Windows\\Temp): остатки установщиков, драйверов и системных служб старше 24 часов.",
                Effect = "Без побочных эффектов. Пункт пропускается, пока Windows устанавливает обновления или ждёт перезагрузки.",
                Blocker = ServicingBlocker,
                Targets = env => [new CleanTarget { Base = Path.Combine(env.Windows, "Temp"), Label = "Windows\\Temp", MinAge = Day, RequiresAdmin = true, Exclude = env.WindowsTempReferences.ToList() }],
            },
            new()
            {
                Id = "temp.systemtemp", Name = "Временная папка системы (SystemTemp)", Category = CleanCategory.System, Tier = CleanTier.Safe,
                DefaultChecked = true, RequiresAdmin = true,
                Description = "Временные файлы служб, работающих от имени SYSTEM (Windows 11 и обновлённая Windows 10). Удаляются файлы старше 7 дней.",
                Effect = "Без побочных эффектов. Защищённые файлы, к которым нет доступа, пропускаются — права на них MONOLITH не меняет.",
                Blocker = ServicingBlocker,
                Targets = env => [new CleanTarget { Base = Path.Combine(env.Windows, "SystemTemp"), Label = "Windows\\SystemTemp", MinAge = 7 * Day, RequiresAdmin = true }],
            },
            new()
            {
                Id = "wer.user", Name = "Отчёты об ошибках (пользователь)", Category = CleanCategory.System, Tier = CleanTier.Safe,
                DefaultChecked = true,
                Description = "Отчёты о сбоях программ, которые Windows собрала для вашей учётной записи. Удаляются отчёты старше 7 дней.",
                Effect = "В «Мониторе стабильности системы» пропадут технические подробности старых сбоев.",
                Targets = env =>
                [
                    new CleanTarget { Base = Path.Combine(env.LocalAppData, @"Microsoft\Windows\WER\ReportArchive"), Label = "WER · ReportArchive", MinAge = 7 * Day },
                    new CleanTarget { Base = Path.Combine(env.LocalAppData, @"Microsoft\Windows\WER\ReportQueue"), Label = "WER · ReportQueue", MinAge = 7 * Day },
                ],
            },
            new()
            {
                Id = "wer.machine", Name = "Отчёты об ошибках (система)", Category = CleanCategory.System, Tier = CleanTier.Safe,
                DefaultChecked = true, RequiresAdmin = true,
                Description = "Отправленные и неотправленные отчёты о сбоях системы и служб старше 7 дней.",
                Effect = "Отправленные отчёты больше не нужны, а неотправленные Windows со временем удаляет сама.",
                Targets = env =>
                [
                    new CleanTarget { Base = Path.Combine(env.ProgramData, @"Microsoft\Windows\WER\ReportArchive"), Label = "WER · ReportArchive", MinAge = 7 * Day, RequiresAdmin = true },
                    new CleanTarget { Base = Path.Combine(env.ProgramData, @"Microsoft\Windows\WER\ReportQueue"), Label = "WER · ReportQueue", MinAge = 7 * Day, RequiresAdmin = true },
                    new CleanTarget { Base = Path.Combine(env.ProgramData, @"Microsoft\Windows\WER\Temp"), Label = "WER · Temp", MinAge = 7 * Day, RequiresAdmin = true },
                ],
            },
            new()
            {
                Id = "shell.inetcache", Name = "Кэш интернета Windows (WinINet)", Category = CleanCategory.System, Tier = CleanTier.Safe,
                DefaultChecked = true,
                Description = "Кэш встроенного веб-движка Windows: справка, виджеты, режим Internet Explorer в Edge и старые программы. Удаляются файлы старше 7 дней.",
                Effect = "Такие страницы один раз загрузятся заново. Открытые вложения Outlook (Content.Outlook) не затрагиваются.",
                Targets = env => [new CleanTarget { Base = Path.Combine(env.LocalAppData, @"Microsoft\Windows\INetCache"), Label = "INetCache", MinAge = 7 * Day, Exclude = ["Content.Outlook"] }],
            },
            new()
            {
                Id = "do.cache", Name = "Кэш оптимизации доставки", Category = CleanCategory.System, Tier = CleanTier.Safe,
                DefaultChecked = true, RequiresAdmin = true, AlwaysShow = true, Method = CleanMethod.DeliveryOptimization,
                Description = "Копии скачанных обновлений Windows и приложений Store, которые служба оптимизации доставки хранит для раздачи другим ПК. Удаляется штатной командой Delete-DeliveryOptimizationCache.",
                Effect = "Другие компьютеры в сети не смогут получить эти файлы с вашего ПК. Закреплённые файлы сохраняются.",
            },
            new()
            {
                Id = "wu.download", Name = "Загрузки Центра обновления", Category = CleanCategory.System, Tier = CleanTier.Optional,
                RequiresAdmin = true, AlwaysShow = true,
                Description = "Пакеты, которые скачал Центр обновления Windows. Удаляются файлы старше 10 дней и только пока служба обновления остановлена.",
                Effect = "Если обновление ещё не установлено, Windows скачает его заново. Службы обновления MONOLITH не останавливает.",
                Blocker = WindowsUpdateBlocker,
                Targets = env => [new CleanTarget { Base = Path.Combine(env.Windows, @"SoftwareDistribution\Download"), Label = "SoftwareDistribution\\Download", MinAge = 10 * Day, RequiresAdmin = true }],
            },
        };

        AddBrowsers(list);
        AddGames(list);
        AddApps(list);
        AddExtra(list);
        return list;
    }

    // ═══════════════ БРАУЗЕРЫ ═══════════════

    private sealed record Chromium(string Id, string Name, string Process, string[] LocalRoots, string? RoamingRoot = null,
        string? PathContains = null, string? PathExcludes = null);

    private static readonly Chromium[] ChromiumBrowsers =
    [
        new("browser.chrome", "Google Chrome", "chrome", [@"Google\Chrome\User Data"]),
        new("browser.edge", "Microsoft Edge", "msedge",
            [@"Microsoft\Edge\User Data", @"Microsoft\Edge Beta\User Data", @"Microsoft\Edge Dev\User Data", @"Microsoft\Edge SxS\User Data"]),
        new("browser.brave", "Brave", "brave", [@"BraveSoftware\Brave-Browser\User Data"]),
        new("browser.opera", "Opera", "opera", [@"Opera Software\Opera Stable"], @"Opera Software\Opera Stable", PathExcludes: @"\Opera GX\"),
        new("browser.operagx", "Opera GX", "opera", [@"Opera Software\Opera GX Stable"], @"Opera Software\Opera GX Stable", PathContains: @"\Opera GX\"),
        new("browser.vivaldi", "Vivaldi", "vivaldi", [@"Vivaldi\User Data"]),
        new("browser.yandex", "Яндекс Браузер", "browser", [@"Yandex\YandexBrowser\User Data"], PathContains: @"\Yandex\"),
    ];

    /// <summary>Per-profile cache folders (T0). Everything else in a profile is never touched.</summary>
    private static readonly string[] ProfileCacheDirs = [@"Cache\Cache_Data", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache", "Media Cache"];

    /// <summary>"User Data"-level GPU caches (T0).</summary>
    private static readonly string[] RootCacheDirs = ["ShaderCache", "GrShaderCache", "GraphiteDawnCache", "GPUPersistentCache"];

    private static void AddBrowsers(List<CleanDefinition> list)
    {
        foreach (var b in ChromiumBrowsers)
        {
            list.Add(new CleanDefinition
            {
                Id = b.Id, Name = b.Name, Category = CleanCategory.Browsers, Tier = CleanTier.Safe, DefaultChecked = true, ProductName = b.Name,
                Description = "Кэш страниц, скриптов и графики во всех профилях браузера. Пароли, cookies, история, вкладки, закладки и расширения не затрагиваются.",
                Effect = $"Сайты один раз загрузятся чуть медленнее. {b.Name} должен быть закрыт, включая фоновый режим, — иначе пункт пропускается.",
                Targets = env => ChromiumTargets(env, b, serviceWorker: false),
            });
        }

        list.Add(new CleanDefinition
        {
            Id = "browser.firefox", Name = "Mozilla Firefox", Category = CleanCategory.Browsers, Tier = CleanTier.Safe, DefaultChecked = true,
            ProductName = "Mozilla Firefox",
            Description = "Кэш страниц (cache2), кэш запуска и миниатюры во всех профилях Firefox. Пароли, cookies, история и сессии не затрагиваются.",
            Effect = "Сайты один раз загрузятся чуть медленнее. Firefox должен быть закрыт — иначе пункт пропускается.",
            Targets = FirefoxTargets,
        });

        list.Add(new CleanDefinition
        {
            Id = "browser.serviceworker", Name = "Офлайн-данные сайтов и отчёты о сбоях браузеров", Category = CleanCategory.Browsers,
            Tier = CleanTier.Optional,
            Description = "Кэш Service Worker (CacheStorage, ScriptCache) — офлайн-копии сайтов и веб-приложений — и отчёты о сбоях браузеров на Chromium.",
            Effect = "Установленные веб-приложения и сайты, работающие офлайн, загрузятся заново и могут попросить войти ещё раз.",
            Targets = env => ChromiumBrowsers.SelectMany(b => ChromiumTargets(env, b, serviceWorker: true)).ToList(),
        });
    }

    private static List<CleanTarget> ChromiumTargets(CleanEnvironment env, Chromium b, bool serviceWorker)
    {
        var targets = new List<CleanTarget>();
        var roaming = b.RoamingRoot is null ? null : Path.Combine(env.RoamingAppData, b.RoamingRoot);
        var opera = roaming is not null;

        foreach (var rel in b.LocalRoots)
        {
            var root = Path.Combine(env.LocalAppData, rel);
            if (!Directory.Exists(root) && (roaming is null || !Directory.Exists(roaming))) continue;

            // The browser holds "<User Data>\lockfile" exclusively while it runs (Opera: in the roaming folder).
            var lockRoot = roaming ?? root;
            var owner = new AppOwner
            {
                Name = b.Name, Processes = [b.Process], PathContains = b.PathContains, PathExcludes = b.PathExcludes,
                LockFiles = [Path.Combine(lockRoot, "lockfile")],
            };

            // Opera keeps settings in %APPDATA% and the cache in the %LOCALAPPDATA% mirror; the root itself can be a profile.
            var roots = opera ? new[] { roaming!, root } : [root];
            var profileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in roots.Where(Directory.Exists))
                profileNames.UnionWith(BrowserProfiles.ChromiumProfiles(r));
            if (opera) profileNames.Add("");

            foreach (var r in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var profile in profileNames)
                {
                    var profileDir = profile.Length == 0 ? r : Path.Combine(r, profile);
                    var subs = serviceWorker ? new[] { @"Service Worker\CacheStorage", @"Service Worker\ScriptCache" } : ProfileCacheDirs;
                    foreach (var sub in subs)
                    {
                        var dir = Path.Combine(profileDir, sub);
                        if (!Directory.Exists(dir)) continue;
                        targets.Add(new CleanTarget { Base = dir, Label = $"{b.Name} · {(profile.Length == 0 ? "профиль" : profile)} · {sub}", Owner = owner });
                    }
                }

                var rootSubs = serviceWorker ? new[] { @"Crashpad\reports" } : RootCacheDirs;
                foreach (var sub in rootSubs)
                {
                    var dir = Path.Combine(r, sub);
                    if (!Directory.Exists(dir)) continue;
                    targets.Add(new CleanTarget { Base = dir, Label = $"{b.Name} · {sub}", Owner = owner });
                }
            }
        }

        // Several roots can resolve to the same folder (Opera roaming/local): keep each folder once.
        return targets.DistinctBy(t => PathGuard.Normalize(t.Base) ?? t.Base, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<CleanTarget> FirefoxTargets(CleanEnvironment env)
    {
        var targets = new List<CleanTarget>();
        var roamingRoot = Path.Combine(env.RoamingAppData, @"Mozilla\Firefox");
        var localRoot = Path.Combine(env.LocalAppData, @"Mozilla\Firefox");
        var ini = Path.Combine(roamingRoot, "profiles.ini");
        if (!File.Exists(ini)) return targets;

        List<(string Path, bool IsRelative)> profiles;
        try { profiles = BrowserProfiles.ParseProfilesIni(BrowserProfiles.ReadShared(ini)); }
        catch { return targets; }

        foreach (var (path, isRelative) in profiles)
        {
            string? roamingProfile, cacheProfile;
            if (isRelative)
            {
                roamingProfile = PathGuard.Normalize(Path.Combine(roamingRoot, path));
                cacheProfile = PathGuard.Normalize(Path.Combine(localRoot, path));
                // A relative path must stay inside its root ("..\" tricks are ignored).
                if (roamingProfile is null || cacheProfile is null ||
                    !PathGuard.IsStrictlyUnder(roamingProfile, PathGuard.Normalize(roamingRoot)!) ||
                    !PathGuard.IsStrictlyUnder(cacheProfile, PathGuard.Normalize(localRoot)!)) continue;
            }
            else
            {
                roamingProfile = cacheProfile = PathGuard.Normalize(path);
                if (roamingProfile is null) continue;
            }

            var owner = new AppOwner
            {
                Name = "Mozilla Firefox", Processes = ["firefox"],
                LockFiles = [Path.Combine(roamingProfile, "parent.lock")],
            };
            var name = Path.GetFileName(roamingProfile);
            foreach (var sub in new[] { "cache2", "startupCache", "thumbnails" })
            {
                var dir = Path.Combine(cacheProfile!, sub);
                if (Directory.Exists(dir))
                    targets.Add(new CleanTarget { Base = dir, Label = $"Firefox · {name} · {sub}", Owner = owner });
            }
        }
        return targets.DistinctBy(t => PathGuard.Normalize(t.Base) ?? t.Base, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ═══════════════ ИГРЫ И ЛАУНЧЕРЫ ═══════════════

    private const string ShaderStutter =
        "Игры заново скомпилируют шейдеры: при первом запуске и в первые часы возможны фризы и статтеры (особенно DX12 и Unreal Engine). Имеет смысл только после обновления драйвера или при графических артефактах.";

    private static AppOwner SteamOwner() => new() { Name = "Steam", Processes = ["steam", "steamwebhelper"] };

    private static void AddGames(List<CleanDefinition> list)
    {
        list.Add(new CleanDefinition
        {
            Id = "gpu.d3d", Name = "Кэш шейдеров DirectX", Category = CleanCategory.Games, Tier = CleanTier.Optional,
            Description = "Скомпилированные шейдеры игр и программ на DirectX (D3DSCache). Драйвер сам ограничивает размер этого кэша — это не мусор.",
            Effect = ShaderStutter,
            Targets = env => [new CleanTarget { Base = Path.Combine(env.LocalAppData, "D3DSCache"), Label = "D3DSCache" }],
        });
        list.Add(new CleanDefinition
        {
            Id = "gpu.nvidia", Name = "Кэш шейдеров NVIDIA", Category = CleanCategory.Games, Tier = CleanTier.Optional, ProductName = "NVIDIA",
            Description = "Кэши шейдеров DirectX, OpenGL и CUDA драйвера NVIDIA (DXCache, GLCache, ComputeCache).",
            Effect = ShaderStutter + " Размер кэша можно ограничить в панели управления NVIDIA или NVIDIA App.",
            Targets = env =>
            [
                new CleanTarget { Base = Path.Combine(env.LocalAppData, @"NVIDIA\DXCache"), Label = "NVIDIA · DXCache" },
                new CleanTarget { Base = Path.Combine(env.LocalAppData, @"NVIDIA\GLCache"), Label = "NVIDIA · GLCache" },
                new CleanTarget { Base = Path.Combine(env.LocalLow, @"NVIDIA\DXCache"), Label = "NVIDIA · LocalLow · DXCache" },
                new CleanTarget { Base = Path.Combine(env.LocalLow, @"NVIDIA\PerDriverVersion\DXCache"), Label = "NVIDIA · PerDriverVersion · DXCache" },
                new CleanTarget { Base = Path.Combine(env.LocalLow, @"NVIDIA\PerDriverVersion\GLCache"), Label = "NVIDIA · PerDriverVersion · GLCache" },
                new CleanTarget { Base = Path.Combine(env.RoamingAppData, @"NVIDIA\ComputeCache"), Label = "NVIDIA · ComputeCache" },
                new CleanTarget { Base = Path.Combine(env.ProgramData, @"NVIDIA Corporation\NV_Cache"), Label = "NVIDIA · NV_Cache", RequiresAdmin = true },
            ],
        });
        list.Add(new CleanDefinition
        {
            Id = "gpu.amd", Name = "Кэш шейдеров AMD", Category = CleanCategory.Games, Tier = CleanTier.Optional, ProductName = "AMD",
            Description = "Кэши шейдеров DirectX, OpenGL и Vulkan драйвера AMD (DxCache, DxcCache, GLCache, VkCache).",
            Effect = ShaderStutter + " Предпочтительный способ — AMD Adrenalin → Настройки → Графика → «Сбросить кэш шейдеров».",
            Targets = env => new[] { "DxCache", "DxcCache", "GLCache", "VkCache" }
                .Select(d => new CleanTarget { Base = Path.Combine(env.LocalAppData, "AMD", d), Label = $"AMD · {d}" })
                .ToList(),
        });

        list.Add(new CleanDefinition
        {
            Id = "steam.htmlcache", Name = "Steam: кэш встроенного браузера", Category = CleanCategory.Games, Tier = CleanTier.Optional,
            ProductName = "Steam",
            Description = "Кэш магазина, сообщества и оверлея Steam — то же, что «Удалить данные веб-браузера» в настройках Steam.",
            Effect = "Страницы магазина загрузятся заново; в магазине и оверлее может понадобиться войти ещё раз. Steam должен быть закрыт.",
            Targets = env =>
            {
                var owner = SteamOwner();
                var t = new List<CleanTarget> { new() { Base = Path.Combine(env.LocalAppData, @"Steam\htmlcache"), Label = "Steam · htmlcache", Owner = owner } };
                if (env.SteamPath is { } sp) t.Add(new CleanTarget { Base = Path.Combine(sp, @"config\htmlcache"), Label = "Steam · config\\htmlcache", Owner = owner });
                return t;
            },
        });
        list.Add(new CleanDefinition
        {
            Id = "steam.misc", Name = "Steam: HTTP-кэш, журналы и дампы", Category = CleanCategory.Games, Tier = CleanTier.Optional,
            IsAvailable = env => env.SteamPath is not null,
            Description = "Кэш загрузок клиента (appcache\\httpcache), журналы Steam и дампы его сбоев старше 30 дней. Обложки библиотеки (librarycache) не трогаются.",
            Effect = "Steam один раз заново загрузит служебные данные. Steam должен быть закрыт.",
            Targets = env =>
            {
                if (env.SteamPath is not { } sp) return [];
                var owner = SteamOwner();
                return
                [
                    new CleanTarget { Base = Path.Combine(sp, @"appcache\httpcache"), Label = "Steam · httpcache", Owner = owner },
                    new CleanTarget { Base = Path.Combine(sp, "logs"), Label = "Steam · logs", Patterns = ["*.txt"], Recurse = false, MinAge = 30 * Day, Owner = owner },
                    new CleanTarget { Base = Path.Combine(sp, "dumps"), Label = "Steam · dumps", Patterns = ["*.dmp"], Recurse = false, MinAge = 30 * Day, Owner = owner },
                ];
            },
        });
        list.Add(new CleanDefinition
        {
            Id = "steam.shader.orphan", Name = "Steam: шейдеры удалённых игр", Category = CleanCategory.Games, Tier = CleanTier.Optional,
            IsAvailable = env => env.SteamPath is not null,
            Description = "Папки shadercache игр, которых больше нет в библиотеке Steam (нет файла appmanifest). Кэш установленных игр не трогается.",
            Effect = "Побочных эффектов нет: если игру установить снова, Steam заново загрузит или соберёт её шейдеры. Steam должен быть закрыт.",
            Targets = env =>
            {
                if (env.SteamPath is not { } sp) return [];
                var found = SteamLibraries.FindOrphanShaderCaches(sp);
                if (found is null) return [];
                var owner = SteamOwner();
                // OnlyTopDirs = orphans: an empty list walks nothing — never "everything".
                return found.Select(f => new CleanTarget
                {
                    Base = f.ShaderCache, Label = $"Steam · {f.ShaderCache}", Owner = owner, OnlyTopDirs = f.Orphans,
                }).ToList();
            },
        });
    }

    // ═══════════════ ПРИЛОЖЕНИЯ ═══════════════

    private static readonly string[] ElectronCacheDirs = [@"Cache\Cache_Data", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache"];

    private static void AddApps(List<CleanDefinition> list)
    {
        list.Add(new CleanDefinition
        {
            Id = "discord.cache", Name = "Discord: кэш", Category = CleanCategory.Apps, Tier = CleanTier.Safe, DefaultChecked = true,
            ProductName = "Discord",
            Description = "Кэш картинок, скриптов и графики Discord (обычная версия, PTB и Canary). Данные входа (Local Storage) и настройки не затрагиваются.",
            Effect = "Аватары и вложения загрузятся заново. Discord должен быть закрыт, в том числе в области уведомлений, — иначе пункт пропускается.",
            Targets = env =>
            {
                var t = new List<CleanTarget>();
                foreach (var (folder, process, name) in new[] { ("discord", "Discord", "Discord"), ("discordptb", "DiscordPTB", "Discord PTB"), ("discordcanary", "DiscordCanary", "Discord Canary") })
                {
                    var owner = new AppOwner { Name = name, Processes = [process] };
                    foreach (var sub in ElectronCacheDirs)
                        t.Add(new CleanTarget { Base = Path.Combine(env.RoamingAppData, folder, sub), Label = $"{name} · {sub}", Owner = owner });
                }
                return t;
            },
        });
        list.Add(new CleanDefinition
        {
            Id = "teams.cache", Name = "Microsoft Teams: кэш", Category = CleanCategory.Apps, Tier = CleanTier.Optional,
            ProductName = "Microsoft Teams",
            Description = "Кэш веб-интерфейса нового и классического Teams. Удаляются только папки кэша — настройки и вход сохраняются.",
            Effect = "Первый запуск Teams после очистки будет медленнее. Teams должен быть закрыт.",
            Targets = env =>
            {
                var t = new List<CleanTarget>();
                var newOwner = new AppOwner { Name = "Microsoft Teams", Processes = ["ms-teams"] };
                var web = Path.Combine(env.LocalAppData, @"Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView");
                try
                {
                    if (Directory.Exists(web))
                        foreach (var profile in Directory.EnumerateDirectories(web))
                        {
                            if ((File.GetAttributes(profile) & FileAttributes.ReparsePoint) != 0) continue;
                            foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache" })
                                t.Add(new CleanTarget { Base = Path.Combine(profile, sub), Label = $"Teams · {Path.GetFileName(profile)} · {sub}", Owner = newOwner });
                        }
                }
                catch { }
                var classicOwner = new AppOwner { Name = "Microsoft Teams (классический)", Processes = ["Teams"] };
                foreach (var sub in new[] { "Cache", "GPUCache", "Code Cache" })
                    t.Add(new CleanTarget { Base = Path.Combine(env.RoamingAppData, @"Microsoft\Teams", sub), Label = $"Teams (классический) · {sub}", Owner = classicOwner });
                return t;
            },
        });
        list.Add(new CleanDefinition
        {
            Id = "office.wef", Name = "Microsoft Office: кэш надстроек", Category = CleanCategory.Apps, Tier = CleanTier.Optional,
            ProductName = "Microsoft Office",
            Description = "Кэш веб-надстроек Office (Wef). Помогает, если надстройка не загружается или устарела. Кэш документов (OfficeFileCache) не затрагивается.",
            Effect = "Надстройки загрузятся заново при следующем открытии Word, Excel или Outlook. Приложения Office должны быть закрыты.",
            Targets = env =>
            [
                new CleanTarget
                {
                    Base = Path.Combine(env.LocalAppData, @"Microsoft\Office\16.0\Wef"), Label = "Office · Wef",
                    Owner = new AppOwner { Name = "Microsoft Office", Processes = ["WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "ONENOTE", "MSACCESS", "MSPUB", "VISIO", "WINPROJ"] },
                },
            ],
        });

        // Developer caches: hidden unless the tool exists; cleaned only by the tool's own command.
        var buildOwner = new AppOwner { Name = "сборка .NET (dotnet / MSBuild)", Processes = ["dotnet", "MSBuild"] };
        var nodeOwner = new AppOwner { Name = "Node.js", Processes = ["node"] };

        list.Add(new CleanDefinition
        {
            Id = "dev.nuget", Name = "NuGet: HTTP-кэш", Category = CleanCategory.Apps, Tier = CleanTier.Optional, Method = CleanMethod.Tools,
            IsAvailable = env => env.DotnetPath is not null,
            Description = "Кэш загрузок пакетов NuGet (v3-cache) и его временные файлы. Очищается штатной командой dotnet nuget locals.",
            Effect = "Следующая сборка заново скачает метаданные пакетов. Глобальная папка пакетов (.nuget\\packages) не затрагивается. Во время сборки пункт пропускается.",
            ToolOwners = [buildOwner],
            Targets = env =>
            {
                var custom = Environment.GetEnvironmentVariable("NUGET_HTTP_CACHE_PATH");
                return [new CleanTarget { Base = string.IsNullOrWhiteSpace(custom) ? Path.Combine(env.LocalAppData, @"NuGet\v3-cache") : custom, Label = "NuGet · v3-cache" }];
            },
            Commands = env => env.DotnetPath is not { } dotnet ? [] :
            [
                new ToolCommand(dotnet, "nuget locals http-cache --clear", "dotnet nuget locals http-cache --clear"),
                new ToolCommand(dotnet, "nuget locals temp --clear", "dotnet nuget locals temp --clear"),
            ],
        });
        list.Add(new CleanDefinition
        {
            Id = "dev.npm", Name = "npm: проверка кэша", Category = CleanCategory.Apps, Tier = CleanTier.Optional, Method = CleanMethod.Tools,
            IsAvailable = env => env.NpmPath is not null,
            Description = "Кэш пакетов npm (_cacache). Команда npm cache verify удаляет повреждённые и ненужные данные, рабочий кэш остаётся.",
            Effect = "Без побочных эффектов: что удалить, решает сам npm. Пока запущен Node.js, пункт пропускается.",
            ToolOwners = [nodeOwner],
            Targets = env =>
            {
                var custom = Environment.GetEnvironmentVariable("npm_config_cache");
                var root = string.IsNullOrWhiteSpace(custom) ? Path.Combine(env.LocalAppData, "npm-cache") : custom;
                return [new CleanTarget { Base = Path.Combine(root, "_cacache"), Label = "npm · _cacache" }];
            },
            Commands = env => env.NpmPath is not { } npm ? [] :
            [
                new ToolCommand(ProcessRunner.Sys("cmd.exe"), $"/d /s /c \"\"{npm}\" cache verify\"", "npm cache verify", OutputEncoding.Oem),
            ],
        });
        list.Add(new CleanDefinition
        {
            Id = "dev.yarn", Name = "Yarn: кэш пакетов", Category = CleanCategory.Apps, Tier = CleanTier.Optional, Method = CleanMethod.Tools,
            IsAvailable = env => env.YarnPath is not null,
            Description = "Кэш скачанных пакетов Yarn. Очищается командой yarn cache clean.",
            Effect = "Следующая установка зависимостей скачает пакеты заново. Пока запущен Node.js, пункт пропускается.",
            ToolOwners = [nodeOwner],
            Targets = env => [new CleanTarget { Base = Path.Combine(env.LocalAppData, @"Yarn\Cache"), Label = "Yarn · Cache" }],
            Commands = env => env.YarnPath is not { } yarn ? [] :
            [
                new ToolCommand(ProcessRunner.Sys("cmd.exe"), $"/d /s /c \"\"{yarn}\" cache clean\"", "yarn cache clean", OutputEncoding.Oem),
            ],
        });
        list.Add(new CleanDefinition
        {
            Id = "dev.pip", Name = "pip: кэш пакетов Python", Category = CleanCategory.Apps, Tier = CleanTier.Optional, Method = CleanMethod.Tools,
            IsAvailable = env => env.Pip is not null,
            Description = "Кэш скачанных пакетов Python. Очищается командой pip cache purge.",
            Effect = "Повторная установка пакетов скачает их заново.",
            ToolOwners = [new AppOwner { Name = "pip", Processes = ["pip", "pip3"] }],
            Targets = env =>
            {
                var custom = Environment.GetEnvironmentVariable("PIP_CACHE_DIR");
                return [new CleanTarget { Base = string.IsNullOrWhiteSpace(custom) ? Path.Combine(env.LocalAppData, @"pip\Cache") : custom, Label = "pip · Cache" }];
            },
            Commands = env => env.Pip is not { } pip ? [] :
            [
                new ToolCommand(pip.File, pip.Prefix + "cache purge", "pip cache purge"),
            ],
        });
    }

    // ═══════════════ ДОПОЛНИТЕЛЬНО ═══════════════

    [GeneratedRegex(@"^setupapi\..+\.\d{8}_\d{6}\.log$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SetupApiArchive();

    /// <summary>Rotated SetupAPI archives only ("setupapi.offline.20240401_072605.log") — never the active logs.</summary>
    public static bool IsSetupApiArchive(string fileName) => SetupApiArchive().IsMatch(fileName);

    private static void AddExtra(List<CleanDefinition> list)
    {
        list.Add(new CleanDefinition
        {
            Id = "recyclebin", Name = "Корзина", Category = CleanCategory.Extra, Tier = CleanTier.Optional, AlwaysShow = true,
            Method = CleanMethod.RecycleBin,
            Description = "Файлы, которые вы удалили в корзину, на всех дисках.",
            Effect = "После очистки вернуть эти файлы будет нельзя. Перед удалением MONOLITH покажет число объектов и их размер.",
        });
        list.Add(new CleanDefinition
        {
            Id = "logs.cbs", Name = "Архивы журналов CBS и DISM", Category = CleanCategory.Extra, Tier = CleanTier.Optional, RequiresAdmin = true,
            Description = "Старые архивы журналов обслуживания Windows (CbsPersist_*) и журнал DISM, если он больше 50 МБ. Текущий CBS.log не трогается.",
            Effect = "Пропадёт история проверок SFC и DISM старше 30 дней.",
            Blocker = ServicingBlocker,
            Targets = env =>
            [
                new CleanTarget { Base = Path.Combine(env.Windows, @"Logs\CBS"), Label = "Logs\\CBS", Patterns = ["CbsPersist_*.log", "CbsPersist_*.cab"], Recurse = false, MinAge = 30 * Day, RequiresAdmin = true },
                new CleanTarget { Base = Path.Combine(env.Windows, @"Logs\DISM"), Label = "Logs\\DISM", Patterns = ["dism.log*"], Recurse = false, MinAge = 30 * Day, RequiresAdmin = true, Filter = (_, size) => size > 50L * 1024 * 1024 },
            ],
        });
        list.Add(new CleanDefinition
        {
            Id = "logs.windows", Name = "Журналы установки и обновления Windows", Category = CleanCategory.Extra, Tier = CleanTier.Optional, RequiresAdmin = true,
            Description = "Журналы сетевой настройки (NetSetup), установки обновлений (MoSetup, WindowsUpdate) и службы восстановления Центра обновления старше 30 дней. MeasuredBoot и WinREAgent не трогаются.",
            Effect = "Сложнее будет разобраться в давней ошибке установки обновлений.",
            Blocker = ServicingBlocker,
            Targets = env =>
            [
                new CleanTarget { Base = Path.Combine(env.Windows, @"Logs\NetSetup"), Label = "Logs\\NetSetup", MinAge = 30 * Day, RequiresAdmin = true },
                new CleanTarget { Base = Path.Combine(env.Windows, @"Logs\MoSetup"), Label = "Logs\\MoSetup", MinAge = 30 * Day, RequiresAdmin = true },
                new CleanTarget { Base = Path.Combine(env.Windows, @"Logs\waasmedic"), Label = "Logs\\waasmedic", MinAge = 30 * Day, RequiresAdmin = true },
                new CleanTarget { Base = Path.Combine(env.Windows, @"Logs\WindowsUpdate"), Label = "Logs\\WindowsUpdate", Patterns = ["*.etl"], MinAge = 30 * Day, RequiresAdmin = true },
            ],
        });
        list.Add(new CleanDefinition
        {
            Id = "logs.setupapi", Name = "Архивы журналов SetupAPI", Category = CleanCategory.Extra, Tier = CleanTier.Optional, RequiresAdmin = true,
            Description = "Старые ротированные архивы журнала установки устройств (setupapi.*.ГГГГММДД_ЧЧММСС.log). Текущие журналы остаются.",
            Effect = "Пропадёт история установки драйверов старше 30 дней.",
            Blocker = ServicingBlocker,
            Targets = env =>
            [
                new CleanTarget { Base = Path.Combine(env.Windows, "INF"), Label = "INF · setupapi", Patterns = ["setupapi.*.log"], Filter = (name, _) => IsSetupApiArchive(name), Recurse = false, MinAge = 30 * Day, RequiresAdmin = true },
            ],
        });
        list.Add(new CleanDefinition
        {
            Id = "dump.memory", Name = "Полный дамп памяти (MEMORY.DMP)", Category = CleanCategory.Extra, Tier = CleanTier.Optional, RequiresAdmin = true,
            Description = "Файл, который Windows записывает при синем экране (BSOD). Может занимать несколько гигабайт и содержит данные из оперативной памяти. Удаляется, только если ему больше 14 дней.",
            Effect = "Причину того синего экрана больше нельзя будет проанализировать.",
            Targets = env =>
            {
                var file = PathGuard.Normalize(env.MemoryDumpFile);
                if (file is null || !file.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)) return [];
                return [new CleanTarget { Base = Path.GetDirectoryName(file)!, Label = file, Patterns = [Path.GetFileName(file)], Recurse = false, RemoveEmptyDirs = false, SingleFile = true, MinAge = 14 * Day, RequiresAdmin = true }];
            },
        });
        list.Add(new CleanDefinition
        {
            Id = "dump.mini", Name = "Мини-дампы синих экранов", Category = CleanCategory.Extra, Tier = CleanTier.Optional, RequiresAdmin = true,
            Description = "Небольшие дампы ошибок BSOD старше 30 дней. Пять самых новых сохраняются всегда.",
            Effect = "Пропадут доказательства старых сбоев — если вы разбираетесь с синими экранами, не удаляйте.",
            Targets = env => [new CleanTarget { Base = env.MinidumpDir, Label = "Minidump", Patterns = ["*.dmp"], Recurse = false, MinAge = 30 * Day, KeepNewest = 5, RequiresAdmin = true }],
        });
        list.Add(new CleanDefinition
        {
            Id = "dump.livekernel", Name = "Отчёты ядра (LiveKernelReports)", Category = CleanCategory.Extra, Tier = CleanTier.Optional, RequiresAdmin = true,
            Description = "Дампы зависаний драйверов старше 30 дней: WATCHDOG — сбросы видеодрайвера (TDR), USBHUB3, NDIS. Часто занимают гигабайты.",
            Effect = "Это главное доказательство проблем видеокарты, в том числе вылетов драйвера на RTX 50. Если вы разбираетесь со сбоями GPU — не удаляйте.",
            Targets = env =>
            [
                new CleanTarget { Base = Path.Combine(env.Windows, "LiveKernelReports"), Label = "LiveKernelReports", Patterns = ["*.dmp"], MinAge = 30 * Day, RequiresAdmin = true },
                new CleanTarget { Base = Path.Combine(env.LocalAppData, @"Microsoft\Windows\TaskManager\LiveKernelDumps"), Label = "TaskManager · LiveKernelDumps", Patterns = ["*.dmp"], MinAge = 30 * Day },
            ],
        });
        list.Add(new CleanDefinition
        {
            Id = "dump.user", Name = "Дампы сбоев приложений", Category = CleanCategory.Extra, Tier = CleanTier.Optional,
            Description = "Дампы памяти упавших программ старше 30 дней, которые сохраняет служба отчётов об ошибках Windows.",
            Effect = "Разработчики программ не смогут разобрать старые сбои по этим файлам.",
            Targets = env => [new CleanTarget { Base = Path.Combine(env.LocalAppData, "CrashDumps"), Label = "CrashDumps", Patterns = ["*.dmp"], Recurse = false, MinAge = 30 * Day }],
        });
        list.Add(new CleanDefinition
        {
            Id = "wu.cbs", Name = "Очистка хранилища компонентов (WinSxS)", Category = CleanCategory.Extra, Tier = CleanTier.Expert,
            RequiresAdmin = true, IsLong = true, AlwaysShow = true, Method = CleanMethod.ComponentStore,
            Description = "Штатная очистка DISM: сначала /AnalyzeComponentStore проверяет, нужна ли она, затем /StartComponentCleanup удаляет версии компонентов, заменённые обновлениями. Режим /ResetBase не используется.",
            Effect = "Безопасно, но необратимо: старые версии удаляются сразу, без 30-дневного ожидания. Занимает от нескольких минут до часа и сильно нагружает диск — не запускайте во время игры.",
            Blocker = ServicingBlocker,
        });
    }
}
