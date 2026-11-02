using Monolith.Core;
using Monolith.Modules.Drivers.Hardware;

namespace Monolith.Modules.Devices.Vendors;

/// <summary>
/// Runs after the device providers: names the devices behind generic cards (the model, the receiver and what was plugged in by
/// cable before), adds the vendor programs (open — signed, unelevated; install — on Drivers), the notes that only the vendor
/// side can give (onboard memory, Dynamic Lighting, RGB conflicts), and cards for devices no provider covers (the board's RGB
/// controller). Read-only: never writes a device, never starts anything by itself.
/// </summary>
internal sealed class VendorEnricher : IDeviceEnricher
{
    public string Name => Loc.T("программы производителей");

    public Task EnrichAsync(List<DeviceCard> cards, DeviceScanContext ctx, CancellationToken ct)
    {
        var groups = DeviceInventory.Read();
        ct.ThrowIfCancellationRequested();
        var apps = VendorApps.DetectNow();
        ct.ThrowIfCancellationRequested();
        var present = groups.Select(g => g.VidPid).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var history = new Dictionary<string, IReadOnlyList<HistoryHint>>(StringComparer.OrdinalIgnoreCase);
        Enrich(cards, groups, apps,
            vid => history.TryGetValue(vid, out var h) ? h : history[vid] = DeviceInventory.ReadHistory(vid, InventoryKind.Mouse, present),
            VendorApps.LaunchAsync);
        Log.Info("Устройства: найдено физических устройств {0}; программы производителей: {1}.", groups.Count,
            apps.Installed.Any() ? string.Join(", ", apps.Installed.Select(a => a.Name)) : Loc.T("отсутствует"));
        return Task.CompletedTask;
    }

    // ───────────────────────────── Texts ─────────────────────────────

    public static string OnboardMemoryNote => Loc.T("Чтобы DPI и частота опроса работали без G HUB, включите в G HUB режим встроенной памяти и сохраните профиль в мышь");
    public static string InzoneNote => Loc.T("Эквалайзер и пространственный звук — в INZONE Hub (работают при подключении через USB-адаптер)");
    public static string RazerMicNote => Loc.T("Частота дискретизации и микшер — в Razer Synapse");
    public const string LightingSettings = "ms-settings:personalization-lighting";

    public static string OpenFromStartNote(string app) => Loc.Tf("Откройте {0} из меню «Пуск»", app);
    public static string NotInstalledNote(string app) => Loc.Tf("{0} не установлен — установка в разделе «Драйверы»", app);
    public static string DynamicLightingNote(string app) =>
        Loc.Tf("Подсветку можно настраивать через Windows (Динамическое освещение) или {0} — выберите что-то одно", app);
    /// <summary>«OpenRGB и G HUB могут спорить за подсветку — пользуйтесь одной программой».</summary>
    public static string UnifierNote(IReadOnlyList<string> unifiers, IReadOnlyList<string> vendorApps) =>
        vendorApps.Count == 1
            ? Loc.Tf("{0} и {1} могут спорить за подсветку — пользуйтесь одной программой", string.Join(", ", unifiers), vendorApps[0])
            : Loc.Tf("{0} и программы производителей ({1}) могут спорить за подсветку — пользуйтесь одной программой", string.Join(", ", unifiers), string.Join(", ", vendorApps));
    public static string UnifierOwnsNote(string unifier, bool board) =>
        board ? Loc.Tf("Подсветкой платы уже может управлять {0} — откройте его из меню «Пуск»", unifier)
              : Loc.Tf("Подсветкой этого устройства уже может управлять {0} — откройте его из меню «Пуск»", unifier);

    // ───────────────────────────── Enrichment (pure over its inputs) ─────────────────────────────

    /// <summary>
    /// Enriches <paramref name="cards"/> in place (cards whose subtitle changes are replaced by a copy). <paramref name="history"/>
    /// gives a vendor's cable history (newest first); <paramref name="launch"/> opens a program and reports a failure itself.
    /// </summary>
    internal static void Enrich(List<DeviceCard> cards, IReadOnlyList<InventoryGroup> groups, VendorAppSnapshot apps,
        Func<string, IReadOnlyList<HistoryHint>> history, Func<VendorApp, Task<bool>> launch)
    {
        var claimed = new HashSet<InventoryGroup>(ReferenceEqualityComparer.Instance);
        var onceNotes = new HashSet<string>(StringComparer.Ordinal); // a device-specific note appears on one card only

        for (var i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            if (card.Kind == DeviceKind.Monitor) continue;
            var mine = groups.Where(g => Claims(card, g)).ToList();
            if (mine.Count == 0) continue;
            claimed.UnionWith(mine);

            // The groups that ARE this kind of device (a receiver's keyboard collection does not make it a keyboard).
            var own = mine.Where(g => SameKind(card.Kind, g.Kind)).ToList();
            if (own.Count == 0) own = mine;

            card = NameCard(card, own, history);
            cards[i] = card;
            AddVendorApps(card, own, apps, launch, onceNotes);
        }

        AddInventoryCards(cards, groups.Where(g => !claimed.Contains(g)).ToList(), apps, history, launch, onceNotes);
        AddDynamicLightingNote(cards, groups, apps);
        AddUnifierNote(cards, apps);
        FixHdrCalibrationLinks(cards);
    }

    /// <summary>A card stands for a group when one of its hardware refs has the group's VID:PID, real container or a node of it.</summary>
    internal static bool Claims(DeviceCard card, InventoryGroup g) => card.Hardware.Any(h =>
        (h.Vid is not null && h.Pid is not null && string.Equals(h.Vid, g.Vid, StringComparison.OrdinalIgnoreCase)
         && string.Equals(h.Pid, g.Pid, StringComparison.OrdinalIgnoreCase))
        || (g.ContainerId is not null && h.ContainerId is not null && string.Equals(h.ContainerId, g.ContainerId, StringComparison.OrdinalIgnoreCase))
        || h.InstanceIds.Any(id => g.InstanceIds.Contains(id, StringComparer.OrdinalIgnoreCase)));

    private static bool SameKind(DeviceKind card, InventoryKind group) => (card, group) switch
    {
        (DeviceKind.Mouse, InventoryKind.Mouse) => true,
        (DeviceKind.Keyboard, InventoryKind.Keyboard) => true,
        (DeviceKind.AudioOutput or DeviceKind.AudioInput, InventoryKind.Audio) => true,
        (DeviceKind.Controller, InventoryKind.Controller) => true,
        (DeviceKind.Lighting, InventoryKind.Lighting) => true,
        _ => false,
    };

    // ───────────────────────────── a) The model name ─────────────────────────────

    /// <summary>
    /// A generic title («Мышь», «USB Receiver», an endpoint name) gets the model as its subtitle; a receiver its PID-table name
    /// plus, on a mouse card, what was plugged in by cable before («раньше по кабелю подключалась: …»).
    /// </summary>
    private static DeviceCard NameCard(DeviceCard card, IReadOnlyList<InventoryGroup> own, Func<string, IReadOnlyList<HistoryHint>> history)
    {
        var title = card.Title.Trim();
        var generic = IsGenericTitle(card) || card.Kind is DeviceKind.AudioOutput or DeviceKind.AudioInput
                      || own.Any(g => g.Name.Contains(title, StringComparison.OrdinalIgnoreCase) && g.Name.Length > title.Length);
        if (!generic) return card;

        var parts = new List<string>();
        foreach (var g in own)
        {
            if (title.Contains(g.Name, StringComparison.OrdinalIgnoreCase) || parts.Contains(g.Name, StringComparer.OrdinalIgnoreCase)) continue;
            parts.Add(g.Name);
        }
        if (card.Kind == DeviceKind.Mouse && own.FirstOrDefault(g => ReceiverHints.IsReceiver(g.Vid, g.Pid)) is { Vid: { } vid })
        {
            IReadOnlyList<HistoryHint> past;
            try { past = history(vid); }
            catch (Exception ex)
            {
                Log.Warn("Устройства: история подключений не прочитана ({0}).", ex.Message);
                past = [];
            }
            if (past.FirstOrDefault() is { } last) parts.Add(ReceiverHints.HistoryText(last.Name));
        }
        if (parts.Count == 0) return card;
        if (card.Subtitle is { Length: > 0 } existing && !parts.Any(p => existing.Contains(p, StringComparison.OrdinalIgnoreCase)))
            parts.Add(existing);
        var subtitle = string.Join(" · ", parts.Take(3));
        return subtitle == card.Subtitle ? card : WithSubtitle(card, subtitle);
    }

    private static bool IsGenericTitle(DeviceCard card)
    {
        var t = card.Title.Trim();
        return string.Equals(t, card.KindLabel, StringComparison.OrdinalIgnoreCase)
               || string.Equals(t, card.GroupTitle, StringComparison.OrdinalIgnoreCase)
               || ReceiverHints.IsGenericProductName(t) || HardwareRules.IsGenericDeviceName(t);
    }

    /// <summary>DeviceCard's title and subtitle are fixed at creation: the enriched card is a copy sharing every collection.</summary>
    internal static DeviceCard WithSubtitle(DeviceCard c, string? subtitle) => new()
    {
        Key = c.Key, Kind = c.Kind, Title = c.Title, Subtitle = subtitle, Order = c.Order, GlyphOverride = c.GlyphOverride,
        Hardware = c.Hardware, Settings = c.Settings, Tools = c.Tools, VendorApps = c.VendorApps, Facts = c.Facts, Notes = c.Notes,
    };

    // ───────────────────────────── b–e) Vendor programs and their notes ─────────────────────────────

    private static void AddVendorApps(DeviceCard card, IReadOnlyList<InventoryGroup> own, VendorAppSnapshot apps,
        Func<VendorApp, Task<bool>> launch, HashSet<string> onceNotes)
    {
        var names = own.Select(g => g.Name).Append(card.Title).ToList();
        var vids = own.Select(g => g.Vid).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var vid in vids)
        {
            var candidates = VendorApps.All.Where(a => !a.IsRgbUnifier && a.AppliesTo(vid, names)).ToList();
            if (candidates.Count == 0) continue;
            var installed = candidates.Where(a => apps.IsInstalled(a.Key)).ToList();
            foreach (var app in installed)
            {
                if (apps.Get(app.Key) is { Launchable: true } state) AddLink(card, LaunchLink(app, card.Kind, state.Running, launch));
                else AddNote(card, new DeviceNote(OpenFromStartNote(app.Name)));
            }
            if (installed.Count == 0 && !(card.Kind == DeviceKind.Lighting && apps.Installed.Any(a => a.IsRgbUnifier))
                && candidates.FirstOrDefault(a => a.Install is not null) is { } offer)
                AddLink(card, InstallLink(offer, vid));
        }

        // c) Logitech G mice keep DPI / polling in onboard memory only when G HUB is told to (mouse cards only).
        if (card.Kind == DeviceKind.Mouse && apps.IsInstalled(VendorApps.GHub.Key)
            && own.Any(g => ReceiverHints.IsLogitechGamingMouse(g.Vid, g.Pid)))
            AddNote(card, new DeviceNote(OnboardMemoryNote));

        // e) What only the vendor program sets (shown when it is installed, so the note can be acted on), once per device.
        foreach (var g in own)
        {
            if (card.Kind == DeviceKind.AudioOutput && VendorApps.InzoneHub.AppliesTo(g.Vid, [g.Name, card.Title])
                && apps.IsInstalled(VendorApps.InzoneHub.Key) && onceNotes.Add(g.Key + "|inzone"))
                AddNote(card, new DeviceNote(InzoneNote));
            if (card.Kind == DeviceKind.AudioInput && string.Equals(g.Vid, "1532", StringComparison.OrdinalIgnoreCase)
                && apps.IsInstalled(VendorApps.Synapse.Key) && onceNotes.Add(g.Key + "|razer-mic"))
                AddNote(card, new DeviceNote(RazerMicNote));
        }
    }

    /// <summary>«ОТКРЫТЬ G HUB». The launcher shows its own reason when it fails, so the click is always «handled».</summary>
    private static VendorAppLink LaunchLink(VendorApp app, DeviceKind kind, bool running, Func<VendorApp, Task<bool>> launch) => new()
    {
        AppName = app.Name,
        IsInstalled = true,
        Tooltip = LaunchTooltip(app, kind, running),
        Launch = async () =>
        {
            await launch(app).ConfigureAwait(false);
            return true;
        },
    };

    /// <summary>«УСТАНОВИТЬ В «ДРАЙВЕРАХ»» → Drivers → «Подбор для вашего ПК», the vendor's row ("periph:046D").</summary>
    private static VendorAppLink InstallLink(VendorApp app, string vid) => new()
    {
        AppName = app.Name,
        IsInstalled = false,
        OfferInstall = true,
        InstallFocusId = "periph:" + vid.ToUpperInvariant(),
        Note = NotInstalledNote(app.Name),
        Tooltip = Loc.Tf("{0} (по желанию). Устройство работает и без неё.", NotInstalledNote(app.Name)),
    };

    /// <summary>What the program configures for this kind of device (the keyboard's polling rate lives here, not in a note).</summary>
    internal static string LaunchTooltip(VendorApp app, DeviceKind kind, bool running)
    {
        var what = kind switch
        {
            DeviceKind.Mouse => Loc.Tf("DPI, частота опроса, кнопки и подсветка мыши настраиваются в {0}.", app.Name),
            DeviceKind.Keyboard => Loc.Tf("Частота опроса, подсветка и макросы клавиатуры настраиваются в {0}.", app.Name),
            _ => Loc.Tf("Настройки этого устройства — в {0}.", app.Name),
        };
        return what + " " + (running ? Loc.T("Программа уже запущена — откроется её окно.") : Loc.T("Запускается без прав администратора."));
    }

    private static void AddLink(DeviceCard card, VendorAppLink link)
    {
        if (card.VendorApps.Any(a => string.Equals(a.AppName, link.AppName, StringComparison.OrdinalIgnoreCase))) return;
        card.VendorApps.Add(link);
    }

    private static void AddNote(DeviceCard card, DeviceNote note)
    {
        if (card.Notes.Any(n => string.Equals(n.Text, note.Text, StringComparison.Ordinal))) return;
        card.Notes = [.. card.Notes, note];
    }

    // ───────────────────────────── Devices no provider covers ─────────────────────────────

    /// <summary>
    /// Unclaimed lighting controllers, gamepads and devices of known vendors become cards — only when there is something to do
    /// here (open the program, or a note to act on). An install offer alone stays on Drivers.
    /// </summary>
    private static void AddInventoryCards(List<DeviceCard> cards, IReadOnlyList<InventoryGroup> unclaimed, VendorAppSnapshot apps,
        Func<string, IReadOnlyList<HistoryHint>> history, Func<VendorApp, Task<bool>> launch, HashSet<string> onceNotes)
    {
        var unifier = apps.Installed.FirstOrDefault(a => a.IsRgbUnifier);
        // Mice, keyboards and audio belong to their providers; only when none of them reported anything is the group shown here.
        var providerMice = cards.Any(c => c.Kind == DeviceKind.Mouse);
        var providerKeyboards = cards.Any(c => c.Kind == DeviceKind.Keyboard);
        var providerAudio = cards.Any(c => c.Kind is DeviceKind.AudioOutput or DeviceKind.AudioInput);
        var order = 80;
        foreach (var g in unclaimed)
        {
            if (g.Kind == InventoryKind.Other) continue;
            if (g.Kind is not (InventoryKind.Lighting or InventoryKind.Controller) && g.Vendor is null) continue;
            if (g.Kind == InventoryKind.Mouse && providerMice) continue;
            if (g.Kind == InventoryKind.Keyboard && providerKeyboards) continue;
            if (g.Kind == InventoryKind.Audio && providerAudio) continue;

            var kind = g.Kind switch
            {
                InventoryKind.Mouse => DeviceKind.Mouse,
                InventoryKind.Keyboard => DeviceKind.Keyboard,
                InventoryKind.Audio => DeviceKind.AudioOutput,
                InventoryKind.Controller => DeviceKind.Controller,
                InventoryKind.Lighting => DeviceKind.Lighting,
                _ => DeviceKind.Peripheral,
            };
            var board = g.VidPid is "0DB0:0076";
            var subtitle = board ? Loc.T("подсветка материнской платы") : null;
            if (kind == DeviceKind.Mouse && ReceiverHints.IsReceiver(g.Vid, g.Pid) && g.Vid is { } vid)
            {
                try { subtitle = history(vid).FirstOrDefault() is { } last ? ReceiverHints.HistoryText(last.Name) : null; }
                catch (Exception ex) { Log.Warn("Устройства: история подключений не прочитана ({0}).", ex.Message); }
            }
            var card = new DeviceCard
            {
                Key = g.CardKey,
                Kind = kind,
                Title = DeviceInventory.DisplayTitle(g),
                Subtitle = subtitle,
                Order = order,
                Hardware = [new DeviceHardwareRef(g.Vid, g.Pid, g.ContainerId, g.InstanceIds)],
            };

            AddVendorApps(card, [g], apps, launch, onceNotes);
            // The board's RGB with OpenRGB already installed: never recommend a third lighting program.
            var vendorInstalled = VendorApps.All.Any(a => !a.IsRgbUnifier && a.AppliesTo(g.Vid, [g.Name]) && apps.IsInstalled(a.Key));
            if (!vendorInstalled && kind == DeviceKind.Lighting && unifier is not null)
                AddNote(card, new DeviceNote(UnifierOwnsNote(unifier.Name, board)));

            // Only an install offer: that lives on Drivers (ПОДБОР ДЛЯ ВАШЕГО ПК) — no card here.
            if (!card.VendorApps.Any(a => a.CanLaunch) && card.Notes.Count == 0) continue;
            cards.Add(card);
            order++;
        }
    }

    // ───────────────────────────── Page-wide notes (once) ─────────────────────────────

    /// <summary>
    /// d) A device with LampArray children (G HUB's Dynamic Lighting driver): one note per page, on the keyboard card, else on the
    /// first card of such a device, with a link to Windows lighting settings.
    /// </summary>
    private static void AddDynamicLightingNote(List<DeviceCard> cards, IReadOnlyList<InventoryGroup> groups, VendorAppSnapshot apps)
    {
        var lit = groups.Where(g => g.HasLampArray).ToList();
        if (lit.Count == 0) return;
        bool HasLamp(DeviceCard c) => c.Kind != DeviceKind.Monitor && lit.Any(g => Claims(c, g));
        var target = cards.FirstOrDefault(c => c.Kind == DeviceKind.Keyboard && HasLamp(c)) ?? cards.FirstOrDefault(HasLamp);
        if (target is null) return;
        var group = lit.First(g => Claims(target, g));
        var app = VendorApps.All.FirstOrDefault(a => a.ControlsLighting && a.AppliesTo(group.Vid, [group.Name]) && apps.IsInstalled(a.Key))?.Name
                  ?? VendorApps.All.FirstOrDefault(a => a.ControlsLighting && a.AppliesTo(group.Vid, [group.Name]))?.Name
                  ?? Loc.T("программу производителя");
        AddNote(target, new DeviceNote(DynamicLightingNote(app), new DeviceLink(DeviceLinkKind.Settings, Loc.T("ПАРАМЕТРЫ"), LightingSettings)));
    }

    /// <summary>OpenRGB / SignalRGB next to a vendor's own lighting program: one note, on the lighting card, else the keyboard card.</summary>
    private static void AddUnifierNote(List<DeviceCard> cards, VendorAppSnapshot apps)
    {
        var unifiers = apps.Installed.Where(a => a.IsRgbUnifier).Select(a => a.Name).ToList();
        var vendors = apps.Installed.Where(a => a.ControlsLighting).Select(a => a.Name).ToList();
        if (unifiers.Count == 0 || vendors.Count == 0) return;
        var target = cards.FirstOrDefault(c => c.Kind == DeviceKind.Lighting) ?? cards.FirstOrDefault(c => c.Kind == DeviceKind.Keyboard);
        if (target is not null) AddNote(target, new DeviceNote(UnifierNote(unifiers, vendors)));
    }

    // ───────────────────────────── HDR calibration: never a dead end ─────────────────────────────

    /// <summary>
    /// A monitor note that sends the user to «Приложения» for Microsoft's HDR calibration app while the Apps catalog has no such
    /// entry would end nowhere: the note keeps its text and the card gets a button that opens the app's Microsoft Store page
    /// (install or open it there), started without MONOLITH's admin token.
    /// </summary>
    internal static void FixHdrCalibrationLinks(List<DeviceCard> cards, bool? inAppsCatalog = null)
    {
        var catalogHasIt = inAppsCatalog ?? VendorApps.HdrCalibrationInAppsCatalog;
        foreach (var card in cards.Where(c => c.Kind == DeviceKind.Monitor))
        {
            var notes = card.Notes.Where(IsHdrCalibrationNote).ToList();
            if (notes.Count == 0) continue;
            // A note that sends the user to «Приложения» is fine while the catalog can install the app there.
            if (catalogHasIt && notes.Any(n => LinksToApps(n))) continue;
            // The displays package links the note to the Windows HDR settings page (kept); only a link into «Приложения» would
            // dead-end without the catalog entry. Either way the card gets the button that opens the Store page.
            card.Notes = card.Notes.Select(n => IsHdrCalibrationNote(n) && LinksToApps(n) ? n with { Link = null } : n).ToList();
            AddLink(card, new VendorAppLink
            {
                AppName = Loc.T("калибровку HDR"),
                IsInstalled = false,
                Tooltip = Loc.T("Приложение Microsoft «Калибровка HDR в Windows»: откроется его страница в Microsoft Store — установить или запустить."),
                Launch = VendorApps.OpenHdrCalibrationAsync,
            });
        }
    }

    private static bool LinksToApps(DeviceNote n) => n.Link is { Kind: DeviceLinkKind.Navigate, Target: "apps" };

    /// <summary>A note about Microsoft's HDR calibration app, whatever its link (into «Приложения», the HDR settings page, or none).</summary>
    private static bool IsHdrCalibrationNote(DeviceNote n) =>
        (n.Link is { Kind: DeviceLinkKind.Navigate, Target: "apps" } link
         && (link.FocusId?.Contains(VendorApps.HdrCalibrationStoreId, StringComparison.OrdinalIgnoreCase) == true
             || link.FocusId?.Contains("hdr", StringComparison.OrdinalIgnoreCase) == true))
        || (n.Text.Contains("HDR", StringComparison.OrdinalIgnoreCase) && (n.Text.Contains(Loc.Keep("алибр"), StringComparison.OrdinalIgnoreCase) || n.Text.Contains("alibrat", StringComparison.OrdinalIgnoreCase)));
}
