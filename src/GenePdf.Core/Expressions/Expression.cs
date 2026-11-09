using System.Globalization;
using System.Text.Json.Nodes;

namespace GenePdf.Expressions;

public sealed class ExpressionException(string message, string expression, int position)
    : Exception($"{message} at position {position} in expression '{expression}'")
{
    public string Expression { get; } = expression;
    public int Position { get; } = position;
}

/// <summary>Variables visible to an expression: loop variables first, then the root data object.</summary>
public sealed class Scope
{
    private readonly Scope? _parent;
    private readonly Dictionary<string, object?> _vars = new(StringComparer.Ordinal);

    public Scope(JsonObject root, CultureInfo culture, Action<string>? warn = null)
    {
        Root = root;
        Culture = culture;
        Warn = warn ?? (_ => { });
    }

    private Scope(Scope parent, Action<string>? warn = null)
    {
        _parent = parent;
        Root = parent.Root;
        Culture = parent.Culture;
        Warn = warn ?? parent.Warn;
    }

    /// <summary>Same variables, no "unknown variable" warnings (for existence tests and default values).</summary>
    public Scope Quiet() => new(this, _ => { });

    public JsonObject Root { get; }
    public CultureInfo Culture { get; }
    public Action<string> Warn { get; }

    public Scope Child(string name, object? value)
    {
        var s = new Scope(this);
        s._vars[name] = value;
        return s;
    }

    public Scope Child(IReadOnlyDictionary<string, object?> vars)
    {
        var s = new Scope(this);
        foreach (var (k, v) in vars) s._vars[k] = v;
        return s;
    }

    internal bool TryLookup(string name, out object? value)
    {
        for (var s = this; s is not null; s = s._parent)
            if (s._vars.TryGetValue(name, out value)) return true;

        if (name == "$root") { value = Root; return true; }
        if (Root.TryGetPropertyValue(name, out var node)) { value = Values.FromNode(node); return true; }
        value = null;
        return false;
    }
}

/// <summary>A parsed, reusable expression.</summary>
public sealed class Expression
{
    private readonly Func<Scope, object?> _eval;

    private Expression(string source, Func<Scope, object?> eval)
    {
        Source = source;
        _eval = eval;
    }

    public string Source { get; }

    public static Expression Parse(string source) => new(source, new Parser(source).ParseRoot());

    public object? Evaluate(Scope scope) => _eval(scope);

    public bool EvaluateBool(Scope scope) => Values.Truthy(_eval(scope));

    public string EvaluateString(Scope scope) => Values.ToDisplayString(_eval(scope), scope.Culture);
}

/// <summary>Parses "item in collection" (used by g:each).</summary>
public sealed record EachExpression(string ItemName, Expression Collection)
{
    public static EachExpression Parse(string source)
    {
        var tokens = Lexer.Tokenize(source);
        if (tokens.Count < 4 || tokens[0].Kind != TokenKind.Identifier || tokens[1].Kind != TokenKind.In)
            throw new ExpressionException("Expected 'item in collection'", source, 0);
        return new EachExpression(tokens[0].Text, Expression.Parse(source[tokens[2].Position..]));
    }
}
