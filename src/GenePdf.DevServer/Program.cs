using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using GenePdf;
using GenePdf.Rendering;
using GenePdf.Templates;
using GenePdf.Templating;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration.GetSection("GenePdf");
var templateRoot = Path.GetFullPath(config["TemplateRoot"] ?? Path.Combine(builder.Environment.ContentRootPath, "../../templates"));
var readOnly = config.GetValue("ReadOnly", false);

builder.Services.AddGenePdf(o =>
{
    o.TemplateRoot = templateRoot;
    o.ChromiumPath = config["ChromiumPath"] ?? FindChromium();
    o.MaxConcurrency = config.GetValue("MaxConcurrency", 2);
});
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("render", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = config.GetValue("RenderPerMinute", 120), Window = TimeSpan.FromMinutes(1) }));
});
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 5 * 1024 * 1024);

var app = builder.Build();
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception ex) when (ex is ArgumentException or TemplateException && !ctx.Response.HasStarted)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();

var repo = () => new TemplateRepository(new FileSystemTemplateSource(templateRoot));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/templates", () =>
{
    var r = repo();
    return Results.Ok(new
    {
        readOnly,
        generator = PdfGenerator.GeneratorVersion,
        documents = r.List(TemplateKind.Document),
        layouts = r.List(TemplateKind.Layout),
    });
});

// Everything the editor needs for one document version: its files, its layout's files, its samples.
app.MapGet("/api/documents/{name}/{version}", (string name, string version) =>
{
    var r = repo();
    var dir = TemplateRepository.DirectoryOf(TemplateKind.Document, name, version);
    var files = new List<object>();
    void AddFile(string path, string role) => files.Add(new { path, role, content = r.Source.ReadText(path) });

    AddFile($"{dir}/{TemplateRepository.DocumentFile}", "document");
    string? layoutName = null, layoutVersion = null, layoutError = null;
    try
    {
        var doc = r.LoadDocument(name, version);
        layoutName = doc.LayoutName;
        layoutVersion = doc.LayoutVersion;
        var layoutDir = TemplateRepository.DirectoryOf(TemplateKind.Layout, doc.LayoutName, doc.LayoutVersion);
        AddFile($"{layoutDir}/{TemplateRepository.LayoutFile}", "layout");
        foreach (var css in r.Source.ListFiles(layoutDir).Where(f => f.EndsWith(".css")))
            AddFile($"{layoutDir}/{css}", "stylesheet");
    }
    catch (TemplateException ex)
    {
        layoutError = ex.Message; // still let the user open and fix the document
    }

    var samples = r.Source.ListFiles($"{dir}/{TemplateRepository.SamplesDir}")
        .Where(f => f.EndsWith(".json"))
        .Select(f => new { name = Path.GetFileNameWithoutExtension(f), path = $"{dir}/{TemplateRepository.SamplesDir}/{f}", content = r.Source.ReadText($"{dir}/{TemplateRepository.SamplesDir}/{f}") })
        .ToList();
    return Results.Ok(new { name, version, layout = layoutName, layoutVersion, layoutError, files, samples });
});

// Dev render: unsaved edits are sent as overrides and layered over the files on disk.
app.MapPost("/api/render", async (RenderRequest req, PdfGenerator generator, CancellationToken ct) =>
{
    try
    {
        var source = new OverlayTemplateSource(generator.TemplateSource, CheckOverrides(req.Overrides));
        if (req.Html)
        {
            var composed = generator.Compose(new PdfRequest(req.Document, req.Version, req.Json), source);
            return Results.Ok(new { html = composed.BodyHtml, header = composed.HeaderHtml, footer = composed.FooterHtml, diagnostics = composed.Diagnostics });
        }
        var result = await generator.GenerateAsync(new PdfRequest(req.Document, req.Version, req.Json), source, ct);
        return Results.Ok(new
        {
            pdf = Convert.ToBase64String(result.Pdf),
            title = result.Title,
            metadata = PdfAConverter.ReadTechMetadata(result.Pdf),
            diagnostics = result.Diagnostics,
            elapsedMs = (int)result.Elapsed.TotalMilliseconds,
            size = result.Pdf.Length,
        });
    }
    catch (TemplateException ex)
    {
        return Results.UnprocessableEntity(new { error = ex.Detail, file = ex.File, line = ex.Line });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireRateLimiting("render");

// What the host application would call: data in, PDF out.
app.MapPost("/api/generate/{document}/{version?}", async (string document, string? version, HttpRequest http, IPdfGenerator generator, CancellationToken ct) =>
{
    using var reader = new StreamReader(http.Body);
    var json = await reader.ReadToEndAsync(ct);
    try
    {
        var result = await generator.GenerateAsync(new PdfRequest(document, version, json), ct);
        return Results.File(result.Pdf, "application/pdf", $"{document}-{result.Metadata.DocumentVersion}.pdf");
    }
    catch (TemplateException ex)
    {
        return Results.UnprocessableEntity(new { error = ex.Detail, file = ex.File, line = ex.Line });
    }
}).RequireRateLimiting("render");

app.MapPut("/api/files", async (SaveRequest req) =>
{
    if (readOnly) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var path = CheckEditablePath(req.Path);
    var full = new FileSystemTemplateSource(templateRoot).FullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    await File.WriteAllTextAsync(full, req.Content);
    return Results.Ok(new { saved = path });
});

// Versioning = folders: copy documents/<name>/<from> to documents/<name>/<to> and update the version attribute.
app.MapPost("/api/documents/{name}/{version}/copy", (string name, string version, CopyRequest req) =>
{
    if (readOnly) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var fs = new FileSystemTemplateSource(templateRoot);
    var from = fs.FullPath(TemplateRepository.DirectoryOf(TemplateKind.Document, name, version));
    var toName = req.Name ?? name;
    var to = fs.FullPath(TemplateRepository.DirectoryOf(TemplateKind.Document, toName, req.Version));
    if (Directory.Exists(to)) return Results.Conflict(new { error = $"{toName} {req.Version} already exists" });

    foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
    {
        var target = Path.Combine(to, Path.GetRelativePath(from, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target);
    }
    var docFile = Path.Combine(to, TemplateRepository.DocumentFile);
    var text = File.ReadAllText(docFile);
    File.WriteAllText(docFile, VersionAttr().Replace(text, $"$1version=\"{req.Version}\"", 1));
    return Results.Ok(new { name = toName, version = req.Version });
});

app.Run();

static Dictionary<string, string> CheckOverrides(Dictionary<string, string>? overrides)
{
    var result = new Dictionary<string, string>();
    foreach (var (path, content) in overrides ?? []) result[CheckEditablePath(path)] = content;
    return result;
}

static string CheckEditablePath(string path)
{
    var p = TemplatePath.Normalize(path);
    if (!(p.StartsWith("documents/") || p.StartsWith("layouts/")) || !(p.EndsWith(".xhtml") || p.EndsWith(".css") || p.EndsWith(".json")))
        throw new ArgumentException($"Not an editable template file: {path}");
    return p;
}

static string? FindChromium() => new[]
{
    "/usr/bin/chromium", "/usr/bin/chromium-browser", "/usr/bin/google-chrome",
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
    "/Applications/Chromium.app/Contents/MacOS/Chromium",
}.FirstOrDefault(File.Exists);

record RenderRequest(string Document, string? Version, string Json, Dictionary<string, string>? Overrides, bool Html = false);
record SaveRequest(string Path, string Content);
record CopyRequest(string Version, string? Name);

partial class Program
{
    [GeneratedRegex("(<g:document\\b[^>]*?\\s)version=\"[^\"]*\"")]
    private static partial Regex VersionAttr();
}
