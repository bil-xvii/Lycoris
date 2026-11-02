using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace Monolith.Core.Security;

/// <summary>XML from outside (vendor answers, cached copies in the user's folder): no DTD (no entity bombs, no external files), no resolver, bounded size.</summary>
public static class SafeXml
{
    public const int MaxCharacters = 16 * 1024 * 1024;

    public static XmlReaderSettings Settings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxCharacters,
        MaxCharactersFromEntities = 0,
    };

    /// <summary>Parses XML text. Throws <see cref="XmlException"/> for a DTD, a malformed or an oversize document.</summary>
    public static XDocument Parse(string xml)
    {
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, Settings());
        return XDocument.Load(reader);
    }
}
