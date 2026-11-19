using System.Text.Json;

namespace Lycoris.Modules.Removal;

/// <summary>Validates the source descriptor returned by WinGet's read-only source export.</summary>
internal static class ProgramRemovalExport
{
    internal static bool IsOfficialSource(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var source = doc.RootElement;
            return Equal(source, "Name", "winget") && Equal(source, "Type", "Microsoft.PreIndexed.Package") &&
                Equal(source, "Identifier", "Microsoft.Winget.Source_8wekyb3d8bbwe") &&
                Equal(source, "Arg", "https://cdn.winget.microsoft.com/cache");
        }
        catch (JsonException) { return false; }
    }

    private static bool Equal(JsonElement source, string key, string value) =>
        TryProperty(source, key, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() == value;

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
