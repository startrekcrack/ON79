# Third-Party Notices

CHOConverter is licensed under the MIT License (see [LICENSE](LICENSE)).

It uses the following third-party components, which are retrieved via NuGet at
build time and are **not** redistributed in source form in this repository.
Each remains under its own license. This file is provided for attribution.

| Component | Version | License | Project |
|-----------|---------|---------|---------|
| DocumentFormat.OpenXml | 3.4.1 | MIT | https://github.com/dotnet/Open-XML-SDK |
| System.Drawing.Common | 10.0.3 | MIT | https://github.com/dotnet/runtime |
| UglyToad.PdfPig | 1.7.0 | Apache-2.0 | https://github.com/UglyToad/PdfPig |
| Docnet.Core | 2.6.0 | MIT | https://github.com/GowenGit/docnet |
| PDFium (bundled by Docnet.Core, `pdfium.dll`) | — | BSD-3-Clause | https://pdfium.googlesource.com/pdfium/ |
| Tesseract (.NET wrapper) | 5.2.0 | Apache-2.0 | https://github.com/charlesw/tesseract |
| Tesseract OCR engine (bundled native libs) | 5.x | Apache-2.0 | https://github.com/tesseract-ocr/tesseract |
| Leptonica (bundled with Tesseract) | 1.82.0 | BSD-2-Clause style | https://github.com/DanBloomberg/leptonica |
| Sdcb.PaddleInference | 3.0.1 | Apache-2.0 | https://github.com/sdcb/PaddleSharp |
| Sdcb.PaddleOCR | 3.0.1 | Apache-2.0 | https://github.com/sdcb/PaddleSharp |
| Sdcb.PaddleOCR.Models.Local | 3.0.1 | Apache-2.0 | https://github.com/sdcb/PaddleSharp |
| Sdcb.PaddleInference.runtime.win64.mkl | 3.1.0.54 | Apache-2.0 (bundles Intel MKL under the Intel Simplified Software License) | https://github.com/sdcb/PaddleSharp |
| OpenCvSharp4.runtime.win | 4.11.0 | Apache-2.0 | https://github.com/shimat/opencvsharp |

## Tesseract language data

`*.traineddata` files are downloaded separately by the user and are **not**
included in this repository. They are published by the Tesseract project under
the Apache-2.0 License: https://github.com/tesseract-ocr/tessdata

## Notes

- Native binaries (`pdfium.dll`, `tesseract*.dll`, `leptonica*.dll`, MKL,
  OpenCV runtimes, etc.) are pulled in via the NuGet packages above and are
  redistributed only through the compiled installer, not through this source
  repository.
- Version numbers reflect the package references in the project files and may
  change over time; consult each project for its authoritative license text.
