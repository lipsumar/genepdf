using System.Globalization;

namespace GenePdf.Expressions;

public delegate object? Filter(object? input, object?[] args, CultureInfo culture);

/// <summary>
/// Built-in filters, used as <c>expr | name:arg1, arg2</c>. Formatting filters use the document's culture
/// (the <c>lang</c> attribute of the document template).
/// </summary>
public static class Filters
{
    private static readonly Dictionary<string, Filter> Registry = new(StringComparer.Ordinal)
    {
        ["upper"] = (v, _, c) => Str(v, c).ToUpper(c),
        ["lower"] = (v, _, c) => Str(v, c).ToLower(c),
        ["trim"] = (v, _, c) => Str(v, c).Trim(),
        ["default"] = (v, a, _) => Values.Truthy(v) ? v : Arg(a, 0),
        ["length"] = (v, _, _) => Length(v),
        ["count"] = (v, _, _) => Length(v),
        ["first"] = (v, _, _) => Values.Index(v, 0m),
        ["last"] = (v, _, _) => Values.Index(v, -1m),
        ["abs"] = (v, _, _) => Values.ToNumber(v) is { } d ? Math.Abs(d) : null,
        ["round"] = (v, a, _) => Values.ToNumber(v) is { } d ? Math.Round(d, (int)(Values.ToNumber(Arg(a, 0)) ?? 0), MidpointRounding.AwayFromZero) : null,
        ["join"] = (v, a, c) => Values.AsList(v) is { } l ? string.Join(Values.ToDisplayString(Arg(a, 0) ?? ", ", c), l.Select(x => Values.ToDisplayString(x, c))) : Str(v, c),
        ["sum"] = (v, a, _) => Sum(v, Arg(a, 0) as string),
        ["number"] = (v, a, c) => Values.ToNumber(v) is { } d ? d.ToString(Arg(a, 0) as string ?? "#,##0.##", c) : null,
        ["money"] = (v, a, c) => Money(v, Arg(a, 0) as string, Values.ToNumber(Arg(a, 1)), c),
        ["date"] = (v, a, c) => Date(v, Arg(a, 0) as string, c),
    };

    public static Filter? Get(string name) => Registry.GetValueOrDefault(name);

    public static IEnumerable<string> Names => Registry.Keys;

    private static object? Arg(object?[] args, int i) => i < args.Length ? args[i] : null;

    private static string Str(object? v, CultureInfo c) => Values.ToDisplayString(v, c);

    private static object? Length(object? v) => v switch
    {
        null => 0m,
        string s => (decimal)s.Length,
        _ => Values.AsList(v) is { } l ? (decimal)l.Count() : null,
    };

    private static object? Sum(object? v, string? field)
    {
        if (Values.AsList(v) is not { } list) return null;
        decimal total = 0;
        foreach (var item in list)
            total += Values.ToNumber(field is null ? item : Values.Member(item, field)) ?? 0;
        return total;
    }

    private static readonly Dictionary<string, string> CurrencySymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EUR"] = "€", ["USD"] = "$", ["GBP"] = "£", ["JPY"] = "¥", ["CHF"] = "CHF",
    };

    private static object? Money(object? v, string? currency, decimal? decimals, CultureInfo culture)
    {
        if (Values.ToNumber(v) is not { } d) return null;
        var nfi = (NumberFormatInfo)culture.NumberFormat.Clone();
        if (currency is not null)
            nfi.CurrencySymbol = CurrencySymbols.GetValueOrDefault(currency, currency);
        if (decimals is { } dec) nfi.CurrencyDecimalDigits = (int)dec;
        return d.ToString("C", nfi);
    }

    private static object? Date(object? v, string? format, CultureInfo culture)
    {
        if (v is not string s || string.IsNullOrWhiteSpace(s)) return null;
        format ??= "d";
        if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnly))
            return dateOnly.ToString(format, culture);
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto))
            return dto.ToString(format, culture);
        return s;
    }
}
