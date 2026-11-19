using System.Text.Json;
using Lycoris.Core;

namespace Lycoris.Modules.Removal;

internal sealed record ProgramRemovalPackage(string Id, string Version, ProgramRemovalScope? ExportedScope);

/// <summary>Reads the WinGet JSON export without collapsing duplicate registrations or dropping source identity.</summary>
internal sealed class ProgramRemovalExport
{
    private readonly IReadOnlyList<ProgramRemovalPackage> _packages;

    private ProgramRemovalExport(List<ProgramRemovalPackage> packages) => _packages = packages.AsReadOnly();

    internal ProgramRemovalPackage? Match(string id, string version, ProgramRemovalScope scope)
    {
        var matches = _packages.Where(package => string.Equals(package.Id, id, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 && matches[0].Id == id && matches[0].Version == version &&
            (matches[0].ExportedScope is null || matches[0].ExportedScope == scope) ? matches[0] : null;
    }

    internal static bool IsOfficialSource(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return IsOfficialSource(doc.RootElement, "Arg");
        }
        catch (JsonException) { return false; }
    }

    internal static ProgramRemovalExport? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!TryProperty(doc.RootElement, "Sources", out var sources) || sources.ValueKind != JsonValueKind.Array ||
                sources.GetArrayLength() > 1) return null;
            var packages = new List<ProgramRemovalPackage>();
            foreach (var source in sources.EnumerateArray())
            {
                if (!TryProperty(source, "SourceDetails", out var details) || !IsOfficialSource(details, "Argument") ||
                    !TryProperty(source, "Packages", out var entries) || entries.ValueKind != JsonValueKind.Array) return null;
                foreach (var entry in entries.EnumerateArray())
                {
                    if (!TryProperty(entry, "PackageIdentifier", out var id) || id.ValueKind != JsonValueKind.String) return null;
                    var version = OptionalString(entry, "Version");
                    var scope = OptionalString(entry, "Scope");
                    if (!version.Valid || !scope.Valid) return null;
                    var exportedScope = scope.Value switch
                    {
                        "machine" => (ProgramRemovalScope?)ProgramRemovalScope.Machine,
                        "user" => ProgramRemovalScope.User,
                        _ => null,
                    };
                    if (scope.Value is not null && exportedScope is null) return null;
                    packages.Add(new(id.GetString()!, version.Value ?? "", exportedScope));
                }
            }
            return new(packages);
        }
        catch (JsonException) { return null; }
    }

    private static bool IsOfficialSource(JsonElement source, string argumentName) =>
        Equal(source, "Name", "winget") && Equal(source, "Type", "Microsoft.PreIndexed.Package") &&
        Equal(source, "Identifier", "Microsoft.Winget.Source_8wekyb3d8bbwe") &&
        Equal(source, argumentName, "https://cdn.winget.microsoft.com/cache");

    private static bool Equal(JsonElement source, string key, string value) =>
        TryProperty(source, key, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() == value;

    private static (bool Valid, string? Value) OptionalString(JsonElement element, string key)
    {
        if (!element.TryGetProperty(key, out _)) return (true, null);
        return TryProperty(element, key, out var value) && value.ValueKind == JsonValueKind.String
            ? (true, value.GetString()) : (false, null);
    }

    private static bool TryProperty(JsonElement element, string key, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in element.EnumerateObject())
        {
            if (!property.NameEquals(key)) continue;
            if (value.ValueKind != JsonValueKind.Undefined) return false;
            value = property.Value;
        }
        return value.ValueKind != JsonValueKind.Undefined;
    }
}
