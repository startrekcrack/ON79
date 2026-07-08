using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SongConverters
{
    /// <summary>
    /// OCR-based DOCX converter for documents where the relevant content is embedded as images
    /// (e.g. scanned sheets exported into DOCX).
    /// 
    /// Designed as a separate implementation to avoid changing the existing OpenXml converter.
    /// Output is best-effort: it tries to keep section labels and chord-only lines.
    /// </summary>
    public sealed class DocxToChoConverterOpenXmlImageOcr : IDocxToChoConverter
    {
        private static readonly Regex ChordLikeRe = new(
            @"^(?:[A-Ha-h](?:is|es|[#b])?)(?:m|maj|min|sus|add|dim|aug)?(?:2|4|5|6|7|9|11|13)?(?:/[A-Ha-h](?:is|es|[#b])?)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly IOcrEngine _ocr;

        /// <summary>
        /// Initializes a new OCR-based DOCX converter.
        /// </summary>
        /// <param name="ocrEngine">OCR engine for image extraction.</param>
        public DocxToChoConverterOpenXmlImageOcr(IOcrEngine ocrEngine)
        {
            _ocr = ocrEngine ?? throw new ArgumentNullException(nameof(ocrEngine));
        }

        /// <summary>
        /// Converts a DOCX file to ChordPro format using OCR.
        /// </summary>
        /// <param name="docxPath">Path to DOCX file.</param>
        /// <param name="options">Conversion options.</param>
        /// <returns>ChordPro formatted text.</returns>
        public string Convert(string docxPath, ConverterOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(docxPath) || !File.Exists(docxPath))
                throw new FileNotFoundException("DOCX file not found.", docxPath);

            var opts = options ?? new ConverterOptions();

            using var doc = WordprocessingDocument.Open(docxPath, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body == null) return string.Empty;

            var imageIdSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Modern drawing-based images.
            foreach (var id in body.Descendants<DocumentFormat.OpenXml.Drawing.Blip>()
                         .Select(b => b.Embed?.Value)
                         .Where(id => !id.IsBlankOrWhitespace()))
            {
                imageIdSet.Add(id);
            }

            // Legacy VML images (common in exported/converted DOCX): <v:imagedata r:id="..."/>
            foreach (var id in body.Descendants<DocumentFormat.OpenXml.Vml.ImageData>()
                         .Select(d => d.RelationshipId?.Value)
                         .Where(id => !id.IsBlankOrWhitespace()))
            {
                imageIdSet.Add(id);
            }

            var imageIds = imageIdSet.ToList();

            if (imageIds.Count == 0) return string.Empty;

            var ocrText = new StringBuilder();
            foreach (var relId in imageIds)
            {
                if (doc.MainDocumentPart == null) continue;
                if (doc.MainDocumentPart.GetPartById(relId) is not ImagePart imgPart) continue;

                using var stream = imgPart.GetStream(FileMode.Open, FileAccess.Read);
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                ms.Position = 0;

                var text = _ocr.ReadText(ms) ?? string.Empty;
                if (!text.IsBlankOrWhitespace())
                {
                    ocrText.AppendLine(text.TrimEnd());
                    ocrText.AppendLine();
                }
            }

            var lines = ocrText.ToString()
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n')
                .Select(l => l.TrimEnd())
                .ToList();

            // Remove excessive empties.
            lines = CollapseBlankLines(lines);

            var fallbackTitle = System.IO.Path.GetFileNameWithoutExtension(docxPath);
            var title = fallbackTitle.IsBlankOrWhitespace() ? "Unbekannter Titel" : fallbackTitle;

            var copyright = ExtractCopyright(lines);

            var headerSkip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddHeaderSkip(headerSkip, title);
            AddHeaderSkip(headerSkip, fallbackTitle);
            AddHeaderSkip(headerSkip, copyright);

            var output = new List<string>
            {
                $"{{title: {title}}}",
                !copyright.IsBlankOrWhitespace() ? $"{{copyright: {copyright}}}" : null,
                "{comment: Quelle: Word-Bild (OCR)}",
                "{comment: OCR-best-effort: Akkorde/Sections werden heuristisch erkannt}",
                string.Empty
            }.Where(l => !l.IsBlankOrWhitespace()).ToList();

            string pendingChordLine = null;
            var started = false;
            string activeChordLine = null;

            foreach (var line in lines)
            {
                var text = FixText(line);
                if (text.Length == 0)
                {
                    pendingChordLine = null;
                    activeChordLine = null;
                    EnsureBlank(output);
                    continue;
                }

                if (headerSkip.Contains(NormalizeForHeaderCompare(text)))
                {
                    continue;
                }

                if (LooksLikeStaffNoise(text))
                {
                    // Ignore music-notation OCR noise lines.
                    continue;
                }

                if (LooksLikeMostlyShortTokens(text))
                {
                    continue;
                }

                if (text.Contains('©') || Regex.IsMatch(text, @"^(rechte|copyright)\b", RegexOptions.IgnoreCase))
                {
                    // Already captured as header metadata.
                    continue;
                }

                // Skip preamble garbage until we hit a real lyric/section start.
                if (!started)
                {
                    if (TryParseSectionLabel(text, out var preSection))
                    {
                        started = true;
                        pendingChordLine = null;
                        EnsureBlank(output);
                        output.Add($"{{comment: {preSection}}}");
                        continue;
                    }

                    if (!LooksLikeLyricStart(text))
                    {
                        continue;
                    }

                    started = true;
                }

                if (TryParseSectionLabel(text, out var sectionLabel))
                {
                    pendingChordLine = null;
                    activeChordLine = null;
                    EnsureBlank(output);
                    output.Add($"{{comment: {sectionLabel}}}");
                    continue;
                }

                if (TryGetChordOnlyTokens(text, out var chords) && chords.Count >= 2)
                {
                    // Keep as pending chord line and try to combine with the next lyric line.
                    pendingChordLine = string.Join("\t", chords);
                    activeChordLine = pendingChordLine;
                    continue;
                }

                if (IsProbablyGarbageBodyLine(text))
                {
                    continue;
                }

                if (!pendingChordLine.IsBlankOrWhitespace())
                {
                    if (LooksLikeLyricLine(text))
                    {
                        var composed = OcrChoComposer.InsertChordsPreservingVersePrefix(pendingChordLine, text, opts.TabWidth);
                        output.Add(composed);
                        pendingChordLine = null;
                        continue;
                    }

                    // Don't consume the chord line for non-lyric junk.
                    continue;
                }

                // If we have an active chord line (from earlier) and this is another numbered verse line,
                // reuse the same chord line (common in scans: one chord line + shared staff for multiple verses).
                if (!activeChordLine.IsBlankOrWhitespace() && IsNumberedVerseLine(text) && LooksLikeLyricLine(text))
                {
                    output.Add(OcrChoComposer.InsertChordsPreservingVersePrefix(activeChordLine, text, opts.TabWidth));
                    continue;
                }

                output.Add(text);
            }

            // If we end with a chord line and no lyrics, render it as chord-only.
            if (!pendingChordLine.IsBlankOrWhitespace())
            {
                var chordTokens = pendingChordLine.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                output.Add(string.Join(" ", chordTokens.Select(c => $"[{c}]")));
            }

            return string.Join("\n", output).TrimEnd() + "\n";
        }

        /// <summary>
        /// Returns <c>true</c> if <paramref name="text"/> starts with a digit and a separator
        /// character (period or closing parenthesis), indicating a numbered verse label.
        /// </summary>
        /// <param name="text">Line text to test.</param>
        /// <returns><c>true</c> for a numbered verse line.</returns>
        private static bool IsNumberedVerseLine(string text)
        {
            if (text.IsBlankOrWhitespace()) return false;
            return Regex.IsMatch(text.TrimStart(), @"^\d+\s*[.)]\s+");
        }

        /// <summary>
        /// Collapses consecutive blank lines in <paramref name="lines"/> into a single blank line.
        /// </summary>
        /// <param name="lines">Input lines.</param>
        /// <returns>Lines with multiple consecutive blanks reduced to one.</returns>
        private static List<string> CollapseBlankLines(List<string> lines)
        {
            var result = new List<string>();
            bool lastBlank = false;
            foreach (var l in lines ?? new List<string>())
            {
                var blank = l.IsBlankOrWhitespace();
                if (blank)
                {
                    if (!lastBlank) result.Add(string.Empty);
                    lastBlank = true;
                    continue;
                }
                lastBlank = false;
                result.Add(l);
            }
            return result;
        }

        /// <summary>
        /// Finds and returns the first suitable title line from the list of OCR lines,
        /// skipping chord-only and section-label lines and truncating to 120 characters.
        /// </summary>
        /// <param name="lines">OCR lines to search.</param>
        /// <returns>Extracted title string, or <c>null</c> if none found.</returns>
        private static string ExtractTitle(List<string> lines)
        {
            foreach (var l in lines ?? Enumerable.Empty<string>())
            {
                var t = (l ?? string.Empty).Trim();
                if (t.Length == 0) continue;
                if (TryGetChordOnlyTokens(t, out _)) continue;
                if (TryParseSectionLabel(t, out _)) continue;
                // Keep it short.
                if (t.Length > 120) t = t.Substring(0, 120);
                return t;
            }
            return null;
        }

        /// <summary>
        /// Scans the line list for a copyright or rights attribution line. Prefers explicit
        /// "Rechte:" / "Copyright:" labels; falls back to lines containing the © symbol when
        /// they pass basic sanity checks.
        /// </summary>
        /// <param name="lines">OCR lines to search.</param>
        /// <returns>Cleaned copyright string, or <c>null</c> if none found.</returns>
        private static string ExtractCopyright(List<string> lines)
        {
            string best = null;

            foreach (var l in lines ?? Enumerable.Empty<string>())
            {
                var t = FixText(l);
                if (t.IsBlankOrWhitespace()) continue;

                // Highest priority: explicit labels.
                if (Regex.IsMatch(t, @"^(rechte|copyright)\s*:?\s*", RegexOptions.IgnoreCase))
                {
                    var cleaned = CleanupHeaderLine(t);
                    if (!cleaned.IsBlankOrWhitespace()) return cleaned;
                }

                // Lower priority: © lines, but only if they look sane.
                if (t.Contains('©'))
                {
                    if (LooksLikeStaffNoise(t) || LooksLikeMostlyShortTokens(t)) continue;

                    var letters = t.Count(char.IsLetter);
                    var symbols = t.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c));
                    if (letters < 8) continue;
                    if (symbols > 12) continue;

                    var cleaned = CleanupHeaderLine(t);
                    if (!cleaned.IsBlankOrWhitespace()) best ??= cleaned;
                }
            }

            return best;
        }

        /// <summary>
        /// Attempts to parse a line as a chord-only token sequence. Every token must match
        /// the chord regex after normalisation. Rejects lines that look like staff-note noise
        /// (many single-letter tokens).
        /// </summary>
        /// <param name="line">Line to test.</param>
        /// <param name="tokens">Receives the normalised chord tokens if successful.</param>
        /// <returns><c>true</c> if the line is a valid chord-only line.</returns>
        private static bool TryGetChordOnlyTokens(string line, out List<string> tokens)
        {
            tokens = null;
            var rawTokens = Regex.Split(line.Trim(), @"\s+|\t+")
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .ToList();

            if (rawTokens.Count == 0) return false;

            var normalized = new List<string>(rawTokens.Count);
            foreach (var raw in rawTokens)
            {
                var t = NormalizeChordToken(raw);
                if (t == null || !ChordLikeRe.IsMatch(t)) return false;
                normalized.Add(t);
            }

            tokens = normalized;
            // Reject lines that are likely staff notes: many single-letter tokens.
            var singleLetter = normalized.Count(t => t.Length == 1);
            if (normalized.Count >= 6 && singleLetter >= (normalized.Count * 0.85))
                return false;
            return true;
        }

        /// <summary>
        /// Normalises an OCR text fragment by replacing dashes, pipes, brackets, and common
        /// noise characters, collapsing whitespace, and stripping leading garbage tokens.
        /// </summary>
        /// <param name="text">Raw OCR text.</param>
        /// <returns>Cleaned text string.</returns>
        private static string FixText(string text)
        {
            if (text.IsBlankOrWhitespace()) return string.Empty;
            var t = text.Replace('–', '-').Replace('—', '-');
            t = t.Replace("_", "");
            t = t.Replace("|", " ");
            t = t.Replace("[", " ").Replace("]", " ");

            t = Regex.Replace(t, @"[§®¤•◦·…ﬂ“”’‘]", " ");

            // Remove '~' and '^' which are often staff-line OCR artifacts.
            t = t.Replace("~", " ").Replace("^", "");

            t = Regex.Replace(t, @"(?<=\p{L})\s*-\s*(?=\p{L})", "");

            // Remove stray OCR digits/letters that appear as isolated noise.
            t = Regex.Replace(t, @"\s+[0OoIl]\s+", " ");

            t = Regex.Replace(t, @"\s{2,}", " ");
            t = Regex.Replace(t, @"\s+([,.;:!?])", "$1");
            t = StripLeadingGarbageTokens(t);
            return t.Trim();
        }

        /// <summary>
        /// Removes leading single-character or slash/backslash tokens from a text string
        /// when the string is long enough that these tokens are likely OCR noise.
        /// Only strips when at least three successive garbage tokens are removed.
        /// </summary>
        /// <param name="text">Text to process.</param>
        /// <returns>Text with leading garbage tokens removed, or the original if below the threshold.</returns>
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

            return removed >= 3 ? string.Join(" ", tokens).Trim() : text.Trim();
        }

        /// <summary>
        /// Returns <c>true</c> if the text looks like the beginning of a lyric block: either a
        /// numbered line with enough letters, or a non-digit line that passes the lyric-line heuristic
        /// with at least 20 letters.
        /// </summary>
        /// <param name="text">Text to evaluate.</param>
        /// <returns><c>true</c> if the line is a plausible lyric start.</returns>
        private static bool LooksLikeLyricStart(string text)
        {
            if (text.IsBlankOrWhitespace()) return false;
            var t = text.Trim();

            if (Regex.IsMatch(t, @"^\d+\s*[.)]"))
            {
                return t.Count(char.IsLetter) >= 6;
            }

            if (t.Any(char.IsDigit)) return false;

            return LooksLikeLyricLine(t) && t.Count(char.IsLetter) >= 20;
        }

        /// <summary>
        /// Heuristically determines whether a line contains lyric text rather than metadata or noise,
        /// based on the total letter count and hyphenated-word patterns.
        /// </summary>
        /// <param name="text">Text to evaluate.</param>
        /// <returns><c>true</c> if the line looks like a lyric line.</returns>
        private static bool LooksLikeLyricLine(string text)
        {
            if (text.IsBlankOrWhitespace()) return false;
            var t = Regex.Replace(text, @"^\s*\d+\s*[.)]\s*", "");
            var letters = t.Count(char.IsLetter);
            if (letters >= 10) return true;
            if (Regex.IsMatch(t, @"\p{L}{2,}-\p{L}{2,}") && letters >= 10) return true;
            return false;
        }

        /// <summary>
        /// Returns <c>true</c> when a body line should be discarded as likely OCR noise or
        /// staff-line artefacts (blank, staff noise, mostly short tokens, few letters, or short line).
        /// </summary>
        /// <param name="text">Body line to test.</param>
        /// <returns><c>true</c> if the line is probably garbage.</returns>
        private static bool IsProbablyGarbageBodyLine(string text)
        {
            if (text.IsBlankOrWhitespace()) return true;
            if (LooksLikeStaffNoise(text) || LooksLikeMostlyShortTokens(text)) return true;

            var letters = text.Count(char.IsLetter);
            var digits = text.Count(char.IsDigit);
            var symbols = text.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && c != '-' && c != '/');

            if (!LooksLikeLyricLine(text))
            {
                if (letters < 10) return true;
                if (text.Length < 30) return true;
            }

            if (symbols >= 8 && letters <= 10) return true;
            if (digits >= 8 && letters <= 10) return true;

            return false;
        }

        /// <summary>
        /// Returns <c>true</c> when the line consists mostly (≥ 80 %) of tokens of at most two
        /// characters with no long (≥ 4 char) tokens and few total letters, indicating OCR noise.
        /// </summary>
        /// <param name="text">Text to test.</param>
        /// <returns><c>true</c> if the line is probably noise made of short tokens.</returns>
        private static bool LooksLikeMostlyShortTokens(string text)
        {
            if (text.IsBlankOrWhitespace()) return false;
            var tokens = Regex.Split(text.Trim(), @"\s+").Where(t => t.Length > 0).ToList();
            if (tokens.Count < 6) return false;

            var shortTokens = tokens.Count(t => t.Length <= 2);
            var longTokens = tokens.Count(t => t.Length >= 4);
            var letters = text.Count(char.IsLetter);
            if (shortTokens >= tokens.Count * 0.80 && longTokens == 0 && letters <= 12) return true;
            return false;
        }

        /// <summary>
        /// Normalises <paramref name="text"/> for header deduplication and adds it to <paramref name="set"/>
        /// so that the same content in different formats is not emitted twice.
        /// </summary>
        /// <param name="set">Set of previously seen normalised header texts.</param>
        /// <param name="text">Header text to register.</param>
        private static void AddHeaderSkip(HashSet<string> set, string text)
        {
            if (set == null) return;
            var norm = NormalizeForHeaderCompare(text);
            if (!norm.IsBlankOrWhitespace()) set.Add(norm);
        }

        /// <summary>
        /// Lowercases, fixes OCR artefacts, and strips all non-alphanumeric characters from
        /// <paramref name="text"/> to produce a canonical form used for deduplication comparisons.
        /// </summary>
        /// <param name="text">Text to normalise.</param>
        /// <returns>Normalised string suitable for equality comparison.</returns>
        private static string NormalizeForHeaderCompare(string text)
        {
            if (text.IsBlankOrWhitespace()) return string.Empty;
            var t = FixText(text).ToLowerInvariant();
            t = Regex.Replace(t, @"[^a-z0-9]+", string.Empty);
            return t;
        }

        /// <summary>
        /// Cleans a raw OCR header line by fixing artefacts, trimming everything after a pipe
        /// character, and removing trailing separator patterns.
        /// </summary>
        /// <param name="text">Raw header line.</param>
        /// <returns>Cleaned header text, or <c>null</c> if the result is blank.</returns>
        private static string CleanupHeaderLine(string text)
        {
            if (text.IsBlankOrWhitespace()) return null;
            var t = FixText(text);

            var pipe = t.IndexOf('|');
            if (pipe >= 0) t = t.Substring(0, pipe).Trim();

            t = Regex.Replace(t, @"\s*[=~_\-]{3,}.*$", string.Empty).Trim();
            return t;
        }

        /// <summary>
        /// Returns <c>true</c> when a text line appears to originate from a musical staff rather
        /// than from text content. Detected patterns include separator lines, high symbol ratios,
        /// repeated equals or underscore runs, and many single-character tokens (note names).
        /// </summary>
        /// <param name="text">Text to inspect.</param>
        /// <returns><c>true</c> if the line looks like staff notation noise.</returns>
        private static bool LooksLikeStaffNoise(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;

            if (text.Contains("===") || text.Contains("___") || text.Contains("%%") || text.Contains("||"))
                return true;

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

            // Lots of one-char tokens is a strong indicator for note OCR.
            var tokens = Regex.Split(text, @"\s+").Where(t => t.Length > 0).ToList();
            var oneChar = tokens.Count(t => t.Length == 1);
            if (tokens.Count >= 8 && oneChar >= (tokens.Count * 0.75) && letters <= 8) return true;

            return false;
        }

        /// <summary>
        /// Normalises a raw OCR chord token by stripping unexpected characters, correcting
        /// common mis-reads (e.g. "Cim" → "Cm", "Bmaij7" → "Bmaj7"), and ensuring title-case
        /// for the root letter.
        /// </summary>
        /// <param name="token">Raw token string.</param>
        /// <returns>Normalised chord token, or <c>null</c> if the token is effectively empty.</returns>
        private static string NormalizeChordToken(string token)
        {
            if (token.IsBlankOrWhitespace()) return null;
            var cleaned = Regex.Replace(token.Trim(), @"[^A-Za-z0-9#b/]+", "");
            if (cleaned.Length == 0) return null;

            // Common OCR artifacts.
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])im$", "$1m", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])maij7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])mqj7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])l(?=[A-Za-z])", "$1", RegexOptions.IgnoreCase);

            if (cleaned.Length >= 2)
            {
                var root = cleaned[0];
                var second = cleaned[1];
                if ((root is >= 'A' and <= 'H' or >= 'a' and <= 'h') && (second == 'h' || second == 'H'))
                {
                    cleaned = cleaned[0] + cleaned.Substring(2);
                }
            }

            // Root casing.
            cleaned = char.ToUpperInvariant(cleaned[0]) + (cleaned.Length > 1 ? cleaned.Substring(1) : string.Empty);

            return cleaned;
        }

        /// <summary>
        /// Tries to parse a section label from the start of <paramref name="text"/>.
        /// Recognises "Strophe", "Vers", "Verse", "Refrain", "Chorus", "Bridge", "Intro", "Outro",
        /// "Zwischenspiel", "Interlude" and their common abbreviations.
        /// </summary>
        /// <param name="text">Text to parse.</param>
        /// <param name="section">Receives the canonical section name if matched.</param>
        /// <returns><c>true</c> if a known section label was found.</returns>
        private static bool TryParseSectionLabel(string text, out string section)
        {
            section = null;
            var raw = (text ?? string.Empty).Trim();
            if (raw.Length == 0) return false;

            var m = Regex.Match(raw, @"^(strophe|vers|verse)\s*(\d+)?\b", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                section = "Vers" + (m.Groups[2].Success ? " " + m.Groups[2].Value : "");
                return true;
            }
            if (Regex.IsMatch(raw, @"^(refr\.?|refrain|chorus)\b", RegexOptions.IgnoreCase))
            {
                section = "Chorus";
                return true;
            }
            if (Regex.IsMatch(raw, @"^bridge\b", RegexOptions.IgnoreCase))
            {
                section = "Bridge";
                return true;
            }
            if (Regex.IsMatch(raw, @"^intro\b", RegexOptions.IgnoreCase))
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
        /// Appends an empty line to <paramref name="output"/> if the last line is not already blank,
        /// ensuring a trailing blank separator after the current block.
        /// </summary>
        /// <param name="output">Line list to update in place.</param>
        private static void EnsureBlank(List<string> output)
        {
            if (output.Count == 0) return;
            if (!output[^1].IsBlankOrWhitespace()) output.Add(string.Empty);
        }
    }
}
