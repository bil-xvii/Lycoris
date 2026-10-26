using System.Text.RegularExpressions;

namespace Monolith.Modules.Drivers;

/// <summary>Human-readable names for device classes and hardware vendors.</summary>
internal static partial class DeviceNaming
{
    /// <summary>Device setup classes as Device Manager shows them on Russian Windows.</summary>
    private static readonly Dictionary<string, string> ClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["System"] = "Системные устройства",
        ["Display"] = "Видеоадаптеры",
        ["Net"] = "Сетевые адаптеры",
        ["NetClient"] = "Сетевые клиенты",
        ["NetService"] = "Сетевые службы",
        ["NetTrans"] = "Сетевые протоколы",
        ["Media"] = "Звук, видео и игровые устройства",
        ["AudioEndpoint"] = "Аудиовходы и аудиовыходы",
        ["AudioProcessingObject"] = "Обработка звука",
        ["MidiEndpoint"] = "MIDI-устройства",
        ["USB"] = "Контроллеры USB",
        ["USBDevice"] = "USB-устройства",
        ["HIDClass"] = "Устройства HID",
        ["Keyboard"] = "Клавиатуры",
        ["Mouse"] = "Мыши",
        ["Bluetooth"] = "Bluetooth",
        ["Monitor"] = "Мониторы",
        ["DiskDrive"] = "Дисковые устройства",
        ["SCSIAdapter"] = "Контроллеры запоминающих устройств",
        ["HDC"] = "Контроллеры IDE ATA/ATAPI",
        ["CDROM"] = "DVD-дисководы",
        ["Processor"] = "Процессоры",
        ["Volume"] = "Тома",
        ["VolumeSnapshot"] = "Теневые копии томов",
        ["SoftwareDevice"] = "Программные устройства",
        ["SoftwareComponent"] = "Программные компоненты",
        ["Printer"] = "Принтеры",
        ["PrintQueue"] = "Очереди печати",
        ["WPD"] = "Переносные устройства",
        ["Camera"] = "Камеры",
        ["Image"] = "Устройства обработки изображений",
        ["Firmware"] = "Встроенное ПО",
        ["SecurityDevices"] = "Устройства безопасности",
        ["Computer"] = "Компьютер",
        ["Biometric"] = "Биометрические устройства",
        ["Battery"] = "Батареи",
        ["Ports"] = "Порты (COM и LPT)",
        ["Sensor"] = "Датчики",
        ["SmartCardReader"] = "Устройства чтения смарт-карт",
        ["Extension"] = "Расширения драйверов",
        ["OtherHardware"] = "Прочее оборудование",
        ["Net Adapter"] = "Сетевые адаптеры",
        ["UCM"] = "Контроллеры USB-C",
        ["Infrared"] = "Инфракрасные устройства",
        ["MTD"] = "Устройства памяти",
        ["Modem"] = "Модемы",
        ["MultiFunction"] = "Многофункциональные адаптеры",
        ["SBP2"] = "IEEE 1394",
        ["1394"] = "Контроллеры IEEE 1394",
    };

    /// <summary>"SYSTEM" → "Системные устройства"; unknown classes are returned as-is; empty → "Без класса".</summary>
    public static string ClassName(string? setupClass)
    {
        if (string.IsNullOrWhiteSpace(setupClass)) return "Без класса";
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
