using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SongConverters
{
    /// <summary>
    /// String extension methods for song converters.
    /// </summary>
    internal static class StringExtensions
    {
        /// <summary>
        /// Checks if a string is null, empty, or contains only whitespace.
        /// </summary>
        public static bool IsBlankOrWhitespace(this string str)
        {
            return string.IsNullOrWhiteSpace(str);
        }

        /// <summary>
        /// Checks if a string is blank (null or empty, but faster check).
        /// </summary>
        public static bool IsBlank(this string str)
        {
            return string.IsNullOrEmpty(str);
        }

        /// <summary>
        /// Removes diacritics (accents) from characters.
        /// Example: "Möwen" → "Mowen"
        /// </summary>
        public static string RemoveDiacritics(this string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            var normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            
            foreach (var c in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        /// <summary>
        /// Converts chord notation from English to German.
        /// B → H, Bb → B, Bm → Hm, B7 → H7, Bm/F# → Hm/F#
        /// </summary>
        public static string ToGermanNotation(this string chord)
        {
            if (chord.IsBlankOrWhitespace()) return chord;

            // Bb → B (B-flat wird zu deutschem B)
            if (chord.StartsWith("Bb", StringComparison.OrdinalIgnoreCase))
                return "B" + chord.Substring(2);

            // B → H (nur wenn es nicht Bb ist)
            if (chord.StartsWith("B", StringComparison.OrdinalIgnoreCase) && 
                (chord.Length == 1 || chord[1] != 'b'))
                return "H" + chord.Substring(1);

            // Bass-Akkorde: /B → /H, /Bb → /B
            if (chord.Contains("/"))
            {
                var parts = chord.Split('/');
                if (parts.Length == 2)
                {
                    var bass = parts[1];
                    if (bass == "B")
                        parts[1] = "H";
                    else if (bass.StartsWith("Bb"))
                        parts[1] = "B" + bass.Substring(2);
                    
                    chord = string.Join("/", parts);
                }
            }

            return chord;
        }

        /// <summary>
        /// Smooths hyphenation in lyrics.
        /// Example: "Weih - nachts" → "Weihnachts"
        /// </summary>
        public static string SmoothHyphenation(this string text)
        {
            if (text.IsBlankOrWhitespace()) return text;

            // "Weih - nachts" → "Weihnachts"
            text = Regex.Replace(text, @"(\p{L})\s*-\s*(\p{L})", "$1$2");
            
            // Soft hyphens entfernen
            text = text.Replace("‑", "").Replace("­", "");
            
            return text;
        }

        /// <summary>
        /// Escapes special ChordPro characters.
        /// </summary>
        public static string EscapeChordPro(this string text)
        {
            if (text.IsBlankOrWhitespace()) return text;

            return text.Replace("{", "\\{")
                       .Replace("}", "\\}")
                       .Replace("[", "\\[")
                       .Replace("]", "\\]");
        }

        /// <summary>
        /// Normalizes whitespace (multiple spaces to single, trim).
        /// </summary>
        public static string NormalizeWhitespace(this string text)
        {
            if (text.IsBlankOrWhitespace()) return text;

            text = Regex.Replace(text, @"\s{2,}", " ");
            text = Regex.Replace(text, @"\s+([,.;:!?])", "$1");
            
            return text.Trim();
        }
    }
}
