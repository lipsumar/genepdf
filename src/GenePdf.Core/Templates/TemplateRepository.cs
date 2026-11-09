using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using GenePdf.Templating;

namespace GenePdf.Templates;

public enum TemplateKind { Document, Layout }

/// <summary>A document template version: the body, bound to one layout version.</summary>
public sealed record DocumentTemplate(
    string Name, string Version, string Directory, string FilePath, XElement Root,
    string LayoutName, string LayoutVersion, string Lang, string? Title);

/// <summary>A layout version: page structure, header/footer and stylesheets.</summary>
public sealed record LayoutTemplate(string Name, string Version, string Directory, string FilePath, XElement Root);

public sealed record TemplateVersions(string Name, IReadOnlyList<string> Versions);

/// <summary>
/// Templates live on the filesystem, one folder per version:
/// <code>
/// layouts/&lt;name&gt;/&lt;version&gt;/layout.xhtml   (+ stylesheets, assets)
/// documents/&lt;name&gt;/&lt;version&gt;/document.xhtml (+ samples/*.json, assets)
/// </code>
/// The root element's <c>version</c> attribute must match the folder name.
/// </summary>
public sealed partial class TemplateRepository(ITemplateSource source)
{
    public const string DocumentFile = "document.xhtml";
    public const string LayoutFile = "layout.xhtml";
    public const string SamplesDir = "samples";
    private static readonly XNamespace G = TemplateEngine.G;

    public ITemplateSource Source { get; } = source;

    public static string KindDir(TemplateKind kind) => kind == TemplateKind.Document ? "documents" : "layouts";

    public static string FileName(TemplateKind kind) => kind == TemplateKind.Document ? DocumentFile : LayoutFile;

    public static string DirectoryOf(TemplateKind kind, string name, string version) => $"{KindDir(kind)}/{CheckName(name)}/{CheckName(version)}";

    public IReadOnlyList<TemplateVersions> List(TemplateKind kind)
        => Source.ListDirectories(KindDir(kind))
            .Select(name => new TemplateVersions(name, Source.ListDirectories($"{KindDir(kind)}/{name}")
                .Where(v => Source.ReadBytes($"{KindDir(kind)}/{name}/{v}/{FileName(kind)}") is not null)
                .OrderBy(v => v, VersionComparer.Instance).ToList()))
            .Where(t => t.Versions.Count > 0)
            .ToList();

    /// <summary>Returns the requested version, or the highest one when <paramref name="version"/> is null.</summary>
    public string ResolveVersion(TemplateKind kind, string name, string? version)
    {
        var versions = Source.ListDirectories($"{KindDir(kind)}/{CheckName(name)}");
        if (version is not null)
            return versions.Contains(version) ? version : throw new TemplateException($"{kind} template '{name}' has no version '{version}'");
        return versions.OrderBy(v => v, VersionComparer.Instance).LastOrDefault()
               ?? throw new TemplateException($"{kind} template '{name}' not found");
    }

    public DocumentTemplate LoadDocument(string name, string? version = null)
    {
        version = ResolveVersion(TemplateKind.Document, name, version);
        var dir = DirectoryOf(TemplateKind.Document, name, version);
        var file = $"{dir}/{DocumentFile}";
        var root = LoadXml(file);
        if (root.Name != G + "document")
            throw TemplateException.At(root, "The root element of a document template must be <g:document>");
        CheckVersionAttribute(root, version);
        var layout = Required(root, "layout");
        var layoutVersion = Required(root, "layout-version");
        return new DocumentTemplate(name, version, dir, file, root, layout, layoutVersion,
            (string?)root.Attribute("lang") ?? "en", (string?)root.Attribute("title"));
    }

    public LayoutTemplate LoadLayout(string name, string version)
    {
        if (!Source.ListDirectories($"{KindDir(TemplateKind.Layout)}/{CheckName(name)}").Contains(version))
            throw new TemplateException($"Layout '{name}' version '{version}' not found");
        var dir = DirectoryOf(TemplateKind.Layout, name, version);
        var file = $"{dir}/{LayoutFile}";
        var root = LoadXml(file);
        if (root.Name != G + "layout")
            throw TemplateException.At(root, "The root element of a layout must be <g:layout>");
        CheckVersionAttribute(root, version);
        return new LayoutTemplate(name, version, dir, file, root);
    }

    /// <summary>Parses an XHTML template file with line info, HTML named entities allowed (&amp;nbsp; …).</summary>
    public XElement LoadXml(string path)
    {
        var text = Source.ReadText(path) ?? throw new TemplateException("File not found", path);
        XDocument doc;
        try
        {
            doc = XDocument.Parse(ReplaceHtmlEntities(text), LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new TemplateException(ex.Message.Split(" Line ")[0], path, ex.LineNumber, ex);
        }
        var root = doc.Root!;
        root.Remove();
        SourceInfo.Stamp(root, path);
        return root;
    }

    /// <summary>XML only knows 5 named entities; turn HTML ones into numeric references (same line, so line numbers stay correct).</summary>
    private static string ReplaceHtmlEntities(string text) => HtmlEntity().Replace(text, m =>
    {
        var name = m.Groups[1].Value;
        if (name is "amp" or "lt" or "gt" or "quot" or "apos") return m.Value;
        var decoded = WebUtility.HtmlDecode(m.Value);
        return decoded == m.Value ? m.Value : string.Concat(decoded.EnumerateRunes().Select(r => $"&#x{r.Value:X};"));
    });

    private static void CheckVersionAttribute(XElement root, string folderVersion)
    {
        var v = Required(root, "version");
        if (v != folderVersion)
            throw TemplateException.At(root, $"version=\"{v}\" does not match the folder version '{folderVersion}' (did you copy a version without updating the attribute?)");
    }

    private static string Required(XElement e, string attr)
        => (string?)e.Attribute(attr) is { Length: > 0 } v ? v : throw TemplateException.At(e, $"Missing required attribute '{attr}' on <{e.Name.LocalName}>");

    private static string CheckName(string name)
        => SafeName().IsMatch(name) ? name : throw new TemplateException($"Invalid template name or version '{name}'");

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex SafeName();

    [GeneratedRegex("&([A-Za-z][A-Za-z0-9]*);")]
    private static partial Regex HtmlEntity();
}

/// <summary>Orders versions numerically by segment: 1.2 &lt; 1.10 &lt; 2.0.</summary>
public sealed class VersionComparer : IComparer<string>
{
    public static readonly VersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        var a = (x ?? "").Split('.', '-');
        var b = (y ?? "").Split('.', '-');
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var sa = i < a.Length ? a[i] : "0";
            var sb = i < b.Length ? b[i] : "0";
            var c = long.TryParse(sa, out var na) && long.TryParse(sb, out var nb) ? na.CompareTo(nb) : string.CompareOrdinal(sa, sb);
            if (c != 0) return c;
        }
        return 0;
    }
}
