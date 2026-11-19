using Lycoris.Modules.Apps;
using Lycoris.Modules.Games.Sources;
using Lycoris.Modules.Startup;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Lycoris.Modules.Removal;

internal sealed record PackagedRemovalSnapshot(IReadOnlyList<RemovalRow> Rows, bool Complete);

/// <summary>Reads the desktop account's installed Windows packages and their dependency metadata.</summary>
internal static class PackagedRemovalInventory
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly PackagedRemovalSnapshot Unavailable = new([], false);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private const int MaxPackages = 4096;
    private const int MaxDependencyReferences = 16384;

    internal static async Task<PackagedRemovalSnapshot> ReadAsync(string? sid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sid) || !Gate.Wait(0, ct)) return Unavailable;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Limit);
        var token = deadline.Token;
        Task<PackagedRemovalSnapshot> query;
        try
        {
            query = StaWorker.Run(() =>
            {
                try { return Read(sid, token); }
                catch (SystemException) { return Unavailable; }
                finally { Gate.Release(); }
            }, "Lycoris installed Windows packages");
        }
        catch (SystemException)
        {
            Gate.Release();
            return Unavailable;
        }

        try { return await query.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Unavailable; }
    }

    private sealed record PackageEntry(string FullName, string Name, string? Publisher, string Version, bool Keep);

    private static PackagedRemovalSnapshot Read(string sid, CancellationToken ct)
    {
        var current = CurrentFamily();
        var entries = new Dictionary<string, PackageEntry>(StringComparer.OrdinalIgnoreCase);
        var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packages = new PackageManager().FindPackagesForUser(sid);
        var complete = true;
        var references = 0;
        var visited = 0;
        foreach (var package in packages)
        {
            ct.ThrowIfCancellationRequested();
            if (visited++ >= MaxPackages)
            {
                complete = false;
                break;
            }

            try
            {
                var id = package.Id;
                var version = id.Version;
                var keep = IsManagementPackage(id.FamilyName, current) || IsGamingInfrastructureFamily(id.FamilyName) || package.SignatureKind == PackageSignatureKind.System ||
                    package.IsFramework || package.IsResourcePackage || package.IsBundle || package.IsOptional;
                var name = DisplayText(() => package.DisplayName) ?? id.Name;
                var publisher = DisplayText(() => package.PublisherDisplayName) ?? UninstallEntries.InventoryText(id.Publisher);
                entries[id.FullName] = new(id.FullName, name, publisher,
                    $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}", keep);

                foreach (var dependency in package.Dependencies)
                {
                    if (references++ >= MaxDependencyReferences)
                    {
                        complete = false;
                        break;
                    }
                    dependencies.Add(dependency.Id.FullName);
                }
            }
            catch (SystemException) { complete = false; }
        }

        var rows = new List<RemovalRow>(entries.Count);
        foreach (var entry in entries.Values)
        {
            var keep = entry.Keep || dependencies.Contains(entry.FullName);
            rows.Add(new RemovalRow($"msix:{entry.FullName}", entry.Name, entry.Publisher, entry.Version,
                Loc.T("Пакет Windows · пользователь рабочего стола"),
                keep ? RemovalDecision.Keep : RemovalDecision.Review,
                keep ? Loc.T("Этот пакет обеспечивает работу Windows, других приложений или управления программами.")
                    : Loc.T("Проверьте варианты удаления и сохранения данных в параметрах Windows.")));
        }
        return new(rows.AsReadOnly(), complete);
    }

    private static string? DisplayText(Func<string> read)
    {
        try { return UninstallEntries.InventoryText(read()); }
        catch (SystemException) { return null; }
    }

    private static string? CurrentFamily()
    {
        try { return Package.Current.Id.FamilyName; }
        catch (SystemException) { return null; } // This application normally runs unpackaged.
    }

    private static readonly HashSet<string> GamingInfrastructureFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.GamingApp_8wekyb3d8bbwe", "Microsoft.GamingServices_8wekyb3d8bbwe",
        "Microsoft.XboxApp_8wekyb3d8bbwe", "Microsoft.XboxGamingOverlay_8wekyb3d8bbwe",
        "Microsoft.XboxGameOverlay_8wekyb3d8bbwe", "Microsoft.XboxIdentityProvider_8wekyb3d8bbwe",
        "Microsoft.Xbox.TCUI_8wekyb3d8bbwe", "Microsoft.XboxSpeechToTextOverlay_8wekyb3d8bbwe",
    };

    internal static bool IsGamingInfrastructureFamily(string? family) =>
        family is not null && GamingInfrastructureFamilies.Contains(family);

    private static bool IsManagementPackage(string family, string? current) =>
        string.Equals(family, current, StringComparison.OrdinalIgnoreCase) ||
        family is UnelevatedWinget.AppInstallerFamily or "Microsoft.Winget.Source_8wekyb3d8bbwe" or
            "Microsoft.WindowsStore_8wekyb3d8bbwe" or "Microsoft.StorePurchaseApp_8wekyb3d8bbwe";
}
