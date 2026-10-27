using System.ServiceProcess;
using CommunityToolkit.Mvvm.ComponentModel;
using Monolith.Core;
using Monolith.Tweaks;

namespace Monolith.Modules.Services;

/// <summary>What the leading column of a recommendation/task row shows.</summary>
public enum RowMark { Check, Done, Locked }

/// <summary>The status badge on the right of a recommendation/task row.</summary>
public enum RowBadge { None, Pending, Done, AlreadyManual, Blocked, Changed }

internal static class RiskText
{
    public static string Of(TweakRisk risk) => risk switch
    {
        TweakRisk.Safe => "БЕЗОПАСНО",
        TweakRisk.Optional => "ПО ЖЕЛАНИЮ",
        _ => "ЭКСПЕРТ",
    };
}

/// <summary>One row of the РЕКОМЕНДАЦИИ tab.</summary>
public sealed partial class RecommendationItem : ObservableObject
{
    public RecommendationItem(CuratedService entry) => Entry = entry;

    public CuratedService Entry { get; }
    public string Name => Entry.Name;
    public string Title => Entry.Title;
    public string Description => Entry.Description;
    public string Condition => Entry.Condition;
    public string Impact => Entry.Impact;
    public bool IsConditional => Entry.Conditional;
    public TweakRisk Risk => Entry.Risk;
    public string RiskLabel => RiskText.Of(Entry.Risk);
    public string TargetText => ServiceText.Start(Entry.Target, false);

    /// <summary>Called when the user ticks/unticks the row (companion logic, counters).</summary>
    public Action<RecommendationItem>? SelectionChanged { get; set; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private ServiceLiveState? _state;
    [ObservableProperty] private string? _blockReason;
    [ObservableProperty] private ChangeRecord? _journal;
    [ObservableProperty] private bool _canModify;
    /// <summary>Pre-selected by the current profile (shown as the РЕКОМЕНДОВАНО tag).</summary>
    [ObservableProperty] private bool _isRecommended;

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(Badge));
        SelectionChanged?.Invoke(this);
    }

    partial void OnStateChanged(ServiceLiveState? value) => RaiseComputed();
    partial void OnBlockReasonChanged(string? value) => RaiseComputed();
    partial void OnJournalChanged(ChangeRecord? value) => RaiseComputed();
    partial void OnCanModifyChanged(bool value) => RaiseComputed();

    private void RaiseComputed()
    {
        foreach (var p in new[]
                 {
                     nameof(Exists), nameof(IsAtTarget), nameof(IsAlreadyManual), nameof(IsBlocked), nameof(IsChanged),
                     nameof(CanSelect), nameof(Mark), nameof(Badge), nameof(CurrentText), nameof(StatusText), nameof(IsRunning),
                     nameof(SystemName), nameof(ChangeText),
                 })
            OnPropertyChanged(p);
    }

    public bool Exists => State?.Exists == true;
    public ServiceStartMode? CurrentMode => State?.StartMode;

    /// <summary>Already where MONOLITH would put it (Disabled also satisfies a Manual target).</summary>
    public bool IsAtTarget => CurrentMode == Entry.Target || (Entry.Target == ServiceStartMode.Manual && CurrentMode == ServiceStartMode.Disabled);

    /// <summary>
    /// Research §0.1: already Manual/trigger-start — disabling gains ~nothing, so it counts as optimal. Not for a service
    /// that must stay disabled (Remote Registry): there «Отключена» is the Windows default and a security setting.
    /// </summary>
    public bool IsAlreadyManual =>
        !IsAtTarget && CurrentMode == ServiceStartMode.Manual && Entry.Target == ServiceStartMode.Disabled &&
        !ServiceGuard.MustStayDisabled(Entry.Name);

    /// <summary>
    /// The profile pre-selects this row. Research §0.1: a service that already starts only on demand is left for the
    /// user to tick — «Отключена» saves ~nothing there and only removes the feature.
    /// </summary>
    public bool RecommendedIn(ProfileSet profile) => Entry.IsPreselectedIn(profile) && !IsAlreadyManual;

    public bool IsBlocked => BlockReason is not null;
    public bool IsChanged => Journal is not null;
    public bool CanSelect => CanModify && Exists && !IsAtTarget && !IsBlocked;
    public bool IsSelectable => Exists && !IsAtTarget && !IsBlocked;

    public RowMark Mark => IsBlocked ? RowMark.Locked : IsAtTarget ? RowMark.Done : RowMark.Check;

    public RowBadge Badge =>
        IsChanged ? RowBadge.Changed
        : IsBlocked ? RowBadge.Blocked
        : IsAtTarget ? RowBadge.Done
        : IsSelected ? RowBadge.Pending
        : IsAlreadyManual ? RowBadge.AlreadyManual
        : RowBadge.None;

    public string CurrentText => ServiceText.Start(State?.StartMode, State?.Delayed ?? false, State?.HasTriggers ?? false);
    public string StatusText => ServiceText.Status(State?.Status);
    public bool IsRunning => ServiceText.IsRunning(State?.Status);

    /// <summary>"DiagTrack · Функциональные возможности…" — the real service name and its localized display name.</summary>
    public string SystemName => string.IsNullOrWhiteSpace(State?.DisplayName) ? Name : $"{Name} · {State!.DisplayName}";

    public string ChangeText => Journal is null ? "" : $"{Journal.OldValue} → {Journal.NewValue}";
}

/// <summary>A titled section of the РЕКОМЕНДАЦИИ tab.</summary>
public sealed class RecommendationGroup(ServiceGroup group, IReadOnlyList<RecommendationItem> items)
{
    public ServiceGroup Group { get; } = group;
    public string Title { get; } = ServiceCatalog.GroupTitle(group);
    public string? Note { get; } = ServiceCatalog.GroupNote(group);
    public IReadOnlyList<RecommendationItem> Items { get; } = items;
    public string CountText => Items.Count.ToString("00");
}

/// <summary>One row of the ВСЕ СЛУЖБЫ tab.</summary>
public sealed partial class ServiceItem : ObservableObject
{
    private IReadOnlyList<StartOption> _startOptions;

    public ServiceItem(ServiceInfo info)
    {
        Info = info;
        _startMode = info.StartMode;
        _delayed = info.Delayed;
        _status = info.Status;
        StartScope = ServiceStartPolicy.ScopeFor(info.Name, info.IsThirdParty, info.LockReason is not null);
        _startOptions = ServiceStartPolicy.OptionsFor(StartScope, info.StartMode, info.Delayed);
        _pendingStart = StartOption.For(info.StartMode ?? ServiceStartMode.Manual, info.Delayed);
    }

    public ServiceInfo Info { get; }
    public string Name => Info.Name;
    public string DisplayName => Info.DisplayName;
    public string Description => string.IsNullOrWhiteSpace(Info.Description) ? "Описание не указано." : Info.Description;
    public string Account => string.IsNullOrWhiteSpace(Info.StartName) ? "—" : Info.StartName;
    public string Binary => string.IsNullOrWhiteSpace(Info.Binary) ? "—" : Info.Binary;
    public string Publisher => string.IsNullOrWhiteSpace(Info.Company) ? (Info.IsThirdParty ? "Не указан" : "Компонент Windows") : Info.Company;
    public bool IsThirdParty => Info.IsThirdParty;
    public bool IsPerUser => Info.IsPerUser;
    public string TemplateName => ServiceControl.BaseName(Info.Name);
    public bool HasTriggers => Info.HasTriggers;
    public bool IsLocked => Info.LockReason is not null;
    public string? LockReason => Info.LockReason;
    public bool IsCurated => ServiceCatalog.IsCurated(Info.Name);
    public bool MustStayDisabled => ServiceGuard.MustStayDisabled(Info.Name);

    /// <summary>Which start types MONOLITH may set for this service (research services.md §0.4, §2B).</summary>
    public StartChangeScope StartScope { get; }

    /// <summary>Allowed start types plus the current one.</summary>
    public IReadOnlyList<StartOption> StartOptions => _startOptions;

    /// <summary>Why «Отключена» (or everything but it) is not offered; null when the full list is available.</summary>
    public string? StartPolicyNote => ServiceStartPolicy.Note(StartScope, IsCurated);

    [ObservableProperty] private ServiceStartMode? _startMode;
    [ObservableProperty] private bool _delayed;
    [ObservableProperty] private ServiceControllerStatus? _status;
    [ObservableProperty] private ChangeRecord? _journal;
    [ObservableProperty] private StartOption? _pendingStart;
    [ObservableProperty] private bool _canModify;

    partial void OnStartModeChanged(ServiceStartMode? value)
    {
        UpdateStartOptions();
        RaiseComputed();
    }

    partial void OnDelayedChanged(bool value)
    {
        UpdateStartOptions();
        RaiseComputed();
    }

    /// <summary>The list contains the current start type, so it changes only when that is a type MONOLITH would not set.</summary>
    private void UpdateStartOptions()
    {
        var options = ServiceStartPolicy.OptionsFor(StartScope, StartMode, Delayed);
        if (options.SequenceEqual(_startOptions)) return;
        _startOptions = options;
        OnPropertyChanged(nameof(StartOptions));
    }
    partial void OnStatusChanged(ServiceControllerStatus? value) => RaiseComputed();
    partial void OnJournalChanged(ChangeRecord? value) => RaiseComputed();
    partial void OnPendingStartChanged(StartOption? value) => RaiseComputed();
    partial void OnCanModifyChanged(bool value) => RaiseComputed();

    private void RaiseComputed()
    {
        foreach (var p in new[]
                 {
                     nameof(StartText), nameof(StatusText), nameof(IsRunning), nameof(IsDisabled), nameof(IsChanged), nameof(ChangeText),
                     nameof(IsStartDirty), nameof(CanApplyStart), nameof(CanStart), nameof(CanStop), nameof(CanRevert), nameof(Facts),
                 })
            OnPropertyChanged(p);
    }

    public string StartText => ServiceText.Start(StartMode, Delayed, HasTriggers);
    public string StatusText => ServiceText.Status(Status);
    public bool IsRunning => ServiceText.IsRunning(Status);
    public bool IsDisabled => StartMode == ServiceStartMode.Disabled;
    public bool IsChanged => Journal is not null;
    public string ChangeText => Journal is null ? "" : $"{Journal.OldValue} → {Journal.NewValue}";

    public bool IsStartDirty =>
        PendingStart is not null && StartMode is not null &&
        !(PendingStart.Mode == StartMode && PendingStart.Delayed == (StartMode == ServiceStartMode.Automatic && Delayed));

    public bool CanApplyStart => CanModify && !IsLocked && IsStartDirty && ServiceStartPolicy.Allows(StartScope, PendingStart);
    public bool CanStart => CanModify && !IsLocked && !MustStayDisabled && !IsDisabled && Status == ServiceControllerStatus.Stopped;
    public bool CanStop => CanModify && !IsLocked && Status == ServiceControllerStatus.Running;
    public bool CanRevert => CanModify && IsChanged;

    public ServiceFacts Facts => new(Name, DisplayName, Info.Description, Info.Binary + " " + Info.PathName, IsRunning, StartMode, IsThirdParty);

    /// <summary>Puts the combo box back to the current start type.</summary>
    public void ResetPending() => PendingStart = StartOption.For(StartMode ?? ServiceStartMode.Manual, Delayed);

    public void Apply(ServiceLiveState s)
    {
        if (!s.Exists) return;
        StartMode = s.StartMode;
        Delayed = s.Delayed;
        Status = s.Status ?? Status;
        ResetPending();
    }
}

/// <summary>One row of the ЗАДАЧИ ПЛАНИРОВЩИКА tab.</summary>
public sealed partial class TaskItem : ObservableObject
{
    public TaskItem(CuratedTask entry, string path)
    {
        Entry = entry;
        Path = path;
    }

    public CuratedTask Entry { get; }
    public string Path { get; }
    public string Title => Entry.IsPrefix ? $"{Entry.Title}: {TaskScanner.Split(Path).Name}" : Entry.Title;
    public string Description => Entry.Description;
    public string Impact => Entry.Impact;
    public bool IsConditional => Entry.Conditional;
    public TweakRisk Risk => Entry.Risk;
    public string RiskLabel => RiskText.Of(Entry.Risk);

    public Action<TaskItem>? SelectionChanged { get; set; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private ChangeRecord? _journal;
    [ObservableProperty] private bool _canModify;
    [ObservableProperty] private bool _isRecommended;

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(Badge));
        SelectionChanged?.Invoke(this);
    }

    partial void OnEnabledChanged(bool value) => RaiseComputed();
    partial void OnJournalChanged(ChangeRecord? value) => RaiseComputed();
    partial void OnCanModifyChanged(bool value) => RaiseComputed();

    private void RaiseComputed()
    {
        foreach (var p in new[] { nameof(IsAtTarget), nameof(CanSelect), nameof(Mark), nameof(Badge), nameof(StateText), nameof(IsChanged), nameof(ChangeText) })
            OnPropertyChanged(p);
    }

    public bool IsAtTarget => !Enabled;
    public bool IsChanged => Journal is not null;
    public bool CanSelect => CanModify && Enabled;
    public bool IsSelectable => Enabled;
    public RowMark Mark => IsAtTarget ? RowMark.Done : RowMark.Check;

    public RowBadge Badge =>
        IsChanged ? RowBadge.Changed
        : IsAtTarget ? RowBadge.Done
        : IsSelected ? RowBadge.Pending
        : RowBadge.None;

    public string StateText => Enabled ? "Включена" : "Отключена";
    public string ChangeText => Journal is null ? "" : $"{Journal.OldValue} → {Journal.NewValue}";
}
