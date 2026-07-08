using System;
using System.Text.RegularExpressions;

namespace SongConverters
{
    /// <summary>
    /// Helper class for chord recognition and normalization.
    /// </summary>
    public static class ChordHelper
    {
        /// <summary>
        /// Regex pattern for recognizing valid chords (English and German notation).
        /// Supports: C, Cm, Cmaj7, C7, C/G, H, Hm, B, Bb, etc.
        /// </summary>
        private static readonly Regex ChordPattern = new(
            @"^[A-H](?:#|b|is|es)?(?:m|maj|min|sus|add|dim|aug)?(?:2|4|5|6|7|9|11|13)?(?:/[A-H](?:#|b|is|es)?)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Checks if a token is a valid chord.
        /// </summary>
        /// <param name="token">Token to check.</param>
        /// <returns>True if the token is a valid chord.</returns>
        public static bool IsChord(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            return ChordPattern.IsMatch(token.Trim());
        }

        /// <summary>
        /// Cleans OCR artifacts from chords.
        /// Fixes common OCR mistakes like: Bim → Bm, Blsus4 → Bsus4, etc.
        /// </summary>
        /// <param name="chord">Raw OCR chord text.</param>
        /// <returns>Cleaned chord or empty string if invalid.</returns>
        public static string CleanChord(string chord)
        {
            if (string.IsNullOrWhiteSpace(chord)) return string.Empty;

            var cleaned = Regex.Replace(chord.Trim(), @"[^A-Za-z0-9#b/]+", "");
            if (cleaned.Length == 0) return string.Empty;

            // Common OCR artifact: stray 'l' after root (e.g. "Blsus4" → "Bsus4")
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])l(?=[A-Za-z])", "$1", RegexOptions.IgnoreCase);

            // Common OCR confusions for minor/maj7
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])im$", "$1m", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])i?m$", "$1m", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])ma7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])mqj7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])maij7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])mai7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])i7$", "$17", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])i([0-9]+)$", "$1$2", RegexOptions.IgnoreCase);

            // Single-letter chords: lowercase → minor (e → Em), uppercase → major (E → E)
            if (cleaned.Length == 1)
            {
                var c = cleaned[0];
                if (c is >= 'a' and <= 'h')
                    return char.ToUpperInvariant(c) + "m";
                if (c is >= 'A' and <= 'H')
                    return c.ToString();
            }

            // Normalize root letter casing
            cleaned = char.ToUpperInvariant(cleaned[0]) + 
                      (cleaned.Length > 1 ? cleaned.Substring(1) : string.Empty);

            // Remove stray 'h' after root (Ah → A)
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
        /// Converts chord to German notation (H notation).
        /// B → H, Bb → B, Bm → Hm, B7 → H7, Bm/F# → Hm/F#
        /// </summary>
        /// <param name="chord">Chord in English notation.</param>
        /// <returns>Chord in German notation.</returns>
        public static string ToGermanNotation(string chord)
        {
            return chord.ToGermanNotation(); // Use extension method
        }

        /// <summary>
        /// Extracts the root note from a chord.
        /// Example: "Cmaj7" → "C", "Am/G" → "A"
        /// </summary>
        /// <param name="chord">Chord to extract root from.</param>
        /// <returns>Root note or null if not found.</returns>
        public static string GetRoot(string chord)
        {
            if (string.IsNullOrWhiteSpace(chord)) return null;

            var match = Regex.Match(chord, @"^([A-H](?:#|b|is|es)?)");
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Checks if a chord is minor.
        /// </summary>
        /// <param name="chord">Chord to check.</param>
        /// <returns>True if the chord is minor.</returns>
        public static bool IsMinor(string chord)
        {
            if (string.IsNullOrWhiteSpace(chord)) return false;

            // Check for 'm' but not 'maj'
            return Regex.IsMatch(chord, @"\bm(?!aj)\b", RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Checks if a chord is major.
        /// </summary>
        /// <param name="chord">Chord to check.</param>
        /// <returns>True if the chord is major.</returns>
        public static bool IsMajor(string chord)
        {
            if (string.IsNullOrWhiteSpace(chord)) return false;

            // Explicit major or no modifier (C = Cmaj)
            return Regex.IsMatch(chord, @"maj", RegexOptions.IgnoreCase) || 
                   !IsMinor(chord);
        }

        /// <summary>
        /// Normalizes chord sequence for musical plausibility.
        /// Corrects obvious OCR errors based on context.
        /// </summary>
        /// <param name="chords">Sequence of chords.</param>
        /// <returns>Normalized chord sequence.</returns>
        public static string[] NormalizeSequence(string[] chords)
        {
            if (chords == null || chords.Length == 0) return chords;

            var normalized = new string[chords.Length];
            Array.Copy(chords, normalized, chords.Length);

            for (int i = 0; i < normalized.Length; i++)
            {
                var chord = normalized[i];

                // If chord is single letter and neighbors are minor, assume minor
                if (chord.Length == 1 && i > 0 && i < normalized.Length - 1)
                {
                    var prev = normalized[i - 1];
                    var next = normalized[i + 1];

                    if (IsMinor(prev) && IsMinor(next))
                    {
                        normalized[i] = chord + "m";
                    }
                }
            }

            return normalized;
        }
    }
}
