# GenePdf

PDF generation from XHTML templates and JSON data, as a .NET 10 module. Output is **PDF/A-2b**, and every PDF
carries hidden technical metadata saying which template version produced it.

```
JSON data ──┐
            ├─► document template (XHTML) ─► layout (XHTML + CSS) ─► HTML ─► Chromium ─► PDF ─► PDF/A-2b + XMP metadata
templates/ ─┘    g:if, g:each, {{ }}           header/footer, slots
```

## Projects

| Path | What |
|---|---|
| `src/GenePdf.Core` | The module: templates, expressions, composition, Chromium rendering, PDF/A. Reference this from the host app. |
| `src/GenePdf.DevServer` | Studio (template editor + live PDF preview) and an HTTP API. Also what the hosted demo runs. |
| `tests/GenePdf.Tests` | Unit tests + end-to-end renders through Chromium. |
| `templates/` | Example layout and documents. |

## Templates

Versioning is done with folders. The root element's `version` attribute must match its folder (a copied folder
whose attribute wasn't updated is an error, not a silent mix-up).

```
templates/
  layouts/letter/1.0/layout.xhtml        page structure, header/footer, stylesheets, fonts, logo
  documents/invoice-fr/1.0/document.xhtml
  documents/invoice-fr/1.0/samples/*.json   test data for the studio
```

One template per language (`invoice-fr`, `invoice-nl`, …). The `lang` attribute only drives formatting
(dates, numbers, currency).

**Document** — pins one layout version, fills its slots:

```xml
<g:document xmlns="http://www.w3.org/1999/xhtml" xmlns:g="urn:genepdf"
            version="1.0" layout="letter" layout-version="1.0" lang="fr-BE"
            title="Facture {{ invoice.number }}">
  <g:fill slot="subject">Facture n° {{ invoice.number }}</g:fill>
  <!-- anything outside a g:fill goes to the "body" slot -->
  <tr g:each="line in invoice.lines">…</tr>
  <div g:if="invoice.paid">…</div><div g:else="">…</div>
</g:document>
```

**Layout** — the overall look; owns all stylesheets:

```xml
<g:layout xmlns="http://www.w3.org/1999/xhtml" xmlns:g="urn:genepdf" version="1.0">
  <g:stylesheet href="layout.css"/>
  <g:page-header>…</g:page-header>             <!-- optional, every page -->
  <g:page-footer>… <g:page-number/>/<g:page-count/></g:page-footer>
  <g:body>
    <header class="letterhead">{{ sender.name }} … {{ recipient.name }}</header>   <!-- page 1 -->
    <h1><g:slot name="subject"/></h1>
    <g:slot name="body"/>
  </g:body>
</g:layout>
```

### Syntax

| | |
|---|---|
| `{{ expr }}` | Value in text or attribute. An attribute that is only `{{ expr }}` is dropped when null/false. |
| `g:if` / `g:else-if` / `g:else` | On consecutive sibling elements. |
| `g:each="x in expr"` | Repeats the element. `loop.index`, `loop.number`, `loop.first`, `loop.last`, `loop.count`. Evaluated before `g:if`, so `g:if` filters items. |
| `g:text="expr"` | Replaces the element's content. |
| `<g:block>` | Wrapper that disappears (condition/loop on several elements). |
| Operators | `. [] + - * / % == != < <= > >= and or not && \|\| ! a ? b : c` |
| Filters | `money:'EUR'` `number:'0.00'` `date:'d MMMM yyyy'` `upper` `lower` `trim` `default:x` `join:', '` `sum:'field'` `count` `first` `last` `round:n` `abs` |
| Entities | HTML named entities (`&nbsp;`, `&eacute;`…) are accepted. |

Values are HTML-escaped (templates are XML trees, there is no raw-HTML output). Unknown `g:` elements/attributes are
errors with file:line. A missing variable is a warning (shown in the studio), except in `g:if`, ternary conditions
and `default`, where "optional" is the point.

Assets (`<img src>`, CSS `url()`, fonts) are relative to the file that references them and get inlined as `data:` URIs.
Chromium runs with JavaScript disabled and every network request blocked.

### Page layout notes (Chromium)

- Page size and margins: `@page` in the layout CSS. `@page :first` can differ (e.g. room for the letterhead).
- Header/footer use Chromium's header/footer templates: real HTML in the page margins, repeated on every page,
  with page numbers. They are separate documents, so the layout CSS is injected into them too.
- First-page elements (letterhead, address window) are absolutely positioned inside a fixed-height block at the top
  of the body. Elements can't be positioned in the margins themselves.
- `thead { display: table-header-group }` repeats table headers after page breaks; `break-inside: avoid` keeps blocks together.

## Using it from a .NET application

```csharp
services.AddGenePdf(o =>
{
    o.TemplateRoot = "/data/templates";
    o.ChromiumPath = "/usr/bin/chromium";   // Chromium or Chrome must be installed
    o.MaxConcurrency = 2;
});

// …
PdfResult result = await pdfGenerator.GenerateAsync(new PdfRequest("invoice-fr", "1.0", json));
File.WriteAllBytes("invoice.pdf", result.Pdf);
// result.Metadata: template/layout versions, data hash…   result.Diagnostics: warnings
```

Pin the version in production (`null` = highest version, handy in development only).
`IPdfGenerator` is a singleton owning one Chromium process; each render uses its own tab.

## PDF/A and embedded metadata

Chromium embeds fonts but doesn't produce PDF/A. `PdfAConverter` adds an sRGB OutputIntent (CC0 profile from colord),
marks annotations printable, aligns the Info dictionary, and writes the XMP metadata in an incremental update:

```
pdfaid:part=2, conformance=B, dc:title, xmp dates, pdf:Producer
genepdf:documentTemplate / documentVersion / layoutTemplate / layoutVersion / lang
genepdf:dataSha256 / generatedAt / generator / renderer       (declared with a PDF/A extension schema)
```

Check with veraPDF: `scripts/verify-pdfa.sh some.pdf` (all samples pass PDF/A-2b).

**Licensing:** PDFsharp is pinned to **6.2.4**, the last MIT release. 6.5+ is AGPL-3.0 or commercial and refuses to
run without a license call. Don't let a dependency bot upgrade it.

## Development

No local .NET SDK is needed: `scripts/dotnet.sh` runs the CLI in a container with the SDK and Chromium.

```sh
scripts/dotnet.sh test
PORT=5080 scripts/dotnet.sh run --project src/GenePdf.DevServer --no-launch-profile
open http://localhost:5080
```

With a local SDK: `dotnet run --project src/GenePdf.DevServer` (finds Chrome in /Applications on a Mac).

Studio: pick a document and a sample, edit any file (document, layout, CSS, data), the PDF re-renders as you type.
Unsaved edits are rendered without touching the disk; ⌘S saves; *New version…* copies the folder and bumps the
version attribute. Problems link to file:line. The *Embedded metadata* tab shows what was written into the PDF.

HTTP API (dev server): `POST /api/generate/{document}/{version}` with the JSON as body → `application/pdf`.

Deployment of the demo: see [DEPLOY.md](DEPLOY.md).
