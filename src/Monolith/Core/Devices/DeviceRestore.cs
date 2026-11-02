using Microsoft.Win32;

namespace Monolith.Core.Devices;

/// <summary>
/// The system side of undoing device records (called by WindowsJournalSystem only for records that passed the journal
/// check). Each case re-validates against the live device, writes the original through the writer and reads it back.
/// A device that no longer exists means «nothing to restore» (skipped, not a failure); a missing writer is a failure.
/// </summary>
internal static class DeviceRestore
{
    private static InvalidOperationException NoData() => new(Loc.T("Запись журнала не содержит данных для отката."));

    // ───────────────────────────── SPI ─────────────────────────────

    public static RevertOutcome RestoreUserParameter(ChangeRecord r)
    {
        var snap = r.Spi ?? throw NoData();
        if (Elevation.InteractiveUserProblem is { } problem) throw new InvalidOperationException(problem);
        var entry = SpiCatalog.Find(snap.Id) ?? throw new InvalidOperationException(Loc.Tf("неизвестный параметр «{0}»", snap.Id));
        var writer = DeviceWriters.Spi ?? throw new InvalidOperationException(DeviceWriters.NoWriterRevert);
        SafetyLock.ThrowIfLocked(Loc.T("откат параметра Windows"));
        if (!RestoreSpi(writer, entry, snap.Value, snap.Persisted, out var error)) throw new InvalidOperationException(error);
        return RevertOutcome.Done;
    }

    /// <summary>
    /// Puts a parameter back: live value through SystemParametersInfo (which also rewrites the persisted value in its own
    /// format), then the persisted value exactly as it was — deleted when it did not exist. Used by undo and by a failed apply.
    /// </summary>
    internal static bool RestoreSpi(ISpiWriter writer, SpiEntry entry, int value, RegistryValueSnapshot? persisted, out string error)
    {
        error = "";
        if (!writer.Set(entry, value) || writer.Get(entry) != value)
        {
            error = Loc.Tf("параметр «{0}» не вернулся к значению {1}", entry.Title, entry.Describe(value));
            return false;
        }
        if (persisted is null) return true; // a UserPreferencesMask bit: never restore the whole mask
        var live = RegistryHelper.Snapshot(persisted.Hive, persisted.Path, persisted.Name);
        if (!persisted.ValueExisted)
        {
            if (live.ValueExisted) RegistryHelper.DeleteValue(persisted.Hive, persisted.Path, persisted.Name);
        }
        else if (live.Kind != persisted.Kind || !string.Equals(live.Data, persisted.Data, StringComparison.Ordinal) || !live.ValueExisted)
        {
            RegistryHelper.Restore(persisted);
        }
        return true;
    }

    // ───────────────────────────── DDC/CI ─────────────────────────────

    public static RevertOutcome RestoreMonitor(ChangeRecord r)
    {
        var snap = r.Monitor ?? throw NoData();
        var writer = DeviceWriters.Monitor ?? throw new InvalidOperationException(DeviceWriters.NoWriterRevert);
        SafetyLock.ThrowIfLocked(Loc.T("откат яркости или контраста монитора"));
        if (writer.Get(snap.MonitorPath, snap.Code) is not { } live)
        {
            if (DeviceWriters.Display is { } d && d.ReadCurrentMode(snap.MonitorPath) is null)
                throw new InvalidOperationException(Loc.T("монитор не подключён — подключите его и повторите откат"));
            throw new InvalidOperationException(Loc.T("монитор не отвечает по DDC/CI"));
        }
        if (snap.Value < live.Min || snap.Value > live.Max)
            throw new InvalidOperationException(Loc.Tf("прежнее значение {0} вне диапазона монитора {1}–{2}", snap.Value, live.Min, live.Max));
        if (live.Value == snap.Value) return RevertOutcome.Done;
        if (!writer.Set(snap.MonitorPath, snap.Code, snap.Value) || writer.Get(snap.MonitorPath, snap.Code)?.Value != snap.Value)
            throw new InvalidOperationException(Loc.T("монитор не принял прежнее значение"));
        return RevertOutcome.Done;
    }

    // ───────────────────────────── Endpoint volume ─────────────────────────────

    public static RevertOutcome RestoreAudio(ChangeRecord r)
    {
        var snap = r.Audio ?? throw NoData();
        if (Elevation.InteractiveUserProblem is { } problem) throw new InvalidOperationException(problem);
        var writer = DeviceWriters.Audio ?? throw new InvalidOperationException(DeviceWriters.NoWriterRevert);
        SafetyLock.ThrowIfLocked(Loc.T("откат уровня звука"));
        // IsPresent is false only when Windows knows no endpoint with this id; a COM failure throws (the record stays), and a
        // known but inactive endpoint (unplugged, disabled) fails the read below — also keeping the record.
        if (!writer.IsPresent(snap.EndpointId))
            return new RevertOutcome(Loc.T("Устройства больше нет (или его драйвер переустановлен) — откатывать нечего."), Skipped: true);
        if (writer.Read(snap.EndpointId) is not { } live)
            throw new InvalidOperationException(Loc.T("устройство сейчас неактивно (отключено или выключено) — не удалось прочитать его уровень; подключите его и повторите откат"));

        if (snap.Component == AudioComponent.Mute)
        {
            // Only the mute switch: a level adjusted since stays as it is.
            if (live.Mute == snap.Mute) return RevertOutcome.Done;
            if (!writer.SetMute(snap.EndpointId, snap.Mute) || writer.Read(snap.EndpointId)?.Mute != snap.Mute)
                throw new InvalidOperationException(Loc.T("устройство не приняло прежнее состояние звука"));
            return RevertOutcome.Done;
        }

        // Only the level: a microphone muted since (in Windows or with a hardware key) stays muted.
        var original = snap.Scalar;
        if (Math.Abs(live.Scalar - original) <= 0.001f) return RevertOutcome.Done;
        if (!writer.SetScalar(snap.EndpointId, original) || writer.Read(snap.EndpointId) is not { } back || Math.Abs(back.Scalar - original) > 0.001f)
            throw new InvalidOperationException(Loc.T("устройство не приняло прежний уровень"));
        return RevertOutcome.Done;
    }

    // ───────────────────────────── Network adapter ─────────────────────────────

    public static RevertOutcome RestoreNic(ChangeRecord r)
    {
        var snap = r.Nic ?? throw NoData();
        var writer = DeviceWriters.Nic ?? throw new InvalidOperationException(DeviceWriters.NoWriterRevert);
        SafetyLock.ThrowIfLocked(Loc.T("откат свойства сетевого адаптера"));
        if (GameBoostState.IsActive) throw new InvalidOperationException(Loc.T("во время игрового сеанса MONOLITH не перезапускает сетевой адаптер — повторите откат после игры"));
        if (writer.RunningGame() is { } game)
            throw new InvalidOperationException(Loc.Tf("идёт игра ({0}) — MONOLITH не перезапускает сетевой адаптер, повторите откат после игры", game));
        // The writer answers null only when WMI really knows no such adapter / property; a failed query throws instead, so a
        // transient WMI error fails this undo (the record stays) and never closes it as «gone».
        if (writer.AdapterName(snap.InterfaceGuid) is null)
            return new RevertOutcome(Loc.T("Сетевого адаптера больше нет — откатывать нечего."), Skipped: true);
        if (writer.Read(snap.InterfaceGuid, snap.Keyword) is not { } live)
            return new RevertOutcome(Loc.T("У драйвера адаптера больше нет этого свойства — откатывать нечего."), Skipped: true);
        var value = snap.RestoreValue ?? throw new InvalidOperationException(Loc.T("в записи нет прежнего значения"));
        if (string.Equals(live.Value ?? live.DefaultValue, value, StringComparison.Ordinal)) return RevertOutcome.Done;
        if (live.ValidValues.Count > 0 && !live.ValidValues.Contains(value, StringComparer.Ordinal))
            throw new InvalidOperationException(Loc.Tf("драйвер адаптера больше не принимает прежнее значение {0}", value));
        Log.Info("Откат «{0}»: {1}.", r.Title, DeviceTargets.NicRestartNote);
        if (!writer.Set(snap.InterfaceGuid, snap.Keyword, value) || writer.Read(snap.InterfaceGuid, snap.Keyword)?.Value != value)
            throw new InvalidOperationException(Loc.T("адаптер не принял прежнее значение"));
        return RevertOutcome.Done;
    }

    /// <summary>The persisted HKCU value an SPI record restores must be one of the kinds Windows writes there.</summary>
    internal static bool IsPersistedKind(RegistryValueKind kind) => kind is RegistryValueKind.String or RegistryValueKind.DWord;
}
