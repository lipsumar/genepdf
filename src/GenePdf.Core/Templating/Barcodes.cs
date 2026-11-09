using System.Globalization;
using System.Text;
using System.Xml.Linq;
using ZXing;
using ZXing.Common;

namespace GenePdf.Templating;

/// <summary>
/// <c>&lt;g:barcode type="qr" value="{{ expr }}" width="25mm"/&gt;</c> → inline SVG (vector: sharp in print, no JavaScript,
/// nothing to fetch). Attributes have already been interpolated when this runs. An empty value renders nothing.
/// </summary>
internal static class Barcodes
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private sealed record Kind(BarcodeFormat Format, bool TwoD, string DefaultWidth = "25mm");

    private static readonly Dictionary<string, Kind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["qr"] = new(BarcodeFormat.QR_CODE, true),
        ["datamatrix"] = new(BarcodeFormat.DATA_MATRIX, true, "18mm"),
        ["pdf417"] = new(BarcodeFormat.PDF_417, true, "60mm"),
        ["code128"] = new(BarcodeFormat.CODE_128, false),
        ["code39"] = new(BarcodeFormat.CODE_39, false),
        ["ean13"] = new(BarcodeFormat.EAN_13, false),
        ["ean8"] = new(BarcodeFormat.EAN_8, false),
        ["upca"] = new(BarcodeFormat.UPC_A, false),
        ["itf"] = new(BarcodeFormat.ITF, false),
    };

    /// <summary>Default width of one bar module for 1D codes, in mm (0.33 mm = 100% EAN size, scans reliably).</summary>
    private const double ModuleMm = 0.33;

    public static XNode? Render(XElement e)
    {
        var typeName = e.Attribute("type")?.Value;
        if (string.IsNullOrWhiteSpace(typeName) || !Kinds.TryGetValue(typeName.Trim(), out var kind))
            throw TemplateException.At(e, $"<g:barcode> needs type=\"…\", one of: {string.Join(", ", Kinds.Keys)}");
        var value = e.Attribute("value")?.Value;
        if (string.IsNullOrEmpty(value)) return null;

        BitMatrix matrix;
        try
        {
            var hints = new Dictionary<EncodeHintType, object>
            {
                // Quiet zone, in modules. Part of the SVG so that the code scans wherever it is placed.
                [EncodeHintType.MARGIN] = kind.TwoD ? 2 : 10,
                [EncodeHintType.CHARACTER_SET] = "UTF-8",
            };
            if (kind.Format == BarcodeFormat.QR_CODE)
                hints[EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.M;
            matrix = new MultiFormatWriter().encode(value, kind.Format, 0, 0, hints);
        }
        catch (Exception ex) when (ex is ArgumentException or WriterException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw TemplateException.At(e, $"Cannot encode '{value}' as {typeName}: {ex.Message}");
        }

        var rows = kind.TwoD ? matrix.Height : 1; // 1D: one row, stretched to the height
        var path = new StringBuilder();
        for (var y = 0; y < rows; y++)
            for (var x = 0; x < matrix.Width; x++)
            {
                if (!matrix[x, y]) continue;
                var start = x;
                while (x + 1 < matrix.Width && matrix[x + 1, y]) x++;
                path.Append(CultureInfo.InvariantCulture, $"M{start} {y}h{x - start + 1}v1h-{x - start + 1}z");
            }

        var width = e.Attribute("width")?.Value;
        var height = e.Attribute("height")?.Value;
        if (kind.TwoD)
        {
            if (width is null && height is null) width = kind.DefaultWidth; // the other side follows the aspect ratio
        }
        else
        {
            width ??= (matrix.Width * ModuleMm).ToString("0.##", CultureInfo.InvariantCulture) + "mm";
            height ??= "12mm";
        }

        var style = (width is null ? "" : $"width:{width};") + (height is null ? "" : $"height:{height};")
                    + "display:inline-block;vertical-align:top;" + e.Attribute("style")?.Value;
        return new XElement(Svg + "svg",
            new XAttribute("viewBox", $"0 0 {matrix.Width} {rows}"),
            kind.TwoD ? null : new XAttribute("preserveAspectRatio", "none"),
            new XAttribute("shape-rendering", "crispEdges"),
            new XAttribute("role", "img"),
            new XAttribute("aria-label", value),
            e.Attribute("class") is { } cls ? new XAttribute("class", cls.Value) : null,
            new XAttribute("style", style),
            e.Attributes().Where(a => a.Name.Namespace == SourceInfo.Ns),
            new XElement(Svg + "rect", new XAttribute("width", matrix.Width), new XAttribute("height", rows), new XAttribute("fill", "#fff")),
            new XElement(Svg + "path", new XAttribute("d", path.ToString()), new XAttribute("fill", "#000")));
    }
}
