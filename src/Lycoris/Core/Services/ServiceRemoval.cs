using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Lycoris.Core.Services;

internal enum ServiceRemovalReason
{
    None, UnsupportedService, MissingService, NeedsAdministrator, NotEligible, ExecutablePresent, PathUnverified, InspectionFailed,
    ApprovalRequired, Changed, PolicyBlocked, RecoveryUnavailable, JournalUnavailable, WriterFailed,
}

internal enum ServiceRemovalOutcome { Removed, PendingDeletion, RecoveryRequired, Unchanged, Refused }

internal sealed record ServiceRemovalCandidate(string Name, string DisplayName, string ImagePath, string Fingerprint);
internal sealed record ServiceRemovalProbe(ServiceRemovalCandidate? Candidate, ServiceRemovalReason Reason);
internal sealed record ServiceRemovalRequest(string Name, string Fingerprint, bool ExplicitRemovedProgramAttestation);
internal sealed record ServiceRemovalResult(ServiceRemovalOutcome Outcome, ServiceRemovalReason Reason = ServiceRemovalReason.None,
    Guid? JournalRecordId = null);

// Only SCM settings the v1 recipe can restore are represented. Own-process, LocalSystem, stopped and
// disabled with no extended configuration are fixed invariants, checked again by the Windows backend.
internal sealed record ServiceRemovalSnapshot(string Name, string DisplayName, string ImageCommand, string Executable,
    string Description, int ErrorControl, string ServiceSddl, string RegistrySddl,
    ServiceRemovalPreferredNodeProof PreferredNodeProof = ServiceRemovalPreferredNodeProof.Unverified)
{
    [System.Text.Json.Serialization.JsonIgnore] internal string Fingerprint => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)));
}

internal enum ServiceRemovalPreferredNodeProof { Unverified, ReportedUnset, QueryUnavailableOnNodeZero }

internal enum RemovalPathState { Missing, Present, Unknown }
internal enum RemovalServiceState { Absent, Present, PendingDeletion, Unknown }

internal sealed class ServiceRemovalAuditNeedsElevation : UnauthorizedAccessException;

internal sealed class ServiceRestoreDidNotWrite(Exception inner)
    : InvalidOperationException(inner.Message, inner);

internal interface IServiceRemovalHandle : IDisposable
{
    ServiceRemovalSnapshot? Inspect();
    int Delete();
}

internal interface IServiceRemovalWindows
{
    bool CanDelete();
    bool CanRestore();
    IServiceRemovalHandle Open(string name, bool deleteAccess);
    RemovalPathState InspectPath(ServiceRemovalSnapshot snapshot);
    RemovalServiceState InspectService(string name);
    void Restore(ServiceRemovalSnapshot snapshot);
}

internal sealed record ServiceRemovalBinding(Guid Token, Guid RecordId, Guid BatchId, string Target);

internal interface IServiceRemovalStore
{
    void Save(ServiceRemovalBinding binding, ServiceRemovalSnapshot snapshot);
    ServiceRemovalSnapshot? Read(ServiceRemovalBinding binding);
    // Removes this exact authenticated token from circulation. Archived data stays available for diagnosis.
    bool Consume(ServiceRemovalBinding binding);
    void Release(ServiceRemovalBinding binding);
}

internal static class ServiceRemovalAuthorization
{
    internal static bool CanRestore(bool elevated, bool locked) => elevated && !locked;

    internal static bool CanDelete(bool elevated, bool locked, bool ownerExpert, bool servicesExpert, bool unmanaged) =>
        CanRestore(elevated, locked) && ownerExpert && servicesExpert && unmanaged;
}

internal static class ServiceRemoval
{
    internal const string Source = "services-removal";
    private const string TargetPrefix = "service-removal:";
    internal const string SupportedName = "Everything";

    internal static ServiceRemovalOperations Windows { get; } =
        new(new ServiceRemovalWindows(), new ServiceRemovalRegistryStore());

    internal static bool IsSupportedName(string? name) => string.Equals(name, SupportedName, StringComparison.Ordinal);

    internal static ServiceRemovalProbe Probe(string name) => Windows.Probe(name);

    internal static bool CanProveUnmanaged() => ServiceRemovalWindows.CanProveUnmanaged();

    internal static string Target(string name) => TargetPrefix + name;

    internal static bool IsRecord(ChangeRecord record) =>
        record.Kind == ChangeKind.ServiceRemoval && record.Source == Source && record.Target == Target(SupportedName)
        && record.ServiceRemovalToken is { } token && token != Guid.Empty && record.Id != Guid.Empty
        && record.BatchId != Guid.Empty && record.UserSid is null && record.AlsoSources is { Count: 0 }
        && record.Registry is null && record.Service is null && record.TaskWasEnabled is null
        && record.TaskPath is null && record.RevertFile is null && record.RevertArgs is null
        && record.PowerSchemeOriginal is null && record.PowerSchemeCreated is null && record.PowerSchemesCreatedMore is null
        && record.MaintenanceTask is null && record.BackgroundStartup is null && record.Spi is null
        && record.DisplayMode is null && record.DisplayHdr is null && record.Monitor is null
        && record.Audio is null && record.Nic is null;

    internal static bool IsExpectedFailure(Exception exception) =>
        exception is Win32Exception or IOException or UnauthorizedAccessException or System.Security.SecurityException
            or InvalidOperationException or ArgumentException or ExternalException or CryptographicException or JsonException
            or EntryPointNotFoundException or DllNotFoundException or NotSupportedException;
}

internal sealed class ServiceRemovalOperations(IServiceRemovalWindows windows, IServiceRemovalStore store)
{
    public ServiceRemovalProbe Probe(string name)
    {
        if (!ServiceRemoval.IsSupportedName(name)) return new(null, ServiceRemovalReason.UnsupportedService);
        try
        {
            using var handle = windows.Open(name, deleteAccess: false);
            var snapshot = handle.Inspect();
            var reason = Check(snapshot);
            return reason == ServiceRemovalReason.None && snapshot is not null
                ? new(new(snapshot.Name, snapshot.DisplayName, snapshot.Executable, snapshot.Fingerprint), reason)
                : new(null, reason);
        }
        catch (ServiceRemovalAuditNeedsElevation)
        {
            return new(null, ServiceRemovalReason.NeedsAdministrator);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1060)
        {
            return new(null, ServiceRemovalReason.MissingService);
        }
        catch (Exception ex) when (ServiceRemoval.IsExpectedFailure(ex))
        {
            return new(null, ServiceRemovalReason.InspectionFailed);
        }
    }

    public ServiceRemovalResult Remove(ChangeJournal journal, Guid batchId, ServiceRemovalRequest request, string title)
    {
        if (!ServiceRemoval.IsSupportedName(request.Name)) return Refuse(ServiceRemovalReason.UnsupportedService);
        if (!request.ExplicitRemovedProgramAttestation || request.Fingerprint is not { Length: 64 })
            return Refuse(ServiceRemovalReason.ApprovalRequired);

        ChangeRecord? record = null;
        ServiceRemovalBinding? binding = null;
        var reason = ServiceRemovalReason.PolicyBlocked;
        var attempted = false;
        try
        {
            if (!windows.CanDelete()) return Refuse(reason);
            if (journal.FindActive(ServiceRemoval.Target(request.Name)) is not null)
                return Refuse(ServiceRemovalReason.RecoveryUnavailable);

            reason = ServiceRemovalReason.InspectionFailed;
            int error;
            using (var handle = windows.Open(request.Name, deleteAccess: true))
            {
                var snapshot = handle.Inspect();
                var check = Check(snapshot);
                if (check != ServiceRemovalReason.None || snapshot is null) return Refuse(check);
                if (snapshot.Fingerprint != request.Fingerprint) return Refuse(ServiceRemovalReason.Changed);

                var incoming = new ChangeRecord
                {
                    BatchId = batchId, Source = ServiceRemoval.Source, Title = title, Kind = ChangeKind.ServiceRemoval,
                    Target = ServiceRemoval.Target(request.Name), OldValue = Loc.Keep("отключена"),
                    NewValue = Loc.Keep("запрошено удаление"), ServiceRemovalToken = Guid.NewGuid(),
                };
                binding = Binding(incoming);
                reason = ServiceRemovalReason.RecoveryUnavailable;
                store.Save(binding, snapshot);
                reason = ServiceRemovalReason.JournalUnavailable;
                record = journal.Add(incoming).Record;

                // Re-read using the original DELETE handle after both write-ahead records reach disk.
                reason = ServiceRemovalReason.InspectionFailed;
                var fresh = handle.Inspect();
                if (fresh != snapshot || Check(fresh) != ServiceRemovalReason.None)
                    return Cancel(journal, record, binding, ServiceRemovalReason.Changed);
                if (!windows.CanDelete())
                    return Cancel(journal, record, binding, ServiceRemovalReason.PolicyBlocked);

                attempted = true;
                reason = ServiceRemovalReason.WriterFailed;
                error = handle.Delete();
            }

            // Close our service handle before checking whether SCM has removed its database entry.
            if (error is 5 or 6) return Cancel(journal, record, binding, ServiceRemovalReason.WriterFailed);
            if (error == 0)
            {
                return windows.InspectService(request.Name) switch
                {
                    RemovalServiceState.Absent => new(ServiceRemovalOutcome.Removed, JournalRecordId: record.Id),
                    RemovalServiceState.PendingDeletion => new(ServiceRemovalOutcome.PendingDeletion, JournalRecordId: record.Id),
                    _ => new(ServiceRemovalOutcome.RecoveryRequired, ServiceRemovalReason.WriterFailed, record?.Id),
                };
            }

            return new(ServiceRemovalOutcome.RecoveryRequired, ServiceRemovalReason.WriterFailed, record?.Id);
        }
        catch (Exception ex) when (ServiceRemoval.IsExpectedFailure(ex))
        {
            if (binding is not null && !attempted) return Cancel(journal, record, binding, reason);
            return attempted ? new(ServiceRemovalOutcome.RecoveryRequired, reason, record?.Id) : Refuse(reason);
        }
    }

    public RevertOutcome Restore(ChangeRecord record)
    {
        if (!ServiceRemoval.IsRecord(record) || !windows.CanRestore())
            throw new InvalidOperationException(Loc.T("Восстановление удалённой службы недоступно."));
        var binding = Binding(record);
        var snapshot = store.Read(binding);
        if (snapshot is null || record.Target != ServiceRemoval.Target(snapshot.Name))
            throw new InvalidOperationException(Loc.T("Не найдена подтверждённая резервная копия службы."));

        var state = windows.InspectService(snapshot.Name);
        if (state == RemovalServiceState.PendingDeletion)
            throw new InvalidOperationException(Loc.T("Windows ещё удаляет службу. Повторите откат после завершения удаления."));
        if (state != RemovalServiceState.Absent)
            throw new InvalidOperationException(Loc.T("Не удалось подтвердить, что имя службы свободно."));
        if (Check(snapshot) != ServiceRemovalReason.None)
            throw new InvalidOperationException(Loc.T("Путь к файлу службы изменился или его не удалось проверить."));

        // A crash during CreateService cannot make a copied journal row usable a second time. The authenticated
        // payload stays in the store, but a consumed token cannot create a second service, even with the original IDs.
        if (!store.Consume(binding))
            throw new InvalidOperationException(Loc.T("Резервная копия службы уже использована или больше недоступна."));
        try
        {
            windows.Restore(snapshot);
            return RevertOutcome.Done;
        }
        catch (ServiceRestoreDidNotWrite)
        {
            // Only a positively failed create can release a reservation. Unknown outcomes keep the authenticated
            // snapshot and reservation for diagnosis, so a retry cannot recreate a service twice.
            store.Release(binding);
            throw;
        }
    }

    private ServiceRemovalResult Cancel(ChangeJournal journal, ChangeRecord? record, ServiceRemovalBinding binding,
        ServiceRemovalReason reason)
    {
        try
        {
            if (!store.Consume(binding))
                return new(ServiceRemovalOutcome.Unchanged, ServiceRemovalReason.RecoveryUnavailable, record?.Id);
            if (record is not null) journal.Remove(record);
            return new(ServiceRemovalOutcome.Unchanged, reason);
        }
        catch (Exception ex) when (ServiceRemoval.IsExpectedFailure(ex))
        {
            return new(ServiceRemovalOutcome.Unchanged, ServiceRemovalReason.RecoveryUnavailable);
        }
    }

    private ServiceRemovalReason Check(ServiceRemovalSnapshot? snapshot)
    {
        if (snapshot is null || !ServiceRemoval.IsSupportedName(snapshot.Name))
            return ServiceRemovalReason.NotEligible;
        return windows.InspectPath(snapshot) switch
        {
            RemovalPathState.Missing => ServiceRemovalReason.None,
            RemovalPathState.Present => ServiceRemovalReason.ExecutablePresent,
            _ => ServiceRemovalReason.PathUnverified,
        };
    }

    private static ServiceRemovalBinding Binding(ChangeRecord record) =>
        new(record.ServiceRemovalToken!.Value, record.Id, record.BatchId, record.Target);

    private static ServiceRemovalResult Refuse(ServiceRemovalReason reason) => new(ServiceRemovalOutcome.Refused, reason);
}

