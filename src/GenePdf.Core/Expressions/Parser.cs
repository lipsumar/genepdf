namespace GenePdf.Expressions;

/// <summary>
/// Recursive-descent parser that compiles an expression straight into a closure.
/// Precedence (low → high): pipe filters, ternary, or, and, not, comparison, + -, * / %, unary -, member access.
/// </summary>
internal sealed class Parser(string source)
{
    private readonly List<Token> _tokens = Lexer.Tokenize(source);
    private int _pos;

    private Token Current => _tokens[_pos];

    public Func<Scope, object?> ParseRoot()
    {
        if (Current.Kind == TokenKind.End) throw Error("Empty expression");
        var e = ParsePipe();
        if (Current.Kind != TokenKind.End) throw Error($"Unexpected '{Current.Text}'");
        return e;
    }

    private ExpressionException Error(string message) => new(message, source, Current.Position);

    private bool Accept(TokenKind kind)
    {
        if (Current.Kind != kind) return false;
        _pos++;
        return true;
    }

    private Token Expect(TokenKind kind, string what)
    {
        if (Current.Kind != kind) throw Error($"Expected {what}");
        return _tokens[_pos++];
    }

    private Func<Scope, object?> ParsePipe()
    {
        var value = ParseTernary();
        while (Accept(TokenKind.Pipe))
        {
            var nameToken = Expect(TokenKind.Identifier, "filter name");
            var filter = Filters.Get(nameToken.Text) ?? throw new ExpressionException($"Unknown filter '{nameToken.Text}'", source, nameToken.Position);
            var args = new List<Func<Scope, object?>>();
            if (Accept(TokenKind.Colon))
            {
                do args.Add(ParseTernary());
                while (Accept(TokenKind.Comma));
            }
            var input = value;
            var argArray = args.ToArray();
            // "x | default:…" means x is optional: don't warn when it's missing.
            value = nameToken.Text == "default"
                ? scope => filter(input(scope.Quiet()), argArray.Select(a => a(scope)).ToArray(), scope.Culture)
                : scope => filter(input(scope), argArray.Select(a => a(scope)).ToArray(), scope.Culture);
        }
        return value;
    }

    private Func<Scope, object?> ParseTernary()
    {
        var cond = ParseOr();
        if (!Accept(TokenKind.Question)) return cond;
        var whenTrue = ParseTernary();
        Expect(TokenKind.Colon, "':' in ternary");
        var whenFalse = ParseTernary();
        return s => Values.Truthy(cond(s.Quiet())) ? whenTrue(s) : whenFalse(s);
    }

    private Func<Scope, object?> ParseOr()
    {
        var left = ParseAnd();
        while (Accept(TokenKind.Or))
        {
            var l = left; var r = ParseAnd();
            left = s => Values.Truthy(l(s)) || Values.Truthy(r(s));
        }
        return left;
    }

    private Func<Scope, object?> ParseAnd()
    {
        var left = ParseNot();
        while (Accept(TokenKind.And))
        {
            var l = left; var r = ParseNot();
            left = s => Values.Truthy(l(s)) && Values.Truthy(r(s));
        }
        return left;
    }

    private Func<Scope, object?> ParseNot()
    {
        if (Accept(TokenKind.Not))
        {
            var inner = ParseNot();
            return s => !Values.Truthy(inner(s));
        }
        return ParseComparison();
    }

    private Func<Scope, object?> ParseComparison()
    {
        var left = ParseAdditive();
        var kind = Current.Kind;
        if (kind is not (TokenKind.Eq or TokenKind.NotEq or TokenKind.Lt or TokenKind.LtEq or TokenKind.Gt or TokenKind.GtEq))
            return left;
        _pos++;
        var right = ParseAdditive();
        return kind switch
        {
            TokenKind.Eq => s => Values.AreEqual(left(s), right(s)),
            TokenKind.NotEq => s => !Values.AreEqual(left(s), right(s)),
            TokenKind.Lt => s => Values.Compare(left(s), right(s)) is < 0,
            TokenKind.LtEq => s => Values.Compare(left(s), right(s)) is <= 0,
            TokenKind.Gt => s => Values.Compare(left(s), right(s)) is > 0,
            _ => s => Values.Compare(left(s), right(s)) is >= 0,
        };
    }

    private Func<Scope, object?> ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Current.Kind is TokenKind.Plus or TokenKind.Minus)
        {
            var op = _tokens[_pos++].Kind;
            var l = left; var r = ParseMultiplicative();
            left = op == TokenKind.Plus
                ? s => Values.Add(l(s), r(s), s.Culture)
                : s => Values.Arithmetic(l(s), r(s), (a, b) => a - b);
        }
        return left;
    }

    private Func<Scope, object?> ParseMultiplicative()
    {
        var left = ParseUnary();
        while (Current.Kind is TokenKind.Star or TokenKind.Slash or TokenKind.Percent)
        {
            var op = _tokens[_pos++].Kind;
            var l = left; var r = ParseUnary();
            Func<decimal, decimal, decimal?> f = op switch
            {
                TokenKind.Star => (a, b) => a * b,
                TokenKind.Slash => (a, b) => b == 0 ? null : a / b,
                _ => (a, b) => b == 0 ? null : a % b,
            };
            left = s => Values.Arithmetic(l(s), r(s), f);
        }
        return left;
    }

    private Func<Scope, object?> ParseUnary()
    {
        if (Accept(TokenKind.Minus))
        {
            var inner = ParseUnary();
            return s => Values.ToNumber(inner(s)) is { } d ? -d : null;
        }
        return ParsePostfix();
    }

    private Func<Scope, object?> ParsePostfix()
    {
        var target = ParsePrimary(out var path);
        while (true)
        {
            if (Accept(TokenKind.Dot))
            {
                var name = Expect(TokenKind.Identifier, "member name").Text;
                var t = target;
                path = path is null ? null : $"{path}.{name}";
                var display = path;
                target = s =>
                {
                    var obj = t(s);
                    if (obj is System.Text.Json.Nodes.JsonObject o && !o.ContainsKey(name))
                        s.Warn($"'{display ?? name}' is not in the data (expression '{source}')");
                    return Values.Member(obj, name);
                };
            }
            else if (Accept(TokenKind.LBracket))
            {
                var index = ParsePipe();
                Expect(TokenKind.RBracket, "']'");
                var t = target;
                path = null;
                target = s => Values.Index(t(s), index(s));
            }
            else return target;
        }
    }

    private Func<Scope, object?> ParsePrimary(out string? path)
    {
        path = null;
        var token = Current;
        switch (token.Kind)
        {
            case TokenKind.Number: _pos++; { var n = token.Number; return _ => n; }
            case TokenKind.String: _pos++; { var str = token.Text; return _ => str; }
            case TokenKind.True: _pos++; return _ => true;
            case TokenKind.False: _pos++; return _ => false;
            case TokenKind.Null: _pos++; return _ => null;
            case TokenKind.LParen:
                _pos++;
                var inner = ParsePipe();
                Expect(TokenKind.RParen, "')'");
                return inner;
            case TokenKind.Identifier:
                _pos++;
                var name = token.Text;
                path = name;
                return s =>
                {
                    if (s.TryLookup(name, out var v)) return v;
                    s.Warn($"Unknown variable '{name}' in expression '{source}'");
                    return null;
                };
            default:
                throw Error(token.Kind == TokenKind.End ? "Unexpected end of expression" : $"Unexpected '{token.Text}'");
        }
    }
}
