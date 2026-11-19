using System.IO;
using Microsoft.Win32;
using Lycoris.Modules.Games.Sources;
using Lycoris.Modules.Removal;

namespace Lycoris.Tests;

[Collection("Registry")]
public sealed class RemovalProgramInspectorTests
{
    [Theory]
    [InlineData("Indigo Sound Mixer", "IndigoMixer", "IndigoMixer", "Trillium Labs")]
    [InlineData("Holloway Diagram Studio 3.2.1", "Holloway Diagram Studio", "SketchPad", "Holloway Software")]
    [InlineData("Northwind Archive Station", "Northwind Archive Station", "UnpackGui", "Cairn Studios")]
    public async Task Unfamiliar_graphical_app_with_bound_facts_is_eligible(string label, string folder, string exe, string publisher)
    {
        var fixture = new Fixture();
        var app = fixture.Add(label, folder, exe, publisher);
        var inspector = fixture.Inspector();
        var result = Assert.Single(await inspector.InspectAsync(fixture.Inventory, CancellationToken.None));
        Assert.Equal(RemovalDecision.ConsiderRemoval, result.Row.Decision);
        Assert.Equal(app, result.Registration);
        Assert.True(inspector.Revalidate(app, Assert.IsAssignableFrom<ProgramRemovalProof>(result.Proof), CancellationToken.None));
        Assert.Equal(2, fixture.Captures);
    }

    [Theory]
    [InlineData("missing manual entry", RemovalDecision.Review)]
    [InlineData("untrusted file", RemovalDecision.Review)]
    [InlineData("signer mismatch", RemovalDecision.Review)]
    [InlineData("system signer", RemovalDecision.Keep)]
    [InlineData("security role", RemovalDecision.Keep)]
    [InlineData("signed backup role", RemovalDecision.Review)]
    [InlineData("running", RemovalDecision.Review)]
    [InlineData("registered service", RemovalDecision.Keep)]
    [InlineData("external driver", RemovalDecision.Keep)]
    [InlineData("default browser", RemovalDecision.Keep)]
    [InlineData("path consumer", RemovalDecision.Review)]
    [InlineData("shared registration", RemovalDecision.Review)]
    [InlineData("incomplete inventory", RemovalDecision.Review)]
    [InlineData("incomplete processes", RemovalDecision.Review)]
    [InlineData("missing uninstaller", RemovalDecision.Review)]
    [InlineData("uninstaller signer mismatch", RemovalDecision.Review)]
    public async Task Conflicting_missing_or_incomplete_evidence_prevents_proof(string conflict, RemovalDecision expected)
    {
        var fixture = new Fixture();
        var app = fixture.Add("Indigo Journal");
        var path = fixture.PathOf(app);
        var facts = fixture.Facts;
        switch (conflict)
        {
            case "missing manual entry": fixture.Facts = new(); break;
            case "untrusted file": fixture.Files.Remove(path); break;
            case "signer mismatch": fixture.Files[path] = fixture.Files[path] with { Signer = "Someone Else" }; break;
            case "system signer": fixture.Files[path] = fixture.Files[path] with { Signer = "Microsoft Corporation" }; break;
            case "security role": fixture.Files[path] = fixture.Files[path] with { OriginalName = "endpoint-protection.exe" }; break;
            case "signed backup role": fixture.Files[path] = fixture.Files[path] with { Description = "Backup client" }; break;
            case "running": facts.Running.Add(Path.Join(app.InstallLocation!, @"bin\Indigo.exe")); break;
            case "registered service": facts.Services.Add(Path.Join(app.InstallLocation!, @"bin\Worker.exe")); break;
            case "external driver":
                facts.Services.Add(@"C:\Windows\System32\drivers\external.sys");
                facts.Services.Add(app.InstallLocation!); // The driver's SDK registration explicitly links the install root.
                break;
            case "default browser": facts.DefaultBrowser.Add(path); break;
            case "path consumer": facts.Consumers.Add(Path.Join(app.InstallLocation!, "bin")); break;
            case "shared registration":
                fixture.Entries.Add(app with { KeyName = "HiddenDependency", DisplayName = "—", HasDisplayIdentity = false });
                break;
            case "incomplete inventory": fixture.State = UninstallReadState.Partial; break;
            case "incomplete processes": facts.ProcessesComplete = false; break;
            case "missing uninstaller": fixture.Uninstallers.Clear(); break;
            case "uninstaller signer mismatch":
                fixture.Uninstallers[app.UninstallExecutable!] = new("Unrelated signed publisher", "other hash");
                break;
        }

        var result = Assert.Single(await fixture.Inspector().InspectAsync(fixture.Inventory, CancellationToken.None));
        Assert.Equal(expected, result.Row.Decision);
        Assert.Null(result.Proof);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("binding")]
    [InlineData("uninstaller")]
    [InlineData("service")]
    [InlineData("consumer")]
    [InlineData("active process")]
    [InlineData("other registration")]
    [InlineData("fresh registration")]
    [InlineData("another inspector")]
    public async Task Revalidation_rechecks_the_bound_file_and_system_facts(string change)
    {
        var fixture = new Fixture();
        var app = fixture.Add("Indigo Journal");
        var path = fixture.PathOf(app);
        var inspector = fixture.Inspector();
        var result = Assert.Single(await inspector.InspectAsync(fixture.Inventory, CancellationToken.None));
        var proof = Assert.IsAssignableFrom<ProgramRemovalProof>(result.Proof);
        switch (change)
        {
            case "file": fixture.Files[path] = fixture.Files[path] with { Hash = "new hash" }; break;
            case "binding":
                fixture.Facts = new();
                fixture.Facts.AddLaunch(new(path, app.DisplayName, "a different shortcut", UserScope: false));
                break;
            case "uninstaller":
                fixture.Uninstallers[app.UninstallExecutable!] = fixture.Uninstallers[app.UninstallExecutable!] with { Hash = "new hash" };
                break;
            case "service": fixture.Facts.Services.Add(path); break;
            case "consumer": fixture.Facts.Consumers.Add(path); break;
            case "active process": fixture.Facts.Running.Add(path); break;
            case "other registration": fixture.Entries.Add(app with { KeyName = "NewDependency", DisplayName = "New Dependency" }); break;
            case "fresh registration": fixture.Entries[0] = app with { UninstallFingerprint = "new fingerprint" }; break;
            case "another inspector": inspector = fixture.Inspector(); break;
        }

        Assert.False(inspector.Revalidate(app, proof, CancellationToken.None));
    }

    [Fact]
    public async Task All_entries_are_checked_after_128_executable_proofs()
    {
        var fixture = new Fixture();
        for (var i = 0; i < 129; i++) fixture.Add($"Example Application {i:000}");
        var result = await fixture.Inspector().InspectAsync(fixture.Inventory, CancellationToken.None);
        Assert.Equal(129, result.Count);
        Assert.All(result, entry => Assert.Equal(RemovalDecision.ConsiderRemoval, entry.Row.Decision));
        Assert.Equal(129, fixture.ExecutableReads);
        Assert.Equal(1, fixture.Captures);
    }

    [Fact]
    public async Task Advertised_shortcut_requires_the_same_standard_msi_product_code()
    {
        var fixture = new Fixture();
        var app = fixture.Add("Gallery Studio");
        var code = Guid.NewGuid();
        app = app with { KeyName = code.ToString("B"), WindowsInstaller = true, IsStandardMsiHook = true, UninstallExecutable = null };
        fixture.Entries[0] = app;
        var path = fixture.PathOf(app);
        fixture.Facts = new();
        fixture.Facts.AddLaunch(new(path, "Gallery", "advertised install", UserScope: false, ProductCode: code));

        var inspector = fixture.Inspector();
        var result = Assert.Single(await inspector.InspectAsync(fixture.Inventory, CancellationToken.None));
        Assert.Equal(RemovalDecision.ConsiderRemoval, result.Row.Decision);
        fixture.Entries[0] = app with { IsStandardMsiHook = false };
        Assert.Null(Assert.Single(await inspector.InspectAsync(fixture.Inventory, CancellationToken.None)).Proof);
        fixture.Entries[0] = app with { KeyName = Guid.NewGuid().ToString("B") };
        Assert.Null(Assert.Single(await inspector.InspectAsync(fixture.Inventory, CancellationToken.None)).Proof);
    }

    [Theory]
    [InlineData(0, "Vmmem", true)]
    [InlineData(0, "VmmemWSL", true)]
    [InlineData(1, "VmmemWSL", false)]
    [InlineData(0, "VmmemWSLHost", false)]
    [InlineData(0, "A background program", false)]
    public void Only_exact_session_zero_kernel_pseudo_processes_need_no_path(int session, string name, bool expected) =>
        Assert.Equal(expected, RemovalSoftwareWindows.IsKernelPseudoProcess(session, name));

    [Fact]
    public async Task Uninstaller_with_an_expandable_command_cannot_prove_the_literal_hook()
    {
        var fixture = new Fixture();
        var app = fixture.Add("Indigo Journal");
        var temp = $@"Software\Lycoris-Tests\InstallerEvidence-{Guid.NewGuid():N}";
        try
        {
            using var registry = Registry.CurrentUser.CreateSubKey(temp);
            registry.SetValue("DisplayName", app.DisplayName);
            registry.SetValue("Publisher", app.Publisher!);
            registry.SetValue("DisplayVersion", app.Version!);
            registry.SetValue("InstallLocation", app.InstallLocation!);
            var relativePath = Path.GetRelativePath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), app.UninstallExecutable!);
            registry.SetValue("UninstallString", $"\"%ProgramFiles%\\{relativePath}\"", RegistryValueKind.ExpandString);
            var ambiguous = Assert.IsType<UninstallRegistration>(UninstallEntries.ReadRegistration(registry, app.KeyName, app.Scope));
            Assert.Null(ambiguous.UninstallExecutable);
            Assert.NotNull(ambiguous.UninstallFingerprint);
            fixture.Entries[0] = ambiguous;
            var inspector = fixture.Inspector();
            Assert.Null(Assert.Single(await inspector.InspectAsync(fixture.Inventory, CancellationToken.None)).Proof);

            registry.SetValue("UninstallString", $"\"{app.UninstallExecutable}\"", RegistryValueKind.ExpandString);
            var literal = Assert.IsType<UninstallRegistration>(UninstallEntries.ReadRegistration(registry, app.KeyName, app.Scope));
            Assert.Equal(app.UninstallExecutable, literal.UninstallExecutable);
            Assert.NotEqual(ambiguous.UninstallFingerprint, literal.UninstallFingerprint);
            fixture.Entries[0] = literal;
            Assert.NotNull(Assert.Single(await inspector.InspectAsync(fixture.Inventory, CancellationToken.None)).Proof);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(temp, throwOnMissingSubKey: false); }
    }

    private sealed class Fixture
    {
        public List<UninstallRegistration> Entries { get; } = [];
        public Dictionary<string, RemovalExecutable> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, RemovalUninstaller> Uninstallers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public RemovalSoftwareSnapshot Facts { get; set; } = new();
        public UninstallReadState State { get; set; } = UninstallReadState.Complete;
        public UninstallInventory Inventory => new(Entries.ToArray(), State, DateTime.UtcNow);
        public int Captures { get; private set; }
        public int ExecutableReads { get; private set; }

        public UninstallRegistration Add(string displayName, string? folder = null, string? exe = null, string publisher = "Trillium Labs")
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sample Vendor", folder ?? displayName);
            var uninstall = Path.Join(root, "uninstall.exe");
            var entry = new UninstallRegistration(UninstallScope.Machine64, $"Product-{Entries.Count}", displayName, publisher,
                "3.2.1", false, false, false, true, "fingerprint")
            {
                InstallLocation = root, UninstallExecutable = uninstall, WindowsInstaller = false,
            };
            Entries.Add(entry);
            var target = Path.Join(root, $"{exe ?? "Application"}.exe");
            Files.Add(target, new(target, publisher, publisher, displayName, displayName, Path.GetFileName(target), "file hash"));
            Uninstallers.Add(uninstall, new(publisher, "uninstaller hash"));
            Facts.AddLaunch(new(target, displayName, $"manual-{Entries.Count}", UserScope: false));
            return entry;
        }

        public string PathOf(UninstallRegistration app) =>
            Assert.Single(Files.Keys, path => RemovalSoftwareEvidence.IsUnder(path, app.InstallLocation!));

        public ProgramRemovalInspector Inspector() => new(
            _ => { Captures++; return Facts; },
            (path, _) => { ExecutableReads++; return Files.GetValueOrDefault(path); },
            () => Inventory, (path, _) => Uninstallers.GetValueOrDefault(path));
    }
}
