using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.Core.Services;
using Lycoris.Modules.Assistant;
using Lycoris.Shell;
using Lycoris.UI;

namespace Lycoris.Modules.Services;

public sealed partial class ServicesViewModel
{
    private ServiceRemovalCandidate? _cleanupCandidate;

    [ObservableProperty] private string _cleanupStatus = Loc.T("Проверка остатков ещё не запускалась.");
    [ObservableProperty] private string? _cleanupResult;
    [ObservableProperty] private Guid? _cleanupRecordId;
    [ObservableProperty] private bool _cleanupNeedsAdministrator;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckServiceResidueCommand), nameof(RemoveServiceResidueCommand))]
    private bool _isReviewingCleanup;

    public string? CleanupCandidateName => _cleanupCandidate?.Name;
    public string? CleanupCandidatePath => _cleanupCandidate?.ImagePath;
    public string? CleanupCandidateTitle => _cleanupCandidate is not { } candidate ? null
        : candidate.DisplayName == candidate.Name ? candidate.Name : $"{candidate.DisplayName} ({candidate.Name})";

    private bool CanCheckServiceResidue() => !IsBusy && !IsReviewingCleanup;

    [RelayCommand(CanExecute = nameof(CanCheckServiceResidue))]
    private Task CheckServiceResidueAsync() => CanCheckServiceResidue()
        ? RunBusyAsync(OperationClass.Measure, Loc.T("Проверка остатков служб…"), ScanServiceResidueAsync)
        : Task.CompletedTask;

    private async Task ScanServiceResidueAsync()
    {
        CleanupStatus = Loc.T("Проверяем регистрацию службы Everything…");
        ShowServiceResidue(await Task.Run(() => ServiceRemoval.Probe(ServiceRemoval.SupportedName)));
    }

    private void ShowServiceResidue(ServiceRemovalProbe probe)
    {
        _cleanupCandidate = probe.Candidate;
        CleanupNeedsAdministrator = probe.Reason == ServiceRemovalReason.NeedsAdministrator;
        OnPropertyChanged(nameof(CleanupCandidateName));
        OnPropertyChanged(nameof(CleanupCandidatePath));
        OnPropertyChanged(nameof(CleanupCandidateTitle));
        CleanupStatus = ResidueProbeText(probe);
    }

    internal static string ResidueProbeText(ServiceRemovalProbe probe) => probe.Candidate is not null
        ? Loc.T("Служба отключена и остановлена. Файл службы по указанному пути отсутствует.")
        : probe.Reason switch
        {
            ServiceRemovalReason.MissingService => Loc.T("Служба Everything не зарегистрирована."),
            ServiceRemovalReason.NeedsAdministrator => Loc.T("Для полной проверки службы Everything нужны права администратора."),
            ServiceRemovalReason.ExecutablePresent => Loc.T("Файл службы Everything на месте. Служба не предлагается к удалению."),
            ServiceRemovalReason.NotEligible => Loc.T("Служба Everything не отвечает условиям безопасного удаления."),
            _ => Loc.T("Не удалось подтвердить наличие остатка службы Everything."),
        };

    internal static bool AllowsResidueRemoval(bool locked, bool demo, bool expert, bool servicesExpert, bool? managed) =>
        !locked && !demo && ServiceGuard.ExpertModeEnabled(expert, servicesExpert) && managed == false;

    private bool CleanupPolicyAllows() => AllowsResidueRemoval(SafetyLock.IsLocked, AssistantViewModel.Demo,
        AppSettings.Current.ExpertMode, AppSettings.Current.ServicesExpertMode, Machine?.IsManaged);

    private bool CanRemoveServiceResidue(string? name) =>
        CanCheckServiceResidue() && CleanupPolicyAllows() && ServiceRemoval.IsSupportedName(name);

    private void NotifyCleanupCommands()
    {
        CheckServiceResidueCommand.NotifyCanExecuteChanged();
        RemoveServiceResidueCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveServiceResidue))]
    private async Task RemoveServiceResidueAsync(string? name)
    {
        if (name is null || !CanRemoveServiceResidue(name)) return;
        SelectedTab = TabRecommendations;
        IsReviewingCleanup = true;
        CleanupResult = null;
        CleanupRecordId = null;
        try
        {
            var fresh = await ReadCandidateForRemovalAsync(name);
            if (fresh.Candidate is not { } candidate)
            {
                ShowServiceResidue(fresh);
                if (fresh.Reason == ServiceRemovalReason.NeedsAdministrator && !Elevation.IsElevated)
                {
                    RequireAdmin(Loc.T("Проверка остатка службы Everything"), AdminIntent.RemoveServiceResidue, name);
                    return;
                }
                CleanupResult = ResidueResultText(new(ServiceRemovalOutcome.Refused, fresh.Reason));
                return;
            }
            ShowServiceResidue(fresh);
            if (!RequireAdmin(Loc.T("Удаление остатка службы"), AdminIntent.RemoveServiceResidue, candidate.Name)) return;

            var result = await ReviewResidueRemovalAsync(candidate, () => ConfirmResidueRemovalAsync(candidate),
                RunCleanupWorkAsync, async () => (await ReadCandidateForRemovalAsync(candidate.Name)).Candidate,
                ApplyCleanupRemovalAsync);
            CleanupRecordId = result.JournalRecordId;
            CleanupResult = ResidueResultText(result);
            await ScanServiceResidueAsync();
        }
        finally
        {
            IsReviewingCleanup = false;
        }
    }

    private async Task<ServiceRemovalProbe> ReadCandidateForRemovalAsync(string name)
    {
        var denied = new ServiceRemovalProbe(null, ServiceRemovalReason.PolicyBlocked);
        if (!CleanupPolicyAllows()) return denied;
        try
        {
            var fresh = await Task.Run(() => ServiceRemoval.CanProveUnmanaged() ? ServiceRemoval.Probe(name) : denied);
            return CleanupPolicyAllows() ? fresh : denied;
        }
        catch (Exception ex)
        {
            Log.Warn("Проверка условий удаления службы: {0}", ex.GetType().Name);
            return new(null, ServiceRemovalReason.InspectionFailed);
        }
    }

    private static Task<(bool Confirmed, bool Option)> ConfirmResidueRemovalAsync(ServiceRemovalCandidate candidate) =>
        Ui.ConfirmOptionAsync(Loc.T("Удалить остаток службы?"),
            Loc.Tf("Служба «{0}» ({1})\nПуть: {2}\n\nУдаляется только регистрация службы Windows. Откат из журнала восстановит службу в отключённом состоянии.",
                candidate.DisplayName, candidate.Name, candidate.ImagePath),
            Loc.T("Подтверждаю, что Everything уже удалена и не нужна"),
            confirmText: Loc.T("УДАЛИТЬ РЕГИСТРАЦИЮ"));

    // The confirmation belongs to the full observed candidate. Re-read it after acquiring the lease.
    internal static async Task<ServiceRemovalResult> ReviewResidueRemovalAsync(ServiceRemovalCandidate candidate,
        Func<Task<(bool Confirmed, bool Option)>> confirm,
        Func<Func<Task<ServiceRemovalResult>>, Task<ServiceRemovalResult>> run,
        Func<Task<ServiceRemovalCandidate?>> readCurrent,
        Func<ServiceRemovalRequest, Task<ServiceRemovalResult>> apply)
    {
        var (confirmed, attested) = await confirm();
        if (!confirmed || !attested) return new(ServiceRemovalOutcome.Unchanged, ServiceRemovalReason.ApprovalRequired);
        return await run(async () =>
        {
            if (await readCurrent() != candidate) return new(ServiceRemovalOutcome.Unchanged, ServiceRemovalReason.Changed);
            return await apply(new(candidate.Name, candidate.Fingerprint, ExplicitRemovedProgramAttestation: true));
        });
    }

    private async Task<ServiceRemovalResult> RunCleanupWorkAsync(Func<Task<ServiceRemovalResult>> work)
    {
        var result = new ServiceRemovalResult(ServiceRemovalOutcome.Unchanged);
        await RunBusyAsync(OperationClass.JournalChange, Loc.T("Удаление остатка службы…"), async () =>
        {
            result = await work();
        });
        return result;
    }

    private async Task<ServiceRemovalResult> ApplyCleanupRemovalAsync(ServiceRemovalRequest request)
    {
        var title = Loc.Key("Удаление остатка службы Everything");
        var session = await BeginSessionAsync(title, Loc.Sink(title));
        if (session is null) return new(ServiceRemovalOutcome.Unchanged);
        try
        {
            var result = CleanupPolicyAllows()
                ? await Task.Run(() => session.RemoveService(request, title))
                : new ServiceRemovalResult(ServiceRemovalOutcome.Refused, ServiceRemovalReason.PolicyBlocked);
            if (result.Outcome is ServiceRemovalOutcome.Refused or ServiceRemovalOutcome.RecoveryRequired ||
                result.Outcome == ServiceRemovalOutcome.Unchanged &&
                result.Reason is not (ServiceRemovalReason.None or ServiceRemovalReason.ApprovalRequired))
                session.Errors.Add(ResidueResultText(result));
            return result;
        }
        catch
        {
            session.Errors.Add(Loc.T("Не удалось применить изменение службы."));
            throw;
        }
        finally
        {
            session.Complete();
        }
    }

    internal static string ResidueResultText(ServiceRemovalResult result) => result.Outcome switch
    {
        ServiceRemovalOutcome.Removed => Loc.T("Регистрация службы удалена. Откат доступен в «Восстановлении»."),
        ServiceRemovalOutcome.PendingDeletion => Loc.T("Windows приняла запрос на удаление. Регистрация исчезнет после закрытия всех открытых дескрипторов службы."),
        ServiceRemovalOutcome.RecoveryRequired => Loc.T("Результат удаления не подтверждён. Откройте запись в «Восстановлении»."),
        _ when result.Reason == ServiceRemovalReason.ApprovalRequired => Loc.T("Для удаления подтвердите, что Everything уже удалена."),
        _ when result.Reason == ServiceRemovalReason.Changed => Loc.T("Данные службы изменились. Проверьте остатки ещё раз."),
        _ when result.Reason is ServiceRemovalReason.JournalUnavailable or ServiceRemovalReason.RecoveryUnavailable =>
            Loc.T("Удаление заблокировано: не удалось подготовить запись для отката."),
        _ when result.Reason == ServiceRemovalReason.PolicyBlocked => Loc.T("Условия для удаления службы не подтверждены."),
        ServiceRemovalOutcome.Refused => Loc.T("Удаление службы недоступно при текущем состоянии."),
        _ => Loc.T("Регистрация службы не изменена."),
    };

    [RelayCommand]
    private void OpenServiceResidueRecord()
    {
        if (CleanupRecordId is not { } id) return;
        Ui.RequestFocus("restore", CommandPaletteIndex.RestoreSectionPrefix + $"record:{id:D}");
        Ui.Navigate("restore");
    }
}
