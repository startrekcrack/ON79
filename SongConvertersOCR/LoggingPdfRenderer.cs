using System;
using System.IO;

namespace SongConverters
{
    /// <summary>
    /// Simple wrapper to log render progress (useful when OCR rendering is slow).
    /// </summary>
    public sealed class LoggingPdfRenderer : IPdfPageRenderer
    {
        private readonly IPdfPageRenderer _inner;

        /// <summary>
        /// Initializes a new instance of the LoggingPdfRenderer class.
        /// </summary>
        /// <param name="inner">The inner renderer to wrap.</param>
        public LoggingPdfRenderer(IPdfPageRenderer inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <summary>
        /// Renders a PDF page as PNG with logging.
        /// </summary>
        /// <param name="pdfPath">Path to the PDF file.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="dpi">DPI for rendering.</param>
        /// <returns>PNG stream.</returns>
        public Stream RenderPageAsPng(string pdfPath, int pageIndex, int dpi = 200)
        {
            Console.WriteLine($"  OCR: render page {pageIndex + 1} @ {dpi} dpi");
            return _inner.RenderPageAsPng(pdfPath, pageIndex, dpi);
        }
    }
}
