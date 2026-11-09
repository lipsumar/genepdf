using GenePdf.Rendering;

namespace GenePdf.Tests;

/// <summary>
/// Renders the real sample templates through Chromium. Runs when GENEPDF__CHROMIUMPATH points to a browser
/// (it does in the dev container: scripts/dotnet.sh test). PDF/A validity itself is checked with veraPDF,
/// see scripts/verify-pdfa.sh.
/// </summary>
public class EndToEndTests
{
    private static readonly string? Chromium = Environment.GetEnvironmentVariable("GENEPDF__CHROMIUMPATH");

    private static string TemplateRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "templates"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "templates");
    }

    [SkippableTheory]
    [InlineData("invoice-fr", "default")]
    [InlineData("invoice-fr", "overdue-long")]
    [InlineData("reminder-en", "final-notice")]
    [InlineData("pension-decision-fr", "default")]
    [InlineData("pension-decision-fr", "survivor")]
    [InlineData("pension-decision-fr", "default", "1.1")]
    public async Task Renders_samples_to_pdf_with_metadata(string document, string sample, string version = "1.0")
    {
        Skip.If(string.IsNullOrEmpty(Chromium) || !File.Exists(Chromium), "Chromium not available");
        var root = TemplateRoot();
        await using var renderer = new ChromiumRenderer(new ChromiumOptions { ExecutablePath = Chromium });
        var generator = new PdfGenerator(new GenePdfOptions { TemplateRoot = root }, renderer);
        var json = await File.ReadAllTextAsync(Path.Combine(root, "documents", document, version, "samples", sample + ".json"));

        var result = await generator.GenerateAsync(new PdfRequest(document, version, json));

        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(result.Pdf, 0, 5));
        Assert.Empty(result.Diagnostics);
        var meta = PdfAConverter.ReadTechMetadata(result.Pdf);
        Assert.Equal(document, meta["documentTemplate"]);
        Assert.Equal(version, meta["documentVersion"]);
        Assert.False(string.IsNullOrEmpty(meta["layoutTemplate"]));
    }
}
