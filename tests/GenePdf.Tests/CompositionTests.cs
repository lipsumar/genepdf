using System.Text.Json.Nodes;
using GenePdf.Composition;
using GenePdf.Templates;
using GenePdf.Templating;

namespace GenePdf.Tests;

/// <summary>Template loading, versioning rules and layout composition, against an in-memory template tree.</summary>
public class CompositionTests
{
    private sealed class MemorySource(Dictionary<string, string> files) : ITemplateSource
    {
        public byte[]? ReadBytes(string path) => files.TryGetValue(path, out var s) ? System.Text.Encoding.UTF8.GetBytes(s) : null;

        public IReadOnlyList<string> ListDirectories(string path)
            => files.Keys.Where(k => k.StartsWith(path + "/")).Select(k => k[(path.Length + 1)..].Split('/')).Where(p => p.Length > 1).Select(p => p[0]).Distinct().ToList();

        public IReadOnlyList<string> ListFiles(string path)
            => files.Keys.Where(k => k.StartsWith(path + "/") && !k[(path.Length + 1)..].Contains('/')).Select(k => k[(path.Length + 1)..]).ToList();
    }

    private const string Layout = """
        <g:layout xmlns="http://www.w3.org/1999/xhtml" xmlns:g="urn:genepdf" version="2.0">
          <g:stylesheet href="style.css"/>
          <g:page-footer><span>{{ ref }}</span> <g:page-number/>/<g:page-count/></g:page-footer>
          <g:body>
            <img src="logo.svg"/>
            <h1><g:slot name="subject">Default subject</g:slot></h1>
            <g:slot name="body"/>
          </g:body>
        </g:layout>
        """;

    private static Dictionary<string, string> Files(string document, string version = "1.0") => new()
    {
        ["layouts/base/2.0/layout.xhtml"] = Layout,
        ["layouts/base/2.0/style.css"] = "body { background: url(bg.png); } h1 { color: red; }",
        ["layouts/base/2.0/bg.png"] = "PNG",
        ["layouts/base/2.0/logo.svg"] = "<svg/>",
        [$"documents/doc/{version}/document.xhtml"] = document,
    };

    private static ComposedDocument Compose(Dictionary<string, string> files, string json = """{"ref":"R-1","name":"Ann"}""", string? version = null)
        => new DocumentComposer(new TemplateRepository(new MemorySource(files))).Compose("doc", version, JsonNode.Parse(json)!.AsObject());

    private const string Doc = """
        <g:document xmlns="http://www.w3.org/1999/xhtml" xmlns:g="urn:genepdf" version="1.0" layout="base" layout-version="2.0" lang="fr-BE" title="Doc {{ ref }}">
          <g:fill slot="subject">Hello {{ name }}</g:fill>
          <p>Body&nbsp;text</p>
        </g:document>
        """;

    [Fact]
    public void Fills_slots_and_builds_page_footer_and_css()
    {
        var c = Compose(Files(Doc));
        Assert.Equal("Doc R-1", c.Title);
        Assert.Equal("fr-BE", c.Lang);
        Assert.Contains("<h1>Hello Ann</h1>", c.BodyHtml);
        Assert.Contains("<p>Body text</p>", c.BodyHtml);
        Assert.Contains("<span class=\"pageNumber\"></span>/<span class=\"totalPages\"></span>", c.FooterHtml);
        Assert.Contains("<span>R-1</span>", c.FooterHtml);
        Assert.Null(c.HeaderHtml);
        Assert.Contains("url(\"data:image/png;base64,", c.BodyHtml);       // CSS asset inlined
        Assert.Contains("src=\"data:image/svg+xml;base64,", c.BodyHtml);   // img asset inlined, relative to the layout folder
        Assert.DoesNotContain("g:", c.BodyHtml);
        Assert.DoesNotContain("urn:genepdf", c.BodyHtml);
        Assert.Empty(c.Diagnostics);
    }

    [Fact]
    public void Unfilled_slot_keeps_default_content()
    {
        var c = Compose(Files(Doc.Replace("<g:fill slot=\"subject\">Hello {{ name }}</g:fill>", "")));
        Assert.Contains("<h1>Default subject</h1>", c.BodyHtml);
    }

    [Fact]
    public void Non_void_empty_elements_get_an_end_tag()
    {
        var c = Compose(Files(Doc.Replace("<p>Body&nbsp;text</p>", "<div class=\"x\"/><br/>")));
        Assert.Contains("<div class=\"x\"></div><br>", c.BodyHtml);
    }

    [Fact]
    public void Version_attribute_must_match_folder()
    {
        var ex = Assert.Throws<TemplateException>(() => Compose(Files(Doc, version: "1.1")));
        Assert.Contains("does not match the folder version '1.1'", ex.Message);
    }

    [Fact]
    public void Missing_layout_version_is_an_error()
        => Assert.Throws<TemplateException>(() => Compose(Files(Doc.Replace("layout-version=\"2.0\"", "layout-version=\"9.9\""))));

    [Fact]
    public void Unknown_slot_is_an_error()
    {
        var ex = Assert.Throws<TemplateException>(() => Compose(Files(Doc.Replace("slot=\"subject\"", "slot=\"subjet\""))));
        Assert.Contains("no slot named 'subjet'", ex.Message);
    }

    [Fact]
    public void Xml_errors_report_file_and_line()
    {
        var ex = Assert.Throws<TemplateException>(() => Compose(Files(Doc.Replace("<p>Body&nbsp;text</p>", "<p>unclosed"))));
        Assert.Equal("documents/doc/1.0/document.xhtml", ex.File);
        Assert.NotNull(ex.Line);
    }

    [Fact]
    public void Latest_version_is_picked_numerically()
    {
        var files = Files(Doc);
        files["documents/doc/1.10/document.xhtml"] = Doc.Replace("version=\"1.0\"", "version=\"1.10\"").Replace("Hello", "Ten");
        files["documents/doc/1.9/document.xhtml"] = Doc.Replace("version=\"1.0\"", "version=\"1.9\"");
        var c = Compose(files);
        Assert.Equal("1.10", c.Document.Version);
        Assert.Contains("Ten Ann", c.BodyHtml);
    }

    [Fact]
    public void Scripts_event_handlers_and_external_resources_are_neutralised()
    {
        var c = Compose(Files(Doc.Replace("<p>Body&nbsp;text</p>", "<script>alert(1)</script><p onclick=\"x()\">a</p><img src=\"http://169.254.169.254/x.png\"/>")));
        Assert.DoesNotContain("<script", c.BodyHtml);
        Assert.DoesNotContain("onclick", c.BodyHtml);
        Assert.Contains(c.Diagnostics, d => d.Message.Contains("blocked"));
    }

    [Fact]
    public void Paths_cannot_escape_the_template_root()
        => Assert.Throws<ArgumentException>(() => TemplatePath.Normalize("documents/../../etc/passwd"));

    [Fact]
    public void Version_comparer_orders_numerically()
        => Assert.Equal(["1.2", "1.9", "1.10", "2.0"], new[] { "1.10", "2.0", "1.2", "1.9" }.Order(VersionComparer.Instance));
}
