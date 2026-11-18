namespace Lycoris.Core;

public enum ComputerKind { Unknown, Desktop, Laptop, AllInOne, Tablet, Convertible, Detachable, Server, VirtualMachine }

/// <summary>
/// Strict, read-only identity from SMBIOS facts. Batteries (including UPS batteries) and HypervisorPresent
/// are deliberately not evidence of a form factor or of running inside a virtual machine.
/// </summary>
public sealed record ComputerIdentity
{
    public static ComputerIdentity Unknown { get; } = FromFacts(null, null, null, null, false, false);

    public ComputerKind Kind { get; }
    public string Manufacturer { get; }
    public string Model { get; }
    public bool HasConflict { get; }
    public bool IsKnown => Kind != ComputerKind.Unknown && !HasConflict;
    public bool IsPortable => IsKnown && Kind is ComputerKind.Laptop or ComputerKind.Tablet or ComputerKind.Convertible or ComputerKind.Detachable;

    private readonly string _pcTypeEvidence;
    private readonly string _chassisEvidence;
    private readonly bool _computerSystemRead;
    private readonly bool _enclosureRead;

    private ComputerIdentity(ComputerKind kind, string manufacturer, string model, bool conflict,
        string pcTypeEvidence, string chassisEvidence, bool computerSystemRead, bool enclosureRead)
    {
        Kind = kind;
        Manufacturer = manufacturer;
        Model = model;
        HasConflict = conflict;
        _pcTypeEvidence = pcTypeEvidence;
        _chassisEvidence = chassisEvidence;
        _computerSystemRead = computerSystemRead;
        _enclosureRead = enclosureRead;
    }

    public string TypeText => Kind switch
    {
        ComputerKind.Desktop => Loc.T("Настольный компьютер"),
        ComputerKind.Laptop => Loc.T("Ноутбук"),
        ComputerKind.AllInOne => Loc.T("Моноблок"),
        ComputerKind.Tablet => Loc.T("Планшет"),
        ComputerKind.Convertible => Loc.T("Ноутбук-трансформер"),
        ComputerKind.Detachable => Loc.T("Планшет со съёмной клавиатурой"),
        ComputerKind.Server => Loc.T("Сервер"),
        ComputerKind.VirtualMachine => Loc.T("Виртуальная машина"),
        _ => Loc.T("Тип компьютера не определён"),
    };

    public string Reason => HasConflict
        ? Loc.T("Данные SMBIOS о типе компьютера противоречат друг другу.")
        : !IsKnown
            ? Loc.T("Нет достоверных данных SMBIOS для определения типа компьютера.")
            : Kind == ComputerKind.VirtualMachine
                ? Loc.T("Производитель и модель SMBIOS указывают на виртуальную машину.")
                : Loc.T("Тип подтверждён данными SMBIOS; наличие батареи не использовалось.");

    public string EvidenceText => Loc.Tf("Данные SMBIOS: PCSystemType = {0}; ChassisTypes = {1}.",
        _computerSystemRead ? _pcTypeEvidence : Loc.T("нет данных"),
        _enclosureRead ? _chassisEvidence : Loc.T("нет данных"));

    public string Summary
    {
        get
        {
            var product = string.Join(" ", new[] { Manufacturer, Model }.Where(x => x.Length > 0));
            if (product.Length == 0) product = Loc.T("Производитель и модель не указаны");
            return IsKnown ? Loc.Tf("{0} · {1}", TypeText, product)
                : Loc.Tf("{0} · {1}", product, Reason);
        }
    }

    /// <summary>
    /// Pure classifier: only successfully read sources count. One recognized source is sufficient;
    /// disagreement between recognized sources is not resolved by guessing. Unknown codes add no evidence.
    /// See Win32_ComputerSystem.PCSystemType and Win32_SystemEnclosure.ChassisTypes on Microsoft Learn.
    /// </summary>
    public static ComputerIdentity FromFacts(string? manufacturer, string? model, ushort? pcSystemType,
        IReadOnlyCollection<ushort>? chassisTypes, bool computerSystemRead = true, bool enclosureRead = true)
    {
        var maker = computerSystemRead ? NormalizeProduct(manufacturer) : "";
        var product = computerSystemRead ? NormalizeProduct(model) : "";
        var chassis = enclosureRead ? chassisTypes?.ToArray() ?? [] : [];
        var chassisKinds = chassis.Select(ChassisKind).Where(x => x != ComputerKind.Unknown).ToHashSet();
        // Firmware may report a broad family together with its more precise form factor.
        if (chassisKinds.Contains(ComputerKind.AllInOne)) chassisKinds.Remove(ComputerKind.Desktop);
        if (chassisKinds.Overlaps([ComputerKind.Tablet, ComputerKind.Convertible, ComputerKind.Detachable]))
            chassisKinds.Remove(ComputerKind.Laptop);
        var pcKind = computerSystemRead ? PcKind(pcSystemType) : ComputerKind.Unknown;
        var enclosureKind = chassisKinds.Count == 1 ? chassisKinds.Single() : ComputerKind.Unknown;
        var conflict = chassisKinds.Count > 1 || pcKind != ComputerKind.Unknown
            && enclosureKind != ComputerKind.Unknown && !Compatible(pcKind, enclosureKind);
        var kind = conflict ? ComputerKind.Unknown
            : IsVirtualMachine(maker, product) ? ComputerKind.VirtualMachine
            : enclosureKind != ComputerKind.Unknown ? enclosureKind : pcKind;

        return new(kind, maker, product, conflict,
            pcSystemType?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—",
            chassis.Length > 0 ? string.Join(", ", chassis) : "—", computerSystemRead, enclosureRead);
    }

    private static ComputerKind PcKind(ushort? type) => type switch
    {
        1 or 3 => ComputerKind.Desktop,
        2 => ComputerKind.Laptop,
        4 or 5 or 7 => ComputerKind.Server,
        _ => ComputerKind.Unknown,
    };

    private static ComputerKind ChassisKind(ushort type) => type switch
    {
        3 or 4 or 5 or 6 or 7 or 15 or 24 or 35 or 36 => ComputerKind.Desktop,
        8 or 9 or 10 or 14 => ComputerKind.Laptop,
        13 => ComputerKind.AllInOne,
        23 or 28 => ComputerKind.Server,
        30 => ComputerKind.Tablet,
        31 => ComputerKind.Convertible,
        32 => ComputerKind.Detachable,
        _ => ComputerKind.Unknown,
    };

    private static bool Compatible(ComputerKind pcKind, ComputerKind enclosureKind) => pcKind == enclosureKind
        || pcKind == ComputerKind.Desktop && enclosureKind == ComputerKind.AllInOne
        || pcKind == ComputerKind.Laptop && enclosureKind is ComputerKind.Tablet or ComputerKind.Convertible or ComputerKind.Detachable;

    private static string NormalizeProduct(string? value)
    {
        var text = string.Join(" ", (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var compact = string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return compact is "" or "tobefilledbyoem" or "systemproductname" or "systemmanufacturer"
            or "defaultstring" or "unknown" or "none" or "notapplicable" or "na" or "oem" ? "" : text;
    }

    private static bool IsVirtualMachine(string manufacturer, string model)
    {
        static bool Is(string value, params string[] candidates) => candidates.Contains(value, StringComparer.OrdinalIgnoreCase);
        return Is(manufacturer, "VMware, Inc.", "VMware, Inc", "VMware") && Is(model, "VMware Virtual Platform")
            || Is(manufacturer, "Microsoft Corporation") && Is(model, "Virtual Machine")
            || Is(manufacturer, "innotek GmbH", "Oracle Corporation") && Is(model, "VirtualBox")
            || Is(manufacturer, "QEMU") && Is(model, "Standard PC (Q35 + ICH9, 2009)", "Standard PC (i440FX + PIIX, 1996)", "KVM", "Virtual Machine")
            || Is(manufacturer, "Xen") && Is(model, "HVM domU", "Xen")
            || Is(manufacturer, "Parallels Software International Inc.", "Parallels International GmbH") && Is(model, "Parallels Virtual Platform", "Parallels ARM Virtual Machine");
    }
}
