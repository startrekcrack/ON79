using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SongConverters
{
    /// <summary>
    /// Utility class for composing ChordPro lines by inserting chord tokens above lyric lines,
    /// as produced by OCR-based PDF/image converters.
    /// </summary>
    internal static class OcrChoComposer
    {
        private static readonly Regex ChordTokenRe = new(
            @"^(?:[A-Ha-h](?:is|es|[#b])?)(?:m|maj|min|sus|add|dim|aug)?(?:2|4|5|6|7|9|11|13)?(?:sus[24])?(?:/[A-Ha-h](?:is|es|[#b])?)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Determines whether the given text consists entirely of chord tokens.
        /// </summary>
        /// <param name="text">The text to inspect.</param>
        /// <returns><c>true</c> if every whitespace-separated token is a chord; otherwise <c>false</c>.</returns>
        public static bool IsChordOnlyText(string text)
        {
            if (text.IsBlankOrWhitespace()) return false;
            var tokens = Regex.Split(text.Trim(), @"\s+")
                .Select(CleanToken)
                .Where(t => t.Length > 0)
                .ToList();

            return tokens.Count > 0 && tokens.All(t => ChordTokenRe.IsMatch(t));
        }

        /// <summary>
        /// Inserts chord tokens from a chord line into the corresponding lyric line using position-based alignment.
        /// Chord tokens are wrapped in square brackets and inserted before the closest lyric word.
        /// </summary>
        /// <param name="chordLine">A line containing only chord tokens, optionally tab-aligned.</param>
        /// <param name="lyricLine">The lyric line to insert chords into.</param>
        /// <param name="tabWidth">Number of spaces per tab stop used for positional alignment.</param>
        /// <returns>The lyric line with inline chord brackets inserted.</returns>
        public static string InsertChords(string chordLine, string lyricLine, int tabWidth)
        {
            if (lyricLine.IsBlankOrWhitespace()) return lyricLine ?? string.Empty;

            // If chordLine has no tabs (or lost alignment), distribute chords across words.
            if (!string.IsNullOrWhiteSpace(chordLine) && chordLine.Contains(' ') && !chordLine.Contains('\t'))
            {
                var chordTokens = ExtractChordTokensFromText(chordLine);
                var wordSpans = WordSpans(lyricLine);
                if (chordTokens.Count == 0 || wordSpans.Count == 0) return lyricLine;

                var outputLine = lyricLine;
                for (int i = chordTokens.Count - 1; i >= 0; i--)
                {
                    var idx = chordTokens.Count == 1
                        ? 0
                        : (int)Math.Round(i * (wordSpans.Count - 1) / (double)(chordTokens.Count - 1));
                    var insertPos = wordSpans[Math.Max(0, Math.Min(idx, wordSpans.Count - 1))].start;
                    outputLine = outputLine.Insert(insertPos, $"[{chordTokens[i]}]");
                }
                return outputLine;
            }

            var (chords, width) = ChordPositions(chordLine, tabWidth);
            var spans = WordSpans(lyricLine);
            if (chords.Count == 0 || spans.Count == 0) return lyricLine;

            var output = lyricLine;
            var ordered = chords.OrderByDescending(c => c.pos).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                var (pos, chord) = ordered[i];
                var target = (int)Math.Round((double)pos / Math.Max(width, 1) * lyricLine.Length);
                var spanIdx = spans.FindIndex(s => s.start <= target && target <= s.end);
                int insertPos;

                if (spanIdx >= 0)
                {
                    var (start, end) = spans[spanIdx];
                    var len = end - start + 1;
                    insertPos = (len <= 3 && spanIdx + 1 < spans.Count) ? spans[spanIdx + 1].start : start;
                }
                else
                {
                    var prev = spans.LastOrDefault(s => s.start <= target);
                    var next = spans.FirstOrDefault(s => s.start >= target);

                    if (prev != default && next != default)
                        insertPos = (target - prev.end) <= 4 ? prev.start : next.start;
                    else if (prev != default)
                        insertPos = prev.start;
                    else
                        insertPos = next != default ? next.start : 0;
                }

                if (i == ordered.Count - 1 && insertPos > 0)
                {
                    // Put the left-most chord at the beginning.
                    insertPos = 0;
                }

                output = output.Insert(insertPos, $"[{chord}]");
            }

            return output;
        }

        /// <summary>
        /// Inserts chord tokens into a lyric line while preserving a leading verse number prefix (e.g. "1. ", "2) ").
        /// The prefix is kept intact; chords are inserted into the remainder of the line.
        /// </summary>
        /// <param name="chordLine">A line containing only chord tokens.</param>
        /// <param name="lyricLine">The lyric line, optionally starting with a verse number prefix.</param>
        /// <param name="tabWidth">Number of spaces per tab stop for positional alignment.</param>
        /// <returns>The lyric line with inline chord brackets inserted, prefix preserved.</returns>
        public static string InsertChordsPreservingVersePrefix(string chordLine, string lyricLine, int tabWidth)
        {
            if (lyricLine.IsBlankOrWhitespace()) return lyricLine ?? string.Empty;

            // Common pattern: "1. ..." or "2) ...". Keep prefix untouched and insert chords into the remainder.
            // OCR sometimes reads "1." as "I." or "l.".
            var m = Regex.Match(lyricLine, @"^(\s*(?:\d+|[lI])\s*[.)]\s+)(.+)$", RegexOptions.CultureInvariant);
            if (!m.Success) return InsertChords(chordLine, lyricLine, tabWidth);

            var prefix = m.Groups[1].Value;
            var rest = m.Groups[2].Value;
            var composed = InsertChords(chordLine, rest, tabWidth);
            return prefix + composed;
        }

        /// <summary>
        /// Parses a chord line and returns each chord token with its character column position,
        /// expanding tab characters to the given tab width.
        /// </summary>
        /// <param name="line">The chord line to parse.</param>
        /// <param name="tab">Tab width in spaces.</param>
        /// <returns>A list of (position, chord) pairs and the total line width.</returns>
        private static (List<(int pos, string chord)> chords, int width) ChordPositions(string line, int tab)
        {
            int pos = 0;
            var chords = new List<(int pos, string chord)>();
            string token = null;
            int tokenStart = -1;

            foreach (var ch in line ?? string.Empty)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!string.IsNullOrEmpty(token))
                    {
                        chords.Add((tokenStart < 0 ? 0 : tokenStart, CleanToken(token)));
                        token = null;
                        tokenStart = -1;
                    }

                    if (ch == '\t')
                        pos = ((pos / tab) + 1) * tab;
                    else
                        pos++;
                }
                else
                {
                    if (tokenStart < 0) tokenStart = pos;
                    token += ch;
                    pos++;
                }
            }

            if (!string.IsNullOrEmpty(token))
            {
                chords.Add((tokenStart < 0 ? 0 : tokenStart, CleanToken(token)));
            }

            // Filter invalid chord tokens after CleanToken.
            chords = chords.Where(c => !c.chord.IsBlankOrWhitespace() && ChordTokenRe.IsMatch(c.chord)).ToList();
            return (chords, pos);
        }

        /// <summary>
        /// Returns the start and end character indices of each non-whitespace word in the text.
        /// </summary>
        /// <param name="text">The text to scan.</param>
        /// <returns>A list of (start, end) index pairs, one per word.</returns>
        private static List<(int start, int end)> WordSpans(string text)
        {
            var spans = new List<(int, int)>();
            foreach (Match m in Regex.Matches(text ?? string.Empty, @"\S+"))
            {
                spans.Add((m.Index, m.Index + m.Length - 1));
            }
            return spans;
        }

        /// <summary>
        /// Extracts all chord-valid tokens from a text string.
        /// </summary>
        /// <param name="text">The text to extract chord tokens from.</param>
        /// <returns>A list of cleaned chord token strings in their original order.</returns>
        private static List<string> ExtractChordTokensFromText(string text)
        {
            var tokens = Regex.Split((text ?? string.Empty).Trim(), @"\s+")
                .Select(CleanToken)
                .Where(t => t.Length > 0)
                .ToList();

            // Keep order, but only chord-like tokens.
            return tokens.Where(t => ChordTokenRe.IsMatch(t)).ToList();
        }

        /// <summary>
        /// Cleans an OCR-scanned chord token by removing non-musical punctuation, fixing common OCR
        /// misreads (stray letters, maj7 variants), and normalizing the root note casing.
        /// </summary>
        /// <param name="token">The raw OCR token to clean.</param>
        /// <returns>The cleaned chord string, or an empty string if the token is invalid.</returns>
        private static string CleanToken(string token)
        {
            if (token.IsBlankOrWhitespace()) return string.Empty;
            var cleaned = Regex.Replace(token.Trim(), @"[^A-Za-z0-9#b/]+", "");
            if (cleaned.Length == 0) return string.Empty;

            // Common OCR confusions: stray 'l' after root, 'h' after root.
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])l(?=[A-Za-z])", "$1", RegexOptions.IgnoreCase);
            if (cleaned.Length >= 2)
            {
                var root = cleaned[0];
                var second = cleaned[1];
                if ((root is >= 'A' and <= 'H' or >= 'a' and <= 'h') && (second == 'h' || second == 'H'))
                    cleaned = cleaned[0] + cleaned.Substring(2);
            }

            // OCR minor confusions: 'im' -> 'm', stray 'i' before number.
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])im$", "$1m", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])i([0-9]+)$", "$1$2", RegexOptions.IgnoreCase);

            // maj7 OCR confusions.
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])ma7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])mqj7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])maij7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])mai7$", "$1maj7", RegexOptions.IgnoreCase);

            // Normalize root letter casing.
            if (cleaned.Length > 0 && char.IsLetter(cleaned[0]))
                cleaned = char.ToUpperInvariant(cleaned[0]) + (cleaned.Length > 1 ? cleaned.Substring(1) : string.Empty);

            return cleaned;
        }
    }
}
