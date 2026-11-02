using Microsoft.Win32;
using Monolith.Core;
using Monolith.Core.Devices;
using Monolith.Tweaks;

namespace Monolith.Modules.Restore;

/// <summary>
/// What a journaled target looks like right now. <see cref="Drifted"/>: true when the live value differs from the
/// value Lycoris applied (changed later by Windows Update, a policy or the user — research services.md §3.5),
/// false when it matches, null when it cannot be compared (commands, a vanished service or task, read errors).
/// </summary>
public sealed record LiveValue(string Text, bool? Drifted)
{
    public bool IsDrifted => Drifted == true;
}

/// <summary>
/// Read-only live reads for the undo center: the current value of a journal record's target, in the same
/// wording ChangeSession uses for <see cref="ChangeRecord.NewValue"/>. Blocking (registry, SCM, COM, power API):
/// call off the UI thread. Never changes anything.
/// </summary>
public static class LiveState
{
    public const string Unknown = "—";

    public static LiveValue Read(ChangeRecord r)
    {
        try
        {
            return r.Kind switch
            {
                ChangeKind.Service => ReadService(r),
                ChangeKind.Registry => ReadRegistry(r),
                ChangeKind.PowerScheme => PowerSchemes.GetActive() is var active && active != Guid.Empty
                    ? Compare(r, PowerSchemes.GetName(active))
                    : new LiveValue(Unknown, null),
                ChangeKind.ScheduledTask => ReadTask(r),
                // The Windows 11 power mode (AC / DC): the live mode in the record's own wording, so Restore shows drift honestly
                // (DriftDetector keeps excluding Command records; a game session's mode is temporary by design).
                ChangeKind.Command when JournalTrust.PowerModeUndo(r) is { } pm => ReadPowerMode(r, pm.Dc),
                // Device settings: shown, never compared (users change them on purpose — no drift).
                _ when JournalTrust.IsDeviceKind(r.Kind) => new LiveValue(DeviceLive(r) ?? Unknown, null),
                _ => new LiveValue(Unknown, null),
            };
        }
        catch (Exception ex) when (!Elevation.IsElevated && ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Machine-wide keys of system accounts are readable only by administrators: unknown, not a problem.
            return new LiveValue(Unknown, null);
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось прочитать текущее значение «{0}»: {1}", r.Title, ex.Message);
            return new LiveValue(Unknown, null);
        }
    }

    /// <summary>
    /// As <see cref="Read(ChangeRecord)"/>, for a record that may be an older layer: when a newer change of the same
    /// target is active (<paramref name="isTop"/> false), its value was replaced on purpose and is never reported as drift.
    /// </summary>
    public static LiveValue Read(ChangeRecord r, bool isTop)
    {
        var live = Read(r);
        return isTop ? live : live with { Drifted = null };
    }

    // ───────────────────────────── Revert verification ─────────────────────────────

    /// <summary>
    /// Reads the target of a just-reverted record back and compares it with the original the record held
    /// («Откат подтверждён» / «Не совпадает: сейчас X, ожидалось Y»). Read-only, blocking: call off the UI thread.
    /// <see cref="RevertCheck.Matches"/> is null when there is nothing to compare (target gone, a command without a reader).
    /// </summary>
    public static RevertCheck VerifyRevert(ChangeRecord r)
    {
        try
        {
            return r.Kind switch
            {
                ChangeKind.Registry when r.Registry is { } original => VerifyRegistry(original),
                ChangeKind.Service when r.Service is { } original => VerifyService(original),
                ChangeKind.PowerScheme => VerifyPower(r),
                ChangeKind.ScheduledTask when r.TaskPath is { } path && r.TaskWasEnabled is { } was => VerifyTask(path, was),
                ChangeKind.Command => VerifyCommand(r),
                _ when JournalTrust.IsDeviceKind(r.Kind) => VerifyDevice(r),
                _ => RevertCheck.NotChecked,
            };
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось проверить откат «{0}»: {1}", r.Title, ex.Message);
            return RevertCheck.NotChecked;
        }
    }

    // ───────────────────────────── Device records ─────────────────────────────

    /// <summary>The live value of a device record in NewValue wording; null when this build has no reader or the device is gone.</summary>
    private static string? DeviceLive(ChangeRecord r)
    {
        switch (r.Kind)
        {
            case ChangeKind.UserParameter when r.Spi is { } s && SpiCatalog.Find(s.Id) is { } e && DeviceWriters.Spi is { } spi:
                return spi.Get(e) is { } v ? e.Describe(v) : null;
            case ChangeKind.DisplayMode when r.DisplayMode is { } d && DeviceWriters.Display is { } display:
                return display.ReadCurrentMode(d.MonitorPath)?.Describe() ?? "монитор не подключён"; // loc:ignore journal-compared state text (Russian on disk)
            case ChangeKind.DisplayHdr when r.DisplayHdr is { } h && DeviceWriters.Display is { } display:
                return display.ReadHdrEnabled(h.MonitorPath) is { } on ? DeviceTargets.DescribeHdr(on) : "монитор не подключён"; // loc:ignore journal-compared state text (Russian on disk)
            case ChangeKind.MonitorSetting when r.Monitor is { } m && DeviceWriters.Monitor is { } monitor:
                return monitor.Get(m.MonitorPath, m.Code) is { } vcp ? DeviceTargets.DescribeVcp(m.Code, vcp.Value, vcp.Max) : null;
            case ChangeKind.AudioLevel when r.Audio is { } a && DeviceWriters.Audio is { } audio:
                if (!audio.IsPresent(a.EndpointId)) return "устройство не найдено"; // loc:ignore journal-compared state text (Russian on disk)
                return audio.Read(a.EndpointId) is { } level
                    ? a.Component == AudioComponent.Mute ? DeviceTargets.DescribeMute(level.Mute) : DeviceTargets.DescribeLevel(level.Scalar)
                    : null;
            case ChangeKind.NicProperty when r.Nic is { } n && DeviceWriters.Nic is { } nic:
                return nic.Read(n.InterfaceGuid, n.Keyword) is { } p ? DeviceTargets.DescribeNic(p.Value) : "адаптер или свойство не найдены"; // loc:ignore journal-compared state text (Russian on disk)
            default:
                return null;
        }
    }

    /// <summary>After an undo: the live value equals the original part of the record (only the part it changed).</summary>
    private static RevertCheck VerifyDevice(ChangeRecord r)
    {
        switch (r.Kind)
        {
            case ChangeKind.UserParameter when r.Spi is { } s && SpiCatalog.Find(s.Id) is { } e && DeviceWriters.Spi is { } spi:
                return spi.Get(e) is { } v ? new RevertCheck(v == s.Value, e.Describe(v), e.Describe(s.Value)) : RevertCheck.NotChecked;
            case ChangeKind.DisplayMode when r.DisplayMode is { } d && DeviceWriters.Display is { } display:
                return display.ReadCurrentMode(d.MonitorPath) is { } mode
                    ? new RevertCheck(mode.SameMode(d.Original), mode.Describe(), d.Original.Describe()) : RevertCheck.NotChecked;
            case ChangeKind.DisplayHdr when r.DisplayHdr is { } h && DeviceWriters.Display is { } display:
                return display.ReadHdrEnabled(h.MonitorPath) is { } on
                    ? new RevertCheck(on == h.OriginalEnabled, DeviceTargets.DescribeHdr(on), DeviceTargets.DescribeHdr(h.OriginalEnabled))
                    : RevertCheck.NotChecked;
            case ChangeKind.MonitorSetting when r.Monitor is { } m && DeviceWriters.Monitor is { } monitor:
                return monitor.Get(m.MonitorPath, m.Code) is { } vcp
                    ? new RevertCheck(vcp.Value == m.Value, DeviceTargets.DescribeVcp(m.Code, vcp.Value, vcp.Max), DeviceTargets.DescribeVcp(m.Code, m.Value, m.Max))
                    : RevertCheck.NotChecked;
            case ChangeKind.AudioLevel when r.Audio is { } a && DeviceWriters.Audio is { } audio:
                if (!audio.IsPresent(a.EndpointId) || audio.Read(a.EndpointId) is not { } level) return RevertCheck.NotChecked;
                return a.Component == AudioComponent.Mute
                    ? new RevertCheck(level.Mute == a.Mute, DeviceTargets.DescribeMute(level.Mute), DeviceTargets.DescribeMute(a.Mute))
                    : new RevertCheck(Math.Abs(level.Scalar - a.Scalar) <= 0.001f, DeviceTargets.DescribeLevel(level.Scalar), DeviceTargets.DescribeLevel(a.Scalar));
            case ChangeKind.NicProperty when r.Nic is { } n && DeviceWriters.Nic is { } nic:
                if (nic.Read(n.InterfaceGuid, n.Keyword) is not { } p) return RevertCheck.NotChecked;
                var live = p.Value ?? p.DefaultValue;
                return new RevertCheck(string.Equals(live, n.RestoreValue, StringComparison.Ordinal), DeviceTargets.DescribeNic(live), DeviceTargets.DescribeNic(n.RestoreValue));
            default:
                return RevertCheck.NotChecked;
        }
    }

    /// <summary>Pure comparison of a restored registry value with the original snapshot (kind and exact data).</summary>
    public static RevertCheck CompareRegistry(RegistryValueSnapshot original, RegistryValueSnapshot live)
    {
        var same = original.ValueExisted == live.ValueExisted
                   && (!original.ValueExisted || (original.Kind == live.Kind && string.Equals(original.Data, live.Data, StringComparison.Ordinal)));
        return new RevertCheck(same, RegistryText(live), RegistryText(original));
    }

    private static string RegistryText(RegistryValueSnapshot s) => s.ValueExisted ? FormatRegistry(s) : "значения нет"; // loc:ignore journal-compared state text (Russian on disk)

    private static RevertCheck VerifyRegistry(RegistryValueSnapshot original) =>
        CompareRegistry(original, RegistryHelper.Snapshot(original.Hive, original.Path, original.Name));

    /// <summary>Pure comparison of a restored service start configuration with the original (raw delayed flag when recorded).</summary>
    public static RevertCheck CompareService(ServiceStartSnapshot original, ServiceStartSnapshot live)
    {
        var sameStart = live.StartMode == original.StartMode && live.DelayedAutoStart == original.DelayedAutoStart;
        var sameFlag = original.RawDelayed is null || live.RawDelayed == original.RawDelayed;
        if (sameStart && !sameFlag)
            return new RevertCheck(false, Loc.Tf("{0}, флаг отложенного запуска {1}", live.Describe(), Flag(live.RawDelayed)),
                Loc.Tf("{0}, флаг отложенного запуска {1}", original.Describe(), Flag(original.RawDelayed)));
        return new RevertCheck(sameStart && sameFlag, live.Describe(), original.Describe());

        static string Flag(bool? f) => f == true ? "1" : "0";
    }

    private static RevertCheck VerifyService(ServiceStartSnapshot original) =>
        ServiceControl.Query(original.Name) is { } live ? CompareService(original, live) : RevertCheck.NotChecked;

    private static RevertCheck VerifyPower(ChangeRecord r)
    {
        var active = PowerSchemes.GetActive();
        if (active == Guid.Empty) return RevertCheck.NotChecked;
        var expected = r.PowerSchemeOriginal is { } o && o != Guid.Empty && PowerSchemes.Exists(o) ? o : PowerSchemes.Balanced;
        if (active != expected) return new RevertCheck(false, PowerSchemes.GetName(active), PowerSchemes.GetName(expected));
        var leftover = ChangeJournal.CreatedSchemes(r).FirstOrDefault(c => c != active && PowerSchemes.TryExists(c) == true);
        if (leftover != Guid.Empty)
            return new RevertCheck(false, Loc.Tf("осталась созданная Lycoris схема «{0}»", PowerSchemes.GetName(leftover)), Loc.T("созданная схема удалена"));
        return new RevertCheck(true, PowerSchemes.GetName(active), PowerSchemes.GetName(expected));
    }

    private static RevertCheck VerifyTask(string path, bool wasEnabled)
    {
        var info = ScheduledTasks.Query(path);
        if (!info.Exists) return RevertCheck.NotChecked;
        return new RevertCheck(info.Enabled == wasEnabled, TaskText(info.Enabled), TaskText(wasEnabled));

        static string TaskText(bool enabled) => enabled ? "Включена" : "Отключена"; // loc:ignore journal-compared state text (Russian on disk)
    }

    private const string DxPreferencesPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string DxGlobalSettings = "DirectXUserGlobalSettings";

    /// <summary>
    /// Commands with a catalog reader: a power setting's AC or DC value, the AC / DC power mode, an adapter's DNS servers,
    /// a DirectX token.
    /// </summary>
    private static RevertCheck VerifyCommand(ChangeRecord r)
    {
        if (JournalTrust.PowercfgUndo(r) is { } pc)
        {
            var dc = JournalTrust.PowercfgIsDc(r.Target) == true;
            if (PowerSchemes.TryExists(pc.Scheme) != true) return RevertCheck.NotChecked;
            if (TweakCatalog.ReadIndex(dc, pc.Scheme, pc.Subgroup, pc.Setting) is not { } value) return RevertCheck.NotChecked;
            return new RevertCheck(value == pc.Value, $"{Side(dc)}: {value}", $"{Side(dc)}: {pc.Value}");
        }
        if (JournalTrust.PowerModeUndo(r) is { } pm)
        {
            if (ReadPowerMode(pm.Dc) is not { } live) return RevertCheck.NotChecked;
            return new RevertCheck(live == pm.Mode, PowerModeText(pm.Dc, live), PowerModeText(pm.Dc, pm.Mode));
        }
        if (JournalTrust.DnsUndo(r) is { } dns)
        {
            var live = TweakCatalog.StaticDns(dns.Adapter, dns.Ipv6);
            return new RevertCheck(TweakCatalog.SameServers(live, dns.Servers), DnsText(live), DnsText(dns.Servers));
        }
        if (JournalTrust.DxUndo(r) is { } dx)
        {
            var live = TweakCatalog.GetDxToken(RegistryHelper.ReadString(RegHive.HKCU, DxPreferencesPath, DxGlobalSettings), dx.Token);
            return new RevertCheck(string.Equals(live, dx.Value, StringComparison.Ordinal), DxText(live), DxText(dx.Value));
        }
        return RevertCheck.NotChecked;

        static string DnsText(IReadOnlyList<string> servers) => servers.Count == 0 ? "автоматически (DHCP)" : string.Join(", ", servers); // loc:ignore journal-compared state text (Russian on disk)
        static string DxText(string? value) => value is null ? "не задано" : value; // loc:ignore journal-compared state text (Russian on disk)
    }

    private static string Side(bool dc) => dc ? "от батареи" : "от сети"; // loc:ignore journal-compared state text (Russian on disk)

    /// <summary>The user-configured power mode of one side (Windows 11), null when unavailable. Read-only.</summary>
    private static Guid? ReadPowerMode(bool dc) => dc ? TweakCatalog.ReadDcPowerMode() : TweakCatalog.ReadAcPowerMode();

    /// <summary>«от сети: максимальная производительность» — the wording of the power-mode records' NewValue.</summary>
    internal static string PowerModeText(bool dc, Guid mode) => $"{Side(dc)}: {TweakCatalog.DescribePowerMode(mode)}";

    private static LiveValue ReadPowerMode(ChangeRecord r, bool dc) =>
        ReadPowerMode(dc) is { } live ? Compare(r, PowerModeText(dc, live)) : new LiveValue(Unknown, null);

    private static LiveValue ReadService(ChangeRecord r)
    {
        var name = r.Service?.Name ?? StripPrefix(r.Target, "svc:");
        if (string.IsNullOrEmpty(name)) return new LiveValue(Unknown, null);
        return ServiceControl.Query(name) is { } live
            ? Compare(r, live.Describe())
            : new LiveValue("служба не найдена", null); // loc:ignore journal-compared state text (Russian on disk)
    }

    private static LiveValue ReadRegistry(ChangeRecord r)
    {
        // The record's snapshot holds the location (and the original value); the live read uses the same location.
        if (r.Registry is not { } loc) return new LiveValue(Unknown, null);
        return Compare(r, FormatRegistry(RegistryHelper.Snapshot(loc.Hive, loc.Path, loc.Name)));
    }

    private static LiveValue ReadTask(ChangeRecord r)
    {
        var path = r.TaskPath ?? StripPrefix(r.Target, "task:");
        if (string.IsNullOrEmpty(path)) return new LiveValue(Unknown, null);
        var info = ScheduledTasks.Query(path);
        return info.Exists
            ? Compare(r, info.Enabled ? "Включена" : "Отключена") // loc:ignore journal-compared state text (Russian on disk)
            : new LiveValue("задача не найдена", null); // loc:ignore journal-compared state text (Russian on disk)
    }

    private static LiveValue Compare(ChangeRecord r, string live) => new(live, IsDrift(r.Kind, r.NewValue, live));

    /// <summary>
    /// A registry snapshot in ChangeSession's NewValue wording: "удалено" for a missing value,
    /// otherwise "Kind: value" (DWORD as a signed number, binary as hex bytes, multi-string joined with "; ").
    /// </summary>
    public static string FormatRegistry(RegistryValueSnapshot s)
    {
        if (!s.ValueExisted) return "удалено"; // loc:ignore journal-compared state text (Russian on disk)
        var text = s.Kind switch
        {
            RegistryValueKind.DWord when uint.TryParse(s.Data, out var u) => unchecked((int)u).ToString(),
            RegistryValueKind.Binary or RegistryValueKind.MultiString => FormatDeserialized(s),
            _ => s.Data ?? "",
        };
        return $"{s.Kind}: {text}";
    }

    private static string FormatDeserialized(RegistryValueSnapshot s)
    {
        try
        {
            return RegistryHelper.Deserialize(s.Kind, s.Data) switch
            {
                byte[] b => BitConverter.ToString(b).Replace("-", " "),
                string[] a => string.Join("; ", a),
                var o => o.ToString() ?? "",
            };
        }
        catch (FormatException)
        {
            return s.Data ?? "";
        }
    }

    /// <summary>
    /// Pure comparison of the applied value with the live one. Registry DWORDs compare numerically, so a value
    /// written as -1 and read back as 4294967295 is not reported as drift.
    /// </summary>
    public static bool? IsDrift(ChangeKind kind, string? applied, string? live)
    {
        if (string.IsNullOrWhiteSpace(applied) || string.IsNullOrWhiteSpace(live) || live == Unknown) return null;
        if (string.Equals(applied.Trim(), live.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        if (kind == ChangeKind.Registry && DWordOf(applied) is { } a && DWordOf(live) is { } b) return a != b;
        return true;
    }

    private static uint? DWordOf(string text)
    {
        const string prefix = "DWord: ";
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var n = text[prefix.Length..].Trim();
        if (uint.TryParse(n, out var u)) return u;
        if (int.TryParse(n, out var i)) return unchecked((uint)i);
        return null;
    }

    private static string StripPrefix(string target, string prefix) =>
        target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? target[prefix.Length..] : "";
}
