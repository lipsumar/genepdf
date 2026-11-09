using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenePdf.Expressions;

/// <summary>
/// Runtime value model. Values are: null, bool, decimal, string, JsonObject, or a list (JsonArray / IList).
/// </summary>
public static class Values
{
    public static object? FromNode(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o => o,
        JsonArray a => a,
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetValue(out decimal d) ? d : decimal.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture),
            JsonValueKind.String => v.GetValue<string>(),
            _ => null,
        },
        _ => null,
    };

    public static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        decimal d => d != 0,
        string s => s.Length > 0,
        JsonArray a => a.Count > 0,
        ICollection c => c.Count > 0,
        _ => true,
    };

    public static decimal? ToNumber(object? v) => v switch
    {
        decimal d => d,
        bool b => b ? 1 : 0,
        string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    public static IEnumerable<object?>? AsList(object? v) => v switch
    {
        JsonArray a => a.Select(FromNode),
        string => null,
        IEnumerable e => e.Cast<object?>(),
        _ => null,
    };

    public static object? Member(object? target, string name) => target switch
    {
        JsonObject o => o.TryGetPropertyValue(name, out var n) ? FromNode(n) : null,
        JsonArray a when name is "length" or "count" => (decimal)a.Count,
        ICollection c when name is "length" or "count" => (decimal)c.Count,
        string s when name == "length" => (decimal)s.Length,
        _ => null,
    };

    public static object? Index(object? target, object? index)
    {
        if (index is string key) return Member(target, key);
        if (ToNumber(index) is not { } d) return null;
        var i = (int)d;
        return target switch
        {
            JsonArray a => i >= 0 && i < a.Count ? FromNode(a[i]) : i < 0 && -i <= a.Count ? FromNode(a[a.Count + i]) : null,
            IList l => i >= 0 && i < l.Count ? l[i] : null,
            _ => null,
        };
    }

    public static bool AreEqual(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is decimal da && ToNumber(b) is { } db && b is not bool) return da == db;
        if (b is decimal && ToNumber(a) is { } da2 && a is not bool) return da2 == (decimal)b;
        return a switch
        {
            string sa => b is string sb && string.Equals(sa, sb, StringComparison.Ordinal),
            bool ba => b is bool bb && ba == bb,
            _ => ReferenceEquals(a, b),
        };
    }

    public static int? Compare(object? a, object? b)
    {
        if (a is null || b is null) return null;
        if (a is string sa && b is string sb) return string.CompareOrdinal(sa, sb);
        if (ToNumber(a) is { } da && ToNumber(b) is { } db) return da.CompareTo(db);
        return null;
    }

    public static object? Arithmetic(object? a, object? b, Func<decimal, decimal, decimal?> op)
        => ToNumber(a) is { } da && ToNumber(b) is { } db ? op(da, db) : null;

    /// <summary>'+' adds numbers and concatenates as soon as one side is a string.</summary>
    public static object? Add(object? a, object? b, CultureInfo culture)
    {
        if (a is string || b is string) return ToDisplayString(a, culture) + ToDisplayString(b, culture);
        return Arithmetic(a, b, (x, y) => x + y);
    }

    public static string ToDisplayString(object? v, CultureInfo culture) => v switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        decimal d => d.ToString("G29", culture), // G29: no trailing zeros (2 * 10.5 → "21", not "21.0")
        JsonNode n => n.ToJsonString(),
        IFormattable f => f.ToString(null, culture),
        _ => v.ToString() ?? "",
    };
}
