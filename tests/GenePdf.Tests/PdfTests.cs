using System.Text;
using GenePdf.Rendering;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace GenePdf.Tests;

public class PdfTests
{
    private static readonly PdfTechMetadata Meta = new("invoice-fr", "1.2", "letter", "1.0", "fr-BE", "abc123",
        new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero), "GenePdf 0.1.0", "Chrome/156");

    private static byte[] SimplePdf()
    {
        var doc = new PdfDocument();
        doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    [Fact]
    public void Converter_writes_pdfa_identification_tech_metadata_and_output_intent()
    {
        var pdf = PdfAConverter.Convert(SimplePdf(), "Facture <1>", Meta);

        var meta = PdfAConverter.ReadTechMetadata(pdf);
        Assert.Equal("invoice-fr", meta["documentTemplate"]);
        Assert.Equal("1.2", meta["documentVersion"]);
        Assert.Equal("letter", meta["layoutTemplate"]);
        Assert.Equal("1.0", meta["layoutVersion"]);
        Assert.Equal("abc123", meta["dataSha256"]);

        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        var xmp = Encoding.UTF8.GetString(doc.Internals.Catalog.Elements.GetDictionary("/Metadata")!.Stream.Value);
        Assert.Contains("<pdfaid:part>2</pdfaid:part>", xmp);
        Assert.Contains("<pdfaid:conformance>B</pdfaid:conformance>", xmp);
        Assert.Contains("Facture &lt;1&gt;", xmp);
        Assert.Contains($"<pdf:Producer>{doc.Info.Producer}</pdf:Producer>", xmp); // Info and XMP must agree
        Assert.Equal("Facture <1>", doc.Info.Title);
        Assert.NotNull(doc.Internals.Catalog.Elements.GetArray("/OutputIntents"));
    }

    [Fact]
    public void Converter_output_ends_with_a_single_eof()
    {
        var pdf = PdfAConverter.Convert(SimplePdf(), "t", Meta);
        Assert.EndsWith("%%EOF\n", Encoding.Latin1.GetString(pdf[^10..]));
    }
}
