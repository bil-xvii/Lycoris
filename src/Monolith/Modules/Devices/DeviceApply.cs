using System.Text.RegularExpressions;
using Monolith.Core;
using Monolith.Core.Devices;
using Monolith.Modules.Restore;
using Monolith.Tweaks;
using Monolith.UI;

namespace Monolith.Modules.Devices;

/// <summary>What the АВТОНАСТРОЙКА run did, per row.</summary>
public sealed record PlanApplyResult(int Applied, int Declined, int Unchanged, int Blocked, int Failed, IReadOnlyList<string> Errors,
    RestartRequirement Restart, IReadOnlyList<string> Notes);

/// <summary>
/// Applying, reverting and links of the «Устройства и сеть» page. Every change goes through a ChangeSession; the caller
/// (DevicesViewModel) has already checked admin rights with RequireAdmin, and each method checks them again.
/// </summary>
public static class DeviceApply
{
    public const string PlanTitle = "Устройства и сеть: автонастройка";

    // ───────────────────────────── Plan ─────────────────────────────

    /// <summary>Lines of the confirmation dialog: «Мышь · Скорость указателя: 14 → 10».</summary>
    public static IReadOnlyList<string> PlanLines(IEnumerable<PlanItem> items) =>
        items.Select(i => i.Kind == PlanItemKind.OptimizeTweak
                ? $"{i.Card.GroupTitle} · {i.Setting.Title}: пункт «Оптимизации», {i.From} → {i.To}"
                : $"{i.Card.GroupTitle} · {i.Setting.Title}: {i.From} → {i.To}" + (i.NeedsCountdown ? " (подтвердите на экране за 15 с)" : ""))
            .ToList();

    /// <summary>
    /// Asks, then applies the selected rows in one batch with a restore point: Optimize tweaks through TweakEngine (their own
    /// sources), then device settings, then the ones with an on-screen confirmation, one at a time. Null = cancelled.
    /// </summary>
    public static async Task<PlanApplyResult?> ApplyPlanAsync(IReadOnlyList<PlanItem> selected, IProgress<string>? progress, CancellationToken ct)
    {
        if (!Elevation.IsElevated) return null;
        var items = selected.Where(i => i is { Selectable: true, IsSelected: true, HasChange: true }).ToList();
        if (items.Count == 0) return null;

        var confirmed = await Ui.ConfirmAsync("Настроить автоматически?",
            "MONOLITH изменит выбранные настройки. Перед изменением исходные значения записываются в журнал:",
            PlanLines(items), confirmText: "НАСТРОИТЬ",
            footnote: "Каждое изменение откатывается по одному — в «Вручную» или в «Восстановлении».");
        if (!confirmed) return null;

        var session = await Task.Run(() => ChangeSession.BeginAsync(PlanTitle, createRestorePoint: true, progress), CancellationToken.None);
        int applied = 0, declined = 0, unchanged = 0, blocked = 0, failed = 0;
        var notes = new List<string>();
        var restart = RestartRequirement.None;

        var tweaks = items.Where(i => i.Kind == PlanItemKind.OptimizeTweak)
            .Select(i => TweakCatalog.Find(i.TweakId!)).OfType<Tweak>().DistinctBy(t => t.Id).ToList();
        if (tweaks.Count > 0)
        {
            var sp = await SystemProfile.LoadAsync();
            restart = await Task.Run(() =>
            {
                var (_, r) = TweakEngine.ApplyInSession(session, tweaks, sp, progress);
                return r;
            }, CancellationToken.None);
            // Count by the live state afterwards: a tweak that did not end up applied is not reported as done.
            foreach (var t in tweaks)
            {
                var state = await Task.Run(() =>
                {
                    try { return t.Detect(); }
                    catch { return TweakState.Unknown; }
                });
                if (state == TweakState.Applied) applied++;
                else failed++;
            }
        }

        foreach (var item in items.Where(i => i.Kind == PlanItemKind.Setting).OrderBy(i => i.NeedsCountdown))
        {
            if (ct.IsCancellationRequested) break;
            progress?.Report(item.Setting.Title);
            var value = item.Setting.Recommended!.Value;
            var outcome = await RunApplyAsync(session, item.Setting, value, ct);
            switch (outcome)
            {
                case ApplyOutcome.Applied: applied++; break;
                case ApplyOutcome.Declined: declined++; break;
                case ApplyOutcome.NoChange: unchanged++; break;
                case ApplyOutcome.Blocked: blocked++; break;
                default: failed++; break;
            }
        }

        await Task.Run(session.Complete, CancellationToken.None);
        if (restart != RestartRequirement.None)
            notes.Add(restart switch
            {
                RestartRequirement.SignOut => "Часть изменений вступит в силу после выхода из системы.",
                RestartRequirement.Reboot => "Часть изменений вступит в силу после перезагрузки.",
                _ => "Часть изменений вступит в силу после перезапуска Проводника.",
            });
        return new PlanApplyResult(applied, declined, unchanged, blocked, failed, session.Errors.ToList(), restart, notes);
    }

    /// <summary>«Применено: 3 · не подтверждено: 1».</summary>
    public static string SummaryText(PlanApplyResult r)
    {
        var parts = new List<string> { $"Применено: {r.Applied}" };
        if (r.Declined > 0) parts.Add($"не подтверждено: {r.Declined}");
        if (r.Unchanged > 0) parts.Add($"уже было так: {r.Unchanged}");
        if (r.Blocked > 0) parts.Add($"сейчас нельзя: {r.Blocked}");
        if (r.Failed > 0) parts.Add($"не применено: {r.Failed}");
        return string.Join(" · ", parts) + "." + (r.Notes.Count > 0 ? " " + string.Join(" ", r.Notes) : "");
    }

    /// <summary>A setting's Apply on the thread pool; an exception is a failure, never a crash.</summary>
    private static async Task<ApplyOutcome> RunApplyAsync(ChangeSession session, DeviceSetting s, DeviceValue value, CancellationToken ct)
    {
        if (s.Apply is not { } apply) return ApplyOutcome.Failed;
        try
        {
            return await Task.Run(() => apply(session, value, ct), CancellationToken.None);
        }
        catch (Exception ex)
        {
            session.Errors.Add($"{s.Title}: {ex.Message}");
            Log.Warn($"{s.Title} — не применено: {ex.Message}");
            return ApplyOutcome.Failed;
        }
    }

    // ───────────────────────────── One setting ─────────────────────────────

    /// <summary>
    /// ПРИМЕНИТЬ of one row: its own small batch without a restore point. Per-user rows only for the desktop user.
    /// Returns what happened plus the errors of the session.
    /// </summary>
    public static async Task<(ApplyOutcome Outcome, IReadOnlyList<string> Errors)> ApplyOneAsync(DeviceSetting s, DeviceValue value, CancellationToken ct)
    {
        if (!Elevation.IsElevated) return (ApplyOutcome.Blocked, ["Нужны права администратора."]);
        if (s.IsReadOnly) return (ApplyOutcome.Failed, ["Эту настройку здесь не меняют."]);
        if (s.PerUser && await Task.Run(() => Elevation.InteractiveUserProblem) is { } account) return (ApplyOutcome.Blocked, [account]);
        var session = await Task.Run(() => ChangeSession.BeginAsync($"Устройства и сеть: {s.Title}", createRestorePoint: false), CancellationToken.None);
        var outcome = await RunApplyAsync(session, s, value, ct);
        await Task.Run(session.Complete, CancellationToken.None);
        return (outcome, session.Errors.ToList());
    }

    // ───────────────────────────── Revert ─────────────────────────────

    /// <summary>
    /// The record a row's ОТКАТИТЬ undoes: the top active layer of its target, made by this page for this account — and only
    /// with admin rights (a non-elevated process can never record an undo, so it never makes one).
    /// </summary>
    public static ChangeRecord? RevertTarget(IReadOnlyList<ChangeRecord> activeLayers, bool elevated)
    {
        if (!elevated || activeLayers.Count == 0) return null;
        var top = activeLayers[^1];
        return top.Source == DeviceTargets.Source && ChangeJournal.IsMine(top) && !top.Reverted ? top : null;
    }

    public static ChangeRecord? RevertTarget(DeviceSetting s) =>
        s.JournalTarget is { Length: > 0 } target ? RevertTarget(ChangeJournal.Instance.ActiveLayers(target), Elevation.IsElevated) : null;

    /// <summary>ОТКАТИТЬ of one row (the caller checked admin rights). Null when there is nothing to revert.</summary>
    public static async Task<RevertResult?> RevertOneAsync(DeviceSetting s)
    {
        if (!Elevation.IsElevated) return null;
        if (RevertTarget(s) is not { } record) return null;
        if (s.PerUser && await Task.Run(() => Elevation.InteractiveUserProblem) is { } account)
            return new RevertResult(0, 1, [account]);
        return await Task.Run(() => ChangeJournal.Instance.RevertAsync([record], LiveState.VerifyRevert));
    }

    // ───────────────────────────── Links ─────────────────────────────

    private static readonly Regex PageKey = new("^[a-z]{2,20}$", RegexOptions.CultureInvariant);

    /// <summary>Null when the link may be followed; otherwise why not (pure).</summary>
    public static string? ValidateLink(DeviceLink link) => link.Kind switch
    {
        DeviceLinkKind.Settings => link.Target.StartsWith("ms-settings:", StringComparison.Ordinal) && link.Target.Length is > 12 and <= 200
                                   && !link.Target.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '&' or '|')
            ? null : "ссылка не на параметры Windows",
        DeviceLinkKind.SoundPanel => link.Target is "0" or "1" or "2" or "3" ? null : "нет такой вкладки панели звука",
        DeviceLinkKind.FocusTweak => link.Target.Length is > 0 and <= 80 && link.Target.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? null : "неизвестный пункт «Оптимизации»",
        DeviceLinkKind.Navigate => PageKey.IsMatch(link.Target) ? null : "неизвестный раздел",
        DeviceLinkKind.Tool or DeviceLinkKind.VendorApp => string.IsNullOrWhiteSpace(link.Target) ? "не указано устройство" : null,
        _ => "неизвестная ссылка",
    };

    /// <summary>Follows a link that leaves the page (Windows settings, sound panel, Optimize, another page). Never changes anything.</summary>
    public static async Task OpenExternalAsync(DeviceLink link)
    {
        if (ValidateLink(link) is { } problem)
        {
            Log.Warn($"Ссылка «{link.Label}» отклонена: {problem}.");
            return;
        }
        switch (link.Kind)
        {
            case DeviceLinkKind.Settings:
                if (!await UnelevatedShell.StartAsUserAsync(link.Target))
                    Ui.Toast("Не удалось открыть параметры Windows.", ToastKind.Warning);
                break;
            case DeviceLinkKind.SoundPanel:
                if (!await UnelevatedShell.StartAsUserAsync(ProcessRunner.Sys("control.exe"), $"mmsys.cpl,,{link.Target}"))
                    Ui.Toast("Не удалось открыть панель звука.", ToastKind.Warning);
                break;
            case DeviceLinkKind.FocusTweak:
                Ui.RequestFocus("optimize", link.Target);
                Ui.Navigate("optimize");
                break;
            case DeviceLinkKind.Navigate:
                if (link.FocusId is { Length: > 0 } focus) Ui.RequestFocus(link.Target, focus);
                Ui.Navigate(link.Target);
                break;
        }
    }

    /// <summary>A vendor app: start it (signed, unelevated — the provider's Launch), or install it on Drivers.</summary>
    public static async Task OpenVendorAsync(VendorAppLink app)
    {
        if (app.Launch is { } launch)
        {
            bool ok;
            try { ok = await launch(); }
            catch (Exception ex)
            {
                Log.Warn($"{app.AppName}: {ex.Message}");
                ok = false;
            }
            if (!ok) Ui.Toast($"Не удалось запустить {app.AppName}.", ToastKind.Warning);
            return;
        }
        if (app.OfferInstall)
        {
            if (app.InstallFocusId is { Length: > 0 } focus) Ui.RequestFocus("drivers", focus);
            Ui.Navigate("drivers");
        }
    }
}
