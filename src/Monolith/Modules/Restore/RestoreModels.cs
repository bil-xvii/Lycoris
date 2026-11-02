using System.Globalization;
using Monolith.Core;

namespace Monolith.Modules.Restore;

/// <summary>One journal record as shown in the undo center.</summary>
public sealed class RecordItem(ChangeRecord record, DriftItem? drift = null, string note = "", bool otherUser = false)
{
    public ChangeRecord Record { get; } = record;
    /// <summary>Whose change it is, which newer change now sets the value, how the revert went (see <see cref="RestoreFormat.RecordNote"/>).</summary>
    public string Note { get; } = note;
    public bool HasNote => Note.Length > 0;
    /// <summary>A change in another account's profile: shown, never reverted from here.</summary>
    public bool IsOtherUser { get; } = otherUser;
    public bool CanRevert => IsActive && !IsOtherUser;
    /// <summary>The value read back after the revert differs from the original.</summary>
    public bool IsMismatch => Record.Reverted && Record.RevertMismatch is not null;
    /// <summary>Set when the live value no longer equals what Lycoris set (changed by Windows or another program).</summary>
    public DriftItem? Drift { get; } = drift;
    public bool IsDrifted => Drift is not null && !Record.Reverted;
    public string DriftText => Drift is null ? "" : DriftDetector.RowText(Drift);
    public bool CanReapply => IsDrifted && DriftReapply.CanReapply(Drift!);
    /// <summary>No direct re-apply, but the owning page can show it.</summary>
    public bool CanShowOwner => IsDrifted && !CanReapply && Drift!.OwnerPage is not null;
    public string Title => Record.Title.Length > 0 ? Loc.Sink(Record.Title) : Loc.T("Без названия");
    public string KindLabel => RestoreFormat.KindLabel(Record.Kind);
    public string Target => RestoreFormat.DescribeTarget(Record.Target);
    public string RawTarget => Record.Target;
    public string OldValue => string.IsNullOrWhiteSpace(Record.OldValue) ? "—" : Loc.Sink(Record.OldValue);
    public string NewValue => string.IsNullOrWhiteSpace(Record.NewValue) ? "—" : Loc.Sink(Record.NewValue);
    public string Source => string.IsNullOrWhiteSpace(Record.Source) ? "monolith" : Record.Source;
    public string TimeText => Record.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    public RecordState State => RestoreFormat.StateOf(Record);
    public string StatusText => RestoreFormat.StatusText(Record);
    public bool IsActive => !Record.Reverted;
    public bool IsReverted => Record.Reverted;
    public bool IsFailed => State == RecordState.Failed;
    public string? RevertError => Record.RevertError;
}

/// <summary>A batch ("пакет") of changes made by one action.</summary>
public sealed partial class BatchItem(JournalGroup group, Func<ChangeRecord, RecordItem> item) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public JournalGroup Group { get; } = group;
    public Guid Id => Group.Batch.Id;
    public string Title => Group.Batch.Title.Length > 0 ? Loc.Sink(Group.Batch.Title) : Loc.T("Без названия");
    public string DateText => RestoreFormat.FormatDate(Group.Batch.Timestamp);
    public IReadOnlyList<RecordItem> Records { get; } = group.Records.Select(item).ToList();
    public int ActiveCount => Group.ActiveCount;
    public int TotalCount => Group.TotalCount;
    public bool CanRevert => Group.RevertibleCount > 0;
    public bool IsEmpty => TotalCount == 0;
    public bool IsSynthetic => Group.IsSynthetic;

    /// <summary>The batch's records are open (collapsed by default; the header and the revert button are always visible).</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _isExpanded;
    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandText));
    /// <summary>The label of the outlined expander button.</summary>
    public string ExpandText => IsExpanded ? Loc.Tf("СКРЫТЬ ЗАПИСИ · {0}", TotalCount) : Loc.Tf("ПОКАЗАТЬ ЗАПИСИ · {0}", TotalCount);
    public bool HasRestorePointInfo => !IsSynthetic;
    public bool RestorePointCreated => RestoreFormat.IsRestorePointCreated(Group.Batch.RestorePoint);

    public string RestorePointText => IsSynthetic
        ? Loc.T("Записи, пакет которых не найден в журнале.")
        : string.IsNullOrWhiteSpace(Group.Batch.RestorePoint) ? Loc.T("Точка восстановления не создавалась.") : Loc.Sink(Group.Batch.RestorePoint);

    public string CountText => TotalCount == 0
        ? Loc.T("НЕТ ЗАПИСЕЙ")
        : ActiveCount == 0
            ? Loc.T("ВСЁ ОТКАЧЕНО")
            : Loc.Tf("АКТИВНО {0} ИЗ {1}", ActiveCount, TotalCount);
}

/// <summary>A Windows restore point.</summary>
public sealed class RestorePointItem(RestorePointInfo info)
{
    public RestorePointInfo Info { get; } = info;
    public string DateText => Info.Created == DateTime.MinValue ? "—" : Info.Created.ToString("dd.MM.yyyy · HH:mm", CultureInfo.InvariantCulture);
    public string Description => string.IsNullOrWhiteSpace(Info.Description) ? Loc.T("Без описания") : Info.Description;
    public string TypeLabel => RestoreFormat.RestorePointTypeLabel(Info.Type);
    public bool IsMonolith => RestoreFormat.IsMonolithPoint(Info.Description);
    public string SequenceText => "#" + Info.Sequence.ToString(CultureInfo.InvariantCulture);
}
