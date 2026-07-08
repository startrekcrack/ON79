using System;
using System.Collections.Generic;
using System.Text;

namespace SongConverters
{
    /// <summary>
    /// Helper class for building ChordPro format output.
    /// </summary>
    public static class ChordProHelper
    {
        /// <summary>
        /// Builds a ChordPro header with title, subtitle, key, and metadata.
        /// </summary>
        /// <param name="title">Song title (required).</param>
        /// <param name="subtitle">Optional subtitle.</param>
        /// <param name="key">Optional key signature.</param>
        /// <param name="metadata">Optional metadata dictionary (key=value pairs).</param>
        /// <param name="comment">Optional comment line.</param>
        /// <returns>Formatted ChordPro header.</returns>
        public static string BuildHeader(
            string title,
            string subtitle = null,
            string key = null,
            IDictionary<string, string> metadata = null,
            string comment = null)
        {
            var sb = new StringBuilder();

            // Title is required
            if (string.IsNullOrWhiteSpace(title))
                title = "Unbekannter Titel";

            sb.AppendLine($"{{title: {title}}}");

            if (!string.IsNullOrWhiteSpace(subtitle))
                sb.AppendLine($"{{subtitle: {subtitle}}}");

            if (!string.IsNullOrWhiteSpace(key))
                sb.AppendLine($"{{key: {key}}}");

            // Add custom metadata
            if (metadata != null)
            {
                foreach (var (metaKey, metaValue) in metadata)
                {
                    if (!string.IsNullOrWhiteSpace(metaValue))
                        sb.AppendLine($"{{meta: {metaKey}={metaValue}}}");
                }
            }

            if (!string.IsNullOrWhiteSpace(comment))
                sb.AppendLine($"{{comment: {comment}}}");

            sb.AppendLine(); // Blank line after header

            return sb.ToString();
        }

        /// <summary>
        /// Formats a section marker (Verse, Chorus, Bridge, etc.).
        /// </summary>
        /// <param name="sectionType">Type of section (verse, chorus, bridge, etc.).</param>
        /// <param name="sectionNumber">Optional section number (e.g., verse 1, verse 2).</param>
        /// <returns>Formatted ChordPro section marker.</returns>
        public static string FormatSection(string sectionType, int? sectionNumber = null)
        {
            if (string.IsNullOrWhiteSpace(sectionType))
                return string.Empty;

            var normalized = NormalizeSectionType(sectionType);
            var label = sectionNumber.HasValue ? $"{normalized} {sectionNumber}" : normalized;

            return $"{{comment: {label}}}";
        }

        /// <summary>
        /// Inserts chords inline into lyrics.
        /// Example: InsertChords("Die Möwen fliegen", [(0, "Em"), (11, "G")]) 
        ///          → "[Em]Die Möwen [G]fliegen"
        /// </summary>
        /// <param name="lyrics">Lyric line.</param>
        /// <param name="chords">List of (position, chord) tuples.</param>
        /// <returns>Lyric line with inline chords.</returns>
        public static string InsertChordsInline(string lyrics, IEnumerable<(int position, string chord)> chords)
        {
            if (string.IsNullOrEmpty(lyrics)) return lyrics;
            if (chords == null) return lyrics;

            var sb = new StringBuilder(lyrics);
            
            // Sort by position descending (insert from right to left to keep indices stable)
            var sortedChords = new List<(int position, string chord)>(chords);
            sortedChords.Sort((a, b) => b.position.CompareTo(a.position));

            foreach (var (position, chord) in sortedChords)
            {
                if (position >= 0 && position <= sb.Length && !string.IsNullOrWhiteSpace(chord))
                {
                    sb.Insert(position, $"[{chord}]");
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Normalizes section type names.
        /// </summary>
        /// <param name="sectionType">Raw section type.</param>
        /// <returns>Normalized section type.</returns>
        private static string NormalizeSectionType(string sectionType)
        {
            var lower = sectionType.ToLowerInvariant().Trim();

            return lower switch
            {
                "refrain" or "refr" or "refr." or "chorus" => "Chorus",
                "vers" or "verse" or "strophe" => "Vers",
                "bridge" => "Bridge",
                "intro" => "Intro",
                "outro" or "outtro" => "Outro",
                "coda" => "Coda",
                "pre-chorus" or "pre_chorus" or "prechorus" => "Pre-Chorus",
                "instrumental" => "Instrumental",
                "solo" => "Solo",
                "interlude" or "zwischenspiel" => "Interlude",
                _ => char.ToUpperInvariant(lower[0]) + lower.Substring(1)
            };
        }

        /// <summary>
        /// Formats a chord-only line (no lyrics, just chords with spacing).
        /// Example: FormatChordLine(["C", "Am", "F", "G"]) → "[C]  [Am]  [F]  [G]"
        /// </summary>
        /// <param name="chords">List of chords.</param>
        /// <param name="spacing">Number of spaces between chords.</param>
        /// <returns>Formatted chord line.</returns>
        public static string FormatChordLine(IEnumerable<string> chords, int spacing = 2)
        {
            if (chords == null) return string.Empty;

            var sb = new StringBuilder();
            var separator = new string(' ', spacing);

            foreach (var chord in chords)
            {
                if (!string.IsNullOrWhiteSpace(chord))
                {
                    if (sb.Length > 0)
                        sb.Append(separator);
                    sb.Append($"[{chord}]");
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Escapes special ChordPro characters in text.
        /// </summary>
        /// <param name="text">Text to escape.</param>
        /// <returns>Escaped text.</returns>
        public static string Escape(string text)
        {
            return text.EscapeChordPro(); // Use extension method
        }

        /// <summary>
        /// Adds a blank line if the last line is not blank.
        /// </summary>
        /// <param name="sb">StringBuilder to append to.</param>
        public static void EnsureBlankLine(StringBuilder sb)
        {
            if (sb.Length == 0) return;

            var lastLine = sb.ToString().Split('\n')[^1].Trim();
            if (!string.IsNullOrWhiteSpace(lastLine))
                sb.AppendLine();
        }
    }
}
