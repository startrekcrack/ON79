using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SongConverters
{
    /// <summary>
    /// PDF-to-CHO converter that uses page rendering + layout OCR (word bounding boxes)
    /// to align chord lines to lyric lines.
    /// </summary>
    public sealed class PdfToChoConverterOcrLayout : IPdfToChoConverter
    {
        /// <summary>
        /// Configuration options for OCR-based PDF to ChordPro conversion.
        /// </summary>
        /// <param name="TabWidth">Number of spaces per tab stop for chord alignment (default: 12).</param>
        /// <param name="MetaSource">Source identifier for metadata tag (default: "PDF").</param>
        /// <param name="DefaultComment">Default comment text to include in converted output.</param>
        /// <param name="OcrDpi">DPI resolution for OCR rendering (default: 300).</param>
        public sealed record Options(
            int TabWidth = 12,
            string MetaSource = "PDF",
            string DefaultComment = "OCR-basierte Übertragung aus der Vorlage; Silbentrennungen geglättet und Akkorde positionsgenau vor den Silben gesetzt.",
            int OcrDpi = 300
        );

        private readonly IPdfPageRenderer _renderer;
        private readonly IOcrLayoutEngine _ocr;

        /// <summary>
        /// Initializes a new instance of the PdfToChoConverterOcrLayout class.
        /// </summary>
        /// <param name="renderer">PDF page renderer for converting pages to images.</param>
        /// <param name="ocrEngine">OCR engine for text and layout extraction.</param>
        public PdfToChoConverterOcrLayout(IPdfPageRenderer renderer, IOcrLayoutEngine ocrEngine)
        {
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            _ocr = ocrEngine ?? throw new ArgumentNullException(nameof(ocrEngine));
        }

        /// <summary>
        /// Converts a PDF file to ChordPro format using OCR layout analysis.
        /// </summary>
        /// <param name="pdfPath">Path to the PDF file to convert.</param>
        /// <param name="options">Conversion options (or null to use defaults). Note: Only TabWidth is used from PdfConverterOptions.</param>
        /// <returns>A tuple containing the ChordPro content and an optional error message.</returns>
        public (string, string) Convert(string pdfPath, PdfConverterOptions options = null)
        {
            // Keep the interface contract, but this implementation ignores PdfConverterOptions.
            var result = Convert(pdfPath, new Options(TabWidth: options?.TabWidth ?? 12));
            return (result, null); // Return tuple: (cho content, error message)
        }

        /// <summary>
        /// Converts a PDF file to ChordPro format asynchronously using OCR layout analysis.
        /// </summary>
        /// <param name="pdfPath">Path to the PDF file to convert.</param>
        /// <param name="options">Conversion options (or null to use defaults). Note: Only TabWidth is used from PdfConverterOptions.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A tuple containing the ChordPro content and an optional error message.</returns>
        public async Task<(string, string)> ConvertAsync(string pdfPath, PdfConverterOptions options = null, 
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(() => Convert(pdfPath, options), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Converts a PDF file to ChordPro format using OCR layout analysis with specific options.
        /// </summary>
        /// <param name="pdfPath">Path to the PDF file to convert.</param>
        /// <param name="options">OCR conversion options.</param>
        /// <returns>ChordPro formatted string.</returns>
        public string Convert(string pdfPath, Options options)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
                throw new FileNotFoundException("PDF file not found.", pdfPath);

            options ??= new Options();

            // Render pages until renderer throws (page out of range).
            var allLines = new List<OcrLine>();
            for (int pageIndex = 0; ; pageIndex++)
            {
                Stream png = null;
                try
                {
                    png = _renderer.RenderPageAsPng(pdfPath, pageIndex, options.OcrDpi);
                }
                catch
                {
                    break;
                }

                using (png)
                {
                    try
                    {
                        var page = _ocr.ReadLayout(png);
                        if (page?.Lines != null)
                            allLines.AddRange(page.Lines);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"OCR failed on page {pageIndex} of {pdfPath}: {ex.Message}");
                    }
                }

                // Separate pages with an empty line marker.
                allLines.Add(new OcrLine(0, new System.Drawing.RectangleF(0, 0, 0, 0), Array.Empty<OcrWord>()));
            }

            if (allLines.Count == 0)
                return string.Empty;

            var textLines = allLines
                .Select(l => string.Join(" ", (l.Words ?? Array.Empty<OcrWord>()).Select(w => w.Text)).Trim())
                .Where(t => !t.IsBlankOrWhitespace())
                .ToList();

            var (title, subtitle) = ExtractTitleAndSubtitle(textLines);
            var copyright = ExtractCopyright(textLines);
            var key = DetectKey(textLines);

            return OcrChordProComposer.Compose(
                allLines,
                title,
                subtitle,
                copyright,
                key,
                options.MetaSource,
                options.DefaultComment,
                new OcrChordProComposer.Options(TabWidth: options.TabWidth, UseHashHeadings: true));
        }

        /// <summary>
        /// Extracts the song title and optional subtitle from the first non-empty, non-section text lines.
        /// A short line not starting with a known section keyword is used as the title.
        /// A line starting with "Satz:" is used as the subtitle.
        /// </summary>
        /// <param name="lines">Flat list of text lines from all OCR-processed pages.</param>
        /// <returns>A tuple of (title, subtitle); title falls back to "Unbekannter Titel" if not found.</returns>
        private static (string title, string subtitle) ExtractTitleAndSubtitle(List<string> lines)
        {
            string title = null;
            string subtitle = null;

            foreach (var l in lines)
            {
                var t = l.Trim();
                if (t.Length == 0) continue;
                if (subtitle == null && Regex.IsMatch(t, @"^satz\s*:\s*", RegexOptions.IgnoreCase))
                {
                    subtitle = t;
                    continue;
                }
                if (title == null && !Regex.IsMatch(t, @"^(strophe|refr|chorus|vers)\b", RegexOptions.IgnoreCase))
                {
                    // prefer short-ish line as title
                    if (t.Length <= 80)
                    {
                        title = t;
                    }
                }
                if (title != null && subtitle != null) break;
            }

            return (title ?? "Unbekannter Titel", subtitle);
        }

        /// <summary>
        /// Scans the text lines for a copyright declaration (lines starting with "Rechte:" or "Copyright:").
        /// </summary>
        /// <param name="lines">Flat list of text lines.</param>
        /// <returns>The copyright line, or <c>null</c> if none found.</returns>
        private static string ExtractCopyright(List<string> lines)
        {
            foreach (var l in lines)
            {
                var t = l.Trim();
                if (Regex.IsMatch(t, @"^(rechte|copyright)\s*:\s*", RegexOptions.IgnoreCase))
                    return t;
            }
            return null;
        }

        /// <summary>
        /// Detects the song key by returning the first chord-like token found in the text lines.
        /// </summary>
        /// <param name="lines">Flat list of text lines.</param>
        /// <returns>The detected key string, or <c>null</c> if no chord token is found.</returns>
        private static string DetectKey(List<string> lines)
        {
            // Simple heuristic: first chord token we see.
            var chordRe = new Regex(@"\b(?:[A-Ha-h](?:is|es|[#b])?)(?:m|maj|min|sus|add|dim|aug)?\d*(?:/[A-Ha-h](?:is|es|[#b])?)?\b");
            foreach (var l in lines)
            {
                var m = chordRe.Match(l);
                if (m.Success)
                    return m.Value;
            }
            return null;
        }
    }
}
