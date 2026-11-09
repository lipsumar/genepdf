using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace GenePdf.Rendering;

/// <summary>Technical data written into every PDF's XMP metadata (not visible on the page).</summary>
public sealed record PdfTechMetadata(
    string DocumentTemplate,
    string DocumentVersion,
    string LayoutTemplate,
    string LayoutVersion,
    string Lang,
    string DataSha256,
    DateTimeOffset GeneratedAt,
    string Generator,
    string? Renderer);

/// <summary>
/// Turns Chromium's PDF into PDF/A-2b, and stamps the technical metadata:
/// <list type="bullet">
/// <item>sRGB OutputIntent (Chromium paints in device RGB)</item>
/// <item>XMP metadata: PDF/A identification, Dublin Core, and the genepdf: properties, declared with a PDF/A extension schema</item>
/// <item>Info dictionary consistent with the XMP (required by PDF/A)</item>
/// <item>Annotations (links) flagged as printable</item>
/// </list>
/// Fonts are already embedded by Chromium. PDFsharp 6.2.4 is used on purpose: it is the last MIT-licensed version
/// (6.5+ is AGPL or commercial).
/// </summary>
public static class PdfAConverter
{
    public const string XmpNamespace = "https://github.com/lipsumar/genepdf/ns/1.0/";
    public const string XmpPrefix = "genepdf";
    private const string CreatorTool = "GenePdf";

    private static readonly Lazy<byte[]> SrgbProfile = new(() =>
    {
        using var s = typeof(PdfAConverter).Assembly.GetManifestResourceStream("GenePdf.Resources.sRGB.icc")
                      ?? throw new InvalidOperationException("sRGB.icc resource missing");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    });

    public static byte[] Convert(byte[] pdf, string title, PdfTechMetadata meta)
    {
        // Whole seconds, UTC: the Info and XMP representations must be equal.
        var date = new DateTimeOffset(meta.GeneratedAt.UtcDateTime.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

        byte[] saved;
        using (var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Modify))
        {
            var info = doc.Info.Elements;
            foreach (var key in info.Keys.ToList()) info.Remove(key);
            info.SetString("/Title", title);
            info.SetString("/Creator", CreatorTool);
            info.SetString("/Producer", meta.Renderer is null ? "GenePdf" : $"GenePdf; {meta.Renderer}");
            var pdfDate = $"D:{date:yyyyMMddHHmmss}+00'00'";
            info.SetString("/CreationDate", pdfDate);
            info.SetString("/ModDate", pdfDate);

            var catalog = doc.Internals.Catalog;
            AddOutputIntent(doc, catalog);
            SetMetadata(doc, catalog, "<placeholder/>");
            FixAnnotations(doc);

            using var output = new MemoryStream();
            doc.Save(output, false);
            saved = output.ToArray();
        }

        // PDFsharp always regenerates the XMP on save (without PDF/A identification or custom properties), and
        // rewrites /Producer. So we read back what it wrote and replace the metadata object in an incremental update.
        int metadataObject, metadataGeneration;
        string producer;
        using (var reread = PdfReader.Open(new MemoryStream(saved), PdfDocumentOpenMode.Import))
        {
            var reference = reread.Internals.Catalog.Elements.GetReference("/Metadata")
                            ?? throw new InvalidOperationException("PDFsharp did not write a /Metadata reference");
            metadataObject = reference.ObjectNumber;
            metadataGeneration = reference.GenerationNumber;
            producer = reread.Info.Producer;
        }
        return AppendMetadataUpdate(saved, metadataObject, metadataGeneration, Encoding.UTF8.GetBytes(BuildXmp(title, producer, date, meta)));
    }

    /// <summary>Appends an incremental update (ISO 32000-1 §7.5.6) that redefines one object as the XMP stream.</summary>
    private static byte[] AppendMetadataUpdate(byte[] pdf, int objectNumber, int generation, byte[] xmp)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var startxrefAt = text.LastIndexOf("startxref", StringComparison.Ordinal);
        var trailerAt = text.LastIndexOf("trailer", startxrefAt, StringComparison.Ordinal);
        if (startxrefAt < 0 || trailerAt < 0) throw new InvalidOperationException("Unexpected PDF structure: no classic trailer");
        var prevXref = long.Parse(Regex.Match(text[startxrefAt..], @"startxref\s+(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        var trailerDict = text[(text.IndexOf("<<", trailerAt, StringComparison.Ordinal) + 2)..text.LastIndexOf(">>", startxrefAt, StringComparison.Ordinal)];
        trailerDict = Regex.Replace(trailerDict, @"/Prev\s+\d+", "");

        using var ms = new MemoryStream();
        ms.Write(pdf);
        if (pdf[^1] != '\n') ms.WriteByte((byte)'\n');
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));

        var objectOffset = ms.Position;
        W($"{objectNumber} {generation} obj\n<< /Type /Metadata /Subtype /XML /Length {xmp.Length} >>\nstream\n");
        ms.Write(xmp);
        W("\nendstream\nendobj\n");

        var xrefOffset = ms.Position;
        W($"xref\n0 1\n0000000000 65535 f\r\n{objectNumber} 1\n{objectOffset:D10} {generation:D5} n\r\n");
        W($"trailer\n<<{trailerDict.TrimEnd()} /Prev {prevXref} >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }

    private static void AddOutputIntent(PdfDocument doc, PdfCatalog catalog)
    {
        var profile = new PdfDictionary(doc);
        profile.Elements.SetInteger("/N", 3);
        profile.CreateStream(SrgbProfile.Value);
        doc.Internals.AddObject(profile);

        var intent = new PdfDictionary(doc);
        intent.Elements.SetName("/Type", "/OutputIntent");
        intent.Elements.SetName("/S", "/GTS_PDFA1");
        intent.Elements.SetString("/OutputConditionIdentifier", "sRGB IEC61966-2.1");
        intent.Elements.SetString("/Info", "sRGB IEC61966-2.1");
        intent.Elements.SetString("/RegistryName", "http://www.color.org");
        intent.Elements.SetReference("/DestOutputProfile", profile);
        doc.Internals.AddObject(intent);

        catalog.Elements["/OutputIntents"] = new PdfArray(doc, intent.Reference!);
    }

    private static void SetMetadata(PdfDocument doc, PdfCatalog catalog, string xmp)
    {
        var metadata = new PdfDictionary(doc);
        metadata.Elements.SetName("/Type", "/Metadata");
        metadata.Elements.SetName("/Subtype", "/XML");
        metadata.CreateStream(Encoding.UTF8.GetBytes(xmp));
        doc.Internals.AddObject(metadata);
        catalog.Elements.SetReference("/Metadata", metadata);
    }

    /// <summary>PDF/A: every annotation must be printable and not hidden (flags: Print=4, Hidden=2, Invisible=1, NoView=32).</summary>
    private static void FixAnnotations(PdfDocument doc)
    {
        foreach (var page in doc.Pages)
        {
            if (page.Elements.GetArray("/Annots") is not { } annots) continue;
            for (var i = 0; i < annots.Elements.Count; i++)
            {
                if (annots.Elements.GetDictionary(i) is not { } annot) continue;
                var flags = annot.Elements.GetInteger("/F");
                annot.Elements.SetInteger("/F", (flags | 4) & ~(1 | 2 | 32));
            }
        }
    }

    private static string BuildXmp(string title, string producer, DateTimeOffset date, PdfTechMetadata m)
    {
        static string X(string? s) => SecurityElement.Escape(s ?? "");
        var d = date.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture);

        var properties = new (string Name, string Type, string Value, string Description)[]
        {
            ("documentTemplate", "Text", m.DocumentTemplate, "Name of the document template"),
            ("documentVersion", "Text", m.DocumentVersion, "Version of the document template"),
            ("layoutTemplate", "Text", m.LayoutTemplate, "Name of the layout template"),
            ("layoutVersion", "Text", m.LayoutVersion, "Version of the layout template"),
            ("lang", "Text", m.Lang, "Language of the document"),
            ("dataSha256", "Text", m.DataSha256, "SHA-256 of the JSON data the document was generated from"),
            ("generatedAt", "Date", m.GeneratedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), "Generation time"),
            ("generator", "Text", m.Generator, "Software that generated the document"),
            ("renderer", "Text", m.Renderer ?? "", "HTML renderer used"),
        };

        var sb = new StringBuilder();
        sb.Append("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");

        sb.Append("<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">\n");
        sb.Append("<pdfaid:part>2</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance>\n</rdf:Description>\n");

        sb.Append("<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n");
        sb.Append("<dc:format>application/pdf</dc:format>\n");
        sb.Append($"<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{X(title)}</rdf:li></rdf:Alt></dc:title>\n</rdf:Description>\n");

        sb.Append("<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">\n");
        sb.Append($"<xmp:CreatorTool>{CreatorTool}</xmp:CreatorTool><xmp:CreateDate>{d}</xmp:CreateDate><xmp:ModifyDate>{d}</xmp:ModifyDate><xmp:MetadataDate>{d}</xmp:MetadataDate>\n</rdf:Description>\n");

        sb.Append("<rdf:Description rdf:about=\"\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\">\n");
        sb.Append($"<pdf:Producer>{X(producer)}</pdf:Producer>\n</rdf:Description>\n");

        sb.Append($"<rdf:Description rdf:about=\"\" xmlns:{XmpPrefix}=\"{XmpNamespace}\">\n");
        foreach (var p in properties) sb.Append($"<{XmpPrefix}:{p.Name}>{X(p.Value)}</{XmpPrefix}:{p.Name}>\n");
        sb.Append("</rdf:Description>\n");

        // PDF/A only accepts custom XMP properties that are described by an extension schema.
        sb.Append("<rdf:Description rdf:about=\"\" xmlns:pdfaExtension=\"http://www.aiim.org/pdfa/ns/extension/\" xmlns:pdfaSchema=\"http://www.aiim.org/pdfa/ns/schema#\" xmlns:pdfaProperty=\"http://www.aiim.org/pdfa/ns/property#\">\n");
        sb.Append("<pdfaExtension:schemas><rdf:Bag><rdf:li rdf:parseType=\"Resource\">\n");
        sb.Append($"<pdfaSchema:schema>GenePdf technical metadata</pdfaSchema:schema><pdfaSchema:namespaceURI>{XmpNamespace}</pdfaSchema:namespaceURI><pdfaSchema:prefix>{XmpPrefix}</pdfaSchema:prefix>\n");
        sb.Append("<pdfaSchema:property><rdf:Seq>\n");
        foreach (var p in properties)
            sb.Append($"<rdf:li rdf:parseType=\"Resource\"><pdfaProperty:name>{p.Name}</pdfaProperty:name><pdfaProperty:valueType>{p.Type}</pdfaProperty:valueType><pdfaProperty:category>internal</pdfaProperty:category><pdfaProperty:description>{X(p.Description)}</pdfaProperty:description></rdf:li>\n");
        sb.Append("</rdf:Seq></pdfaSchema:property>\n</rdf:li></rdf:Bag></pdfaExtension:schemas>\n</rdf:Description>\n");

        sb.Append("</rdf:RDF>\n</x:xmpmeta>\n");
        sb.Append(new string(' ', 2000)).Append('\n'); // padding, conventional for in-place XMP edits
        sb.Append("<?xpacket end=\"w\"?>");
        return sb.ToString();
    }

    /// <summary>Reads the genepdf: properties back from a PDF (for tests and the dev UI).</summary>
    public static IReadOnlyDictionary<string, string> ReadTechMetadata(byte[] pdf)
    {
        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        var md = doc.Internals.Catalog.Elements.GetDictionary("/Metadata");
        var result = new Dictionary<string, string>();
        if (md?.Stream?.Value is not { } bytes) return result;
        var xml = System.Xml.Linq.XDocument.Parse(Encoding.UTF8.GetString(bytes).TrimStart('﻿'));
        foreach (var e in xml.Descendants().Where(e => e.Name.NamespaceName == XmpNamespace && !e.HasElements))
            result[e.Name.LocalName] = e.Value;
        return result;
    }
}
