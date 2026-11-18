using System.ServiceProcess;
using Lycoris.Core;

namespace Lycoris.Modules.Services;

public enum ServiceAction { Enable, Disable, Start, Stop }

public sealed record ServiceActionRequest(ServiceAction Action, string Target)
{
    /// <summary>Elevation replay accepts only the previously resolved SCM key name.</summary>
    public bool ExactNameOnly { get; init; }
}

public enum ServiceActionOutcome
{
    Applied, AlreadyApplied, Declined, Blocked, Ambiguous, Missing, NeedsAdmin, Stale, Failed, Unverified,
}

public sealed record ServiceActionResult(ServiceActionOutcome Outcome, string Message, string? CanonicalName = null,
    Guid? JournalRecordId = null);

internal sealed record ServiceActionIdentity(string Name, string DisplayName);

internal sealed record ServiceActionSettings(bool Locked, bool Demo, bool Elevated, bool Expert, bool ServicesExpert,
    bool CreateRestorePoints);

internal sealed record ServiceActionFacts(
    ServiceActionIdentity Identity, ServiceStartMode Start, ServiceControllerStatus Status, int Type,
    int ErrorControl, int LaunchProtected, string ImagePath, string Account, string? ServiceDll,
    bool? RawDelayed, long KeyVersion, long? ParametersVersion, long? TriggerVersion,
    bool? Managed, bool? CatalogGatePassed, int? DependentCount, bool? CanChange, Guid? ActiveRecordId,
    string? DependencyState = null);

internal sealed record ServiceActionPlan(ServiceAction Action, ServiceActionSettings Settings, ServiceActionFacts Facts,
    ServiceStartMode Destination, ServiceControllerStatus? DestinationStatus = null);

internal readonly record struct ServiceActionWrite(bool Accepted, Guid? RecordId);

internal interface IServiceActionSession : IDisposable
{
    void Exclusive(Action body);
    ServiceActionWrite Apply(ServiceActionPlan plan);
}

internal interface IServiceActionBackend
{
    ServiceActionSettings ReadSettings();
    IReadOnlyList<ServiceActionIdentity> ScanIdentities();
    ServiceActionFacts? ReadFacts(ServiceActionIdentity identity, ServiceAction action, bool elevated);
    IDisposable? HoldIdentity(ServiceActionIdentity identity);
    ServiceActionFacts? ReadBack(ServiceActionPlan plan);
    Task<bool> ConfirmAsync(ServiceActionPlan plan);
    Task<bool> RunAsync(ServiceActionPlan plan, Func<Task> work);
    Task<IServiceActionSession?> BeginAsync(ServiceActionPlan plan);
    Task<bool> ApplyRuntimeAsync(ServiceActionPlan plan);
}

internal sealed class ServiceActionWorkflow(IServiceActionBackend backend)
{
    public async Task<ServiceActionResult> ExecuteAsync(ServiceActionRequest request, CancellationToken ct)
    {
        try
        {
            var (plan, decision) = await Task.Run(() => Prepare(request), ct);
            if (plan is null) return decision!;
            var name = plan.Facts.Identity.Name;
            ct.ThrowIfCancellationRequested();
            if (!await backend.ConfirmAsync(plan)) return Declined(name);

            ServiceActionResult? result = null;
            var ran = await backend.RunAsync(plan, async () =>
            {
                if (ct.IsCancellationRequested) return;
                if (plan.DestinationStatus is not null)
                {
                    result = await Task.Run(async () =>
                    {
                        using var identity = backend.HoldIdentity(plan.Facts.Identity);
                        if (identity is null || !StillCurrent(request, plan)) return Stale(name);
                        return await ApplyRuntimeAsync(plan, ct);
                    });
                    return;
                }
                if (!await Task.Run(() => StillCurrent(request, plan)))
                {
                    result = Stale(name);
                    return;
                }

                if (ct.IsCancellationRequested) return;
                var session = await backend.BeginAsync(plan);
                if (session is null) { result = Declined(name); return; }

                result = await Task.Run(() =>
                {
                    using (session)
                    {
                        ServiceActionResult? committed = null;
                        session.Exclusive(() =>
                        {
                            using var identity = backend.HoldIdentity(plan.Facts.Identity);
                            committed = ct.IsCancellationRequested ? Declined(name)
                                : identity is not null && StillCurrent(request, plan) ? Apply(session, plan, ct) : Stale(name);
                        });
                        return committed!;
                    }
                });
            });
            if (ct.IsCancellationRequested && result is null) return Declined(name);
            return result ?? (ran ? Failed(name) : Declined(name));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Declined();
        }
        catch (Exception ex)
        {
            Log.Warn("Изменение типа запуска службы: {0}", ex.GetType().Name);
            return Failed();
        }
    }

    private (ServiceActionPlan? Plan, ServiceActionResult? Decision) Prepare(ServiceActionRequest request)
    {
        var settings = backend.ReadSettings();
        if (settings.Locked || settings.Demo)
            return (null, Blocked(Loc.T("Изменения служб в этом режиме недоступны.")));
        if (!Enum.IsDefined(request.Action) || string.IsNullOrWhiteSpace(request.Target) || request.Target.Any(char.IsControl))
            return (null, Blocked(Loc.T("Укажите точное имя одной службы.")));

        ServiceActionIdentity? identity = null;
        try
        {
            var target = request.Target.Trim();
            var identities = backend.ScanIdentities();
            var matches = identities.Where(candidate => SameName(candidate.Name, target)
                || (!request.ExactNameOnly && SameName(candidate.DisplayName, target))).ToList();
            if (matches.Count == 0 && !request.ExactNameOnly && IsSpoolerAlias(target))
                matches = identities.Where(candidate => SameName(candidate.Name, "Spooler")).ToList();
            if (matches.Count == 0)
                return (null, new(ServiceActionOutcome.Missing, Loc.Tf("Служба «{0}» не найдена. Укажите её точное имя.", target)));
            if (matches.Count > 1)
                return (null, new(ServiceActionOutcome.Ambiguous, Loc.Tf("Уточните системное имя службы. Подходят: {0}.",
                    string.Join(", ", matches.Select(match => match.Name)))));

            identity = matches[0];
            var name = identity.Name;
            var nameLock = ServiceGuard.HardBlockReason(name) ?? ServiceGuard.OptimizeOwnedLockReason(name);
            if (nameLock is not null) return (null, Blocked(nameLock, name));
            if (ServiceControl.IsProtected(name))
                return (null, Blocked(Loc.T("Служба защищена Lycoris и не изменяется."), name));
            if ((request.Action is ServiceAction.Enable or ServiceAction.Start) && ServiceGuard.MustStayDisabled(name))
                return (null, Blocked(Loc.T("Эта служба должна оставаться отключённой."), name));

            var facts = backend.ReadFacts(identity, request.Action, settings.Elevated);
            if (facts is null || facts.Managed is null || facts.CatalogGatePassed is null)
                return (null, Blocked(Loc.T("Не удалось проверить настройки и условия безопасности службы."), name));
            if (facts.Managed.Value)
                return (null, Blocked(Loc.T("На компьютере под управлением организации службы доступны только для просмотра."), name));
            if (ServiceGuard.GetLockReason(name, facts.Type, (int)facts.Start, facts.ErrorControl, facts.LaunchProtected,
                    facts.ImagePath) is { } structuralLock)
                return (null, Blocked(structuralLock, name));
            if (ServiceGuard.IsDriverStorePath(facts.ServiceDll))
                return (null, Blocked(Loc.T("Служба из пакета драйвера производителя: без неё перестанут работать функции драйвера."), name));
            if (facts.Start is not (ServiceStartMode.Manual or ServiceStartMode.Automatic or ServiceStartMode.Disabled) ||
                facts.Type is not (0x10 or 0x20 or 0x110 or 0x120) ||
                facts.Status is not (ServiceControllerStatus.Running or ServiceControllerStatus.Stopped or ServiceControllerStatus.Paused))
                return (null, Blocked(Loc.T("Тип запуска, тип службы или текущее состояние не позволяют безопасно выполнить действие."), name));

            var destination = request.Action switch
            {
                ServiceAction.Disable => ServiceStartMode.Disabled,
                ServiceAction.Enable when facts.Start == ServiceStartMode.Disabled => ServiceStartMode.Manual,
                _ => facts.Start,
            };
            var destinationStatus = request.Action switch
            {
                ServiceAction.Start => (ServiceControllerStatus?)ServiceControllerStatus.Running,
                ServiceAction.Stop => ServiceControllerStatus.Stopped,
                _ => null,
            };
            if (destinationStatus is { } status ? facts.Status == status : destination == facts.Start)
                return (null, new(ServiceActionOutcome.AlreadyApplied, DescribeResult(facts, request.Action, already: true), name));
            if (request.Action == ServiceAction.Disable && ServiceGuard.MustStayDisabled(name))
                return (null, Blocked(Loc.T("Точный откат вернул бы этой службе возможность запуска."), name));
            if (request.Action == ServiceAction.Start && facts.Start == ServiceStartMode.Disabled)
                return (null, Blocked(Loc.Tf("Служба отключена. Сначала включите возможность запуска службы «{0}».", name), name));
            if (request.Action == ServiceAction.Start && facts.Status != ServiceControllerStatus.Stopped)
                return (null, Blocked(Loc.T("Запуск доступен для службы в состоянии «Остановлена»."), name));
            if (!facts.CatalogGatePassed.Value)
                return (null, Blocked(Loc.T("Текущее состояние компьютера не допускает это изменение службы."), name));

            var curated = ServiceCatalog.Find(name);
            if (curated is null && !ServiceGuard.ExpertModeEnabled(settings.Expert, settings.ServicesExpert))
                return (null, Blocked(Loc.T("Для этой службы требуется экспертный режим служб."), name));
            if (curated is not null &&
                (!ServiceGuard.CanApplyRisk(curated.Risk, settings.Expert) ||
                 (request.Action == ServiceAction.Disable && curated.Target != destination && !settings.Expert)))
                return (null, Blocked(Loc.T("Для такого изменения требуется экспертный режим."), name));
            if (request.Action is ServiceAction.Disable or ServiceAction.Stop or ServiceAction.Start)
            {
                if (request.Action == ServiceAction.Disable && curated?.Companion is not null)
                    return (null, Blocked(Loc.Tf("Рекомендация для этой службы требует совместного изменения службы «{0}».",
                        curated.Companion), name));
                if (facts.DependentCount is null)
                    return (null, Blocked(Loc.T("Не удалось проверить все зависимости службы."), name));
                if (facts.DependentCount > 0)
                    return (null, Blocked(request.Action switch
                    {
                        ServiceAction.Start => Loc.T("Для запуска пришлось бы запустить другие службы или группу служб."),
                        ServiceAction.Stop => Loc.T("Служба нужна работающим службам. Остановка заблокирована."),
                        _ => Loc.Tf("Служба нужна другим службам ({0}). Отключение заблокировано.", facts.DependentCount),
                    }, name));
            }
            if (backend.ReadSettings() != settings) return (null, Stale(name));
            if (!settings.Elevated)
                return (null, new(ServiceActionOutcome.NeedsAdmin, Loc.T("Для управления службой нужны права администратора."), name));
            if (facts.CanChange != true)
                return (null, Blocked(Loc.T("Действие недоступно с текущими правами и состоянием службы."), name));

            return (new(request.Action, settings, facts, destination, destinationStatus), null);
        }
        catch (Exception ex)
        {
            Log.Warn("Проверка службы: {0}", ex.GetType().Name);
            return (null, Blocked(Loc.T("Не удалось проверить настройки и условия безопасности службы."), identity?.Name));
        }
    }

    private bool StillCurrent(ServiceActionRequest request, ServiceActionPlan plan) => Prepare(request).Plan == plan;

    private ServiceActionResult Apply(IServiceActionSession session, ServiceActionPlan plan, CancellationToken ct)
    {
        var name = plan.Facts.Identity.Name;
        if (ct.IsCancellationRequested) return Declined(name);
        if (backend.ReadSettings() != plan.Settings) return Stale(name);
        ServiceActionWrite written = default;
        try
        {
            written = session.Apply(plan);
            var readBack = backend.ReadBack(plan);
            if (written.Accepted && written.RecordId is not null && readBack is not null &&
                SameConfiguration(plan.Facts, readBack, plan.Destination))
                return new(ServiceActionOutcome.Applied, DescribeResult(readBack, plan.Action, already: false), name, written.RecordId);
            if (!written.Accepted && written.RecordId is null && readBack is not null &&
                SameConfiguration(plan.Facts, readBack, plan.Facts.Start))
                return Failed(name);
        }
        catch (Exception ex)
        {
            Log.Warn("Проверка результата изменения службы: {0}", ex.GetType().Name);
        }
        return Unverified(name, written.RecordId);
    }

    private async Task<ServiceActionResult> ApplyRuntimeAsync(ServiceActionPlan plan, CancellationToken ct)
    {
        var name = plan.Facts.Identity.Name;
        if (ct.IsCancellationRequested) return Declined(name);
        if (backend.ReadSettings() != plan.Settings) return Stale(name);
        try
        {
            var accepted = await backend.ApplyRuntimeAsync(plan);
            var readBack = backend.ReadBack(plan);
            if (readBack is not null && SameConfiguration(plan.Facts, readBack, plan.Facts.Start))
            {
                if (accepted && readBack.Status == plan.DestinationStatus)
                    return new(ServiceActionOutcome.Applied, DescribeResult(readBack, plan.Action, already: false), name);
                if (!accepted && readBack.Status == plan.Facts.Status)
                    return Failed(name);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Проверка состояния службы: {0}", ex.GetType().Name);
        }
        return Unverified(name);
    }

    private static ServiceActionResult Unverified(string name, Guid? recordId = null) =>
        new(ServiceActionOutcome.Unverified, Loc.T("Не удалось подтвердить результат изменения. Проверьте текущее состояние службы."),
            name, recordId);

    private static bool SameConfiguration(ServiceActionFacts before, ServiceActionFacts after, ServiceStartMode expected) =>
        after.Start == expected && before.Identity == after.Identity && before.Type == after.Type &&
        before.ErrorControl == after.ErrorControl && before.LaunchProtected == after.LaunchProtected &&
        before.ImagePath == after.ImagePath && before.Account == after.Account && before.ServiceDll == after.ServiceDll &&
        before.RawDelayed == after.RawDelayed && before.ParametersVersion == after.ParametersVersion &&
        before.TriggerVersion == after.TriggerVersion;

    internal static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsSpoolerAlias(string target) => SameName(target, Loc.Keep("служба печати"))
        || SameName(target, Loc.Keep("печати")) || SameName(target, Loc.Keep("печать"))
        || SameName(target, Loc.Keep("Диспетчер печати")) || SameName(target, "Print Spooler");

    private static string DescribeResult(ServiceActionFacts facts, ServiceAction action, bool already)
    {
        var start = ServiceText.Start(facts.Start, facts.RawDelayed == true && facts.Start == ServiceStartMode.Automatic,
            facts.TriggerVersion is not null);
        var state = ServiceText.Status(facts.Status);
        var label = facts.Identity;
        if (action is ServiceAction.Start or ServiceAction.Stop)
            return Loc.Tf("«{0}» ({1}). Текущее состояние: {2}. Тип запуска: {3}.", label.DisplayName, label.Name, state, start);

        var result = already
            ? Loc.Tf("«{0}» ({1}) уже имеет нужный тип запуска: {2}. Текущее состояние: {3}.", label.DisplayName, label.Name, start, state)
            : Loc.Tf("«{0}» ({1}): тип запуска изменён на «{2}». Текущее состояние: {3}.", label.DisplayName, label.Name, start, state);
        if (action == ServiceAction.Enable && facts.Status == ServiceControllerStatus.Stopped)
            result += " " + Loc.Tf("Для запуска сейчас можно написать «запусти службу {0}».", label.Name);
        return result;
    }

    internal static ServiceActionResult Blocked(string detail, string? name = null) => new(ServiceActionOutcome.Blocked, detail, name);
    private static ServiceActionResult Declined(string? name = null) =>
        new(ServiceActionOutcome.Declined, Loc.T("Изменение службы отменено."), name);
    private static ServiceActionResult Stale(string name) =>
        new(ServiceActionOutcome.Stale, Loc.T("Данные службы или разрешения изменились после подтверждения. Повторите запрос."), name);
    private static ServiceActionResult Failed(string? name = null) =>
        new(ServiceActionOutcome.Failed, Loc.T("Не удалось применить изменение службы."), name);
}
