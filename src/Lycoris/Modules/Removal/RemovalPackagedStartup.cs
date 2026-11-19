using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using Lycoris.Modules.Startup;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Lycoris.Modules.Removal;

internal static class RemovalPackagedStartup
{
    internal sealed record Proof(string PackageFullName, string Family, string Publisher, string ManifestHash,
        string ApplicationId, string Target, string TargetHash);
    internal readonly record struct Assessment(Proof? Package, bool Protected = false);
    private static readonly StringComparer Names = RemovalSoftwareEvidence.Paths;

    internal static string Unverified =>
        Loc.T("Не подтверждены зарегистрированный пакет, его подпись или точка ручного запуска.");

    public static Assessment Read(StartupEntry entry, CancellationToken ct)
    {
        if (entry.PackageFamilyName is not { } family || !PackagedStartup.IsJournalSafe(family, entry.Name)) return default;
        try
        {
            ct.ThrowIfCancellationRequested();
            var packageManager = new PackageManager();
            var main = packageManager.FindPackagesForUser(string.Empty, family)
                .Where(package => !package.IsBundle && !package.IsResourcePackage).Take(2).ToArray();
            if (main.Length != 1) return default;
            var package = main[0];
            var id = package.Id;
            if (!Names.Equals(id.FamilyName, family)) return default;
            var role = RemovalSoftwareEvidence.Role(Path.GetFileName(entry.TargetPath), id.Publisher,
                id.Name, entry.DisplayName, entry.Description, entry.Publisher);
            if (package.IsFramework || package.SignatureKind == PackageSignatureKind.System
                || role == RemovalSoftwareRole.Protected) return new(null, Protected: true);
            if (role == RemovalSoftwareRole.Sensitive || package.IsDevelopmentMode
                || package.SignatureKind != PackageSignatureKind.Store || !package.Status.VerifyIsOK()) return default;

            var root = RemovalSoftwareEvidence.LocalPath(package.InstalledLocation.Path);
            var windowsApps = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            if (root is null || !Names.Equals(root, Path.Join(windowsApps, id.FullName))) return default;
            using var manifest = RemovalSoftwareEvidence.OpenRegularFile(Path.Join(root, "AppxManifest.xml"), 2 * 1024 * 1024);
            if (manifest is null) return default;
            var declared = ReadManualEntry(manifest, root, entry.Name, id.Name, id.Publisher,
                $"{id.Version.Major}.{id.Version.Minor}.{id.Version.Build}.{id.Version.Revision}");
            if (declared is null || !Names.Equals(declared.Value.Target, RemovalSoftwareEvidence.LocalPath(entry.TargetPath)))
                return default;

            var target = declared.Value.Target;
            using var executable = RemovalSoftwareEvidence.OpenExecutable(target);
            if (executable is null || !RemovalSoftwareEvidence.IsGuiExecutable(executable)) return default;
            var version = FileVersionInfo.GetVersionInfo(target);
            role = RemovalSoftwareEvidence.Role(Path.GetFileName(target), id.Publisher, version.CompanyName,
                version.ProductName, version.OriginalFilename, version.FileDescription);
            if (role == RemovalSoftwareRole.Protected) return new(null, Protected: true);
            if (role == RemovalSoftwareRole.Sensitive) return default;
            // Store package identity and status come from the deployment API. The inner PE needn't have a separate signature.
            return new(new Proof(id.FullName, family, id.Publisher, RemovalSoftwareEvidence.Hash(manifest, ct),
                declared.Value.AppId, target, RemovalSoftwareEvidence.Hash(executable, ct)));
        }
        catch (Exception ex) when (RemovalSoftwareEvidence.ReadFailed(ex) || ex is COMException or XmlException or RemovalFileTooLargeException)
        {
            return default;
        }
    }

    internal static (string AppId, string Target)? ReadManualEntry(Stream manifest, string root, string taskId,
        string packageName, string publisher, string version)
    {
        using var reader = XmlReader.Create(manifest, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024,
        });
        var document = XDocument.Load(reader);
        var package = document.Root;
        if (package?.Name != XName.Get("Package", "http://schemas.microsoft.com/appx/manifest/foundation/windows10"))
            return null;
        var identity = package.Element(package.Name.Namespace + "Identity");
        if (!Names.Equals((string?)identity?.Attribute("Name"), packageName)
            || !Names.Equals((string?)identity?.Attribute("Publisher"), publisher)
            || !Names.Equals((string?)identity?.Attribute("Version"), version)) return null;

        var extensions = package.Descendants().Where(element => element.Name.LocalName == "Extension"
            && Names.Equals((string?)element.Attribute("Category"), "windows.startupTask"))
            .SelectMany(extension => extension.Elements().Where(task => task.Name.LocalName == "StartupTask"
                && Names.Equals((string?)task.Attribute("TaskId"), taskId)).Select(_ => extension)).Take(2).ToArray();
        if (extensions.Length != 1) return null;
        var declaration = extensions[0];
        var application = declaration.Ancestors(package.Name.Namespace + "Application").FirstOrDefault();
        var visual = application?.Elements().FirstOrDefault(element => element.Name.LocalName == "VisualElements");
        if (visual is null || (string?)visual.Attribute("AppListEntry") is not (null or "default")
            || (string?)application?.Attribute("Id") is not { Length: > 0 } appId)
            return null;

        var launch = ReadTarget((string?)application?.Attribute("Executable"));
        var startup = (string?)declaration.Attribute("Executable");
        if (launch is null || startup is not null && !Names.Equals(launch, ReadTarget(startup))
            || declaration.Attributes().Any(attribute => attribute.Name.LocalName is "Parameters" or "Arguments"
                && !string.IsNullOrWhiteSpace(attribute.Value))
            || (string?)declaration.Attribute("EntryPoint") is { } entryPoint
                && !Names.Equals(entryPoint, (string?)application?.Attribute("EntryPoint")))
            return null;

        if (application!.Descendants().Any(element => element.Name.LocalName == "Extension"
            && (string?)element.Attribute("Category") is "windows.service" or "windows.driver" or "windows.devicePortalProvider"))
            return null;
        return (appId, launch);

        string? ReadTarget(string? relative)
        {
            if (relative is null || Path.IsPathRooted(relative) || relative.StartsWith('/')) return null;
            var full = RemovalSoftwareEvidence.LocalPath(PackagedStartup.JoinPackagePath(root, relative));
            return full is not null && RemovalSoftwareEvidence.IsUnder(full, root) ? full : null;
        }
    }
}
