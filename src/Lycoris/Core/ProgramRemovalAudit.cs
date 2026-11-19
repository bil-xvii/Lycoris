namespace Lycoris.Core;

internal enum ProgramRemovalScope { Machine, User }

internal enum ProgramRemovalAuditOutcome { Attempted, NotStarted, Failed, Removed, JournalUnavailable }

internal sealed record ProgramRemovalAudit(Guid BatchId, Guid OperationId, string PackageId,
    ProgramRemovalScope Scope, DateTime AttemptedUtc)
{
    public ProgramRemovalAuditOutcome Outcome { get; init; } = ProgramRemovalAuditOutcome.Attempted;
    public int? ExitCode { get; init; }
    public bool FreshVerified { get; init; }
    public DateTime? FinishedUtc { get; init; }
}

internal readonly record struct ProgramRemovalAuditResult(ProgramRemovalAuditOutcome Outcome,
    int? ExitCode = null, bool FreshVerified = false)
{
    public bool IsFinal => Outcome switch
    {
        ProgramRemovalAuditOutcome.Removed => ExitCode == 0 && FreshVerified,
        ProgramRemovalAuditOutcome.Failed => ExitCode is not null and not 0 && FreshVerified,
        ProgramRemovalAuditOutcome.NotStarted => ExitCode is null && !FreshVerified,
        _ => false,
    };
}
