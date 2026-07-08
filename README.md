# CHOConverter

Converts PDF and DOCX song sheets to **ChordPro-compatible** `.cho` files.

Available as **WPF desktop app** (CHOConverterWPF), **Windows Forms GUI** (CHOConverterGUI), and **CLI** (CHOConverterCLI).

## Features

- Native text extraction from DOCX (OpenXML) and PDF (PdfPig)
- OCR fallback for image-based or scanned files (Tesseract + Pdfium)
- Dual-layout OCR for chord/lyric separation
- Sheet music detection to skip non-text pages
- Multiple output files with collision-safe naming (`Song.cho`, `Song_1.cho`, …)
- Light / Dark theme (WPF)

## Requirements

- **Windows** 10 or later (x64)
- **.NET 10** runtime — [download](https://dotnet.microsoft.com/download/dotnet/10.0)
- **Tesseract language data** — place `deu.traineddata` and/or `eng.traineddata` in a `tessdata/` folder next to the executable  
  Download from: <https://github.com/tesseract-ocr/tessdata>

## Build

```powershell
dotnet build CHOConverter.slnx
```

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `DocumentFormat.OpenXml` | 3.4.1 | DOCX parsing |
| `UglyToad.PdfPig` | 1.7.0 | Native PDF text extraction |
| `Docnet.Core` | 2.6.0 | PDF rendering via pdfium (for OCR) |
| `Tesseract` | 5.2.0 | OCR engine (wraps Tesseract 5) |

Native binaries bundled at build time:
- `pdfium.dll` — PDF rendering (from Docnet.Core)
- `tesseract50.dll` + `leptonica-1.82.0.dll` — Tesseract OCR engine

## License

Licensed under the [MIT License](LICENSE) — © 2021-2026 Oliver Niehus.

Third-party components (retrieved via NuGet at build time) remain under their
own licenses. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Disclaimer

CHOConverter is provided as a **sample / demonstration project** and a
**file-format conversion tool**. It ships **without** any copyrighted song
material.

- **No warranty of any kind.** The software is provided "as is", without any
  guarantee of functionality, fitness for a particular purpose, correctness of
  the conversion output, security, or freedom from defects (see [LICENSE](LICENSE)).
  Use at your own risk.
- **Not security-hardened.** This is a hobby/sample project, not audited or
  intended for security-critical or production use. Validate any output before
  relying on it.
- No copyrighted lyrics or sheet music are included in this repository. The
  only bundled sample uses **original placeholder** content (see [examples/](examples/)).
- You are responsible for holding the necessary rights or a license (e.g. CCLI)
  for any song sheets you convert or distribute.

## Trademarks

"ChordPro", "SongBeamer", "OnSong" and other product names may be trademarks of
their respective owners and are used here for identification purposes only. This
project is not affiliated with or endorsed by any of them.
