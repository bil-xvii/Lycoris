namespace Lycoris.Modules.Removal;

internal enum RemovalBatchKind { Uninstall, DisableStartup }

internal sealed record RemovalBatchItem(string Key, string Name, string Scope, string Note);

internal abstract record RemovalBatchPlan(Guid Id, string? UserSid, DateTime ObservedUtc,
    IReadOnlyList<RemovalBatchItem> Items, string? UnavailableReason);

internal sealed record RemovalBatchResult(int Applied, int Skipped, IReadOnlyList<string> Errors, bool UnknownOutcome = false);

internal interface IRemovalBatchAction
{
    RemovalBatchKind Kind { get; }
    Task<RemovalBatchPlan> PrepareAsync(CancellationToken ct);
    Task<RemovalBatchResult> ExecuteAsync(RemovalBatchPlan plan, IReadOnlySet<string> selectedKeys,
        IProgress<string>? progress, CancellationToken ct);
}
