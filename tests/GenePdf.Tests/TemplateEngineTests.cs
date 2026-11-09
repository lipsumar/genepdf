using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using GenePdf.Templating;

namespace GenePdf.Tests;

public class TemplateEngineTests
{
    private static string Render(string body, string json, List<Diagnostic>? diagnostics = null)
    {
        var root = XElement.Parse($"<div xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:g=\"urn:genepdf\">{body}</div>", LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        SourceInfo.Stamp(root, "test.xhtml");
        var engine = new TemplateEngine(diagnostics ?? []);
        engine.Process(root, engine.CreateScope(JsonNode.Parse(json)!.AsObject(), CultureInfo.InvariantCulture));
        SourceInfo.Strip(root);
        return string.Concat(root.Nodes().Select(n => n.ToString(SaveOptions.DisableFormatting)))
            .Replace(" xmlns=\"http://www.w3.org/1999/xhtml\"", "");
    }

    [Fact]
    public void Interpolates_text_and_attributes()
        => Assert.Equal("<p class=\"x-1\">Hi Ann</p>", Render("<p class=\"x-{{ n }}\">Hi {{ name }}</p>", """{"name":"Ann","n":1}"""));

    [Fact]
    public void Attribute_with_only_a_null_placeholder_is_removed()
        => Assert.Equal("<p>x</p>", Render("<p title=\"{{ missing | default:null }}\">x</p>", "{}"));

    [Theory]
    [InlineData(1, "<b>one</b>")]
    [InlineData(2, "<b>two</b>")]
    [InlineData(3, "<b>other</b>")]
    public void If_else_if_else_chain(int n, string expected)
    {
        var html = Render("""<b g:if="n == 1">one</b> <b g:else-if="n == 2">two</b> <b g:else="">other</b>""", $"{{\"n\":{n}}}");
        Assert.Equal(expected, html.Trim().Replace("  ", ""));
    }

    [Fact]
    public void Each_repeats_with_loop_variables_and_per_item_if()
    {
        var html = Render("""<i g:each="x in items" g:if="x.show">{{ loop.number }}/{{ loop.count }}:{{ x.v }}</i>""",
            """{"items":[{"v":"a","show":true},{"v":"b","show":false},{"v":"c","show":true}]}""");
        Assert.Equal("<i>1/3:a</i><i>3/3:c</i>", html);
    }

    [Fact]
    public void Each_over_missing_collection_renders_nothing()
        => Assert.Equal("", Render("""<i g:each="x in nope">{{ x }}</i>""", "{\"other\":1}").Trim());

    [Fact]
    public void Block_unwraps_and_text_replaces_content()
        => Assert.Equal("<b>A&amp;B</b>c", Render("""<g:block g:if="true"><b g:text="v">placeholder</b>c</g:block>""", """{"v":"A&B"}"""));

    [Fact]
    public void Nested_loops()
        => Assert.Equal("[1,2][3]", Render("""<g:block g:each="g in groups">[<g:block g:each="i in g">{{ loop.first ? '' : ',' }}{{ i }}</g:block>]</g:block>""", """{"groups":[[1,2],[3]]}"""));

    [Fact]
    public void Typos_in_directives_are_errors_with_line()
    {
        var ex = Assert.Throws<TemplateException>(() => Render("\n<p g:iff=\"x\">a</p>", "{}"));
        Assert.Contains("g:iff", ex.Message);
        Assert.Equal(2, ex.Line);
        Assert.Equal("test.xhtml", ex.File);
    }

    [Fact]
    public void Else_without_if_is_an_error()
        => Assert.Throws<TemplateException>(() => Render("<p>a</p><p g:else=\"\">b</p>", "{}"));

    [Fact]
    public void Unknown_variables_are_reported_with_location()
    {
        var diagnostics = new List<Diagnostic>();
        Render("\n\n<p>{{ custmer.name }}</p>", """{"customer":{}}""", diagnostics);
        var d = Assert.Single(diagnostics);
        Assert.Equal(3, d.Line);
    }

    [Fact]
    public void Existence_checks_do_not_warn()
    {
        var diagnostics = new List<Diagnostic>();
        Render("""<p g:if="contact">x</p><p g:if="customer.vat">y</p>""", """{"customer":{}}""", diagnostics);
        Assert.Empty(diagnostics);
    }
}
