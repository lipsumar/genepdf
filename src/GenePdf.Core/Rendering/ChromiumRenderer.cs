using System.Text;
using GenePdf.Composition;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PuppeteerSharp;
using PuppeteerSharp.Media;

namespace GenePdf.Rendering;

public sealed class ChromiumOptions
{
    /// <summary>Path to the Chromium/Chrome executable. Required (no browser download at runtime).</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>Maximum number of PDFs rendered at the same time (each uses a browser tab).</summary>
    public int MaxConcurrency { get; set; } = 2;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Chromium's sandbox needs privileges that containers usually don't have.</summary>
    public bool NoSandbox { get; set; } = true;
}

/// <summary>
/// Renders composed HTML to PDF with headless Chromium. One browser process is shared; each render uses
/// its own tab with JavaScript disabled and every network request blocked (assets are inlined as data: URIs).
/// </summary>
public sealed class ChromiumRenderer(ChromiumOptions options, ILogger<ChromiumRenderer>? logger = null) : IAsyncDisposable
{
    /// <summary>The page is served from this fake origin via request interception; nothing else is reachable.</summary>
    private const string DocumentUrl = "https://document.genepdf.invalid/";

    private readonly ILogger _logger = logger ?? NullLogger<ChromiumRenderer>.Instance;
    private readonly SemaphoreSlim _concurrency = new(Math.Max(1, options.MaxConcurrency));
    private readonly SemaphoreSlim _launchLock = new(1, 1);
    private IBrowser? _browser;

    public string? BrowserVersion { get; private set; }

    public async Task<byte[]> RenderAsync(ComposedDocument doc, CancellationToken ct = default)
    {
        await _concurrency.WaitAsync(ct);
        try
        {
            var browser = await GetBrowserAsync();
            await using var page = await browser.NewPageAsync();
            page.DefaultTimeout = (int)options.Timeout.TotalMilliseconds;
            await page.SetJavaScriptEnabledAsync(false);
            await page.SetRequestInterceptionAsync(true);
            var html = Encoding.UTF8.GetBytes(doc.BodyHtml);
            page.Request += async (_, e) =>
            {
                try
                {
                    if (e.Request.Url == DocumentUrl)
                        await e.Request.RespondAsync(new ResponseData { Status = System.Net.HttpStatusCode.OK, ContentType = "text/html; charset=utf-8", BodyData = html });
                    else if (e.Request.Url.StartsWith("data:", StringComparison.Ordinal))
                        await e.Request.ContinueAsync();
                    else
                    {
                        _logger.LogWarning("Blocked request from PDF template: {Url}", e.Request.Url);
                        await e.Request.AbortAsync();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Request interception failed for {Url}", e.Request.Url);
                }
            };

            await page.GoToAsync(DocumentUrl, new NavigationOptions { WaitUntil = [WaitUntilNavigation.Load] });
            await page.EvaluateExpressionHandleAsync("document.fonts.ready").ConfigureAwait(false);

            var hasHeaderFooter = doc.HeaderHtml is not null || doc.FooterHtml is not null;
            return await page.PdfDataAsync(new PdfOptions
            {
                PreferCSSPageSize = true,
                PrintBackground = true,
                DisplayHeaderFooter = hasHeaderFooter,
                // An empty template still needs an element, otherwise Chromium prints its default date/title.
                HeaderTemplate = doc.HeaderHtml ?? "<span></span>",
                FooterTemplate = doc.FooterHtml ?? "<span></span>",
                Tagged = true,
                Outline = false,
            });
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private async Task<IBrowser> GetBrowserAsync()
    {
        if (_browser is { IsConnected: true }) return _browser;
        await _launchLock.WaitAsync();
        try
        {
            if (_browser is { IsConnected: true }) return _browser;
            if (_browser is not null) await _browser.DisposeAsync();
            if (string.IsNullOrEmpty(options.ExecutablePath) || !File.Exists(options.ExecutablePath))
                throw new InvalidOperationException($"Chromium not found at '{options.ExecutablePath}'. Set GenePdf:ChromiumPath.");

            var args = new List<string> { "--disable-dev-shm-usage", "--disable-gpu", "--font-render-hinting=none", "--disable-extensions" };
            if (options.NoSandbox) args.AddRange(["--no-sandbox", "--disable-setuid-sandbox"]);
            _browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless = true,
                ExecutablePath = options.ExecutablePath,
                Args = args.ToArray(),
            });
            BrowserVersion = await _browser.GetVersionAsync();
            _logger.LogInformation("Started {Version}", BrowserVersion);
            return _browser;
        }
        finally
        {
            _launchLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
    }
}
