using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SongConverters
{
    /// <summary>
    /// Layout OCR-based DOCX converter for documents where the relevant content is embedded as images.
    /// Uses two OCR passes:
    /// - chord pass: sparse + whitelist
    /// - lyric pass: normal
    /// Then pairs chord lines above lyric lines and emits CHO.
    /// </summary>
    public sealed class DocxToChoConverterOpenXmlImageOcrDualLayout : IDocxToChoConverter
    {
        /// <summary>
        /// Chord token regex (strict, for classifying tokens as chords in noisy OCR output).
        /// </summary>
        private static readonly Regex ChordTokenRe = new(
            @"^(?:[A-Ha-h](?:is|es|[#b])?)(?:m|maj|min|sus|add|dim|aug)?(?:2|4|5|6|7|9|11|13)?(?:/[A-Ha-h](?:is|es|[#b])?)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Options for the converter. TabWidth controls how many spaces are used for tab stops when aligning chords above lyrics.
        /// </summary>
        /// <param name="TabWidth"></param>
        /// <param name="MaxChordToLyricGapPx"></param>
        public sealed record Options(
            int TabWidth = 12,
            int MaxChordToLyricGapPx = 180
        );

        private readonly IOcrLayoutEngine _chordOcr;
        private readonly IOcrLayoutEngine _lyricOcr;

        /// <summary>
        /// Dual-layout OCR converter constructor. Requires two separate OCR engines for chords and lyrics to allow for different configurations (e.g. chord OCR can be more aggressive with a whitelist, lyric OCR can be more lenient).
        /// </summary>
        /// <param name="chordOcr"></param>
        /// <param name="lyricOcr"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public DocxToChoConverterOpenXmlImageOcrDualLayout(IOcrLayoutEngine chordOcr, IOcrLayoutEngine lyricOcr)
        {
            _chordOcr = chordOcr ?? throw new ArgumentNullException(nameof(chordOcr));
            _lyricOcr = lyricOcr ?? throw new ArgumentNullException(nameof(lyricOcr));
        }

        /// <summary>
        /// Convert synchronously using Xceed implementation with interface-compatible signature.
        /// </summary>
        /// <param name="docxPath"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        public string Convert(string docxPath, ConverterOptions options = null)
        {
            return Convert(docxPath, new Options(TabWidth: options?.TabWidth ?? 12));
        }

        /// <summary>
        /// Convert asynchronously using Xceed implementation with interface-compatible signature.
        /// </summary>
        /// <param name="docxPath"></param>
        /// <param name="options"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<string> ConvertAsync(string docxPath, ConverterOptions options = null, CancellationToken cancellationToken = default)
        {
            return await Task.Run(() => Convert(docxPath, options), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Convert the DOCX file at the given path to CHO format using dual-layout OCR on embedded images. 
        /// Extracts text from images, classifies lines as chords or lyrics, pairs them based on vertical proximity, and emits CHO with chords aligned above lyrics. 
        /// Also attempts to extract title and copyright information from the text. Skips images that look like sheet music based on a heuristic detector. 
        /// Uses the provided options for tab width and maximum chord-to-lyric gap when pairing lines.
        /// </summary>
        /// <param name="docxPath"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        /// <exception cref="FileNotFoundException"></exception>
        public string Convert(string docxPath, Options options)
        {
            if (string.IsNullOrWhiteSpace(docxPath) || !File.Exists(docxPath))
                throw new FileNotFoundException("DOCX file not found.", docxPath);

            options ??= new Options();

            using var doc = WordprocessingDocument.Open(docxPath, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body == null) return string.Empty;

            var imageIdSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var id in body.Descendants<DocumentFormat.OpenXml.Drawing.Blip>()
                         .Select(b => b.Embed?.Value)
                         .Where(id => !id.IsBlankOrWhitespace()))
            {
                imageIdSet.Add(id);
            }

            foreach (var id in body.Descendants<DocumentFormat.OpenXml.Vml.ImageData>()
                         .Select(d => d.RelationshipId?.Value)
                         .Where(id => !id.IsBlankOrWhitespace()))
            {
                imageIdSet.Add(id);
            }

            var imageIds = imageIdSet.ToList();
            if (imageIds.Count == 0) return string.Empty;

            var lyricLines = new List<OcrLine>();
            var chordLines = new List<OcrLine>();

            foreach (var relId in imageIds)
            {
                if (doc.MainDocumentPart == null) continue;
                if (doc.MainDocumentPart.GetPartById(relId) is not ImagePart imgPart) continue;

                using var stream = imgPart.GetStream(FileMode.Open, FileAccess.Read);
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var bytes = ms.ToArray();

                // Reject sheet music images completely (user wants these skipped).
                if (SheetMusicDetector.LooksLikeSheetMusic(bytes))
                {
                    // Insert a separator so sections don't accidentally merge.
                    lyricLines.Add(new OcrLine(0, RectangleF.Empty, Array.Empty<OcrWord>()));
                    continue;
                }

                using var chordStream = new MemoryStream(bytes);
                using var lyricStream = new MemoryStream(bytes);

                var chordPage = _chordOcr.ReadLayout(chordStream);
                var lyricPage = _lyricOcr.ReadLayout(lyricStream);

                chordLines.AddRange((chordPage?.Lines ?? Array.Empty<OcrLine>()).Where(l => l.Words?.Count > 0));
                lyricLines.AddRange((lyricPage?.Lines ?? Array.Empty<OcrLine>()).Where(l => l.Words?.Count > 0));

                // image/page separator
                lyricLines.Add(new OcrLine(0, RectangleF.Empty, Array.Empty<OcrWord>()));
            }

            var lyricTexts = lyricLines
                .Where(l => !IsNoteNoiseLine(l))
                .Select(l => FixText(string.Join(" ", (l.Words ?? Array.Empty<OcrWord>()).Select(w => w.Text)).Trim()))
                .Where(t => !t.IsBlankOrWhitespace())
                .ToList();

            var fallbackTitle = Path.GetFileNameWithoutExtension(docxPath);
            var title = fallbackTitle.IsBlankOrWhitespace() ? "Unbekannter Titel" : fallbackTitle;
            var copyright = ExtractCopyright(lyricTexts);

            var headerSkip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddHeaderSkip(headerSkip, title);
            AddHeaderSkip(headerSkip, fallbackTitle);
            AddHeaderSkip(headerSkip, copyright);

            var output = new List<string>
            {
                $"{{title: {title}}}",
            };
            if (!copyright.IsBlankOrWhitespace()) output.Add($"{{comment: {copyright}}}");
            output.Add("{comment: Quelle: Word-Bild (Layout-OCR)}");
            output.Add(string.Empty);

            var chordLineItems = chordLines
                .Where(l => l?.Words != null && l.Words.Count > 0)
                .Where(l => !IsChordNoiseLine(l))
                .Select(l => (line: l, chordLine: NormalizeChordLineFromOcrLine(l)))
                .Where(t => !t.chordLine.IsBlankOrWhitespace())
                .ToList();

            string pendingChordLine = null;
            (OcrLine line, string chordLine)? activeChordAbove = null;
            var started = false;

            for (int i = 0; i < lyricLines.Count; i++)
            {
                var l = lyricLines[i];
                var raw = string.Join(" ", (l.Words ?? Array.Empty<OcrWord>()).Select(w => w.Text)).Trim();

                if (raw.IsBlankOrWhitespace())
                {
                    EnsureBlank(output);
                    pendingChordLine = null;
                    activeChordAbove = null;
                    continue;
                }

                var text = FixText(raw);

                if (headerSkip.Contains(NormalizeForHeaderCompare(text)))
                    continue;

                if (IsNoteNoiseLine(l) || LooksLikeStaffNoise(text) || LooksLikeMostlyShortTokens(text))
                    continue;

                if (text.Contains('©') || Regex.IsMatch(text, @"^(rechte|copyright)\b", RegexOptions.IgnoreCase))
                    continue;

                if (!started)
                {
                    if (TryParseSection(text, out var preSection))
                    {
                        started = true;
                        EnsureBlank(output);
                        output.Add($"{{comment: {preSection}}}");
                        continue;
                    }

                    if (!LooksLikeLyricStart(text))
                        continue;

                    started = true;
                }

                if (TryParseSection(text, out var section))
                {
                    EnsureBlank(output);
                    output.Add($"{{comment: {section}}}");
                    pendingChordLine = null;
                    activeChordAbove = null;
                    continue;
                }

                if (IsChordOnlyTextLine(text))
                {
                    pendingChordLine = NormalizeChordLine(text);
                    activeChordAbove = null;
                    continue;
                }

                if (IsProbablyGarbageBodyLine(text))
                    continue;

                var chordAboveItem = FindChordLineAboveItem(chordLineItems, l, options.MaxChordToLyricGapPx);
                if (chordAboveItem.HasValue)
                {
                    activeChordAbove = chordAboveItem;
                    output.Add(OcrChoComposer.InsertChords(chordAboveItem.Value.chordLine, text, options.TabWidth));
                    pendingChordLine = null;
                    continue;
                }

                if (!pendingChordLine.IsBlankOrWhitespace())
                {
                    if (LooksLikeLyricLine(text))
                    {
                        output.Add(OcrChoComposer.InsertChords(pendingChordLine, text, options.TabWidth));
                        pendingChordLine = null;
                        continue;
                    }

                    continue;
                }

                if (activeChordAbove.HasValue && IsNumberedVerseLine(text) && l?.Box != RectangleF.Empty && activeChordAbove.Value.line?.Box != RectangleF.Empty)
                {
                    var dist = l.Box.Top - activeChordAbove.Value.line.Box.Bottom;
                    if (dist > 0 && dist <= (options.MaxChordToLyricGapPx * 2))
                    {
                        output.Add(OcrChoComposer.InsertChords(activeChordAbove.Value.chordLine, text, options.TabWidth));
                        continue;
                    }
                }

                output.Add(text);
            }

            return string.Join("\n", output).TrimEnd() + "\n";
        }

        /// <summary>
        /// IsNumberedVerseLine heuristic: detects lines that likely start with verse numbers (e.g. "1.", "2)", "I.", "l)") which often indicate the start of a new verse section.
        /// </summary>
        /// <param name="text">The text line to evaluate.</param>
        /// <returns>True if the line likely starts with a verse number; otherwise, false.</returns>
        private static bool IsNumberedVerseLine(string text)
        {
            if (text.IsBlankOrWhitespace()) return false;
            return Regex.IsMatch(text.TrimStart(), @"^(?:\d+|[lI])\s*[.)]\s+", RegexOptions.CultureInvariant);
        }

        /// <summary>
        /// Extracts the most relevant copyright information from a collection of text lines.   
        /// </summary>
        /// <remarks>The method scans each line for common copyright indicators, such as 'copyright',
        /// 'rechte', or the © symbol. It applies filters to exclude lines that are likely irrelevant or noisy, and
        /// returns the first valid copyright line that meets the criteria.</remarks>
        /// <param name="lines">A list of strings representing lines of text to search for copyright information. Cannot be null.</param>
        /// <returns>A string containing the extracted copyright line if found; otherwise, null.</returns>
        private static string ExtractCopyright(List<string> lines)
        {
            string best = null;
            foreach (var l in lines ?? new List<string>())
            {
                var t = FixText(l);
                if (t.IsBlankOrWhitespace()) continue;

                if (Regex.IsMatch(t, @"^(rechte|copyright)\s*:?\s*", RegexOptions.IgnoreCase))
                    return CleanupHeaderLine(t);

                if (Regex.IsMatch(t, @"^\s*©\b"))
                {
                    if (LooksLikeStaffNoise(t) || LooksLikeMostlyShortTokens(t)) continue;
                    var letters = t.Count(char.IsLetter);
                    var symbols = t.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c));
                    if (letters < 8) continue;
                    if (symbols > 12) continue;
                    best ??= CleanupHeaderLine(t);
                }
            }
            return best;
        }

        /// <summary>
        /// Adds a normalized version of the given text to the header skip set if it's not blank. This helps to avoid misclassifying title/copyright lines as lyric lines later on.
        /// </summary>
        /// <param name="set"></param>
        /// <param name="text"></param>
        private static void AddHeaderSkip(HashSet<string> set, string text)
        {
            if (set == null) return;
            var norm = NormalizeForHeaderCompare(text);
            if (!norm.IsBlankOrWhitespace()) set.Add(norm);
        }

        /// <summary>
        /// Normalizes the specified text for header comparison by removing non-alphanumeric characters and converting
        /// it to lowercase.    
        /// </summary>
        /// <remarks>This method removes all non-alphanumeric characters from the input text, ensuring
        /// that only lowercase letters and digits are retained.</remarks>
        /// <param name="text">The text to normalize for header comparison. Must not be null or whitespace.</param>
        /// <returns>A normalized string suitable for header comparison, which is empty if the input text is blank or whitespace.</returns>
        private static string NormalizeForHeaderCompare(string text)
        {
            if (text.IsBlankOrWhitespace()) return string.Empty;
            var t = FixText(text).ToLowerInvariant();
            t = Regex.Replace(t, @"[^a-z0-9]+", string.Empty);
            return t;
        }

        /// <summary>
        /// CleanupHeaderLine heuristic: applies additional cleaning to lines that are likely to be title or copyright headers, such as removing trailing pipe characters, dashes, 
        /// underscores, equal signs, and similar artifacts that often appear in noisy OCR output. This helps to produce cleaner metadata for the CHO output.
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
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
        /// FixText heuristic: applies a series of transformations to clean up OCR text lines, such as replacing common misread characters, 
        /// removing extraneous symbols, collapsing multiple spaces, and stripping leading garbage tokens. This helps 
        /// to improve the quality of the extracted text before further processing.
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        private static string FixText(string text)
        {
            if (text.IsBlankOrWhitespace()) return string.Empty;
            var t = text.Replace('–', '-').Replace('—', '-');
            t = t.Replace("_", "");
            t = t.Replace("|", " ");
            t = t.Replace("[", " ").Replace("]", " ");
            t = Regex.Replace(t, @"[§®¤•◦·…ﬂ“”’‘]", " ");
            t = Regex.Replace(t, @"(?<=\p{L})\s*-\s*(?=\p{L})", "");
            t = Regex.Replace(t, @"\s{2,}", " ");
            t = Regex.Replace(t, @"\s+([,.;:!?])", "$1");
            t = StripLeadingGarbageTokens(t);
            return t.Trim();
        }

        /// <summary>
        /// StripLeadingGarbageTokens heuristic: removes up to the first 10 leading tokens from the input text if they are likely to be garbage (e.g. single letters, slashes) and 
        /// if at least 3 tokens are removed. This helps to clean up OCR lines that start with noise before the actual lyric content begins.
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
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
        /// TryParseSection heuristic: detects lines that likely indicate the start of a new section (e.g. "Strophe 1", "Refrain", "Bridge") based on common keywords and patterns.
        /// </summary>
        /// <param name="text">The text to analyze for section headers. Cannot be null.</param>
        /// <param name="section">The detected section name if a section header is found; otherwise, null.</param>
        /// <returns>True if a section header is detected; otherwise, false.</returns>
        private static bool TryParseSection(string text, out string section)
        {
            section = null;
            var raw = (text ?? string.Empty).Trim();
            var m = Regex.Match(raw, @"^(strophe|vers|verse)\s*(\d+)?\b", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var num = m.Groups[2].Success ? NormalizeVerseNumber(m.Groups[2].Value) : null;
                section = "Vers" + (!num.IsBlankOrWhitespace() ? " " + num : "");
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
            return false;
        }

        /// <summary>
        /// Normalizes a verse number string by trimming whitespace and converting it to a valid positive integer
        /// representation. 
        /// </summary>
        /// <remarks>Valid verse numbers must be positive integers. If the input represents a verse number
        /// greater than 20, only the first digit is returned to ensure compatibility with downstream
        /// processing.</remarks>
        /// <param name="raw">The raw string input representing a verse number. Cannot be null, empty, or consist solely of whitespace.</param>
        /// <returns>A string containing the normalized verse number if valid; otherwise, null. If the verse number is greater
        /// than 20, only the first digit is returned.</returns>
        private static string NormalizeVerseNumber(string raw)
        {
            if (raw.IsBlankOrWhitespace()) return null;
            raw = raw.Trim();
            if (!int.TryParse(raw, out var n)) return null;
            if (n <= 0) return null;

            if (n > 20)
            {
                var firstDigit = raw.FirstOrDefault(char.IsDigit);
                if (firstDigit != default) return firstDigit.ToString();
            }

            return n.ToString();
        }

        /// <summary>
        /// LooksLikeLyricStart heuristic: detects lines that are likely to be the start of meaningful lyric content based on various heuristics,
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        private static bool LooksLikeLyricStart(string text)
        {
            if (text.IsBlankOrWhitespace()) return false;
            var t = text.Trim();

            if (Regex.IsMatch(t, @"^(?:\d+|[lI])\s*[.)]", RegexOptions.CultureInvariant))
                return t.Count(char.IsLetter) >= 6;

            if (t.Any(char.IsDigit)) return false;

            return LooksLikeLyricLine(t) && t.Count(char.IsLetter) >= 20;
        }

        /// <summary>
        /// LooksLikeLyricLine heuristic: detects lines that are likely to be meaningful lyric lines based on various heuristics, 
        /// such as having a sufficient number of letters, containing hyphenated words, and not looking like staff noise or mostly short tokens. 
        /// This helps to identify valid lyric lines in the OCR output and distinguish them from noise or irrelevant lines.
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
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
        /// IsProbablyGarbageBodyLine heuristic: detects lines that are likely to be garbage or noise rather than meaningful lyric lines based on various heuristics, 
        /// such as being blank, looking like staff noise, having too few letters, being too short, or having a high ratio 
        /// of symbols/digits to letters. This helps to filter out irrelevant lines from the OCR output before processing them as lyrics.
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
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
        /// IsChordOnlyTextLine heuristic: determines if a given text line is likely to consist solely of chord symbols based on token analysis and ratio of chord-like tokens to total tokens.
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        private static bool IsChordOnlyTextLine(string text)
        {
            if (text.IsBlankOrWhitespace()) return false;

            var tokens = Regex.Split(text.Trim(), @"\s+")
                .Select(CleanChord)
                .Where(t => !t.IsBlankOrWhitespace())
                .ToList();

            if (tokens.Count < 2) return false;
            var chordTokens = tokens.Where(IsChordTokenStrict).ToList();
            if (chordTokens.Count < 2) return false;

            var ratio = (double)chordTokens.Count / Math.Max(1, tokens.Count);
            if (ratio < 0.60) return false;

            return true;
        }

        /// <summary>
        /// Normalizes a chord line by extracting and filtering valid chord tokens from the input string.
        /// </summary>
        /// <remarks>This method uses regular expressions to split the input text into tokens and applies
        /// cleaning and validation to ensure only valid chord tokens are included in the output.</remarks>
        /// <param name="text">The input string containing chord tokens to be normalized. Leading and trailing whitespace is removed before
        /// processing.</param>
        /// <returns>A tab-separated string containing only valid chord tokens extracted from the input. Invalid or blank tokens
        /// are excluded.</returns>
        private static string NormalizeChordLine(string text)
        {
            var tokens = Regex.Split(text.Trim(), @"\s+")
                .Select(CleanChord)
                .Where(t => !t.IsBlankOrWhitespace() && IsChordTokenStrict(t))
                .ToList();
            return string.Join("\t", tokens);
        }

        /// <summary>
        /// Normalizes an OCR line that is classified as a chord line by extracting the text of each word, cleaning it, 
        /// filtering out non-chord tokens, and joining the remaining chord tokens with spaces.
        /// </summary>
        /// <param name="line"></param>
        /// <returns></returns>
        private static string NormalizeChordLineFromOcrLine(OcrLine line)
        {
            if (line?.Words == null || line.Words.Count == 0) return null;
            var tokens = line.Words
                .OrderBy(w => w.Box.Left)
                .Select(w => w.Text)
                .Where(t => !t.IsBlankOrWhitespace())
                .Select(CleanChord)
                .Where(t => !t.IsBlankOrWhitespace() && IsChordTokenStrict(t))
                .ToList();

            if (tokens.Count < 2) return null;
            return string.Join(" ", tokens);
        }

        /// <summary>
        /// Cleans a chord token by removing extraneous characters and normalizing common OCR misreads.
        /// </summary>
        /// <param name="token">The chord token to clean.</param>
        /// <returns>The cleaned chord token.</returns>
        private static string CleanChord(string token)
        {
            if (token.IsBlankOrWhitespace()) return string.Empty;
            var cleaned = Regex.Replace(token.Trim(), @"[^A-Za-z0-9#b/]+", "");
            if (cleaned.Length == 0) return string.Empty;

            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])im$", "$1m", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])maij7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])mqj7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])l(?=[A-Za-z])", "$1", RegexOptions.IgnoreCase);

            cleaned = char.ToUpperInvariant(cleaned[0]) + (cleaned.Length > 1 ? cleaned.Substring(1) : string.Empty);

            if (cleaned.Length >= 2)
            {
                var root = cleaned[0];
                var second = cleaned[1];
                if ((root is >= 'A' and <= 'H') && (second == 'h' || second == 'H'))
                    cleaned = cleaned[0] + cleaned.Substring(2);
            }

            return cleaned;
        }

        /// <summary>
        /// IsChordTokenStrict heuristic: determines if a given token is likely to be a valid chord symbol based on a strict regex pattern and additional checks.
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        private static bool IsChordTokenStrict(string token)
        {
            if (token.IsBlankOrWhitespace()) return false;
            if (!ChordTokenRe.IsMatch(token)) return false;

            if (token.Length == 1)
            {
                var c = token[0];
                return (c is >= 'A' and <= 'H') || (c is >= 'a' and <= 'h');
            }

            return true;
        }

        /// <summary>
        /// IsChordNoiseLine heuristic: detects OCR lines that are likely to be noise from sheet music notation rather than actual chord lines.
        /// </summary>
        /// <param name="line"></param>
        /// <returns></returns>
        private static bool IsChordNoiseLine(OcrLine line)
        {
            if (line?.Words == null || line.Words.Count == 0) return true;

            var tokens = line.Words.Select(w => CleanChord(w.Text)).Where(t => !t.IsBlankOrWhitespace()).ToList();
            var chordish = tokens.Where(IsChordTokenStrict).ToList();
            if (chordish.Count == 0) return true;

            if (chordish.Count >= 6 && chordish.All(t => t.Length == 1)) return true;

            var text = string.Join(" ", line.Words.Select(w => w.Text ?? string.Empty));
            if (LooksLikeStaffNoise(text) && chordish.Count < 2) return true;

            return false;
        }

        /// <summary>
        /// FindChordLineAboveItem: Given a list of chord line items (each with an OCR line and its normalized chord line text) and a lyric
        /// OCR line, this method searches for the closest chord line that is vertically above the lyric line within a specified maximum gap. 
        /// It checks that the chord line is reasonably aligned horizontally with the lyric 
        /// line and that it contains valid chord tokens. If such a chord line is found, it returns both the OCR line and the normalized chord line text; otherwise, it returns null.
        /// </summary>
        /// <param name="chordLineItems"></param>
        /// <param name="lyricLine"></param>
        /// <param name="maxGap"></param>
        /// <returns></returns>
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
                if (cl.Box.Bottom > top) continue;

                var dist = top - cl.Box.Bottom;
                if (dist < 0 || dist > maxGap) continue;

                if (cl.Box.Right < left || cl.Box.Left > right) continue;

                if (dist < best.dist)
                    best = (dist, cl, item.chordLine);
            }

            return best.line == null || best.chordLine.IsBlankOrWhitespace() ? null : (best.line, best.chordLine);
        }

        /// <summary>
        /// LooksLikeStaffNoise heuristic: detects lines that contain patterns commonly associated with sheet music staff lines 
        /// or similar OCR artifacts, such as long sequences of dashes, underscores, 
        /// equal signs, pipes, or a high ratio of non-alphanumeric symbols to letters. Such lines are unlikely to be meaningful lyric lines and can be skipped.
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        private static bool LooksLikeStaffNoise(string text)
        {
            if (text.IsBlankOrWhitespace()) return true;

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

            return false;
        }

        /// <summary>
        /// LooksLikeMostlyShortTokens heuristic: detects lines that consist mostly of short tokens 
        /// (1-2 characters) with very few letters, which is common in OCR noise from sheet 
        /// music staff lines or similar artifacts. Such lines are unlikely to be meaningful lyric lines and can be skipped.
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
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
        /// IsNoteNoiseLine heuristic: detects OCR lines that are likely to be noise from sheet music notation rather than actual chord lines.
        /// </summary>
        /// <param name="line"></param>
        /// <returns></returns>
        private static bool IsNoteNoiseLine(OcrLine line)
        {
            if (line?.Words == null || line.Words.Count == 0) return false;

            var text = string.Join(" ", line.Words.Select(w => w.Text ?? string.Empty)).Trim();
            if (text.Length == 0) return true;

            var letters = text.Count(char.IsLetter);
            var digits = text.Count(char.IsDigit);
            var noise = text.Count(c => c is '_' or '|' or '=' or '~' or '^' or '[' or ']' or '{' or '}' or '\\' or '/' or '*' or '“' or '”' or '’' or '‘');
            var ratioLetters = (double)letters / Math.Max(1, text.Length);

            if (letters <= 2 && noise >= 4) return true;
            if (ratioLetters < 0.20 && noise >= 6) return true;

            if (LooksLikeStaffNoise(text)) return true;

            var tokens = Regex.Split(text, @"\s+").Where(t => t.Length > 0).ToList();
            var singleChar = tokens.Count(t => t.Length == 1);
            if (tokens.Count >= 6 && singleChar >= (tokens.Count * 0.7) && letters <= 4) return true;

            var chordish = tokens.Count(t => IsChordTokenStrict(CleanChord(t)));
            if (chordish >= 2) return false;

            if (text.Length < 20 && (noise >= 3 || digits >= 4) && letters <= 3) return true;

            return false;
        }

        /// <summary>
        /// Ensures that the output list ends with a blank line. This is used to separate sections and prevent accidental merging 
        /// of lines when the next line is added. If the last line is not blank or whitespace, a new empty string is added to the list.
        /// </summary>
        /// <param name="output"></param>
        private static void EnsureBlank(List<string> output)
        {
            if (output.Count == 0) return;
            if (!output[^1].IsBlankOrWhitespace()) output.Add(string.Empty);
        }
    }
}
