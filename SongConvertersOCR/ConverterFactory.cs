namespace SongConverters
{
    /// <summary>
    /// Factory for creating DOCX to CHO converters.
    /// Allows easy switching between implementations.
    /// </summary>
    public static class ConverterFactory
    {
        /// <summary>
        /// Creates the recommended converter (OpenXML-based, no external dependencies).
        /// </summary>
        public static IDocxToChoConverter CreateDefault()
        {
            return new DocxToChoConverterOpenXml();
        }

        /// <summary>
        /// Creates an OpenXML-based converter.
        /// No Xceed dependency, pure OpenXML implementation.
        /// </summary>
        public static IDocxToChoConverter CreateOpenXmlConverter()
        {
            return new DocxToChoConverterOpenXml();
        }

        /// <summary>
        /// Creates a PDF converter based on PdfPig + OCR pipeline.
        /// </summary>
        public static IPdfToChoConverter CreatePdfConverter()
        {
            return new PdfToChoConverterPdfSharpCore();
        }
    }
}
