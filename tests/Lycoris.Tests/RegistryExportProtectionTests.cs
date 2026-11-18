using System.IO;
using Lycoris.Core;
using Microsoft.Win32;

namespace Lycoris.Tests;

[Collection("Registry")]
public sealed class RegistryExportProtectionTests
{
    [Fact]
    public async Task ExportOfExistingSubtreeLeavesNoOutputFileOrDirectory()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var path = $@"Software\Lycoris-Tests-{suffix}";
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"lycoris-export-test-{suffix}");
        var outputFile = Path.Combine(outputDirectory, "fixture.reg");

        try
        {
            using (var root = Registry.CurrentUser.CreateSubKey(path))
            {
                root.SetValue("Fixture", "public-marker", RegistryValueKind.String);
                using var child = root.CreateSubKey("Child");
                child.SetValue("Fixture", "nested-marker", RegistryValueKind.String);
            }

            var exported = await RegistryHelper.ExportKeyAsync(RegHive.HKCU, path, outputFile);

            Assert.Equal((false, false, false), (exported, File.Exists(outputFile), Directory.Exists(outputDirectory)));
            using var original = Registry.CurrentUser.OpenSubKey(path + @"\Child");
            Assert.Equal("nested-marker", original?.GetValue("Fixture"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            if (File.Exists(outputFile)) File.Delete(outputFile);
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory);
        }
    }
}
