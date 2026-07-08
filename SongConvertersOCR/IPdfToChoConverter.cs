using System.Threading;
using System.Threading.Tasks;

namespace SongConverters
{
    /// <summary>
    /// Interface for PDF to CHO converters.
    /// </summary>
    public interface IPdfToChoConverter
    {
        /// <summary>
        /// Converts a PDF file to CHO (ChordPro) format.
        /// </summary>
        /// <param name="pdfPath">Path to the PDF file</param>
        /// <param name="options">Conversion options (tab width, reference CHO path, OCR hooks)</param>
        /// <returns>CHO formatted string and error message (if any)</returns>
        (string, string) Convert(string pdfPath, PdfConverterOptions options = null);

        /// <summary>
        /// Converts a PDF file to CHO (ChordPro) format asynchronously.
        /// </summary>
        /// <param name="pdfPath">Path to the PDF file</param>
        /// <param name="options">Conversion options (tab width, reference CHO path, OCR hooks)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>CHO formatted string and error message (if any)</returns>
        Task<(string, string)> ConvertAsync(string pdfPath, PdfConverterOptions options = null,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Conversion options for PDF to CHO converters.
    /// </summary>
    public sealed record PdfConverterOptions(
        int TabWidth = 12,
        string ReferenceChoPath = null,
        bool AllowReferenceMatch = false,
        bool AbortOnImageOnly = true,
        bool AbortOnNoChords = true,
        int OcrDpi = 450,
        bool AllowOcr = false,
        string OcrDumpDirectory = null,
        IPdfPageRenderer PageRenderer = null,
        IOcrEngine OcrEngine = null
    );

    /// <summary>
    /// Optional renderer interface for OCR pipelines.
    /// Implement with Pdfium/Docnet, etc.
    /// </summary>
    public interface IPdfPageRenderer
    {
        /// <summary>
        /// Renders a PDF page to a PNG stream.
        /// Page index is zero-based.
        /// </summary>
        System.IO.Stream RenderPageAsPng(string pdfPath, int pageIndex, int dpi = 200);
    }

    /// <summary>
    /// Optional OCR engine interface.
    /// Implement with a custom OCR engine (optional).
    /// </summary>
    public interface IOcrEngine
    {
        /// <summary>
        /// Runs OCR on a PNG stream and returns recognized text.
        /// </summary>
        string ReadText(System.IO.Stream pngStream);
    }
}
