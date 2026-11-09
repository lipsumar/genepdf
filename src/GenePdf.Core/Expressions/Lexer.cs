using System.Globalization;
using System.Text;

namespace GenePdf.Expressions;

internal enum TokenKind
{
    Number, String, Identifier,
    Dot, Comma, Colon, Question, Pipe, LParen, RParen, LBracket, RBracket,
    Plus, Minus, Star, Slash, Percent,
    Eq, NotEq, Lt, LtEq, Gt, GtEq,
    And, Or, Not,
    True, False, Null, In,
    End,
}

internal readonly record struct Token(TokenKind Kind, string Text, int Position, decimal Number = 0);

internal static class Lexer
{
    private static readonly Dictionary<string, TokenKind> Keywords = new()
    {
        ["and"] = TokenKind.And,
        ["or"] = TokenKind.Or,
        ["not"] = TokenKind.Not,
        ["true"] = TokenKind.True,
        ["false"] = TokenKind.False,
        ["null"] = TokenKind.Null,
        ["in"] = TokenKind.In,
    };

    public static List<Token> Tokenize(string source)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            var start = i;
            if (char.IsDigit(c))
            {
                while (i < source.Length && (char.IsDigit(source[i]) || source[i] == '.' && i + 1 < source.Length && char.IsDigit(source[i + 1])))
                    i++;
                var text = source[start..i];
                tokens.Add(new Token(TokenKind.Number, text, start, decimal.Parse(text, CultureInfo.InvariantCulture)));
                continue;
            }

            if (char.IsLetter(c) || c == '_' || c == '$')
            {
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] is '_' or '$'))
                    i++;
                var text = source[start..i];
                tokens.Add(Keywords.TryGetValue(text, out var kw)
                    ? new Token(kw, text, start)
                    : new Token(TokenKind.Identifier, text, start));
                continue;
            }

            if (c is '\'' or '"')
            {
                var sb = new StringBuilder();
                i++;
                while (true)
                {
                    if (i >= source.Length) throw new ExpressionException("Unterminated string", source, start);
                    var ch = source[i++];
                    if (ch == c) break;
                    if (ch == '\\' && i < source.Length) { sb.Append(source[i++] switch { 'n' => '\n', 't' => '\t', var e => e }); continue; }
                    sb.Append(ch);
                }
                tokens.Add(new Token(TokenKind.String, sb.ToString(), start));
                continue;
            }

            string two = i + 1 < source.Length ? source.Substring(i, 2) : "";
            TokenKind? twoKind = two switch
            {
                "==" => TokenKind.Eq, "!=" => TokenKind.NotEq, "<=" => TokenKind.LtEq, ">=" => TokenKind.GtEq,
                "&&" => TokenKind.And, "||" => TokenKind.Or,
                _ => null,
            };
            if (twoKind is { } tk) { tokens.Add(new Token(tk, two, start)); i += 2; continue; }

            TokenKind? oneKind = c switch
            {
                '.' => TokenKind.Dot, ',' => TokenKind.Comma, ':' => TokenKind.Colon, '?' => TokenKind.Question,
                '|' => TokenKind.Pipe, '(' => TokenKind.LParen, ')' => TokenKind.RParen,
                '[' => TokenKind.LBracket, ']' => TokenKind.RBracket,
                '+' => TokenKind.Plus, '-' => TokenKind.Minus, '*' => TokenKind.Star, '/' => TokenKind.Slash, '%' => TokenKind.Percent,
                '<' => TokenKind.Lt, '>' => TokenKind.Gt, '!' => TokenKind.Not,
                _ => null,
            };
            if (oneKind is { } ok) { tokens.Add(new Token(ok, c.ToString(), start)); i++; continue; }

            throw new ExpressionException($"Unexpected character '{c}'", source, start);
        }
        tokens.Add(new Token(TokenKind.End, "", source.Length));
        return tokens;
    }
}
