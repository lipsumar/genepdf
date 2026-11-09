using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenePdf.Composition;
using GenePdf.Rendering;
using GenePdf.Templates;
using GenePdf.Templating;

namespace GenePdf;

public sealed class GenePdfOptions
{
    /// <summary>Folder that contains <c>documents/</c> and <c>layouts/</c>.</summary>
    public string TemplateRoot { get; set; } = "templates";

    public string? ChromiumPath { get; set; }

    public int MaxConcurrency { get; set; } = 2;

    /// <summary>Produce PDF/A-2b (default). When false, Chromium's output is only stamped with metadata.</summary>
    public bool PdfA { get; set; } = true;
}

/// <param name="Document">Document template name, e.g. "invoice-fr".</param>
/// <param name="Version">Template version; null picks the highest version (prefer pinning it).</param>
/// <param name="Json">The data, as JSON text. Its root must be an object.</param>
public sealed record PdfRequest(string Document, string? Version, string Json);

public sealed record PdfResult(
    byte[] Pdf,
    PdfTechMetadata Metadata,
    string Title,
    IReadOnlyList<Diagnostic> Diagnostics,
    TimeSpan Elapsed);

public interface IPdfGenerator
{
    Task<PdfResult> GenerateAsync(PdfRequest request, CancellationToken ct = default);

    /// <summary>Same as <see cref="GenerateAsync(PdfRequest, CancellationToken)"/> but reads templates from a custom source (dev UI: unsaved edits).</summary>
    Task<PdfResult> GenerateAsync(PdfRequest request, ITemplateSource source, CancellationToken ct = default);

    /// <summary>Only the HTML stage, for debugging templates.</summary>
    ComposedDocument Compose(PdfRequest request, ITemplateSource? source = null);
}

public sealed class PdfGenerator(GenePdfOptions options, ChromiumRenderer renderer) : IPdfGenerator
{
    public static readonly string GeneratorVersion =
        "GenePdf " + (typeof(PdfGenerator).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    private readonly ITemplateSource _defaultSource = new FileSystemTemplateSource(options.TemplateRoot);

    public ITemplateSource TemplateSource => _defaultSource;

    public Task<PdfResult> GenerateAsync(PdfRequest request, CancellationToken ct = default)
        => GenerateAsync(request, _defaultSource, ct);

    public async Task<PdfResult> GenerateAsync(PdfRequest request, ITemplateSource source, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var composed = Compose(request, source);
        var pdf = await renderer.RenderAsync(composed, ct);

        var meta = new PdfTechMetadata(
            composed.Document.Name, composed.Document.Version,
            composed.Layout.Name, composed.Layout.Version,
            composed.Lang,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.Json))),
            started, GeneratorVersion, renderer.BrowserVersion);

        pdf = options.PdfA ? PdfAConverter.Convert(pdf, composed.Title, meta) : pdf;
        return new PdfResult(pdf, meta, composed.Title, composed.Diagnostics, DateTimeOffset.UtcNow - started);
    }

    public ComposedDocument Compose(PdfRequest request, ITemplateSource? source = null)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(request.Json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new TemplateException($"Invalid JSON data: {ex.Message}", "data.json", (int?)ex.LineNumber + 1, ex);
        }
        if (node is not JsonObject data) throw new TemplateException("The JSON data must be an object", "data.json");

        var composer = new DocumentComposer(new TemplateRepository(source ?? _defaultSource));
        return composer.Compose(request.Document, request.Version, data);
    }
}
