using System.Xml;
using System.Xml.Linq;

namespace Cadence.Core.TaskXml;

/// <summary>Shared constants and the canonical-outer-XML helpers used for unmodeled elements.</summary>
internal static class XmlCanon
{
    public static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>
    /// Namespace-free, whitespace-free text of an element with attributes and children sorted by name,
    /// so that Task Scheduler's reordering of children does not change it. For triggers, defaults that
    /// Task Scheduler drops (Enabled true, StopAtDurationEnd false) are removed too.
    /// </summary>
    public static string Outer(XElement element, bool trigger)
    {
        var copy = Strip(element);
        if (trigger)
        {
            copy.Elements("Enabled").Where(e => e.Value == "true").Remove();
            copy.Elements("Repetition").Elements("StopAtDurationEnd").Where(e => e.Value == "false").Remove();
        }
        return copy.ToString(SaveOptions.DisableFormatting);
    }

    static XElement Strip(XElement e)
    {
        var c = new XElement(e.Name.LocalName);
        foreach (var a in e.Attributes().Where(a => !a.IsNamespaceDeclaration).OrderBy(a => a.Name.LocalName, StringComparer.Ordinal))
            c.SetAttributeValue(a.Name.LocalName, a.Value);
        var kids = e.Elements().Select(Strip).OrderBy(k => k.Name.LocalName, StringComparer.Ordinal).ToList();
        if (kids.Count > 0)
            foreach (var k in kids) c.Add(k);
        else
        {
            string v = e.Value.Trim();
            if (v.Length > 0) c.Add(v);
        }
        return c;
    }

    /// <summary>Inverse of <see cref="Outer"/>: parses the text and moves every element into the Task Scheduler namespace.</summary>
    public static XElement Import(string xml)
    {
        XElement e;
        try { e = XElement.Parse(xml); }
        catch (XmlException ex) { throw new TaskXmlException("A preserved element is not well-formed XML: " + ex.Message, ex); }
        foreach (var x in e.DescendantsAndSelf().ToList()) x.Name = Ns + x.Name.LocalName;
        return e;
    }
}
