using System;
using System.IO;

namespace SongConverters
{
    /// <summary>
    /// Placeholder renderer (WinRT removed).
    /// </summary>
    public sealed class WindowsPdfRenderer : IPdfPageRenderer
    {
        /// <summary>
        /// Renders a PDF page (throws NotSupportedException).
        /// </summary>
        /// <param name="pdfPath">PDF path (not used).</param>
        /// <param name="pageIndex">Page index (not used).</param>
        /// <param name="dpi">DPI (not used).</param>
        /// <returns>Never returns.</returns>
        public Stream RenderPageAsPng(string pdfPath, int pageIndex, int dpi = 200)
        {
            throw new NotSupportedException("WinRT PDF rendering has been removed.");
        }
    }
}
