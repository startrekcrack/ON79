using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Docnet.Core;
using Docnet.Core.Models;
using UglyToad.PdfPig;

namespace SongConverters
{
    /// <summary>
    /// Docnet (PDFium) renderer for OCR pipelines.
    /// </summary>
    public sealed class DocnetPdfRenderer : IPdfPageRenderer
    {
        /// <summary>
        /// Renders a PDF page as PNG.
        /// </summary>
        /// <param name="pdfPath">Path to PDF file.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="dpi">Rendering DPI.</param>
        /// <returns>PNG stream.</returns>
        public Stream RenderPageAsPng(string pdfPath, int pageIndex, int dpi = 200)
        {
            if (string.IsNullOrWhiteSpace(pdfPath)) throw new ArgumentNullException(nameof(pdfPath));
            if (!File.Exists(pdfPath)) throw new FileNotFoundException("PDF file not found.", pdfPath);
            if (pageIndex < 0) throw new ArgumentOutOfRangeException(nameof(pageIndex));

            var (widthPx, heightPx) = GetPagePixelSize(pdfPath, pageIndex, dpi);
            try
            {
                return RenderWithDimensions(pdfPath, pageIndex, widthPx, heightPx);
            }
            catch (Exception ex) when (ex.Message.Contains("dimOne can't be more than dimTwo", StringComparison.OrdinalIgnoreCase))
            {
                var min = Math.Min(widthPx, heightPx);
                var max = Math.Max(widthPx, heightPx);
                return RenderWithDimensions(pdfPath, pageIndex, min, max);
            }
        }

        /// <summary>
        /// Renders a specific PDF page with explicit pixel dimensions and returns it as a PNG stream.
        /// </summary>
        /// <param name="pdfPath">Path to the PDF file.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="widthPx">Target render width in pixels.</param>
        /// <param name="heightPx">Target render height in pixels.</param>
        /// <returns>A <see cref="MemoryStream"/> containing the PNG-encoded page image, positioned at 0.</returns>
        private static Stream RenderWithDimensions(string pdfPath, int pageIndex, int widthPx, int heightPx)
        {
            var dimensions = new PageDimensions(widthPx, heightPx);

            using var docReader = DocLib.Instance.GetDocReader(pdfPath, dimensions);
            if (pageIndex >= docReader.GetPageCount()) throw new ArgumentOutOfRangeException(nameof(pageIndex));

            using var pageReader = docReader.GetPageReader(pageIndex);
            var rawBytes = pageReader.GetImage();
            var width = pageReader.GetPageWidth();
            var height = pageReader.GetPageHeight();

            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
            try
            {
                Marshal.Copy(rawBytes, 0, data.Scan0, rawBytes.Length);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            var memory = new MemoryStream();
            bitmap.Save(memory, ImageFormat.Png);
            memory.Position = 0;
            return memory;
        }

        /// <summary>
        /// Calculates the pixel dimensions of a PDF page at the requested DPI
        /// based on the page's native size in PDF points (1 pt = 1/72 inch).
        /// </summary>
        /// <param name="pdfPath">Path to the PDF file.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="dpi">Target rendering resolution in dots per inch.</param>
        /// <returns>Width and height in pixels (minimum 1×1).</returns>
        private static (int widthPx, int heightPx) GetPagePixelSize(string pdfPath, int pageIndex, int dpi)
        {
            using var doc = PdfDocument.Open(pdfPath);
            var page = doc.GetPage(pageIndex + 1);
            var widthPx = (int)Math.Ceiling(page.Width / 72.0 * dpi);
            var heightPx = (int)Math.Ceiling(page.Height / 72.0 * dpi);
            return (Math.Max(1, widthPx), Math.Max(1, heightPx));
        }
    }
}
