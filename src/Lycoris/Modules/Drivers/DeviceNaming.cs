using System.Text.RegularExpressions;

namespace Lycoris.Modules.Drivers;

/// <summary>Human-readable names for device classes and hardware vendors.</summary>
internal static partial class DeviceNaming
{
    /// <summary>Device setup classes as Device Manager shows them on Russian Windows.</summary>
    private static readonly Dictionary<string, string> ClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["System"] = Loc.T("Системные устройства"),
        ["Display"] = Loc.T("Видеоадаптеры"),
        ["Net"] = Loc.T("Сетевые адаптеры"),
        ["NetClient"] = Loc.T("Сетевые клиенты"),
        ["NetService"] = Loc.T("Сетевые службы"),
        ["NetTrans"] = Loc.T("Сетевые протоколы"),
        ["Media"] = Loc.T("Звук, видео и игровые устройства"),
        ["AudioEndpoint"] = Loc.T("Аудиовходы и аудиовыходы"),
        ["AudioProcessingObject"] = Loc.T("Обработка звука"),
        ["MidiEndpoint"] = Loc.T("MIDI-устройства"),
        ["USB"] = Loc.T("Контроллеры USB"),
        ["USBDevice"] = Loc.T("USB-устройства"),
        ["HIDClass"] = Loc.T("Устройства HID"),
        ["Keyboard"] = Loc.T("Клавиатуры"),
        ["Mouse"] = Loc.T("Мыши"),
        ["Bluetooth"] = "Bluetooth",
        ["Monitor"] = Loc.T("Мониторы"),
        ["DiskDrive"] = Loc.T("Дисковые устройства"),
        ["SCSIAdapter"] = Loc.T("Контроллеры запоминающих устройств"),
        ["HDC"] = Loc.T("Контроллеры IDE ATA/ATAPI"),
        ["CDROM"] = Loc.T("DVD-дисководы"),
        ["Processor"] = Loc.T("Процессоры"),
        ["Volume"] = Loc.T("Тома"),
        ["VolumeSnapshot"] = Loc.T("Теневые копии томов"),
        ["SoftwareDevice"] = Loc.T("Программные устройства"),
        ["SoftwareComponent"] = Loc.T("Программные компоненты"),
        ["Printer"] = Loc.T("Принтеры"),
        ["PrintQueue"] = Loc.T("Очереди печати"),
        ["WPD"] = Loc.T("Переносные устройства"),
        ["Camera"] = Loc.T("Камеры"),
        ["Image"] = Loc.T("Устройства обработки изображений"),
        ["Firmware"] = Loc.T("Встроенное ПО"),
        ["SecurityDevices"] = Loc.T("Устройства безопасности"),
        ["Computer"] = Loc.T("Компьютер"),
        ["Biometric"] = Loc.T("Биометрические устройства"),
        ["Battery"] = Loc.T("Батареи"),
        ["Ports"] = Loc.T("Порты (COM и LPT)"),
        ["Sensor"] = Loc.T("Датчики"),
        ["SmartCardReader"] = Loc.T("Устройства чтения смарт-карт"),
        ["Extension"] = Loc.T("Расширения драйверов"),
        ["OtherHardware"] = Loc.T("Прочее оборудование"),
        ["Net Adapter"] = Loc.T("Сетевые адаптеры"),
        ["UCM"] = Loc.T("Контроллеры USB-C"),
        ["Infrared"] = Loc.T("Инфракрасные устройства"),
        ["MTD"] = Loc.T("Устройства памяти"),
        ["Modem"] = Loc.T("Модемы"),
        ["MultiFunction"] = Loc.T("Многофункциональные адаптеры"),
        ["SBP2"] = "IEEE 1394",
        ["1394"] = Loc.T("Контроллеры IEEE 1394"),
    };

    /// <summary>"SYSTEM" → "Системные устройства"; unknown classes are returned as-is; empty → "Без класса".</summary>
    public static string ClassName(string? setupClass)
    {
        if (string.IsNullOrWhiteSpace(setupClass)) return Loc.T("Без класса");
        var key = setupClass.Trim();
        return ClassNames.TryGetValue(key, out var ru) ? ru : key;
    }

    /// <summary>PCI / USB vendor ids → vendor (research §2 "local VEN/VID table").</summary>
    private static readonly Dictionary<string, string> Vendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["10DE"] = "NVIDIA",
        ["1002"] = "AMD",
        ["1022"] = "AMD",
        ["AMDI"] = "AMD",
        ["8086"] = "Intel",
        ["8087"] = "Intel",
        ["10EC"] = "Realtek",
        ["0BDA"] = "Realtek",
        ["14E4"] = "Broadcom",
        ["168C"] = "Qualcomm",
        ["17CB"] = "Qualcomm",
        ["1B21"] = "ASMedia",
        ["1462"] = "MSI",
    };

    [GeneratedRegex(@"(?:VEN|VID)_([0-9A-Z]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VendorIdRegex();

    /// <summary>Raw vendor id from a hardware id: "PCI\VEN_10DE&amp;DEV_2C05" → "10DE", "USB\VID_046D&amp;PID_C339" → "046D".</summary>
    public static string? VendorId(string? hardwareId)
    {
        if (string.IsNullOrWhiteSpace(hardwareId)) return null;
        var m = VendorIdRegex().Match(hardwareId);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>Vendor name for a hardware id, or the raw "VEN_xxxx" when it is not in the table; null when there is no id.</summary>
    public static string? VendorName(string? hardwareId)
    {
        var id = VendorId(hardwareId);
        if (id is null) return null;
        if (Vendors.TryGetValue(id, out var name)) return name;
        var prefix = hardwareId!.Contains("VID_", StringComparison.OrdinalIgnoreCase) ? "VID_" : "VEN_";
        return prefix + id;
    }

    /// <summary>The most specific hardware id (Windows lists them from most to least specific).</summary>
    public static string? PrimaryHardwareId(IEnumerable<string>? ids) =>
        ids?.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim();
}
