using System.ComponentModel;
using System.IO;
using System.Security.AccessControl;
using Lycoris.Core;
using Lycoris.Core.Services;

namespace Lycoris.Tests;

[Collection("ExpertAccessPolicy")]
public sealed class ServiceRemovalTests : IDisposable
{
    private static readonly ServiceRemovalSnapshot Original = new("Everything", "Everything",
        "\"C:\\Program Files\\Everything\\Everything.exe\" -svc", @"C:\Program Files\Everything\Everything.exe",
        "File indexing", 1, "D:(A;;GA;;;SY)", "O:BAD:(A;;GA;;;BA)");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lycoris-ServiceRemoval-" + Guid.NewGuid().ToString("N"));
    private readonly bool _bypass = ChangeSession.BypassElevationForTests;
    private readonly FakeWindows _windows = new();
    private readonly FakeStore _store = new();

    public ServiceRemovalTests() => ChangeSession.BypassElevationForTests = true;

    public void Dispose()
    {
        ChangeSession.BypassElevationForTests = _bypass;
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Removal_writes_authenticated_backup_and_durable_typed_journal_before_delete_and_reverts_exactly_once()
    {
        var journalFile = Path.Combine(_root, "journal.json");
        var journal = NewJournal(journalFile);
        var session = await Begin(journal);
        var candidate = Assert.IsType<ServiceRemovalCandidate>(journal.ServiceRemovalAccess.Probe("Everything").Candidate);
        Assert.Equal(Original.Executable, candidate.ImagePath);
        Assert.Equal(0, _store.Saves);
        Assert.Equal(0, _windows.DeleteCalls);
        _windows.OnDelete = () =>
        {
            var record = Assert.Single(NewJournal(journalFile).ActiveRecords);
            Assert.Equal(ChangeKind.ServiceRemoval, record.Kind);
            Assert.True(ServiceRemoval.IsRecord(record));
            Assert.Equal(Original, _store.Read(new(record.ServiceRemovalToken!.Value, record.Id, record.BatchId, record.Target)));
            Assert.Equal(2, _windows.LastHandle!.Inspections);
            Assert.DoesNotContain("Everything.exe", File.ReadAllText(journalFile), StringComparison.OrdinalIgnoreCase);
        };

        var result = session.RemoveService(new(candidate.Name, candidate.Fingerprint, true), "test");
        Assert.Equal(ServiceRemovalOutcome.Removed, result.Outcome);
        Assert.Equal(1, session.Applied);
        Assert.Equal(1, _windows.DeleteCalls);

        // Reload the journal as the Restore center does after an application restart.
        var reloaded = NewJournal(journalFile);
        var record = Assert.Single(reloaded.ActiveRecords);
        Assert.Equal(record.Id, result.JournalRecordId);
        Assert.Equal(1, (await reloaded.RevertAsync([record])).Reverted);
        Assert.Equal(Original, _windows.Current);
        Assert.True(record.Reverted);
        Assert.Equal(1, _windows.RestoreCalls);

        _windows.Current = null;
        Assert.Equal(1, (await reloaded.RevertAsync([Copy(record)])).Failed);
        Assert.Equal(1, _windows.RestoreCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Missing_attestation_or_stale_fingerprint_never_writes_a_backup(bool attested, bool stale)
    {
        var journal = NewJournal();
        var session = await Begin(journal);
        var request = new ServiceRemovalRequest("Everything", stale ? new string('0', 64) : Original.Fingerprint, attested);
        Assert.Equal(ServiceRemovalOutcome.Refused, session.RemoveService(request, "test").Outcome);
        Assert.Empty(journal.ActiveRecords);
        Assert.Equal(0, _store.Saves);
        Assert.Equal(0, _windows.DeleteCalls);
    }

    [Fact]
    public async Task Second_read_detects_changed_configuration_under_the_same_handle()
    {
        var journal = NewJournal();
        var session = await Begin(journal);
        _store.Saved = () => _windows.Current = Original with { Description = "Changed while saving" };

        var result = session.RemoveService(Approved(), "test");

        Assert.Equal(ServiceRemovalOutcome.Unchanged, result.Outcome);
        Assert.Equal(ServiceRemovalReason.Changed, result.Reason);
        Assert.Empty(journal.ActiveRecords);
        Assert.Equal(0, _windows.DeleteCalls);
        Assert.Single(_store.Consumed);
        Assert.Equal(2, _windows.LastHandle!.Inspections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backup_and_journal_failures_prevent_delete(bool journalFailure)
    {
        var path = Path.Combine(_root, "journal.json");
        var journal = NewJournal(path);
        var session = await Begin(journal);
        if (journalFailure) Directory.CreateDirectory(path + ".tmp");
        else _store.Untrusted = true;

        var result = session.RemoveService(Approved(), "test");

        Assert.Equal(journalFailure ? ServiceRemovalReason.JournalUnavailable : ServiceRemovalReason.RecoveryUnavailable, result.Reason);
        Assert.Empty(journal.ActiveRecords);
        Assert.Equal(0, session.Applied);
        Assert.Equal(0, _windows.DeleteCalls);
    }

    [Theory]
    [InlineData(5, false, "Unchanged")]
    [InlineData(1726, false, "RecoveryRequired")]
    [InlineData(0, true, "PendingDeletion")]
    public async Task Failed_and_pending_deletes_keep_recovery_only_when_outcome_is_uncertain(int error, bool pending,
        string expected)
    {
        var journal = NewJournal();
        var session = await Begin(journal);
        _windows.DeleteError = error;
        _windows.PendingAfterDelete = pending;

        var result = session.RemoveService(Approved(), "test");

        Assert.Equal(expected, result.Outcome.ToString());
        Assert.Equal(expected == "Unchanged" ? 0 : 1, journal.ActiveRecords.Count);
        if (!pending) return;

        var record = Assert.Single(journal.ActiveRecords);
        Assert.Equal(1, (await journal.RevertAsync([record])).Failed);
        Assert.Equal(0, _windows.RestoreCalls);
        Assert.Empty(_store.Consumed);
        _windows.Pending = false;
        Assert.Equal(1, (await journal.RevertAsync([record])).Reverted);
    }

    [Theory]
    [InlineData("name-reused")]
    [InlineData("path-changed")]
    [InlineData("payload-tampered")]
    [InlineData("different-record")]
    [InlineData("different-batch")]
    [InlineData("untrusted-key")]
    [InlineData("policy")]
    public async Task Restore_refuses_unverified_authority_or_target(string failure)
    {
        var journal = NewJournal();
        var session = await Begin(journal);
        Assert.Equal(ServiceRemovalOutcome.Removed, session.RemoveService(Approved(), "test").Outcome);
        var record = Assert.Single(journal.ActiveRecords);
        switch (failure)
        {
            case "name-reused": _windows.Current = Original; break; // Even an identical reinstall is not this operation's entry.
            case "path-changed": _windows.ImageState = RemovalPathState.Unknown; break;
            case "payload-tampered": _store.Tamper = true; break;
            case "different-record": record = Copy(record, id: Guid.NewGuid()); break;
            case "different-batch": record = Copy(record, batch: Guid.NewGuid()); break;
            case "untrusted-key": _store.Untrusted = true; break;
            case "policy": _windows.MutationAllowed = false; break;
        }

        Assert.Equal(1, (await journal.RevertAsync([record])).Failed);
        Assert.Equal(0, _windows.RestoreCalls);
        Assert.Empty(_store.Consumed);
    }

    [Fact]
    public async Task Known_no_write_create_failure_releases_reservation_for_retry()
    {
        var journal = NewJournal();
        var session = await Begin(journal);
        Assert.Equal(ServiceRemovalOutcome.Removed, session.RemoveService(Approved(), "test").Outcome);
        var record = Assert.Single(journal.ActiveRecords);
        _windows.RestoreError = new ServiceRestoreDidNotWrite(new Win32Exception(5));
        Assert.Equal(1, (await journal.RevertAsync([record])).Failed);
        Assert.Empty(_store.Consumed);
        Assert.False(record.Reverted);
        _windows.RestoreError = null;
        Assert.Equal(1, (await journal.RevertAsync([record])).Reverted);
        Assert.Equal(Original, _windows.Current);
    }

    [Fact]
    public async Task Unknown_create_failure_retains_authenticated_payload_but_never_replays_reservation()
    {
        var journal = NewJournal();
        var session = await Begin(journal);
        Assert.Equal(ServiceRemovalOutcome.Removed, session.RemoveService(Approved(), "test").Outcome);
        var record = Assert.Single(journal.ActiveRecords);
        _windows.RestoreError = new Win32Exception(1726);
        Assert.Equal(1, (await journal.RevertAsync([record])).Failed);
        Assert.Single(_store.Consumed);
        Assert.Single(_store.Envelopes);
        _windows.RestoreError = null;
        Assert.Equal(1, (await journal.RevertAsync([record])).Failed);
        Assert.Equal(1, _windows.RestoreCalls);
    }

    [Fact]
    public void Journal_rejects_untyped_restoration_data_and_cross_source_aliases()
    {
        var record = new ChangeRecord
        {
            Source = ServiceRemoval.Source, Kind = ChangeKind.ServiceRemoval, Target = ServiceRemoval.Target("Everything"),
            BatchId = Guid.NewGuid(), ServiceRemovalToken = Guid.NewGuid(), RevertFile = "cmd.exe",
        };
        Assert.NotNull(JournalTrust.Check(record));
        Assert.Null(JournalTrust.Check(Copy(record)));
        Assert.NotNull(JournalTrust.Check(new ChangeRecord
        {
            Source = "services", Kind = ChangeKind.Service, Target = "svc:Everything", ServiceRemovalToken = Guid.NewGuid(),
        }));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Everything\\Everything.exe\" -svc", true)]
    [InlineData("\"C:\\Program Files\\Everything\\Everything.exe\"", true)]
    [InlineData("C:\\Program Files\\Everything\\Everything.exe -svc", false)]
    [InlineData("\"C:\\Program Files\\Everything\\Everything.exe\" -svc -svc-pipe-name X", false)]
    [InlineData("\"C:\\Program Files\\Everything\\Everything.exe\"  -svc", false)]
    [InlineData("\"C:\\Program Files\\Everything\\..\\Everything\\Everything.exe\" -svc", false)]
    [InlineData("\"%ProgramFiles%\\Everything\\Everything.exe\" -svc", false)]
    [InlineData("\"\\\\server\\Everything\\Everything.exe\" -svc", false)]
    public void Only_exact_compiled_executable_and_switch_are_supported(string command, bool accepted) =>
        Assert.Equal(accepted, ServiceRemovalPath.RecipeExecutable(command, @"C:\Program Files") is not null);

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(5, false)]
    [InlineData(21, false)]
    [InlineData(53, false)]
    [InlineData(123, false)]
    public void Missing_path_errors_exclude_access_device_network_and_parse_failures(int error, bool missing) =>
        Assert.Equal(missing, ServiceRemovalPath.IsMissing(error));

    [Theory]
    [InlineData("O:BAD:P(A;;KA;;;SY)(A;;KA;;;BA)", true)]
    [InlineData("O:BAD:P(A;;KA;;;SY)(A;;KA;;;BA)(A;;KR;;;BU)", false)]
    [InlineData("O:BUD:P(A;;KA;;;SY)(A;;KA;;;BA)", false)]
    [InlineData("O:BAD:NO_ACCESS_CONTROL", false)]
    [InlineData("O:BAD:AI(A;;KA;;;SY)(A;;KA;;;BA)", false)]
    public void Hmac_key_requires_admin_owned_protected_dacl(string sddl, bool trusted) =>
        Assert.Equal(trusted, ServiceRemovalRegistry.TrustedAcl(new RawSecurityDescriptor(sddl), exclusive: true));

    [Fact]
    public void Envelope_is_bound_to_machine_token_ids_and_service_identity()
    {
        var key = new byte[32];
        var machine = Guid.NewGuid();
        var binding = new ServiceRemovalBinding(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ServiceRemoval.Target("Everything"));
        var envelope = ServiceRemovalEnvelope.Seal(key, machine, binding, Original);
        Assert.Equal(Original, ServiceRemovalEnvelope.Open(key, machine, binding, envelope));
        Assert.Null(ServiceRemovalEnvelope.Open(key, Guid.NewGuid(), binding, envelope));
        Assert.Null(ServiceRemovalEnvelope.Open(key, machine, binding with { Token = Guid.NewGuid() }, envelope));
        Assert.Null(ServiceRemovalEnvelope.Open(key, machine, binding with { RecordId = Guid.NewGuid() }, envelope));
        Assert.Null(ServiceRemovalEnvelope.Open(key, machine, binding with { BatchId = Guid.NewGuid() }, envelope));
        Assert.Null(ServiceRemovalEnvelope.Open(key, machine, binding with { Target = "service-removal:Else" }, envelope));
        key[0] = 1;
        Assert.Null(ServiceRemovalEnvelope.Open(key, machine, binding, envelope));
    }

    [Fact]
    public void Missing_service_and_elevation_needed_are_distinct_read_only_outcomes()
    {
        var operations = NewJournal().ServiceRemovalAccess;
        _windows.OpenError = new Win32Exception(1060);
        Assert.Equal(ServiceRemovalReason.MissingService, operations.Probe("Everything").Reason);
        _windows.OpenError = new ServiceRemovalAuditNeedsElevation();
        Assert.Equal(ServiceRemovalReason.NeedsAdministrator, operations.Probe("Everything").Reason);
        Assert.Equal(ServiceRemovalReason.UnsupportedService, operations.Probe("Spooler").Reason);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public async Task Authorized_service_delete_remains_revertible_after_return_to_simple_mode()
    {
        var journal = NewJournal();
        var session = await Begin(journal);
        Assert.Equal(ServiceRemovalOutcome.Removed, session.RemoveService(Approved(), "test").Outcome);
        var record = Assert.Single(journal.ActiveRecords);
        _windows.ExpertMode = false;
        Assert.False(_windows.CanDelete());
        Assert.True(_windows.CanRestore());
        Assert.Equal(1, (await journal.RevertAsync([record])).Reverted);
        Assert.Equal(Original, _windows.Current);
    }

    [Theory]
    [InlineData(@"SOFTWARE\Lycoris.ServiceRemoval.v1", "AuthenticationKey")]
    [InlineData(@"software\\lycoris.serviceremoval.V1", "Backup-00000000000000000000000000000001")]
    [InlineData(@"SOFTWARE/Lycoris.ServiceRemoval.v1/Consumed-00000000000000000000000000000001", "")]
    [InlineData(@"SOFTWARE\Wow6432Node\Lycoris.ServiceRemoval.v1", "AuthenticationKey")]
    public void Generic_tweak_journal_cannot_address_service_recovery_key_or_children(string path, string value)
    {
        var snapshot = new RegistryValueSnapshot
        {
            Hive = RegHive.HKLM, Path = path, Name = value, KeyExisted = true, ValueExisted = true,
            Kind = Microsoft.Win32.RegistryValueKind.Binary, Data = "AA==", CreatedKeyRoot = path,
        };
        var forged = new ChangeRecord
        {
            Source = "tweak:game-mode", Kind = ChangeKind.Registry, Target = $@"reg:HKLM\{path}\{value}", Registry = snapshot,
        };
        Assert.True(ChangeJournal.IsForbiddenRestoreTarget(snapshot));
        Assert.NotNull(JournalTrust.Check(forged));
    }

    [Theory]
    [InlineData(@"\REGISTRY\MACHINE\SOFTWARE\Lycoris.ServiceRemoval.v1", true)]
    [InlineData(@"\Registry\Machine\Software\Lycoris.ServiceRemoval.v1\Consumed-token", true)]
    [InlineData(@"\REGISTRY\MACHINE\SOFTWARE\Wow6432Node\Lycoris.ServiceRemoval.v1", true)]
    [InlineData(@"\REGISTRY\MACHINE\SOFTWARE\Lycoris.ServiceRemoval.v10", false)]
    [InlineData(@"\REGISTRY\USER\user\SOFTWARE\Lycoris.ServiceRemoval.v1", false)]
    public void Canonical_registry_identity_reserves_the_exact_machine_store(string path, bool reserved) =>
        Assert.Equal(reserved, ServiceRemovalRegistryProtection.IsReservedNativePath(path));

    [Theory]
    [InlineData("O:SYG:SY", true)]
    [InlineData("O:BAG:SY", false)]
    [InlineData("O:SYG:BA", false)]
    [InlineData("", false)]
    public void Service_security_requires_reproducible_owner_and_group_even_with_default_dacl(string trustees, bool reproducible)
    {
        const string standardDacl = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";
        Assert.Equal(reproducible, ServiceRemovalWindows.IsDefaultServiceSecurity(new RawSecurityDescriptor(trustees + standardDacl)));
    }

    [Theory]
    [InlineData(0, 1, 0, false, 0, false, true)]
    [InlineData(0, 2, 0, false, 0, false, true)]
    [InlineData(0, 3, 0, false, 0, false, false)]
    [InlineData(0, 0, 0, false, 0, false, false)]
    [InlineData(5, 2, 0, false, 0, false, false)]
    [InlineData(0, 2, 0, true, 0, false, false)]
    [InlineData(0, 2, 1, false, 0, false, true)]
    [InlineData(0, 2, 1, true, 0, false, false)]
    [InlineData(0, 2, 2, false, 0, false, false)]
    [InlineData(0, 2, unchecked((int)0x80070005), false, 0, false, false)]
    [InlineData(0, 2, 0, false, 0, true, false)]
    [InlineData(0, 2, 0, false, 5, false, false)]
    [InlineData(0, 2, 0, false, null, null, false)]
    public void Management_probe_requires_all_three_successful_negative_checks(int domainCode, int joinStatus, int entraCode,
        bool hasEntraInfo, int? mdmCode, bool? mdmRegistered, bool unmanaged)
    {
        var evidence = new ServiceManagementEvidence(new(domainCode, joinStatus, null), new(entraCode, hasEntraInfo, null),
            new(mdmCode, mdmRegistered, mdmCode is null ? nameof(DllNotFoundException) : null));
        Assert.Equal(unmanaged, evidence.ProvenUnmanaged);
    }

    private ChangeJournal NewJournal(string? path = null)
    {
        var journal = ChangeJournal.ForTests(path);
        journal.ServiceRemovalAccess = new(_windows, _store);
        return journal;
    }

    private static Task<ChangeSession> Begin(ChangeJournal journal) =>
        ChangeSession.BeginAsync("test", createRestorePoint: false, journal: journal);

    private static ServiceRemovalRequest Approved() => new(Original.Name, Original.Fingerprint, true);

    private static ChangeRecord Copy(ChangeRecord original, Guid? id = null, Guid? batch = null) => new()
    {
        Id = id ?? original.Id, BatchId = batch ?? original.BatchId, Source = original.Source, Kind = original.Kind,
        Target = original.Target, ServiceRemovalToken = original.ServiceRemovalToken,
    };

    private sealed class FakeWindows : IServiceRemovalWindows
    {
        public ServiceRemovalSnapshot? Current { get; set; } = Original;
        public RemovalPathState ImageState { get; set; } = RemovalPathState.Missing;
        public bool MutationAllowed { get; set; } = true;
        public int DeleteError { get; set; }
        public bool PendingAfterDelete { get; set; }
        public bool Pending { get; set; }
        public int DeleteCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public Action? OnDelete { get; set; }
        public Exception? RestoreError { get; set; }
        public Exception? OpenError { get; set; }
        public FakeHandle? LastHandle { get; private set; }

        public bool ExpertMode { get; set; } = true;
        public bool CanDelete() => ServiceRemovalAuthorization.CanDelete(MutationAllowed, false, ExpertMode, ExpertMode, true);
        public bool CanRestore() => ServiceRemovalAuthorization.CanRestore(MutationAllowed, false);
        public IServiceRemovalHandle Open(string name, bool deleteAccess)
        {
            if (OpenError is { } error) throw error;
            return LastHandle = new FakeHandle(this, deleteAccess);
        }
        public RemovalPathState InspectPath(ServiceRemovalSnapshot snapshot) => ImageState;
        public RemovalServiceState InspectService(string name)
        {
            Assert.True(LastHandle?.Closed);
            return Pending ? RemovalServiceState.PendingDeletion
                : Current is not null ? RemovalServiceState.Present : RemovalServiceState.Absent;
        }
        public void Restore(ServiceRemovalSnapshot snapshot)
        {
            RestoreCalls++;
            if (RestoreError is { } error) throw error;
            Assert.Null(Current);
            Current = snapshot;
        }

        public sealed class FakeHandle(FakeWindows windows, bool deleteAccess) : IServiceRemovalHandle
        {
            public bool Closed { get; private set; }
            public int Inspections { get; private set; }
            public ServiceRemovalSnapshot? Inspect()
            {
                Assert.False(Closed);
                Inspections++;
                return windows.Current;
            }
            public int Delete()
            {
                Assert.True(deleteAccess);
                Assert.False(Closed);
                windows.DeleteCalls++;
                windows.OnDelete?.Invoke();
                if (windows.DeleteError == 0)
                {
                    windows.Current = null;
                    windows.Pending = windows.PendingAfterDelete;
                }
                return windows.DeleteError;
            }
            public void Dispose() => Closed = true;
        }
    }

    private sealed class FakeStore : IServiceRemovalStore
    {
        private readonly byte[] _key = new byte[32];
        private readonly Guid _machine = Guid.NewGuid();
        public Dictionary<Guid, byte[]> Envelopes { get; } = [];
        public HashSet<Guid> Consumed { get; } = [];
        public bool Tamper { get; set; }
        public bool Untrusted { get; set; }
        public int Saves { get; private set; }
        public Action? Saved { get; set; }

        public void Save(ServiceRemovalBinding binding, ServiceRemovalSnapshot snapshot)
        {
            if (Untrusted) throw new UnauthorizedAccessException();
            Envelopes.Add(binding.Token, ServiceRemovalEnvelope.Seal(_key, _machine, binding, snapshot));
            Saves++;
            Saved?.Invoke();
        }

        public ServiceRemovalSnapshot? Read(ServiceRemovalBinding binding)
        {
            if (Untrusted) throw new UnauthorizedAccessException();
            if (Consumed.Contains(binding.Token) || !Envelopes.TryGetValue(binding.Token, out var bytes)) return null;
            var candidate = bytes.ToArray();
            if (Tamper) candidate[^1] ^= 1;
            return ServiceRemovalEnvelope.Open(_key, _machine, binding, candidate);
        }

        public bool Consume(ServiceRemovalBinding binding) => Read(binding) is not null && Consumed.Add(binding.Token);

        public void Release(ServiceRemovalBinding binding) => Assert.True(Consumed.Remove(binding.Token));
    }
}

