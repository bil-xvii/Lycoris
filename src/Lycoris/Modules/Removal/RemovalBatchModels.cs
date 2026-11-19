namespace Lycoris.Modules.Removal;

internal enum RemovalBatchKind { Uninstall, DisableStartup }

internal sealed record RemovalBatchItem(string Key, string Name, string Scope, string Note)
{
    public string? Version { get; init; }
    public string? NativeInstallerId { get; init; }
}

internal abstract record RemovalBatchPlan(Guid Id, string? UserSid, DateTime ObservedUtc,
    IReadOnlyList<RemovalBatchItem> Items, string? UnavailableReason)
{
    public IReadOnlyList<RemovalRow> AssessedRows { get; init; } = [];
    public string? Summary { get; init; }
}

internal sealed record RemovalBatchResult(int Applied, int Skipped, IReadOnlyList<string> Errors, bool UnknownOutcome = false);

internal interface IRemovalBatchAction
{
    RemovalBatchKind Kind { get; }
    Task<RemovalBatchPlan> PrepareAsync(CancellationToken ct);
    Task<RemovalBatchResult> ExecuteAsync(RemovalBatchPlan plan, IReadOnlySet<string> selectedKeys,
        IProgress<string>? progress, CancellationToken ct);
}
