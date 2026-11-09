using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GenePdf.Expressions;

namespace GenePdf.Templating;

/// <summary>
/// Applies data to an XHTML tree, in place. Directives (namespace <c>urn:genepdf</c>, prefix <c>g</c>):
/// <list type="bullet">
/// <item><c>g:if="expr"</c>, <c>g:else-if="expr"</c>, <c>g:else=""</c> on consecutive sibling elements</item>
/// <item><c>g:each="item in expr"</c> repeats the element; <c>loop.index</c> (0-based), <c>loop.number</c>, <c>loop.first</c>, <c>loop.last</c>, <c>loop.count</c>. Evaluated before g:if, so g:if can filter items.</item>
/// <item><c>g:text="expr"</c> replaces the element's content with the value</item>
/// <item><c>{{ expr }}</c> in text and attribute values. An attribute whose whole value is <c>{{ expr }}</c> is dropped when the value is null or false.</item>
/// <item><c>&lt;g:block&gt;</c> is a wrapper that disappears from the output (useful for g:if/g:each on several elements).</item>
/// </list>
/// </summary>
public sealed partial class TemplateEngine
{
    public static readonly XNamespace G = "urn:genepdf";
    private static readonly XName IfAttr = G + "if", ElseIfAttr = G + "else-if", ElseAttr = G + "else", EachAttr = G + "each", TextAttr = G + "text";
    private static readonly XName Block = G + "block";

    /// <summary>g: elements that the engine leaves alone (handled by the composer afterwards).</summary>
    private static readonly HashSet<string> PassThroughElements = ["page-number", "page-count"];

    private readonly Dictionary<string, Expression> _expressions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EachExpression> _eachExpressions = new(StringComparer.Ordinal);
    private readonly List<Diagnostic> _diagnostics;
    private readonly HashSet<string> _seenWarnings = [];
    private XObject? _current;

    public TemplateEngine(List<Diagnostic> diagnostics) => _diagnostics = diagnostics;

    public Scope CreateScope(JsonObject data, CultureInfo culture) => new(data, culture, Warn);

    public void Process(XElement root, Scope scope)
    {
        bool? chain = null;
        ProcessElement(root, scope, ref chain);
    }

    /// <summary>Evaluates a string containing {{ }} placeholders (used for the document title).</summary>
    public string Interpolate(string text, Scope scope, XObject? location)
    {
        _current = location;
        return InterpolateText(text, scope);
    }

    private void Warn(string message)
    {
        var (file, line) = SourceInfo.Get(_current);
        var d = new Diagnostic(message, file, line);
        if (_seenWarnings.Add(d.ToString())) _diagnostics.Add(d);
    }

    private void ProcessChildren(XElement parent, Scope scope)
    {
        bool? chain = null; // null: no open if-chain, false: open and nothing matched yet, true: a branch matched
        foreach (var node in parent.Nodes().ToList())
        {
            switch (node)
            {
                case XComment or XProcessingInstruction:
                    node.Remove();
                    break;
                case XText text:
                    if (string.IsNullOrWhiteSpace(text.Value)) break; // whitespace doesn't break an if/else chain
                    chain = null;
                    if (text.Value.Contains("{{"))
                    {
                        _current = parent;
                        text.Value = InterpolateText(text.Value, scope);
                    }
                    break;
                case XElement e:
                    ProcessElement(e, scope, ref chain);
                    break;
            }
        }
    }

    private void ProcessElement(XElement e, Scope scope, ref bool? chain)
    {
        _current = e;

        // --- g:each (before g:if, so that g:if is evaluated per item)
        if (e.Attribute(EachAttr) is { } eachAttr)
        {
            if (e.Attribute(ElseIfAttr) is not null || e.Attribute(ElseAttr) is not null)
                throw TemplateException.At(e, "g:each cannot be combined with g:else / g:else-if (wrap it in a g:block)");
            var each = ParseEach(eachAttr);
            eachAttr.Remove();
            var value = Eval(each.Collection, scope, e);
            var items = value is null ? [] : (Values.AsList(value) ?? [value]).ToList();
            for (var i = 0; i < items.Count; i++)
            {
                var clone = new XElement(e);
                e.AddBeforeSelf(clone);
                var loop = new JsonObject
                {
                    ["index"] = i, ["number"] = i + 1, ["first"] = i == 0, ["last"] = i == items.Count - 1, ["count"] = items.Count,
                };
                bool? itemChain = null;
                ProcessElement(clone, scope.Child(new Dictionary<string, object?> { [each.ItemName] = items[i], ["loop"] = loop }), ref itemChain);
            }
            e.Remove();
            chain = null;
            return;
        }

        // --- g:if / g:else-if / g:else
        var ifAttr = e.Attribute(IfAttr);
        var elseIfAttr = e.Attribute(ElseIfAttr);
        var elseAttr = e.Attribute(ElseAttr);
        if ((ifAttr is not null ? 1 : 0) + (elseIfAttr is not null ? 1 : 0) + (elseAttr is not null ? 1 : 0) > 1)
            throw TemplateException.At(e, "Use only one of g:if, g:else-if, g:else on an element");

        if (elseIfAttr is not null || elseAttr is not null)
        {
            if (chain is null)
                throw TemplateException.At(e, $"{(elseAttr is not null ? "g:else" : "g:else-if")} must directly follow an element with g:if or g:else-if");
            if (chain == true) { e.Remove(); if (elseAttr is not null) chain = null; return; }
            if (elseIfAttr is not null)
            {
                var ok = Values.Truthy(Eval(GetExpression(elseIfAttr), scope.Quiet(), e));
                elseIfAttr.Remove();
                if (!ok) { e.Remove(); return; }
                chain = true;
            }
            else
            {
                elseAttr!.Remove();
                chain = null;
            }
        }
        else if (ifAttr is not null)
        {
            var ok = Values.Truthy(Eval(GetExpression(ifAttr), scope.Quiet(), e));
            ifAttr.Remove();
            if (!ok) { e.Remove(); chain = false; return; }
            chain = true;
        }
        else
        {
            chain = null;
        }

        // --- unknown g: things are errors (catches typos such as g:iff)
        if (e.Name.Namespace == G && e.Name != Block && !PassThroughElements.Contains(e.Name.LocalName))
            throw TemplateException.At(e, $"Unknown or misplaced element <g:{e.Name.LocalName}>");
        foreach (var a in e.Attributes())
            if (a.Name.Namespace == G && a.Name != TextAttr)
                throw TemplateException.At(e, $"Unknown attribute g:{a.Name.LocalName}");

        // --- attributes
        foreach (var a in e.Attributes().ToList())
        {
            if (a.IsNamespaceDeclaration || a.Name.Namespace == G || a.Name.Namespace == SourceInfo.Ns || !a.Value.Contains("{{")) continue;
            var whole = WholePlaceholder().Match(a.Value);
            if (whole.Success)
            {
                var v = Eval(GetExpression(whole.Groups[1].Value, a), scope, e);
                if (v is null or false) a.Remove();
                else a.Value = Values.ToDisplayString(v, scope.Culture);
            }
            else a.Value = InterpolateText(a.Value, scope);
        }

        // --- g:text
        if (e.Attribute(TextAttr) is { } textAttr)
        {
            var text = Values.ToDisplayString(Eval(GetExpression(textAttr), scope, e), scope.Culture);
            textAttr.Remove();
            e.ReplaceNodes(new XText(text));
        }
        else
        {
            ProcessChildren(e, scope);
        }

        if (e.Name == Block)
        {
            if (e.Parent is null) throw TemplateException.At(e, "<g:block> cannot be the root element");
            e.ReplaceWith(e.Nodes().ToList());
        }
    }

    private string InterpolateText(string text, Scope scope)
    {
        var sb = new StringBuilder();
        var last = 0;
        foreach (Match m in Placeholder().Matches(text))
        {
            sb.Append(text, last, m.Index - last);
            sb.Append(Values.ToDisplayString(Eval(GetExpression(m.Groups[1].Value, _current), scope, _current), scope.Culture));
            last = m.Index + m.Length;
        }
        sb.Append(text, last, text.Length - last);
        return sb.ToString();
    }

    private Expression GetExpression(XAttribute attr) => GetExpression(attr.Value, attr);

    private Expression GetExpression(string source, XObject? location)
    {
        source = source.Trim();
        if (_expressions.TryGetValue(source, out var cached)) return cached;
        try
        {
            return _expressions[source] = Expression.Parse(source);
        }
        catch (ExpressionException ex)
        {
            throw TemplateException.At(location, ex.Message, ex);
        }
    }

    private EachExpression ParseEach(XAttribute attr)
    {
        if (_eachExpressions.TryGetValue(attr.Value, out var cached)) return cached;
        try
        {
            return _eachExpressions[attr.Value] = EachExpression.Parse(attr.Value);
        }
        catch (ExpressionException ex)
        {
            throw TemplateException.At(attr, ex.Message, ex);
        }
    }

    private object? Eval(Expression expr, Scope scope, XObject? location)
    {
        _current = location;
        try
        {
            return expr.Evaluate(scope);
        }
        catch (Exception ex) when (ex is not TemplateException)
        {
            throw TemplateException.At(location, $"Error evaluating '{expr.Source}': {ex.Message}", ex);
        }
    }

    [GeneratedRegex(@"\{\{(.*?)\}\}", RegexOptions.Singleline)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^\s*\{\{(.*?)\}\}\s*$", RegexOptions.Singleline)]
    private static partial Regex WholePlaceholder();
}
