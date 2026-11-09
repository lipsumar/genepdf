using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GenePdf.Templates;
using GenePdf.Templating;

namespace GenePdf.Composition;

/// <summary>What the PDF renderer needs: HTML for the page flow and for the repeated header/footer.</summary>
public sealed record ComposedDocument(
    DocumentTemplate Document,
    LayoutTemplate Layout,
    string Title,
    string Lang,
    string BodyHtml,
    string? HeaderHtml,
    string? FooterHtml,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Merges a document template into its layout and applies the data.
/// <para>Layout structure:</para>
/// <code>
/// &lt;g:layout version="1.0"&gt;
///   &lt;g:stylesheet href="layout.css"/&gt;          0..n, relative to the layout folder
///   &lt;g:page-header&gt;…&lt;/g:page-header&gt;          optional, repeated on every page
///   &lt;g:page-footer&gt;…&lt;/g:page-footer&gt;          optional, repeated on every page (&lt;g:page-number/&gt;, &lt;g:page-count/&gt;)
///   &lt;g:body&gt;… &lt;g:slot name="body"/&gt; …&lt;/g:body&gt;
/// &lt;/g:layout&gt;
/// </code>
/// <para>A document fills slots with <c>&lt;g:fill slot="name"&gt;</c>; content outside any g:fill goes to the "body" slot.
/// A slot with no fill keeps its own (default) content.</para>
/// </summary>
public sealed partial class DocumentComposer(TemplateRepository repository)
{
    private static readonly XNamespace G = TemplateEngine.G;
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    private const string AssetScheme = "genepdf-asset:";

    /// <summary>HTML void elements: the only ones that may be written as &lt;x/&gt; for an HTML parser.</summary>
    private static readonly HashSet<string> VoidElements =
        ["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"];

    public ComposedDocument Compose(string documentName, string? version, JsonObject data)
    {
        var diagnostics = new List<Diagnostic>();
        var document = repository.LoadDocument(documentName, version);
        var layout = repository.LoadLayout(document.LayoutName, document.LayoutVersion);

        CultureInfo culture;
        try { culture = CultureInfo.GetCultureInfo(document.Lang); }
        catch (CultureNotFoundException) { throw TemplateException.At(document.Root, $"Unknown lang '{document.Lang}'"); }

        // Relative URLs are resolved against the folder of the file they appear in, before the trees are merged.
        RebaseAssetUrls(document.Root, document.Directory);
        RebaseAssetUrls(layout.Root, layout.Directory);

        var fills = CollectFills(document.Root);
        var stylesheets = new List<XElement>();
        XElement? header = null, footer = null, body = null;
        foreach (var child in layout.Root.Elements().ToList())
        {
            if (child.Name == G + "stylesheet") stylesheets.Add(child);
            else if (child.Name == G + "page-header") header = header is null ? child : throw TemplateException.At(child, "Only one <g:page-header> is allowed");
            else if (child.Name == G + "page-footer") footer = footer is null ? child : throw TemplateException.At(child, "Only one <g:page-footer> is allowed");
            else if (child.Name == G + "body") body = body is null ? child : throw TemplateException.At(child, "Only one <g:body> is allowed");
            else throw TemplateException.At(child, $"Unexpected <{child.Name.LocalName}> in layout: expected g:stylesheet, g:page-header, g:page-footer or g:body");
        }
        if (body is null) throw TemplateException.At(layout.Root, "The layout has no <g:body>");

        var usedSlots = new HashSet<string>();
        foreach (var part in new[] { header, footer, body }.OfType<XElement>())
            FillSlots(part, fills, usedSlots);
        foreach (var (slot, fill) in fills)
            if (!usedSlots.Contains(slot))
                throw TemplateException.At(fill, $"The layout '{layout.Name}' {layout.Version} has no slot named '{slot}'");

        var engine = new TemplateEngine(diagnostics);
        var scope = engine.CreateScope(data, culture);
        var css = string.Join("\n", stylesheets.Select(s => LoadStylesheet(s, layout.Directory, diagnostics)));

        string title = document.Title is null ? document.Name : engine.Interpolate(document.Title, scope, document.Root.Attribute("title"));

        string RenderPart(XElement part, string wrapperClass)
        {
            var div = new XElement(Xhtml + "div", new XAttribute("class", wrapperClass), part.Attributes().Where(a => a.Name.Namespace == SourceInfo.Ns), part.Nodes());
            engine.Process(div, scope);
            ReplacePageCounters(div);
            Finalize(div, diagnostics);
            return Serialize(div);
        }

        var bodyHtml = RenderPart(body, "g-body");
        var headerHtml = header is null ? null : RenderPart(header, "g-page-header");
        var footerHtml = footer is null ? null : RenderPart(footer, "g-page-footer");

        var lang = document.Lang;
        var page = $"""
            <!DOCTYPE html>
            <html lang="{Attr(lang)}"><head><meta charset="utf-8"/><title>{Text(title)}</title>
            <style>{css}</style>
            </head><body>{bodyHtml}</body></html>
            """;

        // Chromium renders header/footer templates as separate documents: they need the stylesheet too.
        string? Wrap(string? part) => part is null ? null
            : $"<style>{css}\nhtml,body{{margin:0;padding:0;-webkit-print-color-adjust:exact;print-color-adjust:exact}}</style>{part}";

        return new ComposedDocument(document, layout, title, lang, page, Wrap(headerHtml), Wrap(footerHtml), diagnostics);
    }

    private static Dictionary<string, XElement> CollectFills(XElement documentRoot)
    {
        var fills = new Dictionary<string, XElement>();
        var implicitBody = new XElement(G + "fill", new XAttribute("slot", "body"), documentRoot.Attributes().Where(a => a.Name.Namespace == SourceInfo.Ns));
        foreach (var node in documentRoot.Nodes().ToList())
        {
            if (node is XElement e && e.Name == G + "fill")
            {
                var slot = (string?)e.Attribute("slot") ?? throw TemplateException.At(e, "<g:fill> needs a slot attribute");
                if (!fills.TryAdd(slot, e)) throw TemplateException.At(e, $"Slot '{slot}' is filled twice");
                e.Remove();
            }
            else
            {
                node.Remove();
                implicitBody.Add(node);
            }
        }
        if (implicitBody.Nodes().Any(n => n is XElement || n is XText t && !string.IsNullOrWhiteSpace(t.Value)))
        {
            if (fills.ContainsKey("body"))
                throw TemplateException.At(fills["body"], "Content outside <g:fill> goes to the 'body' slot, which is also filled explicitly");
            fills["body"] = implicitBody;
        }
        return fills;
    }

    private static void FillSlots(XElement container, Dictionary<string, XElement> fills, HashSet<string> used)
    {
        foreach (var slot in container.Descendants(G + "slot").ToList())
        {
            var name = (string?)slot.Attribute("name") ?? throw TemplateException.At(slot, "<g:slot> needs a name attribute");
            if (fills.TryGetValue(name, out var fill))
            {
                used.Add(name);
                // Detach before re-attaching so nodes keep their identity (no clone) — a slot can be used only once.
                var nodes = fill.Nodes().ToList();
                foreach (var n in nodes) n.Remove();
                slot.ReplaceWith(nodes);
                fills[name] = new XElement(fill.Name, fill.Attributes()); // later uses of the same slot get nothing
            }
            else
            {
                slot.ReplaceWith(slot.Nodes().ToList());
            }
        }
    }

    /// <summary>Header/footer templates: Chromium fills elements with these classes.</summary>
    private static void ReplacePageCounters(XElement root)
    {
        foreach (var e in root.Descendants(G + "page-number").ToList())
            e.ReplaceWith(new XElement(Xhtml + "span", new XAttribute("class", "pageNumber")));
        foreach (var e in root.Descendants(G + "page-count").ToList())
            e.ReplaceWith(new XElement(Xhtml + "span", new XAttribute("class", "totalPages")));
    }

    // ------------------------------------------------------------------ assets

    private static readonly XName[] UrlAttributes = ["src", "href"];

    /// <summary>
    /// Rewrites relative src/href to "genepdf-asset:&lt;path from template root&gt;". They are turned into data: URIs
    /// once the data has been applied, so that dynamic paths (src="sig/{{ user.id }}.png") work too.
    /// </summary>
    private static void RebaseAssetUrls(XElement root, string baseDir)
    {
        foreach (var e in root.DescendantsAndSelf())
        {
            if (e.Name.Namespace == G) continue;
            foreach (var name in UrlAttributes)
            {
                if (e.Attribute(name) is not { } a || IsAbsoluteUrl(a.Value) || a.Value.StartsWith('#') || a.Value.TrimStart().StartsWith("{{")) continue;
                a.Value = AssetScheme + baseDir + "/" + a.Value;
            }
        }
    }

    private static bool IsAbsoluteUrl(string url) => UrlScheme().IsMatch(url);

    private void Finalize(XElement root, List<Diagnostic> diagnostics)
    {
        foreach (var e in root.DescendantsAndSelf().ToList())
        {
            // Scripts never run (JavaScript is disabled in the renderer); drop them so the output stays inert.
            if (e.Name.LocalName == "script") { e.Remove(); continue; }
            foreach (var a in e.Attributes().ToList())
            {
                if (a.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase)) { a.Remove(); continue; }
                if (a.Name.LocalName is not ("src" or "href")) continue;
                if (a.Value.StartsWith(AssetScheme))
                {
                    var path = a.Value[AssetScheme.Length..];
                    if (a.Name.LocalName == "href" && e.Name.LocalName == "a") { a.Remove(); continue; } // internal links make no sense in a PDF
                    a.Value = ToDataUri(path, diagnostics, a) ?? "";
                }
                else if (a.Name.LocalName == "src" && !a.Value.StartsWith("data:"))
                {
                    var (file, line) = SourceInfo.Get(a);
                    diagnostics.Add(new Diagnostic($"External resource '{a.Value}' is blocked: put assets next to the template", file, line));
                }
            }
        }
        SourceInfo.Strip(root);
    }

    private string? ToDataUri(string path, List<Diagnostic> diagnostics, XObject attr)
    {
        byte[]? bytes;
        string normalized;
        try
        {
            normalized = TemplatePath.Normalize(path);
            bytes = repository.Source.ReadBytes(normalized);
        }
        catch (ArgumentException ex)
        {
            throw TemplateException.At(attr, ex.Message);
        }
        if (bytes is null)
        {
            var (file, line) = SourceInfo.Get(attr);
            diagnostics.Add(new Diagnostic($"Asset not found: {normalized}", file, line));
            return null;
        }
        return $"data:{MimeType(normalized)};base64,{Convert.ToBase64String(bytes)}";
    }

    private string LoadStylesheet(XElement link, string layoutDir, List<Diagnostic> diagnostics)
    {
        var href = (string?)link.Attribute("href") ?? throw TemplateException.At(link, "<g:stylesheet> needs an href");
        var path = TemplatePath.Combine(layoutDir, href);
        var css = repository.Source.ReadText(path) ?? throw TemplateException.At(link, $"Stylesheet not found: {path}");
        var cssDir = TemplatePath.Directory(path);
        return CssUrl().Replace(css, m =>
        {
            var url = m.Groups[2].Value.Trim();
            if (url.StartsWith("data:") || url.StartsWith('#')) return m.Value;
            if (IsAbsoluteUrl(url))
            {
                diagnostics.Add(new Diagnostic($"External resource '{url}' is blocked: put assets next to the stylesheet", path));
                return m.Value;
            }
            var assetPath = TemplatePath.Combine(cssDir, url);
            var bytes = repository.Source.ReadBytes(assetPath);
            if (bytes is null)
            {
                diagnostics.Add(new Diagnostic($"Asset not found: {assetPath}", path));
                return m.Value;
            }
            return $"url(\"data:{MimeType(assetPath)};base64,{Convert.ToBase64String(bytes)}\")";
        });
    }

    private static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".css" => "text/css",
        _ => "application/octet-stream",
    };

    // ------------------------------------------------------------------ serialization

    /// <summary>
    /// Serializes for an HTML parser: no namespace prefixes, and non-void elements always get an end tag
    /// (&lt;div/&gt; would be read as an unclosed div).
    /// </summary>
    private static string Serialize(XElement root)
    {
        var sb = new StringBuilder();
        Write(root, sb);
        return sb.ToString();
    }

    private static void Write(XElement e, StringBuilder sb)
    {
        var name = e.Name.LocalName;
        sb.Append('<').Append(name);
        foreach (var a in e.Attributes())
        {
            if (a.IsNamespaceDeclaration) continue;
            var attrName = a.Name.Namespace == XNamespace.None ? a.Name.LocalName
                : a.Name.Namespace == XNamespace.Xml ? "xml:" + a.Name.LocalName
                : a.Name.LocalName;
            sb.Append(' ').Append(attrName).Append("=\"").Append(Attr(a.Value)).Append('"');
        }
        sb.Append('>');
        if (VoidElements.Contains(name)) return;
        foreach (var n in e.Nodes())
        {
            switch (n)
            {
                case XElement child: Write(child, sb); break;
                case XCData cdata when name is "style": sb.Append(cdata.Value); break;
                case XText t: sb.Append(name is "style" ? t.Value : Text(t.Value)); break;
            }
        }
        sb.Append("</").Append(name).Append('>');
    }

    private static string Text(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Attr(string s) => Text(s).Replace("\"", "&quot;");

    [GeneratedRegex(@"url\(\s*(['""]?)([^'"")]+)\1\s*\)")]
    private static partial Regex CssUrl();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9+.-]*:")]
    private static partial Regex UrlScheme();
}
