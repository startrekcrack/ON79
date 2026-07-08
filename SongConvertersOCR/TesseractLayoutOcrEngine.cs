using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Tesseract;

namespace SongConverters
{
    /// <summary>
    /// Tesseract OCR engine that returns word-level layout.
    /// </summary>
    public sealed class TesseractLayoutOcrEngine : IOcrLayoutEngine, IDisposable
    {
        private readonly TesseractEngine _engine;

        /// <summary>
        /// OCR engine options with preprocessing.
        /// </summary>
        /// <param name="Language">OCR language (e.g., "deu+eng").</param>
        /// <param name="EngineMode">Tesseract engine mode.</param>
        /// <param name="PageSegMode">Page segmentation mode.</param>
        /// <param name="Preprocess">Enable image preprocessing.</param>
        /// <param name="PreprocessOptions">Preprocessing options.</param>
        /// <param name="CharWhitelist">Character whitelist (null = all).</param>
        public sealed record Options(
            string Language = "deu+eng",
            EngineMode EngineMode = EngineMode.Default,
            PageSegMode PageSegMode = PageSegMode.SingleBlock,
            bool Preprocess = true,
            ImagePreprocessor.Options PreprocessOptions = null,
            string CharWhitelist = null
        );

        private readonly Options _options;

        /// <summary>
        /// Initializes a new Tesseract layout OCR engine.
        /// </summary>
        /// <param name="dataPath">Path to tessdata directory.</param>
        /// <param name="options">OCR options.</param>
        public TesseractLayoutOcrEngine(string dataPath, Options options = null)
        {
            if (string.IsNullOrWhiteSpace(dataPath))
                throw new ArgumentNullException(nameof(dataPath));
            if (!Directory.Exists(dataPath))
                throw new DirectoryNotFoundException($"tessdata not found: {dataPath}");

            _options = options ?? new Options();
            _engine = new TesseractEngine(dataPath, _options.Language ?? "deu+eng", _options.EngineMode);
            _engine.SetVariable("preserve_interword_spaces", "1");

            if (!string.IsNullOrWhiteSpace(_options.CharWhitelist))
            {
                _engine.SetVariable("tessedit_char_whitelist", _options.CharWhitelist);
            }
        }

        /// <summary>
        /// Reads layout information from a PNG stream.
        /// </summary>
        /// <param name="pngStream">PNG image stream.</param>
        /// <returns>OCR page with word-level bounding boxes.</returns>
        public OcrPage ReadLayout(Stream pngStream)
        {
            if (pngStream == null) throw new ArgumentNullException(nameof(pngStream));

            using var ms = new MemoryStream();
            pngStream.CopyTo(ms);
            var bytes = ms.ToArray();

            if (_options.Preprocess)
            {
                try
                {
                    bytes = ImagePreprocessor.PreprocessToPng(bytes, _options.PreprocessOptions ?? new ImagePreprocessor.Options());
                }
                catch
                {
                    // best-effort
                }
            }

            using var pix = Pix.LoadFromMemory(bytes);
            using var page = _engine.Process(pix, _options.PageSegMode);

            var lines = ExtractLines(page);
            var width = GetPixWidth(pix);
            var height = GetPixHeight(pix);
            return new OcrPage(width, height, lines);
        }

        /// <summary>
        /// Safely retrieves the pixel width of a Pix image.
        /// </summary>
        /// <param name="pix">The Tesseract Pix object.</param>
        /// <returns>Pixel width, or 0 if the value cannot be read.</returns>
        private static int GetPixWidth(Pix pix)
        {
            try { return pix.Width; } catch { return 0; }
        }

        /// <summary>
        /// Safely retrieves the pixel height of a Pix image.
        /// </summary>
        /// <param name="pix">The Tesseract Pix object.</param>
        /// <returns>Pixel height, or 0 if the value cannot be read.</returns>
        private static int GetPixHeight(Pix pix)
        {
            try { return pix.Height; } catch { return 0; }
        }

        /// <summary>
        /// Extracts word-level OCR lines from a processed Tesseract page by grouping words
        /// into line buckets based on their bounding-box top/bottom coordinates,
        /// then sorts lines from top to bottom.
        /// </summary>
        /// <param name="page">The Tesseract processed page.</param>
        /// <returns>An ordered list of <see cref="OcrLine"/> objects with bounding boxes and words.</returns>
        private static IReadOnlyList<OcrLine> ExtractLines(Page page)
        {
            var result = new List<OcrLine>();

            using var iterator = page.GetIterator();
            iterator.Begin();

            // Group words by line bounding box (top/bottom) with tolerance.
            var buckets = new List<(RectangleF box, List<OcrWord> words)>();

            do
            {
                var wordText = iterator.GetText(PageIteratorLevel.Word);
                if (string.IsNullOrWhiteSpace(wordText))
                    continue;

                if (!TryGetBox(iterator, PageIteratorLevel.Word, out var wordBox))
                    continue;

                if (!TryGetBox(iterator, PageIteratorLevel.TextLine, out var lineBox))
                {
                    lineBox = wordBox;
                }

                var word = new OcrWord(wordText.Trim(), wordBox);

                var idx = FindLineBucket(buckets, lineBox);
                if (idx < 0)
                {
                    buckets.Add((lineBox, new List<OcrWord> { word }));
                }
                else
                {
                    buckets[idx].words.Add(word);
                    buckets[idx] = (Union(buckets[idx].box, lineBox), buckets[idx].words);
                }
            }
            while (iterator.Next(PageIteratorLevel.Word));

            foreach (var b in buckets.OrderByDescending(b => b.box.Top))
            {
                var words = b.words.OrderBy(w => w.Box.Left).ToList();
                var baselineY = b.box.Top + (b.box.Height * 0.8f);
                result.Add(new OcrLine(baselineY, b.box, words));
            }

            // Sort from top to bottom for further processing.
            return result.OrderBy(l => l.Box.Top).ToList();
        }

        /// <summary>
        /// Finds an existing line bucket whose bounding box top and bottom coordinates
        /// are within a small tolerance of the candidate box.
        /// </summary>
        /// <param name="buckets">List of existing line buckets.</param>
        /// <param name="candidate">Bounding box of the current word's line.</param>
        /// <returns>Index of the matching bucket, or -1 if none found.</returns>
        private static int FindLineBucket(List<(RectangleF box, List<OcrWord> words)> buckets, RectangleF candidate)
        {
            const float tol = 6f;
            for (int i = 0; i < buckets.Count; i++)
            {
                var b = buckets[i].box;
                if (Math.Abs(b.Top - candidate.Top) <= tol && Math.Abs(b.Bottom - candidate.Bottom) <= (tol * 2))
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Computes the union (bounding rectangle) of two <see cref="RectangleF"/> values.
        /// </summary>
        /// <param name="a">First rectangle.</param>
        /// <param name="b">Second rectangle.</param>
        /// <returns>The smallest rectangle that contains both <paramref name="a"/> and <paramref name="b"/>.</returns>
        private static RectangleF Union(RectangleF a, RectangleF b)
        {
            var left = Math.Min(a.Left, b.Left);
            var top = Math.Min(a.Top, b.Top);
            var right = Math.Max(a.Right, b.Right);
            var bottom = Math.Max(a.Bottom, b.Bottom);
            return RectangleF.FromLTRB(left, top, right, bottom);
        }

        /// <summary>
        /// Attempts to retrieve the bounding box for the current iterator position at the given level.
        /// Returns <c>false</c> if the box is empty or an exception is thrown.
        /// </summary>
        /// <param name="iterator">The Tesseract result iterator.</param>
        /// <param name="level">Page iterator level (Word, TextLine, etc.).</param>
        /// <param name="box">When successful, the bounding box in page pixels.</param>
        /// <returns><c>true</c> if a valid, non-empty bounding box was obtained.</returns>
        private static bool TryGetBox(ResultIterator iterator, PageIteratorLevel level, out RectangleF box)
        {
            box = default;
            try
            {
                if (!iterator.TryGetBoundingBox(level, out var rect))
                    return false;

                box = RectangleF.FromLTRB(rect.X1, rect.Y1, rect.X2, rect.Y2);
                return box.Width > 0 && box.Height > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Disposes the Tesseract engine and releases resources.
        /// </summary>
        public void Dispose()
        {
            _engine?.Dispose();
            
            // Force garbage collection to clean up unmanaged Tesseract resources
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
