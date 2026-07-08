using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Tesseract;

namespace SongConverters
{
    /// <summary>
    /// Tesseract OCR engine with configurable page segmentation and optional preprocessing.
    /// Includes a multi-PSM fallback that tends to work better for scanned chord sheets.
    /// </summary>
    public sealed class TesseractOcrEngineAdvanced : IOcrEngine, IDisposable
    {
        /// <summary>
        /// Advanced OCR options with fallback modes.
        /// </summary>
        /// <param name="Language">OCR language (e.g., "deu+eng").</param>
        /// <param name="EngineMode">Tesseract engine mode.</param>
        /// <param name="Preprocess">Enable image preprocessing.</param>
        /// <param name="PreprocessOptions">Preprocessing options.</param>
        /// <param name="PrimaryPageSegMode">Primary page segmentation mode.</param>
        /// <param name="FallbackPageSegModes">Fallback modes if primary fails.</param>
        public sealed record Options(
            string Language = "deu+eng",
            EngineMode EngineMode = EngineMode.Default,
            bool Preprocess = true,
            ImagePreprocessor.Options PreprocessOptions = null,
            PageSegMode PrimaryPageSegMode = PageSegMode.Auto,
            IReadOnlyList<PageSegMode> FallbackPageSegModes = null
        );

        private static readonly Regex ChordLikeRe = new(
            @"\b[A-Ha-h](?:is|es|[#b])?(?:m|maj|min|sus|add|dim|aug)?\d*(?:/[A-Ha-h](?:is|es|[#b])?)?\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex NoiseHeavyRe = new(@"[\|_—–…\[\]{}\\/\*]+", RegexOptions.Compiled);

        private readonly TesseractEngine _engine;
        private readonly Options _options;

        /// <summary>
        /// Initializes a new advanced Tesseract OCR engine.
        /// </summary>
        /// <param name="dataPath">Path to tessdata directory.</param>
        /// <param name="options">Advanced OCR options.</param>
        public TesseractOcrEngineAdvanced(string dataPath, Options options = null)
        {
            if (string.IsNullOrWhiteSpace(dataPath))
                throw new ArgumentNullException(nameof(dataPath));

            if (!Directory.Exists(dataPath))
                throw new DirectoryNotFoundException($"tessdata not found: {dataPath}");

            _options = options ?? new Options();
            _engine = new TesseractEngine(dataPath, _options.Language ?? "deu+eng", _options.EngineMode);

            // Helps keep chord alignment (spaces) intact.
            _engine.SetVariable("preserve_interword_spaces", "1");
        }

        /// <summary>
        /// Reads text from a PNG stream with fallback modes.
        /// </summary>
        /// <param name="pngStream">PNG image stream.</param>
        /// <returns>Recognized text.</returns>
        public string ReadText(Stream pngStream)
        {
            if (pngStream == null) throw new ArgumentNullException(nameof(pngStream));

            using var ms = new MemoryStream();
            pngStream.CopyTo(ms);
            var raw = ms.ToArray();

            if (_options.Preprocess)
            {
                var preOpt = _options.PreprocessOptions ?? new ImagePreprocessor.Options();
                try
                {
                    raw = ImagePreprocessor.PreprocessToPng(raw, preOpt);
                }
                catch
                {
                    // Preprocessing is best-effort; fall back to original bytes.
                }
            }

            var modes = BuildModesList();
            string bestText = string.Empty;
            double bestScore = double.NegativeInfinity;

            foreach (var mode in modes)
            {
                var text = RunTesseract(raw, mode);
                var score = Score(text);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestText = text;
                }
            }

            bestText ??= string.Empty;
            return PostProcess(bestText);
        }

        /// <summary>
        /// Builds the ordered list of page segmentation modes to try, starting with the
        /// primary mode and appending any fallback modes without duplicates.
        /// </summary>
        /// <returns>Ordered list of <see cref="PageSegMode"/> values.</returns>
        private IReadOnlyList<PageSegMode> BuildModesList()
        {
            var list = new List<PageSegMode>();
            list.Add(_options.PrimaryPageSegMode);

            var fallbacks = _options.FallbackPageSegModes;
            if (fallbacks == null)
            {
                fallbacks = new[]
                {
                    PageSegMode.SingleColumn,
                    PageSegMode.SingleBlock,
                    PageSegMode.SparseText,
                };
            }

            foreach (var f in fallbacks)
            {
                if (!list.Contains(f)) list.Add(f);
            }
            return list;
        }

        /// <summary>
        /// Runs Tesseract OCR on raw image bytes using the specified page segmentation mode.
        /// </summary>
        /// <param name="imageBytes">PNG image bytes to process.</param>
        /// <param name="mode">Tesseract page segmentation mode.</param>
        /// <returns>Recognized text, or an empty string on failure.</returns>
        private string RunTesseract(byte[] imageBytes, PageSegMode mode)
        {
            try
            {
                using var pix = Pix.LoadFromMemory(imageBytes);
                using var page = _engine.Process(pix, mode);
                return page.GetText() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Scores recognized OCR text by estimating how much useful lyric/chord content it contains.
        /// Higher scores indicate more letters, umlauts, chord hits, and real words;
        /// lower scores indicate noise characters and short fragments.
        /// </summary>
        /// <param name="text">The OCR result text to score.</param>
        /// <returns>A floating-point quality score; higher is better.</returns>
        private static double Score(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return -1;

            var normalized = text.Replace("\r\n", "\n");
            var letters = normalized.Count(char.IsLetter);
            var digits = normalized.Count(char.IsDigit);
            var spaces = normalized.Count(c => c == ' ' || c == '\t');
            var lines = normalized.Count(c => c == '\n');
            var weird = normalized.Count(c => c is '�' or '™' or '—' or '_' or '|' or '[' or ']' or '"');
            var chordHits = ChordLikeRe.Matches(normalized).Count;

            // German umlauts / diacritics are a strong signal for real lyric text.
            var umlauts = normalized.Count(c => c is 'ä' or 'ö' or 'ü' or 'Ä' or 'Ö' or 'Ü' or 'ß');

            // Real lyric/chord lines tend to have reasonable word lengths (3-12 chars).
            var words = System.Text.RegularExpressions.Regex.Split(normalized, @"\s+")
                .Where(w => w.Length > 0).ToList();
            var realWords = words.Count(w => w.Length >= 3 && w.Length <= 16 && w.Any(char.IsLetter));
            var fragmentWords = words.Count(w => w.Length == 1 && !char.IsLetterOrDigit(w[0]));

            // Encourage: more letters, some spaces, line breaks, chords, umlauts, real words.
            // Discourage: lots of box/line art, single-char fragments.
            return letters + (digits * 0.25) + (spaces * 1.2) + (lines * 2.0)
                   + (chordHits * 20.0) + (umlauts * 8.0) + (realWords * 3.0)
                   - (weird * 3.0) - (fragmentWords * 1.5);
        }

        /// <summary>
        /// Post-processes raw Tesseract output: removes noise lines, joins hyphenated syllables,
        /// collects consecutive chord-only tokens into tab-separated chord runs,
        /// and normalizes whitespace throughout.
        /// </summary>
        /// <param name="text">Raw Tesseract OCR text.</param>
        /// <returns>Cleaned, joined text suitable for further ChordPro conversion.</returns>
        private static string PostProcess(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            var rawLines = normalized.Split('\n')
                .Select(l => l.TrimEnd())
                .ToList();

            // Remove clearly broken/garbage lines early.
            rawLines = rawLines
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .Where(l => !IsMostlyNoise(l))
                .ToList();

            if (rawLines.Count == 0) return string.Empty;

            var output = new List<string>();
            string current = null;
            var chordRun = new List<string>();

            foreach (var line in rawLines)
            {
                if (TryGetChordOnlyTokens(line, out var chordTokens))
                {
                    chordRun.AddRange(chordTokens);
                    continue;
                }

                if (chordRun.Count > 0)
                {
                    FlushCurrent();
                    output.Add(string.Join("\t", chordRun));
                    chordRun.Clear();
                }

                if (LooksLikeSectionLabel(line))
                {
                    FlushCurrent();
                    output.Add(NormalizeWhitespace(line));
                    continue;
                }

                if (current == null)
                {
                    current = line;
                    continue;
                }

                if (ShouldJoin(current, line))
                {
                    current = Join(current, line);
                }
                else
                {
                    output.Add(NormalizeWhitespace(current));
                    current = line;
                }
            }

            if (chordRun.Count > 0)
            {
                FlushCurrent();
                output.Add(string.Join("\t", chordRun));
                chordRun.Clear();
            }

            FlushCurrent();
            return string.Join("\n", output) + "\n";

            void FlushCurrent()
            {
                if (!string.IsNullOrWhiteSpace(current))
                    output.Add(NormalizeWhitespace(current));
                current = null;
            }
        }

        /// <summary>
        /// Determines whether a line is mostly noise (no letters, box-drawing symbols,
        /// unusual characters) and should be discarded.
        /// </summary>
        /// <param name="line">The text line to inspect.</param>
        /// <returns><c>true</c> if the line is noise and should be dropped.</returns>
        private static bool IsMostlyNoise(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return true;

            var letters = line.Count(char.IsLetter);
            if (letters == 0)
            {
                // Keep pure numbers (page number) occasionally.
                return !line.All(c => char.IsDigit(c) || char.IsWhiteSpace(c));
            }

            // Short lines that are purely notation symbols (dashes, underscores, pipes, equals)
            if (letters <= 2 && line.Count(c => c is '_' or '|' or '=' or '~' or '^' or '*') >= 4)
                return true;

            var noiseHits = NoiseHeavyRe.Matches(line).Count;
            var weird = line.Count(c => c is '�' or '™' or '—' or '_' or '|');
            var ratio = (double)letters / Math.Max(1, line.Length);

            if (ratio < 0.20 && (weird + noiseHits) >= 2) return true;
            if (weird >= 6) return true;
            return false;
        }

        /// <summary>
        /// Checks whether all tokens in the line are valid chord tokens after cleaning.
        /// </summary>
        /// <param name="line">The text line to inspect.</param>
        /// <param name="tokens">When true, contains the normalized chord token list.</param>
        /// <returns><c>true</c> if every token is a chord; otherwise <c>false</c>.</returns>
        private static bool TryGetChordOnlyTokens(string line, out List<string> tokens)
        {
            tokens = null;
            var rawTokens = Regex.Split(line.Trim(), @"\s+")
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .ToList();

            if (rawTokens.Count == 0) return false;

            var normalized = new List<string>(rawTokens.Count);
            foreach (var raw in rawTokens)
            {
                var t = NormalizeChordToken(raw);
                if (t == null) return false;
                normalized.Add(t);
            }

            // Consider a single chord as chord-only too, so we can build chord runs.
            if (normalized.Count == 1 && ChordLikeRe.IsMatch(normalized[0]))
            {
                tokens = normalized;
                return true;
            }

            if (normalized.All(t => ChordLikeRe.IsMatch(t)))
            {
                tokens = normalized;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Normalizes a single chord token from OCR output by stripping non-musical punctuation
        /// and correcting common OCR misreads (stray letters, maj7 variants).
        /// </summary>
        /// <param name="token">Raw token to normalize.</param>
        /// <returns>Cleaned chord token, or <c>null</c> if the token is not a valid chord candidate.</returns>
        private static string NormalizeChordToken(string token)
        {
            if (token.IsBlankOrWhitespace()) return null;

            var cleaned = Regex.Replace(token.Trim(), @"[^A-Za-z0-9#b/]+", "");
            if (cleaned.Length == 0) return null;

            // Common OCR artifact: inserted 'h' after the root note (e.g. "Bhm" instead of "Bm").
            if (cleaned.Length >= 2)
            {
                var root = cleaned[0];
                var second = cleaned[1];
                if ((root is >= 'A' and <= 'H' or >= 'a' and <= 'h') && (second == 'h' || second == 'H'))
                {
                    cleaned = cleaned[0] + cleaned.Substring(2);
                }
            }

            // Fix frequent OCR confusions.
            cleaned = cleaned.Replace("%", string.Empty);
            cleaned = cleaned.Replace("\\", string.Empty);

            // OCR often confuses 'l'/'I' for '1', stray 'i' after root for nothing.
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])l(?=[A-Za-z])", "$1", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])im$", "$1m", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])i([0-9]+)$", "$1$2", RegexOptions.IgnoreCase);

            // Common maj7/sus confusions.
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])ma7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])mqj7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])maij7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])mai7$", "$1maj7", RegexOptions.IgnoreCase);

            // Normalize root letter casing.
            if (cleaned.Length > 0 && char.IsLetter(cleaned[0]))
                cleaned = char.ToUpperInvariant(cleaned[0]) + (cleaned.Length > 1 ? cleaned.Substring(1) : string.Empty);

            return cleaned.Length == 0 ? null : cleaned;
        }

        /// <summary>
        /// Returns <c>true</c> if the line looks like a song section label
        /// (e.g. "Strophe", "Refrain", "Chorus", "Bridge", "Intro", "Outro", "Interlude",
        /// "Zwischenspiel", or "Vers 1").
        /// </summary>
        /// <param name="line">The text line to inspect.</param>
        /// <returns><c>true</c> if the line is a section label.</returns>
        private static bool LooksLikeSectionLabel(string line)
        {
            var s = line.Trim();
            return s.Equals("strophe", StringComparison.OrdinalIgnoreCase)
                   || s.Equals("refr", StringComparison.OrdinalIgnoreCase)
                   || s.StartsWith("refr", StringComparison.OrdinalIgnoreCase)
                   || s.StartsWith("chor", StringComparison.OrdinalIgnoreCase)
                   || s.StartsWith("bridge", StringComparison.OrdinalIgnoreCase)
                   || s.StartsWith("intro", StringComparison.OrdinalIgnoreCase)
                   || s.StartsWith("outro", StringComparison.OrdinalIgnoreCase)
                   || s.StartsWith("interlude", StringComparison.OrdinalIgnoreCase)
                   || s.StartsWith("zwischenspiel", StringComparison.OrdinalIgnoreCase)
                   || Regex.IsMatch(s, @"^(vers|verse|strophe)\s*\d*\b", RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Determines whether the current and next OCR line fragments should be joined into
        /// a single line (e.g. hyphenated words, short fragments, lowercase continuation).
        /// </summary>
        /// <param name="current">The current accumulated line.</param>
        /// <param name="next">The next line candidate.</param>
        /// <returns><c>true</c> if the lines should be concatenated.</returns>
        private static bool ShouldJoin(string current, string next)
        {
            // Join if we seem to have short OCR fragments.
            if (current.Length < 24 && next.Length < 24) return true;

            // Join hyphenated syllables.
            if (current.EndsWith("-", StringComparison.Ordinal)) return true;

            // Join if the next starts with lowercase or punctuation.
            var first = next.FirstOrDefault(c => !char.IsWhiteSpace(c));
            if (first != default && (char.IsLower(first) || first is ',' or '.' or ':' or ';')) return true;

            // Join if current ends with comma-less word and next continues.
            if (!Regex.IsMatch(current, @"[\.!\?]$"))
            {
                if (Regex.IsMatch(next, @"^[A-Za-zÄÖÜäöüß]"))
                    return current.Length < 60;
            }

            return false;
        }

        /// <summary>
        /// Joins two line fragments, removing a trailing hyphen if present, otherwise
        /// concatenating with a single space.
        /// </summary>
        /// <param name="current">The current line.</param>
        /// <param name="next">The continuation line.</param>
        /// <returns>The joined line.</returns>
        private static string Join(string current, string next)
        {
            if (current.EndsWith("-", StringComparison.Ordinal))
            {
                return current.Substring(0, current.Length - 1) + next;
            }

            return current.TrimEnd() + " " + next.TrimStart();
        }

        /// <summary>
        /// Collapses runs of whitespace to a single space and trims the result.
        /// </summary>
        /// <param name="s">The string to normalize.</param>
        /// <returns>The whitespace-normalized string.</returns>
        private static string NormalizeWhitespace(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            s = Regex.Replace(s, @"\s{2,}", " ");
            return s.Trim();
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
