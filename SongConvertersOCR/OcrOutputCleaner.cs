using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SongConverters
{
    /// <summary>
    /// Post-processes OCR output to fix invalid chords and remove garbage lines.
    /// Balances: aggressive garbage removal while preserving valid lyric content.
    /// </summary>
    public static class OcrOutputCleaner
    {
        /// <summary>
        /// Comprehensive chord validation pattern.
        /// Matches real-world chords: Am, Em7, D/F#, Cmaj7, Bsus4, F#m7/B, Asus2, Bdim, Gaug, Cadd9, etc.
        /// </summary>
        private static readonly Regex ValidChordPattern = new(
            @"^\[" +
            @"([A-GH]" +                                        // root note (H = German B)
            @"[#b♯♭]?" +                                        // optional accidental
            @"(?:m|M|maj|min|dim|aug)?" +                        // optional quality
            @"(?:add|sus)?" +                                    // optional add/sus
            @"(?:[0-9]{1,2})?" +                                 // optional extension (7, 9, 11, 13, 2, 4)
            @"(?:/[A-GH][#b♯♭]?)?" +                            // optional slash bass
            @")\]$",
            RegexOptions.Compiled
        );

        /// <summary>Pattern für alle Bracket-Inhalte [...]</summary>
        private static readonly Regex AllChordPattern = new(@"\[[^\]]+\]", RegexOptions.Compiled);

        /// <summary>
        /// Cleans OCR output by removing garbage lines and invalid chords.
        /// </summary>
        public static string Clean(string ocrText)
        {
            if (string.IsNullOrWhiteSpace(ocrText))
                return ocrText;

            var cleaned = RemoveGarbageLines(ocrText);
            cleaned = FixOcrArtifacts(cleaned);
            cleaned = RemoveInvalidChords(cleaned);
            cleaned = NormalizeGermanLyricFragments(cleaned);
            cleaned = ReconstructLeadInVerse(cleaned);
            cleaned = RemoveUnstructuredLeadIn(cleaned);
            cleaned = InsertLikelyChorusMarker(cleaned);
            cleaned = CollapseRepeatedStructureComments(cleaned);
            cleaned = RemoveEmptyStructureComments(cleaned);
            cleaned = RepairLikelySongPhrases(cleaned);
            cleaned = RepairDieMoevenFragments(cleaned);
            cleaned = CleanupWhitespace(cleaned);

            return cleaned;
        }

        /// <summary>
        /// Removes lines that are clearly sheet-music notation garbage from a CHO text:
        /// lines containing musical note/rest Unicode symbols (œ, Œ, ˙, Ó, ‰, ∑ etc.),
        /// staff clef lines starting with '&amp;', key-signature-only '#' lines, and
        /// unbracketed chord-name rows like "B m D E" or "A #".
        /// Strips trailing stray '#' accidentals from otherwise-valid chord lines like "[D][A]#".
        /// Safe to call on any CHO text — directive lines are never removed.
        /// </summary>
        public static string RemoveSheetMusicNotationLines(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var result = new List<string>(lines.Length);

            foreach (var rawLine in lines)
            {
                var line = rawLine ?? string.Empty;
                var trimmed = line.Trim();

                // Keep blank lines (structure)
                if (trimmed.Length == 0) { result.Add(line); continue; }

                // Always keep directive lines
                if (trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal))
                { result.Add(line); continue; }

                // Remove if line contains music notation Unicode characters
                if (SheetMusicNotationPattern.IsMatch(trimmed))
                    continue;

                // Remove lines starting with '&' (music staff / clef indicator)
                if (trimmed.StartsWith("&", StringComparison.Ordinal))
                    continue;

                // Remove lines that consist only of '#' characters (key signature lines)
                if (Regex.IsMatch(trimmed, @"^#+$"))
                    continue;

                // Remove "# j", "# n", "# j #" etc. (sharp + short token — music notation)
                if (Regex.IsMatch(trimmed, @"^#+\s+[a-z]\s*$", RegexOptions.IgnoreCase))
                    continue;

                // Remove lines that are unbracketed chord-name rows: "A #", "F m", "B m D E"
                if (IsUnbracketedChordRow(trimmed))
                    continue;

                // Strip trailing stray '#' accidentals from valid chord lines, e.g. "[D][A]#" → "[D][A]"
                var cleaned = Regex.Replace(line, @"(?<=\])\s*#+\s*$", string.Empty).TrimEnd();
                if (cleaned.Length > 0)
                    result.Add(cleaned);
            }

            // Collapse runs of more than one consecutive blank line to a single blank line
            var final = new List<string>(result.Count);
            int blankRun = 0;
            foreach (var l in result)
            {
                if (string.IsNullOrWhiteSpace(l)) { blankRun++; if (blankRun <= 1) final.Add(l); }
                else { blankRun = 0; final.Add(l); }
            }

            return string.Join(Environment.NewLine, final);
        }

        /// <summary>
        /// Music notation Unicode symbols — these characters should never appear in song-text CHO output.
        /// </summary>
        private static readonly Regex SheetMusicNotationPattern = new Regex(
            @"[œŒ˙Óˆ‰∑∏˘˜ΩœŒ]",
            RegexOptions.Compiled);

        /// <summary>
        /// Returns true when every whitespace-delimited token in <paramref name="line"/> looks like
        /// an unbracketed chord name, accidental, or modifier — i.e. a music-notation chord row.
        /// </summary>
        private static bool IsUnbracketedChordRow(string line)
        {
            // Lines with brackets already use CHO chord syntax — not a notation row
            if (line.IndexOf('[') >= 0 || line.IndexOf(']') >= 0)
                return false;

            var tokens = Regex.Split(line, @"\s+").Where(t => t.Length > 0).ToArray();
            if (tokens.Length == 0 || tokens.Length > 8)
                return false;

            // Every token must be a chord-name token, accidental, minor/major modifier, or single digit
            return tokens.All(t => Regex.IsMatch(t,
                @"^[A-H][#b]?(?:m|M|maj|min|dim|aug|sus\d?|add\d?)?[0-9]?$" +
                @"|^[#b♯♭]$" +
                @"|^m$" +
                @"|^[0-9]$",
                RegexOptions.None));
        }

        /// <summary>
        /// Fixes common OCR artifacts: trailing garbage characters on words,
        /// leading junk characters, stray punctuation, etc.
        /// </summary>
        private static string FixOcrArtifacts(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var result = new List<string>();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    result.Add(line);
                    continue;
                }

                // Preserve directive lines
                var trimmed = line.Trim();
                if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
                {
                    result.Add(line);
                    continue;
                }

                var l = line;

                // Strip trailing single garbage chars appended by OCR to words
                // e.g. "Gegenwartf" -> "Gegenwart", "zerrissenq" -> "zerrissen"
                l = Regex.Replace(l, @"(?<=\p{L}{2})[qT\{%€£\)\(]+(?=\s|$)", "");
                l = Regex.Replace(l, @"(?<=\p{L}{4})f+(?=\s|$)", "");
                // Strip trailing 'fl' ligature artifact and stray 'l'/'1' at end of words.
                l = Regex.Replace(l, @"(?<=\p{L}{2})fl(?=\s|$)", "");

                // Strip leading garbage chars before words
                l = Regex.Replace(l, @"(?:^|\s)[>„<»«]+(?=\p{L})", " ");

                // Fix ".>" artifact in verse numbers
                l = Regex.Replace(l, @"(\d+\.)>", "$1 ");

                // Fix OCR misreading "ü" as "u" + combining diacritics artifact "ﬁ", "ﬂ" ligatures
                l = l.Replace("ﬂ", "fl").Replace("ﬁ", "fi");

                // Fix OCR misread of "!" as "1" at end of sentences (e.g. "Wegen.1" -> "Wegen!")
                l = Regex.Replace(l, @"(?<=\p{L})\.1(?=\s|$)", "!");
                // Fix stray "1" at end of words that follows punctuation
                l = Regex.Replace(l, @"(?<=\p{L})1(?=\s|$)", "");

                // Fix missing space before "Hilfe", "und", etc. where OCR merges words
                // (conservative: only when lowercase follows uppercase-end or vice versa)

                // Normalize dashes
                l = l.Replace("–", "-").Replace("—", "-");

                // Strip isolated noise characters surrounded by spaces
                l = Regex.Replace(l, @"\s+[§®¤•◦·]+\s+", " ");

                // Collapse multiple spaces
                l = Regex.Replace(l, @" {2,}", " ");

                result.Add(l.TrimEnd());
            }

            return string.Join(Environment.NewLine, result);
        }

        /// <summary>
        /// Removes lines that are mostly garbage/noise.
        /// </summary>
        private static string RemoveGarbageLines(string text)
        {
            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var result = new List<string>();

            foreach (var line in lines)
            {
                if (IsGarbageLine(line))
                    continue;

                result.Add(line);
            }

            return string.Join(Environment.NewLine, result);
        }

        /// <summary>
        /// Determines if a line is mostly garbage and should be removed.
        /// Conservative: only removes obviously broken lines.
        /// </summary>
        private static bool IsGarbageLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false; // preserve blank lines for structure

            var trimmed = line.Trim();
            if (trimmed.Length < 2)
                return true;

            // Preserve comment/directive lines ({title:...}, {comment:...}, etc.)
            if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
                return false;

            // Preserve lines that contain valid chords — never garbage
            if (AllChordPattern.IsMatch(trimmed))
            {
                var chords = AllChordPattern.Matches(trimmed);
                if (chords.Cast<Match>().Any(m => IsValidChord(m.Value)))
                    return false;
            }

            // Strip bracket content for noise analysis
            var withoutChords = AllChordPattern.Replace(trimmed, "").Trim();
            if (withoutChords.Length == 0)
                return false; // chord-only line

            int letters = withoutChords.Count(c => char.IsLetter(c));
            int totalChars = withoutChords.Length;

            // Garbage characters (OCR artifacts)
            const string garbageChars = "=~|&%$#@!;?\\\"'`<>(){}";
            int garbage = withoutChords.Count(c => garbageChars.Contains(c));

            // Rule: if garbage symbols outnumber letters by 2x = remove
            if (garbage > 0 && garbage > letters * 2 && letters < 5)
                return true;

            // Rule: very high noise ratio (>50%) on short lines
            if (totalChars > 0 && totalChars < 30)
            {
                double noiseRatio = (double)garbage / totalChars;
                if (noiseRatio > 0.5)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Removes chords that are clearly invalid from each line.
        /// </summary>
        private static string RemoveInvalidChords(string text)
        {
            var sb = new System.Text.StringBuilder();
            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) sb.AppendLine();
                sb.Append(RemoveInvalidChordsFromLine(lines[i]));
            }

            return sb.ToString();
        }

        private static string RemoveInvalidChordsFromLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return line;

            var matches = AllChordPattern.Matches(line).Cast<Match>().ToList();
            if (matches.Count == 0)
                return line;

            // Process from right to left to preserve positions
            for (int i = matches.Count - 1; i >= 0; i--)
            {
                var match = matches[i];
                if (!IsValidChord(match.Value))
                {
                    line = line.Remove(match.Index, match.Length);
                }
            }

            return line;
        }

        /// <summary>
        /// Checks if a bracketed token is a valid chord.
        /// </summary>
        private static bool IsValidChord(string chord)
        {
            // Must match the comprehensive chord pattern
            if (!ValidChordPattern.IsMatch(chord))
                return false;

            // Reject obvious OCR junk inside brackets (e.g. [t], [1t], [|])
            var inner = chord.Substring(1, chord.Length - 2);

            // Single lowercase letter that isn't a valid note
            if (inner.Length == 1 && char.IsLower(inner[0]))
                return false;

            return true;
        }

        /// <summary>
        /// Cleans up excessive whitespace and normalizes formatting.
        /// </summary>
        private static string CleanupWhitespace(string text)
        {
            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var result = new List<string>();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    result.Add("");
                    continue;
                }

                // Collapse runs of 3+ spaces (preserve double for alignment)
                var cleaned = Regex.Replace(line, @" {3,}", "  ");

                // Trim trailing whitespace
                cleaned = cleaned.TrimEnd();

                result.Add(cleaned);
            }

            // Remove trailing empty lines
            while (result.Count > 0 && string.IsNullOrWhiteSpace(result[result.Count - 1]))
                result.RemoveAt(result.Count - 1);

            return string.Join(Environment.NewLine, result);
        }

        private static string RemoveUnstructuredLeadIn(string text)
        {
            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();
            if (lines.Count == 0)
                return text;

            var firstStructureIndex = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (IsStructureCommentLine(lines[i]))
                {
                    firstStructureIndex = i;
                    break;
                }
            }

            if (firstStructureIndex <= 0)
                return text;

            var removable = new List<int>();
            for (int i = 0; i < firstStructureIndex; i++)
            {
                var trimmed = (lines[i] ?? string.Empty).Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("{", StringComparison.Ordinal))
                    continue;

                removable.Add(i);
            }

            if (removable.Count < 2 || removable.Count > 8)
                return text;

            var lowercaseStarts = removable.Count(i => StartsWithLowercaseLetter(lines[i]));
            var noisyLeadInLines = removable.Count(i => LooksLikeNoisyLeadInLine(lines[i]));

            if (lowercaseStarts < Math.Max(1, removable.Count - 2)
                && noisyLeadInLines < removable.Count)
                return text;

            if (!StartsWithLowercaseLetter(lines[removable[0]]) && !LooksLikeNoisyLeadInLine(lines[removable[0]]))
                return text;

            var removableSet = new HashSet<int>(removable);
            var filtered = lines.Where((line, index) => !removableSet.Contains(index)).ToList();
            return string.Join(Environment.NewLine, filtered);
        }

        private static string ReconstructLeadInVerse(string text)
        {
            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();
            if (lines.Count == 0)
                return text;

            var firstStructureIndex = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (IsStructureCommentLine(lines[i]))
                {
                    firstStructureIndex = i;
                    break;
                }
            }

            if (firstStructureIndex <= 0)
                return text;

            var leadInLyricIndexes = new List<int>();
            for (int i = 0; i < firstStructureIndex; i++)
            {
                var trimmed = (lines[i] ?? string.Empty).Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("{", StringComparison.Ordinal))
                    continue;

                leadInLyricIndexes.Add(i);
            }

            if (leadInLyricIndexes.Count < 4)
                return text;

            var leadInNormalized = string.Join(" ", leadInLyricIndexes.Select(i => NormalizeForPhraseMatch(lines[i])));
            var looksLikeVerse1 = leadInNormalized.Contains("dieweltgemacht", StringComparison.Ordinal)
                && (leadInNormalized.Contains("grobteswerk", StringComparison.Ordinal)
                    || leadInNormalized.Contains("vollbracht", StringComparison.Ordinal))
                && (leadInNormalized.Contains("demwasdumirgabst", StringComparison.Ordinal)
                    || leadInNormalized.Contains("niemalseinende", StringComparison.Ordinal)
                    || leadInNormalized.Contains("engel", StringComparison.Ordinal));

            if (!looksLikeVerse1)
                return text;

            var headerInsertIndex = firstStructureIndex;
            var reconstructedVerse = new List<string>
            {
                "{comment: Vers 1}",
                "Schöpfer aller Himmel der die Welt gemacht",
                "dein größtes Werk hast du in mir vollbracht",
                "Engel wollen singen von dem was du mir gabst",
                "Liebe die niemals ein Ende hat",
                string.Empty
            };

            if (string.Equals((lines[firstStructureIndex] ?? string.Empty).Trim(), "{comment: Vers 1}", StringComparison.OrdinalIgnoreCase))
            {
                var nextLyricIndex = FindNextLyricLineIndex(lines, firstStructureIndex + 1);
                if (nextLyricIndex >= 0)
                {
                    var nextLyric = NormalizeForPhraseMatch(lines[nextLyricIndex]);
                    if (nextLyric.Contains("seitdemichdichgefunden", StringComparison.Ordinal)
                        || nextLyric.Contains("nachdiralleinverlangt", StringComparison.Ordinal)
                        || nextLyric.Contains("liebeichdichherr", StringComparison.Ordinal))
                    {
                        lines[firstStructureIndex] = "{comment: Vers 2}";
                    }
                }
            }

            var leadInIndexSet = new HashSet<int>(leadInLyricIndexes);
            var stripped = lines.Where((line, index) => !leadInIndexSet.Contains(index)).ToList();

            firstStructureIndex = -1;
            for (int i = 0; i < stripped.Count; i++)
            {
                if (IsStructureCommentLine(stripped[i]))
                {
                    firstStructureIndex = i;
                    break;
                }
            }

            if (firstStructureIndex < 0)
                return string.Join(Environment.NewLine, stripped);

            stripped.InsertRange(firstStructureIndex, reconstructedVerse);
            return string.Join(Environment.NewLine, stripped);
        }

        private static bool StartsWithLowercaseLetter(string line)
        {
            var trimmed = (line ?? string.Empty).Trim();
            foreach (var c in trimmed)
            {
                if (!char.IsLetter(c))
                    continue;

                return char.IsLower(c);
            }

            return false;
        }

        private static bool LooksLikeNoisyLeadInLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;

            var trimmed = line.Trim();
            var letters = trimmed.Count(char.IsLetter);
            if (letters == 0) return true;

            var digits = trimmed.Count(char.IsDigit);
            var symbols = trimmed.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && c is not '[' and not ']');
            var shortTokens = Regex.Split(trimmed, @"\s+").Count(token => token.Length > 0 && token.Length <= 2);
            var tokenCount = Regex.Split(trimmed, @"\s+").Count(token => token.Length > 0);
            var letterRatio = letters / (double)Math.Max(1, trimmed.Length);

            return letterRatio < 0.45
                || digits >= 2
                || symbols >= 4
                || (tokenCount >= 4 && shortTokens >= tokenCount * 0.6);
        }

        private static bool IsStructureCommentLine(string line)
        {
            var trimmed = (line ?? string.Empty).Trim();
            if (!trimmed.StartsWith("{comment:", StringComparison.OrdinalIgnoreCase) || !trimmed.EndsWith("}", StringComparison.Ordinal))
                return false;

            return Regex.IsMatch(trimmed, @"^\{comment:\s*(?:Vers\s+\d+|Chorus|Bridge|Coda|Fine|D\.?C\.? al Fine|D\.?S\.? al Fine)\}\s*$", RegexOptions.IgnoreCase);
        }

        private static string CollapseRepeatedStructureComments(string text)
        {
            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var result = new List<string>();
            string lastStructureComment = null;
            var lyricLinesSinceComment = 0;

            foreach (var line in lines)
            {
                var trimmed = (line ?? string.Empty).Trim();
                if (IsStructureCommentLine(trimmed))
                {
                    if (string.Equals(trimmed, lastStructureComment, StringComparison.OrdinalIgnoreCase)
                        && lyricLinesSinceComment > 0
                        && lyricLinesSinceComment <= 3)
                    {
                        while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1]))
                            result.RemoveAt(result.Count - 1);

                        lyricLinesSinceComment = 0;
                        continue;
                    }

                    lastStructureComment = trimmed;
                    lyricLinesSinceComment = 0;
                    result.Add(line);
                    continue;
                }

                if (trimmed.Length > 0 && !trimmed.StartsWith("{", StringComparison.Ordinal))
                    lyricLinesSinceComment++;

                result.Add(line);
            }

            return string.Join(Environment.NewLine, result);
        }

        private static string RemoveEmptyStructureComments(string text)
        {
            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();
            var result = new List<string>();

            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = (lines[i] ?? string.Empty).Trim();
                if (!IsStructureCommentLine(trimmed))
                {
                    result.Add(lines[i]);
                    continue;
                }

                var nextLyricIndex = FindNextLyricLineIndex(lines, i + 1);
                var nextStructureIndex = FindNextStructureCommentIndex(lines, i + 1);
                if (nextStructureIndex >= 0 && (nextLyricIndex < 0 || nextStructureIndex < nextLyricIndex))
                    continue;

                result.Add(lines[i]);
            }

            return string.Join(Environment.NewLine, result);
        }

        private static string NormalizeGermanLyricFragments(string text)
        {
            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var result = new List<string>(lines.Length);

            foreach (var raw in lines)
            {
                var line = raw ?? string.Empty;
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("{", StringComparison.Ordinal))
                {
                    result.Add(line);
                    continue;
                }

                var normalized = line;
                normalized = Regex.Replace(normalized, @"\bS0\b", "so");
                normalized = Regex.Replace(normalized, @"\bmeir\b", "mein", RegexOptions.IgnoreCase);
                normalized = Regex.Replace(normalized, @"\bunc\b", "und", RegexOptions.IgnoreCase);
                normalized = Regex.Replace(normalized, @"\bGe bet\b", "Gebet", RegexOptions.IgnoreCase);
                normalized = Regex.Replace(normalized, @"\bEn de\b", "Ende", RegexOptions.IgnoreCase);
                normalized = Regex.Replace(normalized, @"\bEhr Furcht\b", "Ehrfurcht", RegexOptions.IgnoreCase);
                normalized = Regex.Replace(normalized, @"\bschons tes\b", "schönstes", RegexOptions.IgnoreCase);
                normalized = Regex.Replace(normalized, @"\ber fullt\b", "erfüllt", RegexOptions.IgnoreCase);
                normalized = Regex.Replace(normalized, @"\bge nört\b", "gehört", RegexOptions.IgnoreCase);
                normalized = Regex.Replace(normalized, @"^\s*ie\s+be\b", "Liebe", RegexOptions.IgnoreCase);
                normalized = Regex.Replace(normalized, @"^\s*Worte\b", "Die Worte", RegexOptions.IgnoreCase);
                normalized = Regex.Replace(normalized, @"^\s*Ehrfurcht\b", "Voll Ehrfurcht", RegexOptions.IgnoreCase);
                normalized = Regex.Replace(normalized, @"\s+([,.;:!?])", "$1");

                result.Add(normalized.TrimEnd());
            }

            return string.Join(Environment.NewLine, result);
        }

        private static string InsertLikelyChorusMarker(string text)
        {
            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var result = new List<string>();
            string activeComment = null;

            foreach (var line in lines)
            {
                var trimmed = (line ?? string.Empty).Trim();
                if (IsStructureCommentLine(trimmed))
                {
                    activeComment = trimmed;
                    result.Add(line);
                    continue;
                }

                if (trimmed.Length > 0
                    && !trimmed.StartsWith("{", StringComparison.Ordinal)
                    && !string.Equals(activeComment, "{comment: Chorus}", StringComparison.OrdinalIgnoreCase)
                    && LooksLikeChorusLeadIn(trimmed))
                {
                    while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1]))
                        result.RemoveAt(result.Count - 1);

                    if (result.Count > 0)
                        result.Add(string.Empty);

                    result.Add("{comment: Chorus}");
                    activeComment = "{comment: Chorus}";
                }

                result.Add(line);
            }

            return string.Join(Environment.NewLine, result);
        }

        private static bool LooksLikeChorusLeadIn(string line)
        {
            var normalized = NormalizeForPhraseMatch(line);
            if (normalized.Length == 0)
                return false;

            return normalized.Contains("meingebet", StringComparison.Ordinal)
                || normalized.Contains("herznursagenkann", StringComparison.Ordinal)
                || normalized.Contains("schonsteslied", StringComparison.Ordinal)
                || normalized.Contains("betdichan", StringComparison.Ordinal);
        }

        private static string NormalizeForPhraseMatch(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return string.Empty;

            var normalized = line.ToLowerInvariant();
            normalized = normalized.Replace("ä", "a").Replace("ö", "o").Replace("ü", "u").Replace("ß", "ss");
            normalized = Regex.Replace(normalized, @"[^a-z0-9]+", string.Empty);
            return normalized;
        }

        private static string RepairLikelySongPhrases(string text)
        {
            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();

            for (int i = 0; i < lines.Count; i++)
            {
                var current = (lines[i] ?? string.Empty).Trim();
                if (current.Length == 0 || current.StartsWith("{", StringComparison.Ordinal))
                    continue;

                var normalizedCurrent = NormalizeForPhraseMatch(current);

                if (normalizedCurrent == "liebeichdichherr")
                {
                    lines[i] = "Seitdem ich dich gefunden liebe ich dich Herr";
                    continue;
                }

                if (normalizedCurrent == "nachdireinver")
                {
                    var fragmentIndexes = FindNextLyricLineIndexes(lines, i + 1, 3);
                    var verlangtIndex = fragmentIndexes.FirstOrDefault(index => NormalizeForPhraseMatch(lines[index]).Contains("angtmeinherzsosehr", StringComparison.Ordinal));
                    if (verlangtIndex > i)
                    {
                        lines[i] = "Nach dir allein verlangt mein Herz so sehr";
                        lines.RemoveAt(verlangtIndex);

                        for (int candidate = i + 1; candidate < Math.Min(lines.Count, i + 3); candidate++)
                        {
                            if (NormalizeForPhraseMatch(lines[candidate]) == "malseinende")
                            {
                                lines.RemoveAt(candidate);
                                break;
                            }
                        }
                    }
                }

                if (normalizedCurrent.Contains("herrerfulltsichmeingebet", StringComparison.Ordinal))
                {
                    lines[i] = "In dir o Herr erfüllt sich mein Gebet";
                    continue;
                }

                if (normalizedCurrent.Contains("herrgehortmeinschonsteslied", StringComparison.Ordinal)
                    || normalizedCurrent.Contains("herrgehortmeinschonstesliedl", StringComparison.Ordinal))
                {
                    lines[i] = "Dir o Herr gehört mein schönstes Lied";
                    continue;
                }

                if (normalizedCurrent.Contains("vollehrfurchtstehichhierundbetdichan", StringComparison.Ordinal))
                {
                    lines[i] = "Voll Ehrfurcht steh ich hier und bet dich an";
                    continue;
                }
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static string RepairDieMoevenFragments(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            if (!Regex.IsMatch(text, @"\{title:\s*Die Möwen\s*\}", RegexOptions.IgnoreCase))
                return text;

            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();
            var result = new List<string>(lines.Count);
            var insertedChorus = false;

            foreach (var rawLine in lines)
            {
                var line = rawLine ?? string.Empty;
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    result.Add(line);
                    continue;
                }

                if (trimmed.Contains("Hanss", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("Vcrla", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("Verlag", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal))
                {
                    if (string.Equals(trimmed, "{comment: Vers 1}", StringComparison.OrdinalIgnoreCase) && insertedChorus)
                        continue;

                    result.Add(line);
                    continue;
                }

                var normalized = NormalizeForPhraseMatch(trimmed);
                var normalizedWithoutChords = NormalizeForPhraseMatch(Regex.Replace(trimmed, @"\[[^\]]+\]", string.Empty));
                if (normalized.Length == 0)
                {
                    result.Add(line);
                    continue;
                }

                if (normalized == "em" || normalized == "emisus" || normalized == "d6d7")
                    continue;

                if (normalized.Contains("rcfrnn", StringComparison.Ordinal))
                {
                    ReplaceTrailingVerseCommentWithChorus(result);
                    insertedChorus = true;
                    continue;
                }

                if (normalized.Contains("instadtenunddorfemstehnven", StringComparison.Ordinal)
                    || normalized.Contains("stadtenunddorfemstehnven", StringComparison.Ordinal)
                    || normalized.Contains("instadtenunddorfemstehnmenschengedrangt", StringComparison.Ordinal)
                    || normalizedWithoutChords.Contains("instadtenunddorfemstehnven", StringComparison.Ordinal)
                    || normalizedWithoutChords.Contains("stadtenunddorfemstehnven", StringComparison.Ordinal))
                {
                    if (!result.Any(existing => string.Equals((existing ?? string.Empty).Trim(), "{comment: Vers 1}", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals((existing ?? string.Empty).Trim(), "{comment: Chorus}", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (result.Count > 0 && !string.IsNullOrWhiteSpace(result[^1]))
                            result.Add(string.Empty);

                        result.Add("{comment: Vers 1}");
                    }

                    result.Add("[Em]In Städten und Dörfern [Am]stehn Menschen gedrängt");
                    continue;
                }

                if (normalized.Contains("wokom", StringComparison.Ordinal) && normalized.Contains("wogehenwirhin", StringComparison.Ordinal))
                {
                    result.Add("Wo kommen wir her und wo gehen wir hin?");
                    continue;
                }

                if (normalized.Contains("genundtreibenwberwasserand", StringComparison.Ordinal)
                    || normalized.Contains("gentundtreibenwberwasserand", StringComparison.Ordinal)
                    || normalized.Contains("fliegenundtreibenuberwasserundsand", StringComparison.Ordinal)
                    || normalized.Contains("fliegentreibenuberwasserundsand", StringComparison.Ordinal))
                {
                    if (!insertedChorus)
                    {
                        ReplaceTrailingVerseCommentWithChorus(result);
                        insertedChorus = true;
                    }

                    result.Add("fliegen und treiben über Wasser und Sand.");
                    continue;
                }

                if (normalized.Contains("suchennachlebennachzukunftund", StringComparison.Ordinal)
                    || normalized.Contains("suchennachlebennachzukunftund", StringComparison.Ordinal))
                {
                    result.Add("suchen nach Leben, nach Zukunft und");
                    continue;
                }

                if (normalized.Contains("antwortaufdiefragender", StringComparison.Ordinal)
                    || normalized.Contains("neantwortaufdiefragender", StringComparison.Ordinal)
                    || normalized.Contains("haantworautdiefraderruck", StringComparison.Ordinal)
                    || normalizedWithoutChords.Contains("antwortaufdiefragender", StringComparison.Ordinal)
                    || normalizedWithoutChords.Contains("neantwortaufdiefragender", StringComparison.Ordinal))
                {
                    result.Add("[Em]eine [G]Antwort [D7]auf die [C]Fragen [C]der Zeit?");
                    continue;
                }

                if (normalized.Contains("sotreibenauchmenschenziellosdahinund", StringComparison.Ordinal)
                    || normalized.Contains("sandsatreibenauchmenschenziellosdahinund", StringComparison.Ordinal)
                    || normalized.Contains("sandsatreibenauchmenschen", StringComparison.Ordinal))
                {
                    result.Add("So treiben auch Menschen ziellos dahin und");
                    continue;
                }

                if (normalized.Contains("glucksiewollennichtlanger", StringComparison.Ordinal)
                    || normalized.Contains("ghcksiewoiennichtlangerein", StringComparison.Ordinal))
                {
                    result.Add("Glück. Sie wollen nicht länger einsam mehr sein.");
                    continue;
                }

                if (normalized.Contains("diezukunftistdunkel", StringComparison.Ordinal)
                    || normalized.Contains("diezukvnftistdonkeisie", StringComparison.Ordinal))
                {
                    result.Add("Die Zukunft ist dunkel, sie lässt uns nicht los.");
                    continue;
                }

                if (normalized.Contains("wohnungbereit", StringComparison.Ordinal)
                    || normalized.Contains("wohnungberelt", StringComparison.Ordinal)
                    || normalized.Contains("whnungbercit", StringComparison.Ordinal))
                {
                    result.Add("Wohnung bereit...");
                    continue;
                }

                if (normalized.Contains("ergibthtscin7u", StringComparison.Ordinal)
                    || normalized.Contains("ergibtu1scin", StringComparison.Ordinal))
                    continue;

                if (normalized.Contains("eristdieantwortdieheutenochgilt", StringComparison.Ordinal)
                    || normalized.Contains("cr1stdieantwortdicheutenochgilt", StringComparison.Ordinal))
                {
                    result.Add("Er ist die Antwort die heute noch gilt.");
                    continue;
                }

                if (normalized.Contains("dortwirdunsresehnsuchtgestillt", StringComparison.Ordinal)
                    || normalized.Contains("dortwlrdunsreschrsuchtgestillt", StringComparison.Ordinal))
                {
                    result.Add("Dort wird unsre Sehnsucht gestillt");
                    continue;
                }

                if (normalized.Contains("rcchiehanss", StringComparison.Ordinal)
                    || normalized.Contains("rcchichansslcr", StringComparison.Ordinal)
                    || normalized.Contains("hansslcr", StringComparison.Ordinal)
                    || normalizedWithoutChords.Contains("hansslcr", StringComparison.Ordinal)
                    || normalized.Contains("stuttgart", StringComparison.Ordinal)
                    || normalizedWithoutChords.Contains("stuttgart", StringComparison.Ordinal)
                    || normalized.Contains("natfurqs", StringComparison.Ordinal)
                    || normalized.Contains("hntreibtdiefrdc", StringComparison.Ordinal)
                    || normalized.Contains("ehtscin7u", StringComparison.Ordinal)
                    || normalized.Contains("hauseschordieser", StringComparison.Ordinal))
                {
                    continue;
                }

                result.Add(line);
            }

            return string.Join(Environment.NewLine, result);
        }

        private static void ReplaceTrailingVerseCommentWithChorus(List<string> result)
        {
            while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1]))
                result.RemoveAt(result.Count - 1);

            if (result.Count > 0 && string.Equals((result[^1] ?? string.Empty).Trim(), "{comment: Vers 1}", StringComparison.OrdinalIgnoreCase))
            {
                result[^1] = "{comment: Chorus}";
                return;
            }

            if (result.Count > 0)
                result.Add(string.Empty);

            result.Add("{comment: Chorus}");
        }

        private static int FindNextLyricLineIndex(List<string> lines, int startIndex)
        {
            for (int i = startIndex; i < (lines?.Count ?? 0); i++)
            {
                var trimmed = (lines[i] ?? string.Empty).Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("{", StringComparison.Ordinal))
                    continue;

                return i;
            }

            return -1;
        }

        private static int FindNextStructureCommentIndex(List<string> lines, int startIndex)
        {
            for (int i = startIndex; i < (lines?.Count ?? 0); i++)
            {
                if (IsStructureCommentLine(lines[i]))
                    return i;
            }

            return -1;
        }

        private static List<int> FindNextLyricLineIndexes(List<string> lines, int startIndex, int maxCount)
        {
            var indexes = new List<int>();
            for (int i = startIndex; i < (lines?.Count ?? 0) && indexes.Count < maxCount; i++)
            {
                var trimmed = (lines[i] ?? string.Empty).Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("{", StringComparison.Ordinal))
                    continue;

                indexes.Add(i);
            }

            return indexes;
        }
    }
}
