using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SongConverters
{
    /// <summary>
    /// Composes a complete ChordPro document from OCR-extracted lines by pairing chord lines
    /// with lyric lines based on bounding-box x positions and emitting ChordPro directives.
    /// </summary>
    internal static class OcrChordProComposer
    {
        private static readonly Regex ChordTokenRe = new(
            @"^(?:[A-Ha-h](?:is|es|[#b])?)(?:m|maj|min|sus|add|dim|aug)?\d*(?:sus[24])?(?:/[A-Ha-h](?:is|es|[#b])?)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public sealed record Options(
            int TabWidth = 12,
            bool UseHashHeadings = true
        );

        /// <summary>
        /// Composes a ChordPro document from OCR lines with header metadata.
        /// Chord lines are detected, matched to the following lyric line, and combined into
        /// inline ChordPro notation using bounding-box x-coordinates for alignment.
        /// </summary>
        /// <param name="lines">OCR lines extracted from the source document.</param>
        /// <param name="title">Song title for the ChordPro header.</param>
        /// <param name="subtitle">Optional subtitle (arranger, etc.).</param>
        /// <param name="copyright">Optional copyright string.</param>
        /// <param name="key">Optional key signature (e.g. "G", "Am").</param>
        /// <param name="metaSource">Value for the {meta: source=...} tag.</param>
        /// <param name="comment">Optional comment added after the header.</param>
        /// <param name="options">Composition options (tab width, heading style).</param>
        /// <returns>A ChordPro-formatted string.</returns>
        public static string Compose(
            IEnumerable<OcrLine> lines,
            string title,
            string subtitle,
            string copyright,
            string key,
            string metaSource,
            string comment,
            Options options)
        {
            options ??= new Options();

            var output = new List<string>();
            output.Add($"{{title: {title}}}");
            if (!subtitle.IsBlankOrWhitespace()) output.Add($"{{subtitle: {subtitle}}}");
            if (!copyright.IsBlankOrWhitespace()) output.Add($"{{copyright: {copyright}}}");
            if (!key.IsBlankOrWhitespace()) output.Add($"{{key: {key}}}");
            if (!metaSource.IsBlankOrWhitespace()) output.Add($"{{meta: source={metaSource}}}");
            if (!comment.IsBlankOrWhitespace()) output.Add($"{{comment: {comment}}}");
            output.Add(string.Empty);

            var cleaned = (lines ?? Enumerable.Empty<OcrLine>())
                .Select(l => NormalizeLineText(l))
                .Where(t => !t.text.IsBlankOrWhitespace())
                .ToList();

            // Pair chord lines with lyric lines below, based on y and x alignment.
            for (int i = 0; i < cleaned.Count; i++)
            {
                var line = cleaned[i];

                if (TryParseSection(line.text, out var sectionTitle))
                {
                    output.Add(options.UseHashHeadings ? $"# {sectionTitle}" : $"{{comment: {sectionTitle}}}");
                    continue;
                }

                if (IsChordLine(line))
                {
                    var nextIdx = FindNextLyricLine(cleaned, i + 1);
                    if (nextIdx >= 0)
                    {
                        var composed = InsertChordsByX(line, cleaned[nextIdx]);
                        output.Add(composed);
                        i = nextIdx; // consumed lyric line
                        continue;
                    }

                    // chord-only fallback
                    var tokens = line.words.Where(w => IsChordToken(w.Text)).Select(w => CleanChord(w.Text)).Where(t => t.Length > 0).ToList();
                    if (tokens.Count > 0)
                    {
                        output.Add(string.Join(" ", tokens.Select(t => $"[{t}]")));
                        continue;
                    }
                }

                output.Add(FixHyphenation(line.text));
            }

            return string.Join("\n", output).TrimEnd() + "\n";
        }

        /// <summary>
        /// Searches forward in the line list for the next non-chord, non-section line.
        /// </summary>
        /// <param name="lines">Normalized OCR lines.</param>
        /// <param name="start">Index to start searching from.</param>
        /// <returns>Index of the next lyric line, or -1 if none found before a section header or end.</returns>
        private static int FindNextLyricLine(List<(string text, List<OcrWord> words)> lines, int start)
        {
            for (int i = start; i < lines.Count; i++)
            {
                var t = lines[i].text;
                if (t.IsBlankOrWhitespace()) continue;
                if (TryParseSection(t, out _)) return -1;
                if (IsChordLine(lines[i])) continue;
                return i;
            }
            return -1;
        }

        /// <summary>
        /// Determines whether a line is predominantly made up of chord tokens
        /// (at least 70% of non-empty tokens must be valid chords).
        /// </summary>
        /// <param name="line">The normalized OCR line to inspect.</param>
        /// <returns><c>true</c> if the line is a chord-only or chord-dominant line.</returns>
        private static bool IsChordLine((string text, List<OcrWord> words) line)
        {
            var chordWords = line.words.Select(w => CleanChord(w.Text)).Where(t => t.Length > 0).ToList();
            if (chordWords.Count == 0) return false;
            var chordish = chordWords.Count(t => IsChordToken(t));
            return chordish >= Math.Max(1, (int)Math.Ceiling(chordWords.Count * 0.7));
        }

        /// <summary>
        /// Inserts chord tokens from a chord OCR line into a lyric line using the x-coordinates
        /// of each word's bounding box to determine insertion point.
        /// </summary>
        /// <param name="chordLine">The chord OCR line with word bounding boxes.</param>
        /// <param name="lyricLine">The lyric OCR line into which chords are inserted.</param>
        /// <returns>The lyric text with inline chord brackets inserted at positionally appropriate positions.</returns>
        private static string InsertChordsByX((string text, List<OcrWord> words) chordLine, (string text, List<OcrWord> words) lyricLine)
        {
            var lyric = FixHyphenation(lyricLine.text);
            var spans = WordSpans(lyric);
            if (spans.Count == 0) return lyric;

            // Map each lyric word span to a representative x coordinate.
            var lyricWordBoxes = lyricLine.words.OrderBy(w => w.Box.Left).ToList();
            var spanXs = new List<float>();

            // Heuristic: match spans to OCR words in order.
            for (int i = 0; i < spans.Count; i++)
            {
                if (i < lyricWordBoxes.Count)
                {
                    var w = lyricWordBoxes[i];
                    spanXs.Add(w.Box.Left + (w.Box.Width * 0.2f));
                }
                else
                {
                    spanXs.Add(spanXs.Count > 0 ? spanXs[^1] + 10 : 0);
                }
            }

            var chordWords = chordLine.words
                .Select(w => (x: w.Box.Left + (w.Box.Width * 0.2f), chord: CleanChord(w.Text)))
                .Where(t => IsChordToken(t.chord))
                .OrderBy(t => t.x)
                .ToList();

            if (chordWords.Count == 0) return lyric;

            var sb = new StringBuilder(lyric);

            // Insert from right to left (descending span index) to keep indices stable.
            var insertions = new List<(int pos, string chord)>();
            foreach (var c in chordWords)
            {
                var idx = FindClosestSpan(spanXs, c.x);
                idx = Math.Clamp(idx, 0, spans.Count - 1);
                insertions.Add((spans[idx].start, c.chord));
            }

            foreach (var ins in insertions.OrderByDescending(i => i.pos))
            {
                sb.Insert(ins.pos, $"[{ins.chord}]");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Returns the index of the span whose x-coordinate is closest to the given x value.
        /// </summary>
        /// <param name="xs">List of x-coordinates for each word span.</param>
        /// <param name="x">Target x-coordinate.</param>
        /// <returns>Index of the closest span.</returns>
        private static int FindClosestSpan(List<float> xs, float x)
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
        /// Normalizes an OCR line by joining its word texts with single spaces and stripping
        /// runs of multiple spaces.
        /// </summary>
        /// <param name="line">The OCR line to normalize.</param>
        /// <returns>A tuple of the normalized text and the original word list.</returns>
        private static (string text, List<OcrWord> words) NormalizeLineText(OcrLine line)
        {
            var words = (line?.Words ?? Array.Empty<OcrWord>()).ToList();
            var text = string.Join(" ", words.Select(w => w.Text)).Trim();
            text = Regex.Replace(text, @"\s{2,}", " ");
            return (text, words);
        }

        /// <summary>
        /// Normalizes OCR dash characters (em-dash, en-dash, non-breaking spaces) to standard
        /// ASCII equivalents and trims the result.
        /// </summary>
        /// <param name="text">The text to fix.</param>
        /// <returns>The normalized text.</returns>
        private static string FixHyphenation(string text)
        {
            if (text.IsBlankOrWhitespace()) return text ?? string.Empty;
            // Keep explicit hyphens, but normalize weird OCR dash chars.
            return text
                .Replace('–', '-')
                .Replace('—', '-')
                .Replace(" ", " ")
                .Trim();
        }

        /// <summary>
        /// Attempts to parse the text as a song section label (e.g. "Strophe 2", "Chorus", "Bridge").
        /// </summary>
        /// <param name="text">The text to inspect.</param>
        /// <param name="section">When successful, contains the normalized section name (e.g. "Verse 2").</param>
        /// <returns><c>true</c> if the text was recognized as a section label.</returns>
        private static bool TryParseSection(string text, out string section)
        {
            section = null;
            if (text.IsBlankOrWhitespace()) return false;
            var raw = text.Trim();

            var m = Regex.Match(raw, @"^(strophe|vers|verse)\s*(\d+)?\b", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var num = m.Groups[2].Success ? (" " + m.Groups[2].Value) : string.Empty;
                section = char.ToUpperInvariant(m.Groups[1].Value[0]) + m.Groups[1].Value.Substring(1).ToLowerInvariant() + num;
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
        /// Returns <c>true</c> if the token matches the chord regular expression.
        /// </summary>
        /// <param name="token">Token to test.</param>
        /// <returns><c>true</c> if the token is a valid chord token.</returns>
        private static bool IsChordToken(string token)
            => !token.IsBlankOrWhitespace() && ChordTokenRe.IsMatch(token);

        /// <summary>
        /// Cleans an OCR-scanned chord token by stripping non-musical characters and correcting
        /// common OCR confusions (stray letters, maj7 variants), then normalizes root note casing.
        /// </summary>
        /// <param name="token">The raw chord token to clean.</param>
        /// <returns>The cleaned chord string, or an empty string for invalid tokens.</returns>
        private static string CleanChord(string token)
        {
            if (token.IsBlankOrWhitespace()) return string.Empty;
            var cleaned = Regex.Replace(token.Trim(), @"[^A-Za-z0-9#b/]+", "");
            if (cleaned.Length == 0) return string.Empty;

            // Common OCR confusions: stray 'l' after root.
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])l(?=[A-Za-z])", "$1", RegexOptions.IgnoreCase);

            // OCR artifact: Bhm -> Bm
            if (cleaned.Length >= 2)
            {
                var root = cleaned[0];
                var second = cleaned[1];
                if ((root is >= 'A' and <= 'H' or >= 'a' and <= 'h') && (second == 'h' || second == 'H'))
                    cleaned = cleaned[0] + cleaned.Substring(2);
            }

            // OCR minor/maj confusions.
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])im$", "$1m", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])i([0-9]+)$", "$1$2", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])ma7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])mqj7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])maij7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])mai7$", "$1maj7", RegexOptions.IgnoreCase);

            // Normalize root letter casing.
            if (cleaned.Length > 0 && char.IsLetter(cleaned[0]))
                cleaned = char.ToUpperInvariant(cleaned[0]) + (cleaned.Length > 1 ? cleaned.Substring(1) : string.Empty);

            return cleaned;
        }

        /// <summary>
        /// Returns the start and end character indices of each non-whitespace word in the text.
        /// </summary>
        /// <param name="text">The text to scan.</param>
        /// <returns>A list of (start, end) index pairs, one per word.</returns>
        private static List<(int start, int end)> WordSpans(string text)
        {
            var spans = new List<(int start, int end)>();
            foreach (Match m in Regex.Matches(text ?? string.Empty, @"\S+"))
            {
                spans.Add((m.Index, m.Index + m.Length - 1));
            }
            return spans;
        }
    }
}
