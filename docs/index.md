# PdfiumRaster documentation

Render PDF pages as images in .NET, or add a pool of isolated workers when your application needs parallel rendering.

| Library | Use it for |
| --- | --- |
| [PdfiumRaster](site/pdfiumraster.md) | Rendering and encoding pages in one process, including repeated work on a document. |
| [PdfiumRaster.Orchestrator](site/orchestrator.md) | Bounded, parallel rendering across local worker processes, with crash isolation and hard timeouts. |

PdfiumRaster uses PDFium to render pages and SkiaSharp to encode PNG, JPEG, and WebP images. It also supports BMP.
The orchestrator builds on PdfiumRaster; it is not a separate rendering engine. Both packages target .NET Standard 2.1
for their client libraries. The orchestrator packages self-contained workers for supported platforms.

PDFium calls are serialized within a process. The orchestrator achieves native rendering parallelism by running
separate worker processes, each with its own PDFium runtime. Those workers run under the application's OS identity:
they provide process isolation, not a security sandbox.

Both projects focus on PDF-to-image conversion, not editing, text extraction, form filling, signing, or viewer UI.

[PdfiumRaster on GitHub](https://github.com/gabisonia/PdfiumRaster) ·
[Orchestrator on GitHub](https://github.com/gabisonia/PdfiumRaster.Orchestrator)
