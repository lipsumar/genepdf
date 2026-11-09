using System.Globalization;
using System.Text.Json.Nodes;
using GenePdf.Expressions;

namespace GenePdf.Tests;

public class ExpressionTests
{
    private static readonly JsonObject Data = JsonNode.Parse("""
        {
          "name": "Atelier Nord",
          "total": 1546.08,
          "paid": false,
          "count": 3,
          "empty": [],
          "customer": { "vat": "BE 0987", "country": "BE" },
          "lines": [ { "qty": 2, "price": 10.5 }, { "qty": 1, "price": 4 } ],
          "date": "2026-11-09",
          "nothing": null
        }
        """)!.AsObject();

    private static (object? Value, List<string> Warnings) Eval(string expr, string culture = "fr-BE")
    {
        var warnings = new List<string>();
        var scope = new Scope(Data, CultureInfo.GetCultureInfo(culture), warnings.Add);
        return (Expression.Parse(expr).Evaluate(scope), warnings);
    }

    private static string Str(string expr, string culture = "fr-BE")
        => Values.ToDisplayString(Eval(expr, culture).Value, CultureInfo.GetCultureInfo(culture));

    [Theory]
    [InlineData("name", "Atelier Nord")]
    [InlineData("customer.vat", "BE 0987")]
    [InlineData("customer['country']", "BE")]
    [InlineData("lines[1].price", "4")]
    [InlineData("lines[-1].qty", "1")]
    [InlineData("lines.length", "2")]
    [InlineData("lines[0].qty * lines[0].price", "21")]
    [InlineData("'a' + 1", "a1")]
    [InlineData("count > 2 ? 'many' : 'few'", "many")]
    [InlineData("paid ? 'yes' : 'no'", "no")]
    [InlineData("not paid and count == 3", "true")]
    [InlineData("paid || customer.country == 'BE'", "true")]
    [InlineData("-count + 1", "-2")]
    [InlineData("(1 + 2) * 3", "9")]
    [InlineData("10 / 0", "")]
    public void Evaluates(string expr, string expected) => Assert.Equal(expected, Str(expr, "en"));

    [Theory]
    [InlineData("total | money:'EUR'", "1 546,08 €")]
    [InlineData("total | number:'0.0'", "1546,1")]
    [InlineData("date | date:'d MMMM yyyy'", "9 novembre 2026")]
    [InlineData("name | upper", "ATELIER NORD")]
    [InlineData("nothing | default:'n/a'", "n/a")]
    [InlineData("lines | sum:'qty'", "3")]
    [InlineData("lines | count", "2")]
    [InlineData("empty | count", "0")]
    [InlineData("total | round:0", "1546")]
    public void Filters_use_document_culture(string expr, string expected)
        => Assert.Equal(expected, Str(expr).Replace(' ', ' ').Replace(' ', ' '));

    [Fact]
    public void Unknown_variable_warns_and_yields_null()
    {
        var (value, warnings) = Eval("custmer.vat");
        Assert.Null(value);
        Assert.Contains(warnings, w => w.Contains("custmer"));
    }

    [Fact]
    public void Missing_member_of_existing_object_warns()
    {
        var (_, warnings) = Eval("customer.vta");
        Assert.Contains(warnings, w => w.Contains("customer.vta"));
    }

    [Theory]
    [InlineData("customer.vta | default:'x'")]
    [InlineData("customer.vta ? 1 : 2")]
    [InlineData("nothing")]
    public void Optional_values_do_not_warn(string expr) => Assert.Empty(Eval(expr).Warnings);

    [Theory]
    [InlineData("name |")]
    [InlineData("name | nope")]
    [InlineData("(1 + 2")]
    [InlineData("'unterminated")]
    [InlineData("a ? b")]
    public void Syntax_errors_throw(string expr) => Assert.Throws<ExpressionException>(() => Expression.Parse(expr));

    [Fact]
    public void Each_expression_parses()
    {
        var each = EachExpression.Parse("line in invoice.lines");
        Assert.Equal("line", each.ItemName);
        Assert.Equal("invoice.lines", each.Collection.Source);
    }
}
