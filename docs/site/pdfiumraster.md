# Getting started with PdfiumRaster

PdfiumRaster renders PDF pages to BMP, PNG, JPEG, or WebP. Install the package in a .NET Standard 2.1-compatible application:

```bash
dotnet add package PdfiumRaster
```

No manual PDFium copy step is required for supported runtime identifiers. Render the first page to PNG:

```csharp
using PdfiumRaster;

PdfImageConverter.SavePng("input.pdf", pageNumber: 1, "page-0001.png");
```

Methods accepting a `pageNumber` use one-based numbers; methods accepting a `pageIndex` use zero-based indexes.
For example, `RenderPage("input.pdf", pageIndex: 0)` renders the same first page. Returned `PdfBitmap` pixels are
caller-owned BGRA data.

## Configure rendering

```csharp
var options = new PdfImageConversionOptions
{
    Format = PdfImageOutputFormat.Jpeg,
    ColorMode = PdfImageColorMode.Grayscale,
    Encoding = new PdfImageEncodingOptions { Quality = 85 },
    Render = new PdfPageRenderOptions { Dpi = 150 },
};

PdfImageConverter.SavePageNumber("input.pdf", pageNumber: 1, "page.jpg", options);
```

For repeated operations on one document, open a `PdfRenderSession` and dispose it when finished. A session handles one
operation at a time and can reuse the document and rendering resources.

## Inputs and memory

Paths and seekable streams are useful for large PDFs: seekable streams use random access without copying the entire
document to a managed array. Non-seekable streams are buffered; byte arrays and Base64 inputs hold the complete PDF
in managed memory. Input streams are disposed by default; use `leaveOpen: true` to retain ownership. Output streams
remain caller-owned. Bitmap memory grows with output dimensions.

PDFium calls are serialized inside a process. For actual parallel native rendering, use the
[orchestrator](orchestrator.md). See the upstream [API guide](https://github.com/gabisonia/PdfiumRaster/blob/master/docs/API.md)
for supported overloads, rendering options, sessions, and ownership details.
