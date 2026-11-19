using System.Text.Json;
using Lycoris.Core;
using Lycoris.Modules.Apps;
using Lycoris.Modules.Games.Sources;
using Lycoris.Modules.Startup;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Lycoris.Modules.Removal;

internal sealed record PackagedRemovalSnapshot(IReadOnlyList<RemovalRow> Rows, bool Complete)
{
    internal IReadOnlyList<InstalledPackageRegistration> Registrations { get; init; } = [];
}

internal sealed record InstalledPackageRegistration(string FullName, string Family, string Name, string? Publisher,
    string Version, bool Protected, bool StoreSigned, IReadOnlyList<string> Dependencies)
{
    public PackagedRegistrationKind Kind { get; init; }
    public bool HasAppListEntry { get; init; }
}

internal enum PackagedRegistrationKind { Main, Bundle, Resource, Other }

internal sealed record AppxRemovalRegistration(string PackageFullName, string PackageFamilyName, bool? NonRemovable,
    string[] Dependencies);

internal sealed record PackagedUninstallSnapshot(string UserSid, PackagedRemovalSnapshot Native,
    IReadOnlyList<AppxRemovalRegistration> Appx);

/// <summary>Reads the desktop account's installed Windows packages and their dependency metadata.</summary>
internal static class PackagedRemovalInventory
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly PackagedRemovalSnapshot Unavailable = new([], false);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private const int MaxPackages = 4096;
    private const int MaxDependencyReferences = 16384;
    private const string AppxMetadataScript = """
        $ErrorActionPreference = 'Stop'
        $items = @(Appx\Get-AppxPackage -PackageTypeFilter Main,Framework,Resource,Bundle,Xap,Optional |
            Microsoft.PowerShell.Utility\Select-Object -First 4097 -Property PackageFullName,PackageFamilyName,NonRemovable,Dependencies)
        if ($items.Count -gt 4096) { exit 1 }
        $references = 0
        foreach ($item in $items) {
            $references += $item.Dependencies.Count
            if ($references -gt 16384) { exit 1 }
        }
        Microsoft.PowerShell.Utility\ConvertTo-Json -Compress -InputObject $items
        """;

    internal static async Task<PackagedUninstallSnapshot?> ReadForUninstallAsync(string sid, CancellationToken ct)
    {
        bool SameDesktop() => Elevation.InteractiveUserState == true && UserScope.Same(sid, UserScope.CurrentSid);
        if (!SameDesktop()) return null;
        var native = await ReadAsync(sid, ct, includeAllTypes: true).ConfigureAwait(false);
        if (!native.Complete || !SameDesktop()) return null;
        var appx = await ProcessRunner.RunPowerShellAsync(AppxMetadataScript,
            new ProcessRunOptions { Timeout = Limit, LogCommand = false }, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!appx.Success || !string.IsNullOrWhiteSpace(appx.StdErr) || !SameDesktop()) return null;
        var metadata = ParseAppxMetadata(appx.StdOut);
        return metadata is null ? null : new(sid, native, metadata);
    }

    internal static IReadOnlyList<AppxRemovalRegistration>? ParseAppxMetadata(string json)
    {
        if (json.Length > 2_000_000) return null;
        try
        {
            var entries = JsonSerializer.Deserialize<AppxRemovalRegistration[]>(json);
            if (entries is null || entries.Length > MaxPackages || entries.Any(entry => entry is null
                || !IsFullName(entry.PackageFullName) || !IsFamily(entry.PackageFamilyName)
                || entry.Dependencies is null || entry.Dependencies.Any(dependency => !IsFullName(dependency)))
                || entries.Sum(entry => entry.Dependencies.Length) > MaxDependencyReferences
                || entries.Select(entry => entry.PackageFullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length)
                return null;
            return Array.AsReadOnly(entries);
        }
        catch (JsonException) { return null; }
    }

    private static bool IsIdentityCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_';

    private static bool IsFamily(string? value) => value is { Length: > 0 and <= 255 } && value.All(IsIdentityCharacter);

    private static bool IsFullName(string? value) => value is { Length: > 0 and <= 255 } &&
        (value.All(IsIdentityCharacter) || (value.Contains("_~_", StringComparison.Ordinal) &&
            value.Count(c => c == '~') == 1 && value.All(c => c == '~' || IsIdentityCharacter(c))));

    internal static Task<PackagedRemovalSnapshot> ReadAsync(string? sid, CancellationToken ct) => ReadAsync(sid, ct, false);

    private static async Task<PackagedRemovalSnapshot> ReadAsync(string? sid, CancellationToken ct, bool includeAllTypes)
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
                try { return Read(sid, token, includeAllTypes); }
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

    private static PackagedRemovalSnapshot Read(string sid, CancellationToken ct, bool includeAllTypes)
    {
        var current = CurrentFamily();
        var entries = new Dictionary<string, InstalledPackageRegistration>(StringComparer.OrdinalIgnoreCase);
        var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manager = new PackageManager();
        var mainPackages = includeAllTypes
            ? manager.FindPackagesForUserWithPackageTypes(sid, PackageTypes.Main).Take(MaxPackages + 1)
                .Select(package => package.Id.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        var packages = includeAllTypes
            ? manager.FindPackagesForUserWithPackageTypes(sid, PackageTypes.All)
            : manager.FindPackagesForUser(sid);
        var complete = mainPackages is null || mainPackages.Count <= MaxPackages;
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
                var signature = package.SignatureKind;
                var protectedPackage = IsProtectedFamily(id.FamilyName, current) || signature == PackageSignatureKind.System ||
                    package.IsFramework || package.IsOptional;
                var kind = package.IsFramework || package.IsOptional ? PackagedRegistrationKind.Other
                    : package.IsResourcePackage ? PackagedRegistrationKind.Resource
                    : package.IsBundle ? PackagedRegistrationKind.Bundle
                    : mainPackages?.Contains(id.FullName) == false ? PackagedRegistrationKind.Other
                    : PackagedRegistrationKind.Main;
                var name = DisplayText(() => package.DisplayName) ?? id.Name;
                var publisher = DisplayText(() => package.PublisherDisplayName) ?? UninstallEntries.InventoryText(id.Publisher);
                var appEntry = includeAllTypes && kind == PackagedRegistrationKind.Main && !protectedPackage &&
                    signature == PackageSignatureKind.Store && HasAppListEntry(package);
                var required = new List<string>();
                foreach (var dependency in package.Dependencies)
                {
                    if (references++ >= MaxDependencyReferences)
                    {
                        complete = false;
                        break;
                    }
                    var fullName = dependency.Id.FullName;
                    if (!IsFullName(fullName)) complete = false;
                    dependencies.Add(fullName);
                    required.Add(fullName);
                }
                if (!IsFullName(id.FullName) || !IsFamily(id.FamilyName) ||
                    !entries.TryAdd(id.FullName, new(id.FullName, id.FamilyName, name, publisher,
                        $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}",
                        protectedPackage, signature == PackageSignatureKind.Store, required.AsReadOnly())
                        { Kind = kind, HasAppListEntry = appEntry }))
                    complete = false;
            }
            catch (SystemException) { complete = false; }
        }

        if (mainPackages is not null && !mainPackages.IsSubsetOf(entries.Keys)) complete = false;
        var rows = new List<RemovalRow>(entries.Count);
        foreach (var entry in entries.Values)
        {
            var keep = entry.Protected || entry.Kind != PackagedRegistrationKind.Main || dependencies.Contains(entry.FullName);
            rows.Add(new RemovalRow($"msix:{entry.FullName}", entry.Name, entry.Publisher, entry.Version,
                Loc.T("Пакет Windows · пользователь рабочего стола"),
                keep ? RemovalDecision.Keep : RemovalDecision.Review,
                keep ? Loc.T("Этот пакет обеспечивает работу Windows, других приложений или управления программами.")
                    : Loc.T("Проверьте варианты удаления и сохранения данных в параметрах Windows.")));
        }
        return new(rows.AsReadOnly(), complete) { Registrations = Array.AsReadOnly(entries.Values.ToArray()) };
    }

    private static string? DisplayText(Func<string> read)
    {
        try { return UninstallEntries.InventoryText(read()); }
        catch (SystemException) { return null; }
    }

    private static bool HasAppListEntry(Package package)
    {
        try { return package.GetAppListEntries().Count > 0; }
        catch (SystemException) { return false; }
    }

    private static string? CurrentFamily()
    {
        try { return Package.Current.Id.FamilyName; }
        catch (SystemException) { return null; } // This application normally runs unpackaged.
    }

    internal static bool IsProtectedFamily(string family, string? current) =>
        string.Equals(family, current, StringComparison.OrdinalIgnoreCase) ||
        family.StartsWith("Lycoris_", StringComparison.OrdinalIgnoreCase) ||
        InfrastructureFamilies.Contains(family) ||
        (family.EndsWith("_8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase) &&
            MicrosoftInfrastructurePrefixes.Any(prefix => family.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

    private static readonly HashSet<string> InfrastructureFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        UnelevatedWinget.AppInstallerFamily, "Microsoft.Winget.Source_8wekyb3d8bbwe",
        "Microsoft.WindowsStore_8wekyb3d8bbwe", "Microsoft.StorePurchaseApp_8wekyb3d8bbwe",
        "NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj",
        "RealtekSemiconductorCorp.RealtekAudioControl_dt26b99r8h8gj",
        "Microsoft.ApplicationCompatibilityEnhancements_8wekyb3d8bbwe", "MicrosoftWindows.Client.WebExperience_cw5n1h2txyewy",
        "Microsoft.GamingServices_8wekyb3d8bbwe", "Microsoft.XboxIdentityProvider_8wekyb3d8bbwe",
        "Microsoft.Xbox.TCUI_8wekyb3d8bbwe", "Microsoft.XboxSpeechToTextOverlay_8wekyb3d8bbwe",
    };

    private static readonly string[] MicrosoftInfrastructurePrefixes =
    {
        "Microsoft.LanguageExperiencePack", "Microsoft.WindowsAppRuntime.", "MicrosoftCorporationII.WindowsAppRuntime.",
        "MicrosoftCorporationII.WinAppRuntime.", "Microsoft.WinAppRuntime.", "Microsoft.ProjectReunion.",
    };
}
