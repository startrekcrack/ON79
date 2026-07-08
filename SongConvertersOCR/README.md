# SongConverters DLL

Standalone library for converting DOCX and PDF files to ChordPro (.cho) format.

## Features

- ? **Zero Xceed dependency** - uses only DocumentFormat.OpenXml
- ? **PDF conversion** - line-based PDF extraction via PDFSharpCore
- ? **Optional OCR hooks** - pluggable renderer/OCR interfaces
- ? **100% API compatible** with existing DocxToChoConverter
- ? **Portable** - can be used in any .NET 10+ project
- ? **Efficient** - OpenXML SAX parsing for large documents
- ? **Well-tested** - identical output to Xceed-based converter

## Usage

```csharp
using SongConverters;

// Simple usage
var converter = new DocxToChoConverterOpenXml();
string choContent = converter.Convert("MySong.docx");

// PDF usage
var pdfConverter = new PdfToChoConverterPdfSharpCore();
string pdfCho = pdfConverter.Convert("MySong.pdf");

// With options
var options = new ConverterOptions(
    TabWidth: 12,
    ReferenceChoPath: "reference.cho"
);
string choContent = converter.Convert("MySong.docx", options);
```

## API

### IDocxToChoConverter Interface

```csharp
public interface IDocxToChoConverter
{
    string Convert(string docxPath, ConverterOptions options = null);
}
```

### ConverterOptions

```csharp
public sealed record ConverterOptions(
    int TabWidth = 12,              // Tab width for chord alignment
    string ReferenceChoPath = null  // Path to reference CHO file
);
```

## Implementation Details

### DocxToChoConverterOpenXml

Uses DocumentFormat.OpenXml to:
- Parse DOCX paragraph structure
- Detect chord lines (bold/italic formatting)
- Extract metadata (title, subtitle, footer info)
- Generate ChordPro-compliant output

### Chord Detection

Recognizes chords by:
1. **Pattern matching:** `[A-H](#|b|m|maj|sus|add|dim|aug)?`
2. **Formatting:** Bold or italic text
3. **Structure:** Tab-separated or multiple tokens

### Section Recognition

Automatically detects song sections:
- Chorus/Refrain
- Verse 1, 2, 3...
- Bridge
- Intro/Outro
- Solo sections

## Dependencies

- `DocumentFormat.OpenXml` (>= 3.4.1)
- `PDFSharpCore` (for PDF conversion)
- `.NET 10.0`

## License

Same as parent project (SearchSong).

## Integration

Add to your project:

```xml
<ItemGroup>
    <ProjectReference Include="..\SongConverters\SongConverters.csproj" />
</ItemGroup>
```

Or reference the compiled DLL:

```xml
<ItemGroup>
    <Reference Include="SongConverters">
      <HintPath>..\lib\SongConverters.dll</HintPath>
    </Reference>
</ItemGroup>
```

## OCR (optional)

For image-only PDFs, inject a renderer and OCR engine:

```csharp
var options = new PdfConverterOptions(
        PageRenderer: myRenderer,
        OcrEngine: myOcr
);
string cho = pdfConverter.Convert("MySong.pdf", options);
```
