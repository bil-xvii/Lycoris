using System.ServiceProcess;
using Lycoris.Core;
using Lycoris.Modules.Assistant;
using Lycoris.UI;

namespace Lycoris.Modules.Services;

public sealed partial class ServicesViewModel
{
    private int _assistantServiceAction;

    public async Task<ServiceActionResult> ExecuteAssistantServiceActionAsync(ServiceActionRequest request,
        CancellationToken ct = default)
    {
        if (IsBusy || Interlocked.Exchange(ref _assistantServiceAction, 1) != 0)
            return ServiceActionWorkflow.Blocked(Loc.T("Дождитесь окончания текущей операции со службами."));
        try
        {
            var result = await new ServiceActionWorkflow(new AssistantServiceBackend(this)).ExecuteAsync(request, ct);
            if (result.Outcome == ServiceActionOutcome.Applied && IsLoaded &&
                AllServices.FirstOrDefault(item => ServiceActionWorkflow.SameName(item.Name, result.CanonicalName!)) is { } service)
            {
                try { await RefreshServiceAsync(service); }
                catch (Exception ex) { Log.Warn("Обновление состояния службы: {0}", ex.GetType().Name); }
            }
            return result;
        }
        finally
        {
            Volatile.Write(ref _assistantServiceAction, 0);
        }
    }

    private static ServiceActionSettings ReadServiceActionSettings()
    {
        var settings = AppSettings.Current;
        return new(SafetyLock.IsLocked, AssistantViewModel.Demo, Elevation.IsElevated, settings.ExpertMode,
            settings.ServicesExpertMode, settings.CreateRestorePoints);
    }

    private sealed class AssistantServiceBackend(ServicesViewModel owner) : IServiceActionBackend
    {
        public ServiceActionSettings ReadSettings() => ReadServiceActionSettings();

        public IReadOnlyList<ServiceActionIdentity> ScanIdentities() => ServiceScanner.ScanActionIdentities();

        public ServiceActionFacts ReadFacts(ServiceActionIdentity identity, ServiceAction action, bool elevated) =>
            ServiceScanner.ReadActionFacts(identity, action, elevated);

        public IDisposable? HoldIdentity(ServiceActionIdentity identity) => ServiceScanner.HoldActionServices([identity.Name]);

        public ServiceActionFacts ReadBack(ServiceActionPlan plan) => ServiceScanner.ReadActionConfiguration(plan.Facts.Identity);

        public Task<bool> ConfirmAsync(ServiceActionPlan plan)
        {
            var facts = plan.Facts;
            var name = facts.Identity.Name;
            var displayName = facts.Identity.DisplayName;
            if (plan.DestinationStatus is { } runtimeState)
            {
                var stopping = plan.Action == ServiceAction.Stop;
                var runtimeDetails = new List<string>
                {
                    $"{displayName} ({name}): {ServiceText.Status(facts.Status)} → {ServiceText.Status(runtimeState)}",
                    Loc.Tf("Тип запуска сохраняется: {0}.", StartText(facts.Start, facts.RawDelayed == true, facts)),
                    Loc.T("Текущее состояние не записывается в журнал восстановления."),
                };
                if (!stopping && !string.IsNullOrEmpty(facts.DependencyState))
                {
                    var dependencyFields = facts.DependencyState.Split('\0');
                    runtimeDetails.Add(Loc.Tf("Службы, необходимые для запуска: {0}. Если они остановятся, Windows может запустить их снова.",
                        string.Join(", ", dependencyFields.Where((_, index) => index % 2 == 0))));
                }
                if (ServiceCatalog.Find(name) is { } runtimeEntry)
                {
                    runtimeDetails.Add(stopping ? runtimeEntry.Impact : runtimeEntry.Description);
                    if (stopping && runtimeEntry.Conditional) runtimeDetails.Add(runtimeEntry.Condition);
                }
                return Ui.ConfirmAsync(stopping ? Loc.T("ОСТАНОВИТЬ СЛУЖБУ?") : Loc.T("ЗАПУСТИТЬ СЛУЖБУ?"),
                    stopping
                        ? Loc.Tf("«{0}» будет остановлена сейчас. Программы, которые её используют, могут перестать работать.", displayName)
                        : Loc.Tf("«{0}» будет запущена сейчас. Тип запуска не меняется.", displayName),
                    runtimeDetails, stopping ? Loc.T("ОСТАНОВИТЬ") : Loc.T("ЗАПУСТИТЬ"), danger: stopping);
            }
            var disabling = plan.Destination == ServiceStartMode.Disabled;
            var details = new List<string>
            {
                $"{displayName} ({name}): {StartText(facts.Start, facts.RawDelayed == true, facts)} → {StartText(plan.Destination, false, facts)}",
                Loc.Tf("Текущее состояние: {0}. Действие меняет только тип запуска.", ServiceText.Status(facts.Status)),
            };
            var entry = ServiceCatalog.Find(name);
            if (disabling)
            {
                details.Add(Loc.T("Отключённую службу Windows не запустит даже по требованию: всё, что от неё зависит, перестанет работать."));
                if (entry is not null)
                {
                    details.Add(entry.Impact);
                    if (entry.Conditional) details.Add(entry.Condition);
                }
            }
            else
            {
                details.Add(Loc.T("Службу можно будет запустить по требованию, в том числе по настроенным триггерам."));
                if (entry is not null) details.Add(entry.Description);
            }
            if (entry is null)
                details.Add(Loc.T("Этой службы нет в списке рекомендаций Lycoris — вы меняете её на свой риск."));
            return Ui.ConfirmAsync(disabling ? Loc.T("ОТКЛЮЧИТЬ СЛУЖБУ?") : Loc.T("ИЗМЕНИТЬ ТИП ЗАПУСКА?"),
                Loc.T("Изменится тип запуска службы. Изменение записывается в журнал и откатывается."), details,
                disabling ? Loc.T("ОТКЛЮЧИТЬ") : Loc.T("ПРИМЕНИТЬ"), danger: disabling);
        }

        public async Task<bool> RunAsync(ServiceActionPlan plan, Func<Task> work)
        {
            var ran = false;
            var operation = plan.Action switch
            {
                ServiceAction.Start => Loc.T("Запуск службы…"),
                ServiceAction.Stop => Loc.T("Остановка службы…"),
                _ => Loc.T("Изменение типа запуска…"),
            };
            await owner.RunBusyAsync(OperationClass.JournalChange, operation, async () =>
            {
                ran = true;
                await work();
            });
            return ran;
        }

        public Task<bool> ApplyRuntimeAsync(ServiceActionPlan plan)
        {
            var name = plan.Facts.Identity.Name;
            var timeout = TimeSpan.FromSeconds(30);
            return plan.Action switch
            {
                ServiceAction.Start => ServiceControl.StartExactAsync(name, timeout),
                ServiceAction.Stop => ServiceControl.StopExactAsync(name, timeout),
                _ => throw new ArgumentOutOfRangeException(nameof(plan)),
            };
        }

        public async Task<IServiceActionSession?> BeginAsync(ServiceActionPlan plan)
        {
            var displayName = plan.Facts.Identity.DisplayName;
            var neutral = ServiceControl.DescribeStartNeutral(plan.Destination, false);
            var shown = ServiceControl.DescribeStart(plan.Destination, false);
            var session = await owner.BeginSessionAsync(string.Format(Loc.Keep("Службы: {0} → {1}"), displayName, neutral),
                Loc.Tf("Службы: {0} → {1}", displayName, shown));
            return session is null ? null : new AssistantServiceSession(session);
        }

        private static string StartText(ServiceStartMode mode, bool delayed, ServiceActionFacts facts) =>
            ServiceText.Start(mode, delayed && mode == ServiceStartMode.Automatic, facts.TriggerVersion is not null);
    }

    private sealed class AssistantServiceSession(ChangeSession session) : IServiceActionSession
    {
        public void Exclusive(Action body) => session.Exclusive(body);

        public ServiceActionWrite Apply(ServiceActionPlan plan)
        {
            var identity = plan.Facts.Identity;
            var applied = session.SetServiceStart(identity.Name, plan.Destination, ServiceSource,
                string.Format(Loc.Keep("Служба «{0}»"), identity.DisplayName));
            var recordId = session.Journal.ActiveLayers("svc:" + identity.Name)
                .LastOrDefault(record => record.Kind == ChangeKind.Service && record.BatchId == session.Batch.Id &&
                    record.Source == ServiceSource && record.Service is not null &&
                    ServiceActionWorkflow.SameName(record.Service.Name, identity.Name))?.Id;
            return new(applied, recordId);
        }

        public void Dispose() => session.Complete();
    }
}
