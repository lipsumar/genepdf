using GenePdf.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GenePdf;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IPdfGenerator"/> as a singleton (it owns one shared Chromium process).
    /// <code>services.AddGenePdf(o => { o.TemplateRoot = "/data/templates"; o.ChromiumPath = "/usr/bin/chromium"; });</code>
    /// </summary>
    public static IServiceCollection AddGenePdf(this IServiceCollection services, Action<GenePdfOptions> configure)
    {
        var options = new GenePdfOptions();
        configure(options);
        services.AddSingleton(options);
        services.AddSingleton(sp => new ChromiumRenderer(
            new ChromiumOptions { ExecutablePath = options.ChromiumPath, MaxConcurrency = options.MaxConcurrency },
            sp.GetService<ILogger<ChromiumRenderer>>()));
        services.AddSingleton<PdfGenerator>();
        services.AddSingleton<IPdfGenerator>(sp => sp.GetRequiredService<PdfGenerator>());
        return services;
    }
}
