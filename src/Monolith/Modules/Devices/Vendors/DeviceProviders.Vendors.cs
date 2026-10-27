using Monolith.Modules.Devices.Vendors;

namespace Monolith.Modules.Devices;

public static partial class DeviceProviders
{
    /// <summary>Vendor enrichment: product names, receivers, vendor programs, inventory cards (runs after every provider).</summary>
    static partial void AddVendors(List<IDeviceEnricher> e) => e.Add(new VendorEnricher());
}
