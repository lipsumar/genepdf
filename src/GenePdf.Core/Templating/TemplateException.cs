using System.Xml;
using System.Xml.Linq;

namespace GenePdf.Templating;

/// <summary>An error in a template, located in a file (and line when known).</summary>
public sealed class TemplateException : Exception
{
    public TemplateException(string message, string? file = null, int? line = null, Exception? inner = null)
        : base(Format(message, file, line), inner)
    {
        Detail = message;
        File = file;
        Line = line;
    }

    public string Detail { get; }
    public string? File { get; }
    public int? Line { get; }

    public static TemplateException At(XObject? node, string message, Exception? inner = null)
    {
        var (file, line) = SourceInfo.Get(node);
        return new TemplateException(message, file, line, inner);
    }

    private static string Format(string message, string? file, int? line)
        => file is null ? message : line is null ? $"{file}: {message}" : $"{file}:{line}: {message}";
}

/// <summary>
/// Source location of elements. Stored as attributes (not annotations) because LINQ to XML drops
/// annotations and line info when nodes are cloned, which happens for every g:each iteration.
/// The attributes are stripped before output.
/// </summary>
public static class SourceInfo
{
    public static readonly XNamespace Ns = "urn:genepdf:source";
    private static readonly XName FileAttr = Ns + "file";
    private static readonly XName LineAttr = Ns + "line";

    public static void Stamp(XElement root, string file)
    {
        foreach (var e in root.DescendantsAndSelf())
        {
            e.SetAttributeValue(FileAttr, file);
            if (e is IXmlLineInfo li && li.HasLineInfo()) e.SetAttributeValue(LineAttr, li.LineNumber);
        }
    }

    public static (string? File, int? Line) Get(XObject? node)
    {
        var e = node as XElement ?? node?.Parent;
        if (node is XAttribute a) e = a.Parent;
        if (e is null) return (null, null);
        var line = int.TryParse((string?)e.Attribute(LineAttr), out var l) ? l : (int?)null;
        return ((string?)e.Attribute(FileAttr), line);
    }

    public static void Strip(XElement root)
    {
        foreach (var e in root.DescendantsAndSelf())
            e.Attributes().Where(a => a.Name.Namespace == Ns || a.IsNamespaceDeclaration && a.Value == Ns.NamespaceName).Remove();
    }
}

/// <summary>A non-fatal issue found while rendering (unknown variable, external resource…).</summary>
public sealed record Diagnostic(string Message, string? File = null, int? Line = null)
{
    public override string ToString() => File is null ? Message : Line is null ? $"{File}: {Message}" : $"{File}:{Line}: {Message}";
}
