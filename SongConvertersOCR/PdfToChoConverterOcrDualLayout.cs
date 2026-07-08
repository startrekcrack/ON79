using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SongConverters
{
    /// <summary>
    /// Dual-pass OCR converter:
    /// - OCR pass A optimized for chords (whitelist + sparse)
    /// - OCR pass B optimized for lyrics (normal)
    /// Then aligns chord tokens above lyric lines by x-position and emits ChordPro.
    /// </summary>
    public sealed class PdfToChoConverterOcrDualLayout : IPdfToChoConverter
    {
        private static readonly Regex ChordTokenRe = new(
            @"^(?:[A-Ha-h](?:is|es|[#b])?)(?:m|maj|min|sus|add|dim|aug)?(?:2|4|5|6|7|9|11|13)?(?:/[A-Ha-h](?:is|es|[#b])?)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Conversion options for dual-layout OCR.
        /// </summary>
        /// <param name="TabWidth">Tab width for formatting.</param>
        /// <param name="MetaSource">Metadata source identifier.</param>
        /// <param name="OcrDpi">OCR resolution in DPI.</param>
        /// <param name="MaxChordToLyricGapPx">Maximum vertical distance between chord and lyric line in pixels.</param>
        public sealed record Options(
            int TabWidth = 12,
            string MetaSource = "PDF",
            int OcrDpi = 300,
            int MaxChordToLyricGapPx = 180
        );

        private readonly IPdfPageRenderer _renderer;
        private readonly IOcrLayoutEngine _chordOcr;
        private readonly IOcrLayoutEngine _lyricOcr;

        /// <summary>
        /// Initializes a new instance of PdfToChoConverterOcrDualLayout.
        /// </summary>
        /// <param name="renderer">PDF page renderer.</param>
        /// <param name="chordOcr">OCR engine optimized for chords.</param>
        /// <param name="lyricOcr">OCR engine optimized for lyrics.</param>
        public PdfToChoConverterOcrDualLayout(IPdfPageRenderer renderer, IOcrLayoutEngine chordOcr, IOcrLayoutEngine lyricOcr)
        {
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            _chordOcr = chordOcr ?? throw new ArgumentNullException(nameof(chordOcr));
            _lyricOcr = lyricOcr ?? throw new ArgumentNullException(nameof(lyricOcr));
        }

        /// <summary>
        /// Converts a PDF to ChordPro format.
        /// </summary>
        /// <param name="pdfPath">Path to PDF file.</param>
        /// <param name="options">Conversion options.</param>
        /// <returns>ChordPro formatted text.</returns>
        public (string, string) Convert(string pdfPath, PdfConverterOptions options = null)
        {
            return (Convert(pdfPath, new Options(TabWidth: options?.TabWidth ?? 12)), null);
        }

        /// <summary>
        /// Converts a PDF to ChordPro format with extended options.
        /// </summary>
        /// <param name="pdfPath">Path to PDF file.</param>
        /// <param name="options">Extended conversion options.</param>
        /// <returns>ChordPro formatted text.</returns>
        public string Convert(string pdfPath, Options options)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
                throw new FileNotFoundException("PDF file not found.", pdfPath);

            options ??= new Options();

            var lyricLines = new List<OcrLine>();
            var chordLines = new List<OcrLine>();

            for (int pageIndex = 0; ; pageIndex++)
            {
                using var png = TryRender(pdfPath, pageIndex, options.OcrDpi);
                if (png == null) break;

                try
                {
                    using var ms = new MemoryStream();
                    png.CopyTo(ms);
                    var bytes = ms.ToArray();

                    using var chordStream = new MemoryStream(bytes);
                    using var lyricStream = new MemoryStream(bytes);

                    var chordPage = _chordOcr.ReadLayout(chordStream);
                    var lyricPage = _lyricOcr.ReadLayout(lyricStream);

                    if (chordPage?.Lines != null)
                        chordLines.AddRange(chordPage.Lines.Where(l => l.Words?.Count > 0));
                    if (lyricPage?.Lines != null)
                        lyricLines.AddRange(lyricPage.Lines.Where(l => l.Words?.Count > 0));
                }
                catch (Exception ex)
                {
                    // Managed OCR exceptions; native crashes (Access Violation) will
                    // still kill the process — use out-of-process isolation for those.
                    System.Diagnostics.Debug.WriteLine($"OCR failed on page {pageIndex} of {pdfPath}: {ex.Message}");
                }

                // page separator
                lyricLines.Add(new OcrLine(0, RectangleF.Empty, Array.Empty<OcrWord>()));
            }

            var lyricTexts = lyricLines
                .Where(l => !IsNoteNoiseLine(l))
                .Select(l => string.Join(" ", (l.Words ?? Array.Empty<OcrWord>()).Select(w => w.Text)).Trim())
                .Where(t => !t.IsBlankOrWhitespace())
                .ToList();

            var fallbackTitle = Path.GetFileNameWithoutExtension(pdfPath);
            var (title, subtitle) = ExtractTitleAndSubtitle(lyricTexts, fallbackTitle);
            var copyright = ExtractCopyright(lyricTexts);
            var key = DetectKeyFromChords(chordLines);

            var headerSkip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddHeaderSkip(headerSkip, title);
            AddHeaderSkip(headerSkip, subtitle);
            AddHeaderSkip(headerSkip, copyright);
            AddHeaderSkip(headerSkip, fallbackTitle);

            var output = new List<string>
            {
                $"{{title: {title}}}",
            };
            if (!subtitle.IsBlankOrWhitespace()) output.Add($"{{subtitle: {subtitle}}}");
            if (!copyright.IsBlankOrWhitespace()) output.Add($"{{copyright: {copyright}}}");
            if (!key.IsBlankOrWhitespace()) output.Add($"{{key: {key}}}");
            output.Add($"{{meta: source={options.MetaSource}}}");
            output.Add("{comment: OCR-basierte Übertragung aus der Vorlage; Silbentrennungen geglättet und Akkorde positionsgenau vor den Silben gesetzt.}");
            output.Add(string.Empty);

            // Compose line-by-line from lyric OCR lines:
            // Chord line -> (skip staff/noise) -> lyric line
            string pendingChordLine = null;
            var started = false;

            (OcrLine line, string chordLine)? activeChordAbove = null;

            var chordLineItems = chordLines
                .Where(l => l?.Words != null && l.Words.Count > 0)
                .Where(l => !IsChordNoiseLine(l))
                .Select(l => (line: l, chordLine: NormalizeChordLineFromOcrLine(l)))
                .Where(t => !t.chordLine.IsBlankOrWhitespace())
                .ToList();

            for (int i = 0; i < lyricLines.Count; i++)
            {
                var l = lyricLines[i];
                var text = string.Join(" ", (l.Words ?? Array.Empty<OcrWord>()).Select(w => w.Text)).Trim();

                if (text.IsBlankOrWhitespace())
                {
                    EnsureBlank(output);
                    pendingChordLine = null;
                    activeChordAbove = null;
                    continue;
                }

                if (headerSkip.Contains(NormalizeForHeaderCompare(text)))
                {
                    // Avoid duplicating title/copyright lines that also appear in body OCR.
                    continue;
                }

                // Skip staff/music notation lines (they break chord-to-lyric distance).
                if (IsNoteNoiseLine(l))
                {
                    continue;
                }

                // Extra guard: sometimes staff garbage isn't detected by layout alone.
                if (LooksLikeStaffNoise(text) || LooksLikeMostlyShortTokens(text))
                {
                    continue;
                }

                var fixedText = FixText(text);

                // Skip preamble garbage until we hit the first real lyric/section line.
                if (!started)
                {
                    if (TryParseSection(fixedText, out var preSection))
                    {
                        started = true;
                        EnsureBlank(output);
                        output.Add($"{{comment: {preSection}}}");
                        continue;
                    }

                    if (!LooksLikeLyricStart(fixedText))
                    {
                        // Ignore title/credits/preamble - we already emitted header metadata.
                        continue;
                    }

                    started = true;
                }

                if (text.Contains('©'))
                {
                    // Already captured as header metadata.
                    continue;
                }

                if (TryParseSection(fixedText, out var section))
                {
                    EnsureBlank(output);
                    output.Add($"{{comment: {section}}}");
                    pendingChordLine = null;
                    activeChordAbove = null;
                    continue;
                }

                // If the lyric OCR also contains a chord-only line, treat it as chord line.
                if (IsChordOnlyTextLine(fixedText))
                {
                    pendingChordLine = NormalizeChordLine(fixedText);
                    activeChordAbove = null;
                    continue;
                }

                var lyricText = fixedText;

                if (IsProbablyGarbageBodyLine(lyricText))
                {
                    continue;
                }

                // Prefer chord OCR line above this lyric line.
                var chordAboveItem = FindChordLineAboveItem(chordLineItems, l, options.MaxChordToLyricGapPx);
                if (chordAboveItem.HasValue)
                {
                    activeChordAbove = chordAboveItem;
                    output.Add(OcrChoComposer.InsertChordsPreservingVersePrefix(chordAboveItem.Value.chordLine, lyricText, options.TabWidth));
                    pendingChordLine = null;
                }
                else if (!pendingChordLine.IsBlankOrWhitespace())
                {
                    // If we had a pending chord line from lyric OCR, only apply it to a likely lyric line.
                    if (LooksLikeLyricLine(lyricText))
                    {
                        output.Add(OcrChoComposer.InsertChordsPreservingVersePrefix(pendingChordLine, lyricText, options.TabWidth));
                        pendingChordLine = null;
                    }
                    else
                    {
                        // Skip short/non-lyric noise lines without consuming the pending chord line.
                        continue;
                    }
                }
                else if (activeChordAbove.HasValue && IsNumberedVerseLine(lyricText) && l?.Box != RectangleF.Empty && activeChordAbove.Value.line?.Box != RectangleF.Empty)
                {
                    // Special case: one chord line + shared note line for multiple verses.
                    // If the next verses are below but still near the same chord line, reuse it.
                    var dist = l.Box.Top - activeChordAbove.Value.line.Box.Bottom;
                    if (dist > 0 && dist <= (options.MaxChordToLyricGapPx * 2))
                    {
                        output.Add(OcrChoComposer.InsertChordsPreservingVersePrefix(activeChordAbove.Value.chordLine, lyricText, options.TabWidth));
                    }
                    else
                    {
                        output.Add(lyricText);
                    }
                }
                else
                {
                    output.Add(lyricText);
                }
            }

            var raw = string.Join("\n", output).TrimEnd() + "\n";
            return OcrOutputCleaner.Clean(raw);
        }

        /// <summary>
        /// Attempts to render a PDF page as a PNG stream; returns <c>null</c> on any error
        /// (e.g. page index out of range, corrupted PDF).
        /// </summary>
        /// <param name="pdfPath">Path to the PDF file.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="dpi">Rendering DPI.</param>
        /// <returns>PNG stream positioned at 0, or <c>null</c> if rendering failed.</returns>
        private Stream TryRender(string pdfPath, int pageIndex, int dpi)
        {
            try
            {
                return _renderer.RenderPageAsPng(pdfPath, pageIndex, dpi);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Inserts chord tokens from the chord OCR pass into a lyric line using the x-coordinates
        /// of chord words relative to the lyric line bounding box.
        /// Only chords that are spatially above the lyric line within <paramref name="maxGap"/> pixels
        /// are considered; near-duplicate positions are de-duplicated.
        /// </summary>
        /// <param name="chordWords">List of (word, cleaned chord) pairs from the chord OCR pass.</param>
        /// <param name="lyricLine">The target lyric OCR line.</param>
        /// <param name="maxGap">Maximum vertical gap in pixels between chord and lyric line.</param>
        /// <param name="noteLineAbove">Optional staff noise line to tighten the chord search window.</param>
        /// <returns>The lyric text with chord brackets inserted, or <c>null</c> if no chords were found.</returns>
        private static string InsertChordsFromWordsAbove(List<(OcrWord word, string chord)> chordWords, OcrLine lyricLine, int maxGap, OcrLine noteLineAbove)
        {
            if (lyricLine == null || lyricLine.Words == null || lyricLine.Words.Count == 0) return null;
            if (lyricLine.Box == RectangleF.Empty) return null;

            var lyricText = FixText(string.Join(" ", lyricLine.Words.Select(w => w.Text)).Trim());
            if (lyricText.IsBlankOrWhitespace()) return null;

            var spans = SyllableSpans(lyricText);
            if (spans.Count == 0) return lyricText;

            // Map lyric spans to OCR x positions (best-effort).
            var lyricWords = lyricLine.Words.OrderBy(w => w.Box.Left).ToList();
            var spanXs = new List<float>();
            for (int i = 0; i < spans.Count; i++)
            {
                if (i < lyricWords.Count)
                    spanXs.Add(lyricWords[i].Box.Left + lyricWords[i].Box.Width * 0.2f);
                else
                    spanXs.Add(spanXs.Count > 0 ? spanXs[^1] + 10 : lyricLine.Box.Left);
            }

            // Only chords that are strictly ABOVE this lyric line.
            // If we have a detected staff/noise line above, use it as the anchor (chords sit above the staff).
            var top = lyricLine.Box.Top;

            float windowBottom = top;
            float windowTop = top - maxGap;

            if (noteLineAbove != null && noteLineAbove.Box != RectangleF.Empty)
            {
                // Use a tighter band around the staff line: chords are typically just above it.
                windowBottom = Math.Min(windowBottom, noteLineAbove.Box.Top);
                windowTop = Math.Max(windowTop, windowBottom - 90);
            }

            var left = lyricLine.Box.Left - 20;
            var right = lyricLine.Box.Right + 20;

            var candidates = chordWords
                .Select(c => (x: c.word.Box.Left + c.word.Box.Width * 0.2f, y: c.word.Box.Top + c.word.Box.Height * 0.5f, chord: c.chord))
                .Where(c => c.y < windowBottom && c.y >= windowTop)
                .Where(c => c.x >= left && c.x <= right)
                .OrderBy(c => c.x)
                .ToList();

            if (candidates.Count == 0) return null;

            // De-duplicate near-identical x positions (OCR often duplicates chord tokens).
            var deduped = new List<(float x, string chord)>();
            foreach (var c in candidates)
            {
                if (deduped.Count == 0)
                {
                    deduped.Add((c.x, c.chord));
                    continue;
                }

                var last = deduped[^1];
                if (Math.Abs(last.x - c.x) <= 8 && string.Equals(last.chord, c.chord, StringComparison.OrdinalIgnoreCase))
                    continue;
                deduped.Add((c.x, c.chord));
            }

            var insertions = new List<(int pos, string chord)>();
            foreach (var c in deduped)
            {
                var idx = FindClosest(spanXs, c.x);
                insertions.Add((spans[idx].start, c.chord));
            }

            var sb = new StringBuilder(lyricText);
            foreach (var ins in insertions.OrderByDescending(i => i.pos))
            {
                sb.Insert(ins.pos, $"[{ins.chord}]");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Searches backwards through the lyric line list for the nearest staff/noise line
        /// that appears directly above the given lyric line within a reasonable vertical distance.
        /// </summary>
        /// <param name="lyricLines">All lyric OCR lines in page order.</param>
        /// <param name="startIndex">Index to start searching backwards from (exclusive).</param>
        /// <param name="lyricLine">The reference lyric line.</param>
        /// <returns>The nearest note-noise OCR line above, or <c>null</c> if none found.</returns>
        private static OcrLine FindNearestNoteLineAbove(List<OcrLine> lyricLines, int startIndex, OcrLine lyricLine)
        {
            if (lyricLines == null || startIndex < 0 || lyricLine == null) return null;
            if (lyricLine.Box == RectangleF.Empty) return null;

            // Look backwards up to a reasonable vertical distance.
            var lyricTop = lyricLine.Box.Top;
            for (int i = startIndex; i >= 0 && i >= startIndex - 6; i--)
            {
                var cand = lyricLines[i];
                if (cand?.Words == null || cand.Words.Count == 0) continue;
                if (cand.Box == RectangleF.Empty) continue;

                if (cand.Box.Bottom > lyricTop) continue; // not above
                var dist = lyricTop - cand.Box.Bottom;
                if (dist > 260) break;

                if (IsNoteNoiseLine(cand))
                {
                    return cand;
                }
            }

            return null;
        }

        /// <summary>
        /// Returns the index of the x-coordinate in <paramref name="xs"/> that is closest to <paramref name="x"/>.
        /// </summary>
        /// <param name="xs">List of x-coordinates to search.</param>
        /// <param name="x">Target x value.</param>
        /// <returns>Index of the closest entry.</returns>
        private static int FindClosest(List<float> xs, float x)
        {
            var bestIdx = 0;
            var best = float.MaxValue;
            for (int i = 0; i < xs.Count; i++)
            {
                var d = Math.Abs(xs[i] - x);
                if (d < best)
                {
                    best = d;
                    bestIdx = i;
                }
            }
            return bestIdx;
        }

        /// <summary>
        /// Cleans an OCR body-text line by normalizing dash characters, removing common scan
        /// artifacts (score lines, box chars, ligatures, stray punctuation) and smoothing
        /// OCR syllable hyphenation.
        /// </summary>
        /// <param name="text">Raw OCR text line.</param>
        /// <returns>Fixed text, or an empty string for blank input.</returns>
        private static string FixText(string text)
        {
            if (text.IsBlankOrWhitespace()) return string.Empty;
            var t = text.Replace('–', '-').Replace('—', '-');
            t = t.Replace("_", "");
            t = t.Replace("|", " ");
            t = t.Replace("[", " ").Replace("]", " ");

            // Drop common OCR garbage symbols seen in scanned music sheets.
            t = Regex.Replace(t, @"[§®¤•◦·…ﬂ\u201c\u201d\u2019\u2018]", " ");

            // Strip trailing single garbage characters that OCR appends to words.
            // Common artifacts: f, q, T, {, }, %, etc. at end of words (e.g. "Gegenwartf" -> "Gegenwart").
            t = Regex.Replace(t, @"(?<=\p{L}{2})[qT\{%\u20ac\)\(]+(?=\s|$)", "");
            t = Regex.Replace(t, @"(?<=\p{L}{4})f+(?=\s|$)", "");
            // Also strip trailing 'fl' ligature artifact and stray 'l'/'1' at end of words.
            t = Regex.Replace(t, @"(?<=\p{L}{2})fl(?=\s|$)", "");
            // Also strip leading garbage before lyric words (e.g. ">Vater" -> "Vater").
            t = Regex.Replace(t, @"(?:^|\s)[>\u201e<\u00bb\u00ab]+(?=\p{L})", " ");

            // Strip ".>" prefix artifacts (e.g. "3.>Vater" -> "3. Vater").
            t = Regex.Replace(t, @"(\d+\.)>", "$1 ");

            // Smooth OCR syllable hyphenation like "al - ler" -> "aller".
            // Keep this conservative: only when hyphen is between letters.
            t = Regex.Replace(t, @"(?<=\p{L})\s*-\s*(?=\p{L})", "");

            // Remove isolated punctuation tokens (slashes, stray quotes, etc.).
            t = Regex.Replace(t, @"\s+[\\/]{1,3}\s+", " ");
            t = Regex.Replace(t, @"\s+['`´]{1,3}\s+", " ");

            // Remove '~' and '^' which are often staff-line OCR artifacts.
            t = t.Replace("~", " ").Replace("^", "");

            // Remove stray OCR digits/letters that appear as isolated noise (e.g. single '0', 'O').
            t = Regex.Replace(t, @"\s+[0OoIl]\s+", " ");

            t = StripLeadingGarbageTokens(t);

            t = Regex.Replace(t, @"\s{2,}", " ");
            t = Regex.Replace(t, @"\s+([,.;:!?])", "$1");
            return t.Trim();
        }

        /// <summary>
        /// Attempts to parse the line as a song section label (Strophe/Vers, Chorus, Bridge, Intro, Outro, etc.).
        /// </summary>
        /// <param name="text">Text to inspect.</param>
        /// <param name="section">Normalized section name when successful (e.g. "Vers 2", "Chorus").</param>
        /// <returns><c>true</c> if the text is a section heading.</returns>
        private static bool TryParseSection(string text, out string section)
        {
            section = null;
            var raw = (text ?? string.Empty).Trim();
            var m = Regex.Match(raw, @"^(strophe|vers|verse)\s*(\d+)?\b", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var baseLabel = "Vers";
                section = baseLabel + (m.Groups[2].Success ? " " + m.Groups[2].Value : "");
                return true;
            }
            if (Regex.IsMatch(raw, @"^(refr\.?|refrain|chorus)\b", RegexOptions.IgnoreCase))
            {
                section = "Chorus";
                return true;
            }
            if (Regex.IsMatch(raw, @"^(bridge)\b", RegexOptions.IgnoreCase))
            {
                section = "Bridge";
                return true;
            }
            if (Regex.IsMatch(raw, @"^(intro)\b", RegexOptions.IgnoreCase))
            {
                section = "Intro";
                return true;
            }
            if (Regex.IsMatch(raw, @"^(outro|ending)\b", RegexOptions.IgnoreCase))
            {
                section = "Outro";
                return true;
            }
            if (Regex.IsMatch(raw, @"^(zwischenspiel|interlude)\b", RegexOptions.IgnoreCase))
            {
                section = "Interlude";
                return true;
            }
            return false;
        }

        /// <summary>
        /// Extracts the song title and optional subtitle from the first few non-section text lines.
        /// A short, non-chord line loosely matching the filename is preferred as the title.
        /// A line starting with "Satz:" is used as the subtitle.
        /// </summary>
        /// <param name="lines">Flat text lines from all OCR pages.</param>
        /// <param name="fallbackTitle">Filename-derived fallback title used for similarity filtering.</param>
        /// <returns>Tuple of (title, subtitle).</returns>
        private static (string title, string subtitle) ExtractTitleAndSubtitle(List<string> lines, string fallbackTitle)
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
                if (title == null && LooksLikeTitleCandidate(t, fallbackTitle) && !Regex.IsMatch(t, @"^(strophe|refr|chorus|vers)\b", RegexOptions.IgnoreCase))
                {
                    title = t;
                }
                if (title != null && subtitle != null) break;
            }
            return (title ?? (fallbackTitle.IsBlankOrWhitespace() ? "Unbekannter Titel" : fallbackTitle), subtitle);
        }

        /// <summary>
        /// Heuristically determines whether a text line is a plausible song title candidate.
        /// Rejects lines that are too long, contain noise, look like chord rows, or diverge too much from the filename.
        /// </summary>
        /// <param name="text">The candidate line.</param>
        /// <param name="fallbackTitle">The filename-derived fallback for similarity comparison.</param>
        /// <returns><c>true</c> if the line could be the song title.</returns>
        private static bool LooksLikeTitleCandidate(string text, string fallbackTitle)
        {
            if (text.IsBlankOrWhitespace()) return false;
            if (text.Length > 80) return false;
            if (LooksLikeStaffNoise(text)) return false;

            // Verse numbers are not titles.
            if (Regex.IsMatch(text, @"^\s*\d+\s*[.)]")) return false;

            // ChordPro bracket artifacts or OCR debris shouldn't be titles.
            if (text.Contains('[') || text.Contains(']')) return false;

            var letters = text.Count(char.IsLetter);
            if (letters < 6) return false;

            // Heuristic: title should loosely match the filename (fallback title), otherwise it's likely OCR garbage.
            if (!fallbackTitle.IsBlankOrWhitespace())
            {
                var sim = TitleWordSimilarity(text, fallbackTitle);
                if (sim < 0.25) return false;
            }

            // Avoid lines that are mostly chords.
            var tokens = MergeSplitChordTokens(Regex.Split(text.Trim(), @"\s+"))
                .Select(CleanChord)
                .Where(t => !t.IsBlankOrWhitespace())
                .ToList();
            var chordish = tokens.Count(IsChordTokenStrict);
            if (chordish >= 2 && chordish >= (tokens.Count * 0.6)) return false;

            // Avoid syllabified lyric lines (common in body OCR): lots of spaced hyphens.
            var spacedHyphens = Regex.Matches(text, @"\p{L}\s*-\s*\p{L}").Count;
            if (spacedHyphens >= 3) return false;

            return true;
        }

        /// <summary>
        /// Searches the text lines for a copyright declaration starting with "Rechte:", "Copyright:",
        /// or a '©' symbol.
        /// </summary>
        /// <param name="lines">Text lines to scan.</param>
        /// <returns>Cleaned copyright line, or <c>null</c> if none found.</returns>
        private static string ExtractCopyright(List<string> lines)
        {
            foreach (var l in lines)
            {
                var t = l.Trim();
                if (Regex.IsMatch(t, @"^(rechte|copyright)\s*:\s*", RegexOptions.IgnoreCase))
                    return CleanupHeaderLine(t);
                if (Regex.IsMatch(t, @"^\s*©\b"))
                    return CleanupHeaderLine(t);
            }
            return null;
        }

        /// <summary>
        /// Detects the key signature by returning the first strict chord token found in the chord OCR lines.
        /// </summary>
        /// <param name="chordLines">OCR lines from the chord pass.</param>
        /// <returns>Key string (e.g. "G", "Am"), or <c>null</c> if not detected.</returns>
        private static string DetectKeyFromChords(List<OcrLine> chordLines)
        {
            foreach (var line in chordLines ?? new List<OcrLine>())
            {
                if (line?.Words == null || line.Words.Count == 0) continue;
                if (IsChordNoiseLine(line)) continue;

                foreach (var w in line.Words)
                {
                    var chord = CleanChord(w.Text);
                    if (IsChordTokenStrict(chord)) return chord;
                }
            }

            return null;
        }

        /// <summary>
        /// Cleans an OCR chord token by removing non-musical characters, correcting common OCR
        /// artifacts (stray letters, maj7 variants), and normalizing root note casing.
        /// Single lowercase letters are converted to minor shorthand (e.g. "e" → "Em").
        /// </summary>
        /// <param name="token">Raw OCR token.</param>
        /// <returns>Cleaned chord string, or empty string if invalid.</returns>
        private static string CleanChord(string token)
        {
            if (token.IsBlankOrWhitespace()) return string.Empty;
            var cleaned = Regex.Replace(token.Trim(), @"[^A-Za-z0-9#b/]+", "");
            if (cleaned.Length == 0) return string.Empty;

            // Common OCR artifact: stray 'l' after root (e.g. "Blsus4" -> "Bsus4").
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])l(?=[A-Za-z])", "$1", RegexOptions.IgnoreCase);

            // Common OCR confusions for minor/maj7.
            // Bim -> Bm, Em -> Em (unchanged)
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])im$", "$1m", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])i?m$", "$1m", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])ma7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])mqj7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])maij7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])mai7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])maj7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])i7$", "$17", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])i([0-9]+)$", "$1$2", RegexOptions.IgnoreCase);

            // Special-case: single-letter chords are often OCR'ed as lowercase letters.
            // Convention for this converter: lowercase root => minor (e -> Em), uppercase => major (E -> E).
            if (cleaned.Length == 1)
            {
                var c = cleaned[0];
                if (c is >= 'a' and <= 'h')
                {
                    return char.ToUpperInvariant(c) + "m";
                }

                if (c is >= 'A' and <= 'H')
                {
                    return c.ToString();
                }
            }

            // Normalize root letter casing.
            cleaned = char.ToUpperInvariant(cleaned[0]) + (cleaned.Length > 1 ? cleaned.Substring(1) : string.Empty);

            if (cleaned.Length >= 2)
            {
                var root = cleaned[0];
                var second = cleaned[1];
                if ((root is >= 'A' and <= 'H' or >= 'a' and <= 'h') && (second == 'h' || second == 'H'))
                    cleaned = cleaned[0] + cleaned.Substring(2);
            }
            return cleaned;
        }

        /// <summary>
        /// Returns <c>true</c> if the line is a chord-only line (≥60% valid chord tokens,
        /// at least 2 chord tokens, no staff-note-row pattern).
        /// </summary>
        /// <param name="text">Text line to inspect.</param>
        /// <returns><c>true</c> if the line consists predominantly of chord tokens.</returns>
        private static bool IsChordOnlyTextLine(string text)
        {
            if (text.IsBlankOrWhitespace()) return false;

            var tokens = MergeSplitChordTokens(Regex.Split(text.Trim(), @"\s+"))
                .Select(CleanChord)
                .Where(t => !t.IsBlankOrWhitespace())
                .ToList();

            if (tokens.Count < 2) return false;
            var chordTokens = tokens.Where(IsChordTokenStrict).ToList();
            if (chordTokens.Count < 2) return false;

            // Tolerate a few OCR garbage tokens on chord lines.
            var ratio = (double)chordTokens.Count / Math.Max(1, tokens.Count);
            if (ratio < 0.60) return false;

            // Guard: reject staff note rows (many single-letter tokens).
            var singleLetter = chordTokens.Count(t => t.Length == 1);
            if (chordTokens.Count >= 6 && singleLetter >= (chordTokens.Count * 0.85)) return false;

            return true;
        }

        /// <summary>
        /// Extracts chord tokens from a plain text line and joins them with tabs
        /// for downstream positional chord processing.
        /// </summary>
        /// <param name="text">Text line containing chord tokens mixed or alone.</param>
        /// <returns>Tab-separated chord token string, or empty if no valid chords.</returns>
        private static string NormalizeChordLine(string text)
        {
            var tokens = MergeSplitChordTokens(Regex.Split(text.Trim(), @"\s+"))
                .Select(CleanChord)
                .Where(t => !t.IsBlankOrWhitespace() && IsChordTokenStrict(t))
                .ToList();
            return string.Join("\t", tokens);
        }

        /// <summary>
        /// Extracts chord tokens from an OCR line (preserving x-order from word bounding boxes)
        /// and returns them space-separated for downstream positional chord insertion.
        /// Returns <c>null</c> if fewer than 2 chord tokens are found.
        /// </summary>
        /// <param name="line">OCR line from the chord recognition pass.</param>
        /// <returns>Space-separated chord tokens, or <c>null</c>.</returns>
        private static string NormalizeChordLineFromOcrLine(OcrLine line)
        {
            if (line?.Words == null || line.Words.Count == 0) return null;
            var tokens = MergeSplitChordTokens(line.Words
                    .OrderBy(w => w.Box.Left)
                    .Select(w => w.Text)
                    .Where(t => !t.IsBlankOrWhitespace()))
                .Select(CleanChord)
                .Where(t => !t.IsBlankOrWhitespace() && IsChordTokenStrict(t))
                .ToList();

            // Keep only meaningful chord lines.
            if (tokens.Count < 2) return null;

            // Use spaces (not tabs): OCR has no reliable tabs; composer will distribute across words.
            return string.Join(" ", tokens);
        }

        /// <summary>
        /// Strips leading OCR garbage tokens from a text line (slash/backslash tokens,
        /// isolated single letters) when at least 3 consecutive garbage tokens are found.
        /// Short lines (fewer than 4 tokens) are not modified.
        /// </summary>
        /// <param name="text">Text line to clean.</param>
        /// <returns>The cleaned text.</returns>
        private static string StripLeadingGarbageTokens(string text)
        {
            if (text.IsBlankOrWhitespace()) return string.Empty;

            var tokens = Regex.Split(text.Trim(), @"\s+").Where(t => t.Length > 0).ToList();
            if (tokens.Count < 4) return text.Trim();

            var removed = 0;
            while (tokens.Count > 0 && removed < 10)
            {
                var tok = tokens[0];
                if (tok == "/" || tok == "\\")
                {
                    tokens.RemoveAt(0);
                    removed++;
                    continue;
                }

                if (tok.Length == 1 && char.IsLetter(tok[0]))
                {
                    tokens.RemoveAt(0);
                    removed++;
                    continue;
                }

                break;
            }

            // Only strip if it looked like a real garbage prefix.
            if (removed >= 3) return string.Join(" ", tokens).Trim();

            return text.Trim();
        }

        /// <summary>
        /// Finds the chord text string from the chord OCR pass that appears directly above
        /// the given lyric line within <paramref name="maxGap"/> pixels, with horizontal overlap.
        /// Returns the closest (minimum vertical distance) candidate.
        /// </summary>
        /// <param name="chordLineItems">All chord OCR lines with their cleaned chord strings.</param>
        /// <param name="lyricLine">The lyric OCR line to match against.</param>
        /// <param name="maxGap">Maximum vertical distance in pixels.</param>
        /// <returns>The chord line string, or <c>null</c> if none found.</returns>
        private static string FindChordLineAbove(List<(OcrLine line, string chordLine)> chordLineItems, OcrLine lyricLine, int maxGap)
        {
            if (lyricLine?.Box == RectangleF.Empty) return null;
            if (chordLineItems == null || chordLineItems.Count == 0) return null;

            var top = lyricLine.Box.Top;
            var left = lyricLine.Box.Left - 20;
            var right = lyricLine.Box.Right + 20;

            (float dist, string chordLine) best = (float.MaxValue, null);

            foreach (var item in chordLineItems)
            {
                var cl = item.line;
                if (cl?.Box == RectangleF.Empty) continue;

                // Must be above
                if (cl.Box.Bottom > top) continue;

                var dist = top - cl.Box.Bottom;
                if (dist < 0 || dist > maxGap) continue;

                // Must overlap horizontally
                if (cl.Box.Right < left || cl.Box.Left > right) continue;

                if (dist < best.dist)
                {
                    best = (dist, item.chordLine);
                }
            }

            return best.chordLine;
        }

        /// <summary>
        /// Finds the (line, chordLine) item from the chord OCR pass that appears directly above
        /// the given lyric line within <paramref name="maxGap"/> pixels, with horizontal overlap.
        /// Returns the full item so the caller can access the source OCR line bounding box.
        /// </summary>
        /// <param name="chordLineItems">All chord OCR lines with cleaned chord strings.</param>
        /// <param name="lyricLine">The lyric OCR line to match against.</param>
        /// <param name="maxGap">Maximum vertical distance in pixels.</param>
        /// <returns>The best matching (line, chordLine) item, or <c>null</c> if none found.</returns>
        private static (OcrLine line, string chordLine)? FindChordLineAboveItem(List<(OcrLine line, string chordLine)> chordLineItems, OcrLine lyricLine, int maxGap)
        {
            if (lyricLine?.Box == RectangleF.Empty) return null;
            if (chordLineItems == null || chordLineItems.Count == 0) return null;

            var top = lyricLine.Box.Top;
            var left = lyricLine.Box.Left - 20;
            var right = lyricLine.Box.Right + 20;

            (float dist, OcrLine line, string chordLine) best = (float.MaxValue, null, null);

            foreach (var item in chordLineItems)
            {
                var cl = item.line;
                if (cl?.Box == RectangleF.Empty) continue;

                // Must be above
                if (cl.Box.Bottom > top) continue;

                var dist = top - cl.Box.Bottom;
                if (dist < 0 || dist > maxGap) continue;

                // Must overlap horizontally
                if (cl.Box.Right < left || cl.Box.Left > right) continue;

                if (dist < best.dist)
                {
                    best = (dist, cl, item.chordLine);
                }
            }

            return best.line == null || best.chordLine.IsBlankOrWhitespace() ? null : (best.line, best.chordLine);
        }

        /// <summary>
        /// Checks whether the text starts with a verse number prefix (e.g. "1. ", "2) "),
        /// including OCR misreads of "1" as "l" or "I".
        /// </summary>
        /// <param name="text">Text to inspect.</param>
        /// <returns><c>true</c> if the text starts with a numbering pattern.</returns>
        private static bool IsNumberedVerseLine(string text)
        {
            if (text.IsBlankOrWhitespace()) return false;
            return Regex.IsMatch(text.TrimStart(), @"^(?:\d+|[lI])\s*[.)]\s+", RegexOptions.CultureInvariant);
        }

        /// <summary>
        /// Returns <c>true</c> if the text looks like a lyric line
        /// (at least 8 letters after stripping verse number prefixes, or hyphenated syllables).
        /// </summary>
        /// <param name="text">Text to check.</param>
        /// <returns><c>true</c> if the text is likely lyric content.</returns>
        private static bool LooksLikeLyricLine(string text)
        {
            if (text.IsBlankOrWhitespace()) return false;

            // Allow leading verse numbers.
            var t = Regex.Replace(text, @"^\s*\d+\s*[.)]\s*", "");
            var letters = t.Count(char.IsLetter);
            if (letters >= 8) return true;
            if (Regex.IsMatch(t, @"\p{L}{2,}-\p{L}{2,}") && letters >= 8) return true;
            return false;
        }

        /// <summary>
        /// Determines whether the text is a strong enough lyric start to begin ChordPro output.
        /// Verse-numbered lines or lines with ≥12 letters pass; header/garbage lines do not.
        /// </summary>
        /// <param name="text">Text to inspect.</param>
        /// <returns><c>true</c> if output should start at this line.</returns>
        private static bool LooksLikeLyricStart(string text)
        {
            if (text.IsBlankOrWhitespace()) return false;
            var t = text.Trim();

            // Typical: "1. ..." or "1) ..."
            if (Regex.IsMatch(t, @"^\d+\s*[.)]"))
            {
                var letters = t.Count(char.IsLetter);
                return letters >= 6;
            }

            // If there's any digit (but no verse number), it's usually header/garbage (e.g. "a8").
            if (t.Any(char.IsDigit)) return false;

            // Otherwise require a very strong lyric signal to start output.
            // (We prefer skipping a legitimate unnumbered first line over starting on OCR garbage.)
            return LooksLikeLyricLine(t) && t.Count(char.IsLetter) >= 12;
        }

        /// <summary>
        /// Returns <c>true</c> if the text should be discarded as OCR garbage in the body
        /// (blank, staff noise, mostly-short-token rows, or too few letters).
        /// </summary>
        /// <param name="text">Text to inspect.</param>
        /// <returns><c>true</c> if the line is garbage and should be skipped.</returns>
        private static bool IsProbablyGarbageBodyLine(string text)
        {
            if (text.IsBlankOrWhitespace()) return true;

            if (LooksLikeStaffNoise(text) || LooksLikeMostlyShortTokens(text)) return true;

            var letters = text.Count(char.IsLetter);
            var digits = text.Count(char.IsDigit);
            var symbols = text.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && c != '-' && c != '/');

            // If it doesn't look like lyrics and is short-ish, it's usually OCR debris.
            if (!LooksLikeLyricLine(text))
            {
                if (letters < 10) return true;
                if (text.Length < 30) return true;
            }

            // Too many symbols/digits compared to letters => garbage.
            if (symbols >= 8 && letters <= 10) return true;
            if (digits >= 8 && letters <= 10) return true;

            return false;
        }

        /// <summary>
        /// Merges OCR-split chord tokens (e.g. "B" + "m" → "Bm", "A" + "7" → "A7") that
        /// should form a single chord but were split by Tesseract whitespace segmentation.
        /// </summary>
        /// <param name="rawTokens">Raw token sequence to merge.</param>
        /// <returns>Token list with adjacent chord root + modifier tokens joined.</returns>
        private static IReadOnlyList<string> MergeSplitChordTokens(IEnumerable<string> rawTokens)
        {
            var tokens = (rawTokens ?? Array.Empty<string>())
                .Select(t => (t ?? string.Empty).Trim())
                .Where(t => t.Length > 0)
                .ToList();

            var merged = new List<string>();
            for (int i = 0; i < tokens.Count; i++)
            {
                var a = tokens[i];
                if (i + 1 < tokens.Count)
                {
                    var b = tokens[i + 1];
                    if (IsChordRootToken(a) && IsChordModifierToken(b))
                    {
                        merged.Add(a + b);
                        i++;
                        continue;
                    }

                    if (IsChordRootToken(a) && Regex.IsMatch(b, @"^\d+$"))
                    {
                        merged.Add(a + b);
                        i++;
                        continue;
                    }
                }

                merged.Add(a);
            }

            return merged;
        }

        /// <summary>
        /// Returns <c>true</c> if the token matches the pattern of a chord root note
        /// (e.g. "A", "Bb", "Cis", "Es").
        /// </summary>
        /// <param name="token">Token to check.</param>
        /// <returns><c>true</c> if the token is a chord root.</returns>
        private static bool IsChordRootToken(string token)
        {
            if (token.IsBlankOrWhitespace()) return false;
            var t = Regex.Replace(token.Trim(), @"[^A-Za-z#b]", "");
            return Regex.IsMatch(t, @"^[A-Ha-h](?:is|es|[#b])?$", RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Returns <c>true</c> if the token is a chord quality modifier
        /// (e.g. "m", "maj", "maj7", "sus4", "add9", "dim", "aug", or a numeric extension).
        /// </summary>
        /// <param name="token">Token to check.</param>
        /// <returns><c>true</c> if the token is a chord modifier.</returns>
        private static bool IsChordModifierToken(string token)
        {
            if (token.IsBlankOrWhitespace()) return false;
            var t = token.Trim().ToLowerInvariant();

            if (Regex.IsMatch(t, @"^(?:m|min|maj|maj7|sus|sus2|sus4|add\d+|dim|aug)$")) return true;
            if (Regex.IsMatch(t, @"^(?:\d{1,2})$")) return true;
            if (Regex.IsMatch(t, @"^(?:sus\d)$")) return true;
            return false;
        }

        /// <summary>
        /// Strict chord token test: must match the chord regex AND not be a single
        /// ambiguous letter that could be a lyric word (only A-H and a-h pass as single-char).
        /// </summary>
        /// <param name="token">Cleaned chord token.</param>
        /// <returns><c>true</c> if the token is a valid, unambiguous chord.</returns>
        private static bool IsChordTokenStrict(string token)
        {
            if (token.IsBlankOrWhitespace()) return false;
            if (!ChordTokenRe.IsMatch(token)) return false;

            // Prevent false positives like "e"/"g" from lyric OCR.
            if (token.Length == 1)
            {
                var c = token[0];
                // Allow A-H (major) and a-h (minor shorthand from OCR pass).
                return (c is >= 'A' and <= 'H') || (c is >= 'a' and <= 'h');
            }

            return true;
        }

        /// <summary>
        /// Determines whether a chord OCR line is noise (empty, no chord tokens,
        /// or a staff line made of many single-letter note names).
        /// </summary>
        /// <param name="line">OCR line from the chord recognition pass.</param>
        /// <returns><c>true</c> if the line should be discarded as chord noise.</returns>
        private static bool IsChordNoiseLine(OcrLine line)
        {
            if (line?.Words == null || line.Words.Count == 0) return true;

            var tokens = line.Words.Select(w => CleanChord(w.Text)).Where(t => !t.IsBlankOrWhitespace()).ToList();
            var chordish = tokens.Where(IsChordTokenStrict).ToList();
            if (chordish.Count == 0) return true;

            // A staff line often OCRs as many single letters (note names) with no modifiers.
            if (chordish.Count >= 6 && chordish.All(t => t.Length == 1)) return true;

            // If it's only a single-letter chord, it's very likely a note, not a chord line.
            if (chordish.Count == 1 && chordish[0].Length == 1) return true;

            // If the line is dominated by symbols, treat it as noise.
            var text = string.Join(" ", line.Words.Select(w => w.Text ?? string.Empty));
            if (LooksLikeStaffNoise(text) && chordish.Count < 2) return true;

            return false;
        }

        /// <summary>
        /// Heuristically detects staff-line or music-notation OCR noise (box-drawing characters,
        /// equal/underscore runs, excessive symbol-to-letter ratios).
        /// </summary>
        /// <param name="text">Text to inspect.</param>
        /// <returns><c>true</c> if the text looks like scanned staff/noise.</returns>
        private static bool LooksLikeStaffNoise(string text)
        {
            if (text.IsBlankOrWhitespace()) return true;

            if (text.Contains("===") || text.Contains("___") || text.Contains("%%") || text.Contains("||"))
                return true;

            // Common scan artifacts.
            if (Regex.IsMatch(text, @"[=_]{2,}")) return true;
            if (Regex.IsMatch(text, @"-{4,}")) return true;

            var letters = text.Count(char.IsLetter);
            var digits = text.Count(char.IsDigit);
            var symbols = text.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && c != '-' && c != '/');
            var ratioLetters = (double)letters / Math.Max(1, text.Length);
            var ratioSymbols = (double)symbols / Math.Max(1, text.Length);

            if (ratioSymbols > 0.22 && ratioLetters < 0.40) return true;
            if (symbols >= 10 && letters <= 6) return true;
            if (digits >= 10 && letters <= 6) return true;

            return false;
        }

        /// <summary>
        /// Returns the start/end character index of each non-whitespace word in the text.
        /// </summary>
        /// <param name="text">Text to scan.</param>
        /// <returns>List of (start, end) character index pairs.</returns>
        private static List<(int start, int end)> WordSpans(string text)
        {
            var spans = new List<(int, int)>();
            foreach (Match m in Regex.Matches(text ?? string.Empty, @"\S+"))
                spans.Add((m.Index, m.Index + m.Length - 1));
            return spans;
        }

        /// <summary>
        /// Returns syllable-level character spans by splitting on both spaces and hyphens,
        /// enabling per-syllable chord insertion. Falls back to word spans if fewer than
        /// two syllables are found.
        /// </summary>
        /// <param name="text">The lyric text to split.</param>
        /// <returns>List of (start, end) character index pairs for each syllable.</returns>
        private static List<(int start, int end)> SyllableSpans(string text)
        {
            // Prefer syllable-level spans: split on spaces and hyphens.
            // Example: "Die Mö-wen" -> ["Die", "Mö", "wen"] spans.
            var spans = new List<(int start, int end)>();
            if (text.IsBlankOrWhitespace()) return spans;

            int i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length) break;

                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '-') i++;
                int end = i - 1;
                if (end >= start) spans.Add((start, end));

                // Skip hyphen but treat next segment as new syllable.
                if (i < text.Length && text[i] == '-') i++;
            }

            // Fallback: if we ended up with a single span, keep word spans (better than nothing).
            if (spans.Count <= 1)
            {
                var ws = WordSpans(text);
                if (ws.Count > spans.Count) return ws;
            }
            return spans;
        }

        /// <summary>
        /// Determines whether an OCR line is staff/music-notation noise rather than
        /// real lyric or chord content.
        /// Checks symbol ratios, mostly-single-token patterns, and chord-line heuristics.
        /// </summary>
        /// <param name="line">OCR line to inspect.</param>
        /// <returns><c>true</c> if the line is likely staff/note noise.</returns>
        private static bool IsNoteNoiseLine(OcrLine line)
        {
            if (line?.Words == null || line.Words.Count == 0) return false;

            var text = string.Join(" ", line.Words.Select(w => w.Text ?? string.Empty)).Trim();
            if (text.Length == 0) return true;

            // Typical staff-line OCR noise: lots of punctuation / box-drawing / underscores, very few letters.
            var letters = text.Count(char.IsLetter);
            var digits = text.Count(char.IsDigit);
            var noise = text.Count(c => c is '_' or '|' or '=' or '~' or '^' or '[' or ']' or '{' or '}' or '\\' or '/' or '*' or '“' or '”' or '’' or '‘');
            var ratioLetters = (double)letters / Math.Max(1, text.Length);

            // If it has almost no letters and is mostly symbols, treat as note line.
            if (letters <= 2 && noise >= 4) return true;
            if (ratioLetters < 0.20 && noise >= 6) return true;

            if (LooksLikeStaffNoise(text)) return true;

            // Lines like "a r w w A e S F" are classic notation/OCR debris.
            if (LooksLikeMostlyShortTokens(text)) return true;

            // Lines with many single-character tokens are also often staff noise.
            var tokens = Regex.Split(text, @"\s+").Where(t => t.Length > 0).ToList();
            var singleChar = tokens.Count(t => t.Length == 1);
            if (tokens.Count >= 6 && singleChar >= (tokens.Count * 0.7) && letters <= 4) return true;

            // Allow real lyric lines even if they contain hyphens.
            if (letters >= 8 && ratioLetters > 0.35) return false;

            // Keep chord lines out of here.
            var chordish = tokens.Count(t => IsChordTokenStrict(CleanChord(t)));
            if (chordish >= 2) return false;

            // Remaining short + noisy lines: treat as note noise.
            if (text.Length < 20 && (noise >= 3 || digits >= 4) && letters <= 3) return true;

            return false;
        }

        /// <summary>
        /// Adds the normalized form of <paramref name="text"/> to the header-skip set
        /// so duplicate lines appearing in the body are suppressed.
        /// </summary>
        /// <param name="set">The header-skip set to update.</param>
        /// <param name="text">The header text whose normalized form should be skipped.</param>
        private static void AddHeaderSkip(HashSet<string> set, string text)
        {
            if (set == null) return;
            var norm = NormalizeForHeaderCompare(text);
            if (!norm.IsBlankOrWhitespace()) set.Add(norm);
        }

        /// <summary>
        /// Produces a normalized key for header-skip comparison by stripping diacritics,
        /// lowercasing, and removing all non-alphanumeric characters.
        /// </summary>
        /// <param name="text">Text to normalize.</param>
        /// <returns>Normalized comparison key.</returns>
        private static string NormalizeForHeaderCompare(string text)
        {
            if (text.IsBlankOrWhitespace()) return string.Empty;
            var t = StripDiacritics(FixText(text)).ToLowerInvariant();
            // remove all non-alphanumeric so small OCR spacing differences don't matter
            t = Regex.Replace(t, @"[^a-z0-9]+", string.Empty);
            return t;
        }

        /// <summary>
        /// Returns <c>true</c> if the text consists mostly of very short tokens (1-2 chars),
        /// no long words, and few letters — indicating staff-line or music notation noise.
        /// Requires at least 6 tokens to activate.
        /// </summary>
        /// <param name="text">Text to inspect.</param>
        /// <returns><c>true</c> if the text looks like a mostly-short-token noise row.</returns>
        private static bool LooksLikeMostlyShortTokens(string text)
        {
            if (text.IsBlankOrWhitespace()) return false;
            var tokens = Regex.Split(text.Trim(), @"\s+").Where(t => t.Length > 0).ToList();
            if (tokens.Count < 6) return false;

            // If most tokens are 1-2 chars and there are no real words, it's almost certainly staff/noise.
            var shortTokens = tokens.Count(t => t.Length <= 2);
            var longTokens = tokens.Count(t => t.Length >= 4);
            var letters = text.Count(char.IsLetter);
            if (shortTokens >= tokens.Count * 0.80 && longTokens == 0 && letters <= 12) return true;

            return false;
        }

        /// <summary>
        /// Cleans a header line (title, copyright, etc.) by removing OCR pipe-character
        /// suffixes and trailing symbol noise.
        /// </summary>
        /// <param name="text">Raw header line.</param>
        /// <returns>Cleaned header text, or <c>null</c> for blank input.</returns>
        private static string CleanupHeaderLine(string text)
        {
            if (text.IsBlankOrWhitespace()) return null;
            var t = FixText(text);

            // Many scans contain trailing staff noise separated by '|'.
            var pipe = t.IndexOf('|');
            if (pipe >= 0) t = t.Substring(0, pipe).Trim();

            // Drop obvious trailing symbol noise.
            t = Regex.Replace(t, @"\s*[=~_\-]{3,}.*$", string.Empty).Trim();

            return t;
        }

        /// <summary>
        /// Computes a word-level Jaccard similarity between two strings after diacritic stripping.
        /// Used to filter OCR body lines that are unlikely to be the actual title.
        /// </summary>
        /// <param name="a">First string.</param>
        /// <param name="b">Second string.</param>
        /// <returns>Similarity in the range [0, 1].</returns>
        private static double TitleWordSimilarity(string a, string b)
        {
            var wa = new HashSet<string>(NormalizeTitleWords(a));
            var wb = new HashSet<string>(NormalizeTitleWords(b));
            if (wa.Count == 0 || wb.Count == 0) return 0;

            var inter = wa.Intersect(wb).Count();
            var union = wa.Union(wb).Count();
            return union == 0 ? 0 : (double)inter / union;
        }

        /// <summary>
        /// Yields normalized word tokens from a title string for similarity comparison.
        /// Strips diacritics, lowercases, and yields only tokens of 3+ characters.
        /// </summary>
        /// <param name="text">Title text to normalize.</param>
        /// <returns>Sequence of normalized word tokens.</returns>
        private static IEnumerable<string> NormalizeTitleWords(string text)
        {
            if (text.IsBlankOrWhitespace()) yield break;

            var s = StripDiacritics(text).ToLowerInvariant();
            foreach (var w in Regex.Split(s, @"[^a-z0-9]+"))
            {
                if (w.Length >= 3)
                    yield return w;
            }
        }

        /// <summary>
        /// Strips Unicode diacritical marks from a string by decomposing to NFD form
        /// and removing non-spacing mark characters.
        /// </summary>
        /// <param name="text">Input string, may be <c>null</c>.</param>
        /// <returns>String with diacritics removed, or <c>null</c> if input is <c>null</c>.</returns>
        private static string StripDiacritics(string text)
        {
            if (text == null) return null;
            var normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        /// <summary>
        /// Ensures the output list ends with a blank line,
        /// inserting one if the last line is non-empty.
        /// </summary>
        /// <param name="output">Output line list to modify.</param>
        private static void EnsureBlank(List<string> output)
        {
            if (output.Count == 0) return;
            if (!output[^1].IsBlankOrWhitespace()) output.Add(string.Empty);
        }

        /// <inheritdoc />
        public Task<(string, string)> ConvertAsync(string pdfPath, PdfConverterOptions options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Convert(pdfPath, options));
        }
    }
}
