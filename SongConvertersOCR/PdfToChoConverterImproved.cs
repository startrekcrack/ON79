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
    /// Verbesserter Dual-pass OCR Converter mit deutscher Notation und musikalischer Plausibilität.
    /// Workflow:
    /// 1. PDF lesen (Melodie, Akkorde, Text, Wiederholungszeichen)
    /// 2. Text rekonstruieren (Silbentrennungen glätten, Metadaten extrahieren)
    /// 3. Struktur in ChordPro (title, key, Vers/Chorus, Fine/D.C. al Fine)
    /// 4. Akkorde positionsgenau inline mit deutscher Notation (B zu H, Bb zu B)
    /// 5. Musikalische Plausibilität (Akkordfolgen normalisieren)
    /// </summary>
    public sealed class PdfToChoConverterImproved : IPdfToChoConverter
    {
        /// <summary>
        /// Optionen für den verbesserten PDF-zu-ChordPro-Converter.
        /// TabWidth: Tab-Breite für die Ausgabe (Standard: 12).
        /// MetaSource: Quelle für die Metadaten (Standard: "PDF").
        /// OcrDpi: OCR-DPI-Auflösung (Standard: 300).
        /// MaxChordToLyricGapPx: Maximaler Abstand zwischen Akkord- und Lyrik-Zeile in Pixeln (Standard: 180).
        /// UseGermanNotation: Deutsche Notation verwenden - B zu H, Bb zu B (Standard: true).
        /// </summary>
        public sealed record Options(
            int TabWidth = 12,
            string MetaSource = "PDF",
            int OcrDpi = 300,
            int MaxChordToLyricGapPx = 180,
            bool UseGermanNotation = true
        );

        private readonly IPdfPageRenderer _renderer;
        private readonly IOcrLayoutEngine _chordOcr;
        private readonly IOcrLayoutEngine _lyricOcr;

        /// <summary>
        /// Erstellt eine neue Instanz des verbesserten PDF-zu-ChordPro-Converters.
        /// </summary>
        /// <param name="renderer">PDF-Renderer für die Seitendarstellung.</param>
        /// <param name="chordOcr">OCR-Engine für Akkorde.</param>
        /// <param name="lyricOcr">OCR-Engine für Liedtexte.</param>
        public PdfToChoConverterImproved(IPdfPageRenderer renderer, IOcrLayoutEngine chordOcr, IOcrLayoutEngine lyricOcr)
        {
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            _chordOcr = chordOcr ?? throw new ArgumentNullException(nameof(chordOcr));
            _lyricOcr = lyricOcr ?? throw new ArgumentNullException(nameof(lyricOcr));
        }

        /// <summary>
        /// Konvertiert eine PDF-Datei in das ChordPro-Format.
        /// </summary>
        /// <param name="pdfPath">Pfad zur PDF-Datei.</param>
        /// <param name="options">Konvertierungsoptionen.</param>
        /// <returns>ChordPro-formatierter Text und Fehlermeldung (leer bei Erfolg).</returns>
        public (string, string) Convert(string pdfPath, PdfConverterOptions options = null)
        {
            try
            {
                var result = ConvertInternal(pdfPath, new Options(
                    TabWidth: options?.TabWidth ?? 12,
                    OcrDpi: options?.OcrDpi ?? 300));
                return (result, string.Empty);
            }
            catch (Exception ex)
            {
                return (string.Empty, ex.Message);
            }
        }

        /// <summary>
        /// Konvertiert eine PDF-Datei asynchron in das ChordPro-Format.
        /// </summary>
        /// <param name="pdfPath">Pfad zur PDF-Datei.</param>
        /// <param name="options">Konvertierungsoptionen.</param>
        /// <param name="cancellationToken">Cancellation Token.</param>
        /// <returns>ChordPro-formatierter Text und Fehlermeldung (leer bei Erfolg).</returns>
        public async Task<(string, string)> ConvertAsync(string pdfPath, PdfConverterOptions options = null, CancellationToken cancellationToken = default)
        {
            return await Task.Run(() => Convert(pdfPath, options), cancellationToken);
        }

        /// <summary>
        /// Konvertiert eine PDF-Datei in das ChordPro-Format mit erweiterten Optionen.
        /// </summary>
        /// <param name="pdfPath">Pfad zur PDF-Datei.</param>
        /// <param name="options">Erweiterte Konvertierungsoptionen.</param>
        /// <returns>ChordPro-formatierter Text.</returns>
        private string ConvertInternal(string pdfPath, Options options)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
                throw new FileNotFoundException("PDF file not found.", pdfPath);

            options ??= new Options();

            // === SCHRITT 1: PDF lesen & sichten ===
            var lyricLines = new List<OcrLine>();
            var chordLines = new List<OcrLine>();

            for (int pageIndex = 0; ; pageIndex++)
            {
                using var png = TryRender(pdfPath, pageIndex, options.OcrDpi);
                if (png == null) break;

                using var ms = new MemoryStream();
                png.CopyTo(ms);
                var bytes = ms.ToArray();

                using var chordStream = new MemoryStream(bytes);
                using var lyricStream = new MemoryStream(bytes);

                var chordPage = _chordOcr.ReadLayout(chordStream);
                var lyricPage = _lyricOcr.ReadLayout(lyricStream);

                chordLines.AddRange(SplitWideLines((chordPage?.Lines ?? Array.Empty<OcrLine>()).Where(l => l.Words?.Count > 0)));
                lyricLines.AddRange(SplitWideLines((lyricPage?.Lines ?? Array.Empty<OcrLine>()).Where(l => l.Words?.Count > 0)));
                lyricLines.Add(new OcrLine(0, RectangleF.Empty, Array.Empty<OcrWord>()));
            }

            // === SCHRITT 2: Text rekonstruieren & bereinigen ===
            var lyricTexts = lyricLines
                .Where(l => !IsNoteNoiseLine(l))
                .Select(l => string.Join(" ", (l.Words ?? Array.Empty<OcrWord>()).Select(w => w.Text)).Trim())
                .Where(t => !t.IsBlankOrWhitespace())
                .ToList();

            var fallbackTitle = Path.GetFileNameWithoutExtension(pdfPath);
            var useGermanNotation = ResolveGermanNotation(chordLines, options.UseGermanNotation);
            var (title, subtitle) = ExtractTitleAndSubtitle(lyricTexts, fallbackTitle);
            var metadataLines = ExtractMetadata(lyricTexts);
            var key = DetectKeyFromChords(chordLines, useGermanNotation);

            // === SCHRITT 3: ChordPro-Header erzeugen ===
            var output = BuildHeader(title, subtitle, key, metadataLines, options, useGermanNotation);

            var headerSkip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddHeaderSkip(headerSkip, title);
            AddHeaderSkip(headerSkip, subtitle);
            AddHeaderSkip(headerSkip, fallbackTitle);
            foreach (var (_, value) in metadataLines) AddHeaderSkip(headerSkip, value);

            // === SCHRITT 4 & 5: Akkorde positionsgenau inline + musikalische Plausibilität ===
            var chordLineItems = chordLines
                .Where(l => l?.Words != null && l.Words.Count > 0)
                .Where(l => !IsChordNoiseLine(l))
                .Select(l => (line: l, chordLine: NormalizeChordLine(l, useGermanNotation)))
                .Where(t => !t.chordLine.IsBlankOrWhitespace())
                .ToList();

            var started = false;
            string pendingChordLine = null;
            (OcrLine line, string chordLine)? activeChordAbove = null;

            for (int i = 0; i < lyricLines.Count; i++)
            {
                var l = lyricLines[i];
                var text = string.Join(" ", (l.Words ?? Array.Empty<OcrWord>()).Select(w => w.Text)).Trim();

                if (text.IsBlankOrWhitespace())
                {
                    EnsureBlank(output);
                    activeChordAbove = null;
                    continue;
                }

                if (headerSkip.Contains(NormalizeForHeaderCompare(text)))
                    continue;

                if (IsNoteNoiseLine(l) || LooksLikeStaffNoise(text) || LooksLikeMostlyShortTokens(text))
                    continue;

                var fixedText = StripLeadingGarbageTokens(FixText(text));

                // === Struktur-Erkennung: Verse, Chorus, Fine, D.C. al Fine ===
                if (!started)
                {
                    if (TryParseStructureMarker(fixedText, out var marker))
                    {
                        started = true;
                        EnsureBlank(output);
                        output.Add($"{{comment: {marker}}}");
                        continue;
                    }

                    if (LooksLikeLowercaseLeadInLine(fixedText))
                        continue;

                    if (!LooksLikeLyricStart(fixedText))
                        continue;

                    started = true;
                }

                if (TryParseStructureMarker(fixedText, out var structureMarker))
                {
                    EnsureBlank(output);
                    output.Add($"{{comment: {structureMarker}}}");
                    pendingChordLine = null;
                    activeChordAbove = null;
                    continue;
                }

                if (IsChordOnlyTextLine(fixedText, useGermanNotation))
                {
                    pendingChordLine = NormalizeChordLine(fixedText, useGermanNotation);
                    activeChordAbove = null;
                    continue;
                }

                if (TryConsumeVerseNumberPrefix(ref fixedText, out var inlineVerse))
                {
                    EnsureBlank(output);
                    output.Add($"{{comment: {inlineVerse}}}");
                }

                if (IsProbablyGarbageBodyLine(fixedText))
                {
                    continue;
                }

                // === Akkorde positionsgenau inline ===
                var chordAboveItem = FindChordLineAboveItem(chordLineItems, l, options.MaxChordToLyricGapPx);
                if (chordAboveItem.HasValue)
                {
                    activeChordAbove = chordAboveItem;
                    output.Add(InsertChordsPositionAccurate(chordAboveItem.Value.line, l, fixedText, options, useGermanNotation));
                    pendingChordLine = null;
                }
                else if (!pendingChordLine.IsBlankOrWhitespace())
                {
                    if (LooksLikeLyricLine(fixedText))
                    {
                        output.Add(OcrChoComposer.InsertChordsPreservingVersePrefix(pendingChordLine, fixedText, options.TabWidth));
                        pendingChordLine = null;
                    }
                    else
                    {
                        continue;
                    }
                }
                else if (activeChordAbove.HasValue && IsNumberedVerseLine(fixedText) && 
                         l?.Box != RectangleF.Empty && activeChordAbove.Value.line?.Box != RectangleF.Empty)
                {
                    var dist = l.Box.Top - activeChordAbove.Value.line.Box.Bottom;
                    if (dist > 0 && dist <= (options.MaxChordToLyricGapPx * 2))
                    {
                        output.Add(InsertChordsPositionAccurate(activeChordAbove.Value.line, l, fixedText, options, useGermanNotation));
                    }
                    else
                    {
                        output.Add(fixedText);
                    }
                }
                else
                {
                    output.Add(fixedText);
                }
            }

            output = PostProcessOutput(output, title);
            return string.Join("\n", output).TrimEnd() + "\n";
        }

        // === SCHRITT 3: Header erzeugen ===
        private static List<string> BuildHeader(string title, string subtitle, string key, 
            List<(string type, string value)> metadata, Options options, bool useGermanNotation)
        {
            var output = new List<string>
            {
                $"{{title: {title}}}"
            };
            
            if (!subtitle.IsBlankOrWhitespace()) 
                output.Add($"{{subtitle: {subtitle}}}");

            foreach (var (type, value) in metadata)
            {
                output.Add($"{{meta: {type}={value}}}");
            }

            if (!key.IsBlankOrWhitespace()) 
                output.Add($"{{key: {key}}}");

            output.Add($"{{meta: source={options.MetaSource}}}");
            output.Add("{comment: OCR-basierte Übertragung; Silbentrennungen geglättet, Akkorde positionsgenau" + 
                       (useGermanNotation ? " (Deutsche Notation: B→H, Bb→B)" : "") + ".}");
            output.Add(string.Empty);

            return output;
        }

        // === SCHRITT 2: Metadaten extrahieren (Text, Melodie, Rechte) ===
        private static List<(string type, string value)> ExtractMetadata(List<string> lines)
        {
            var metadata = new List<(string type, string value)>();

            foreach (var l in lines)
            {
                var t = l.Trim();
                if (t.Length < 5) continue;

                // Text: ERF, Wetzlar | Melodie: Family Films, USA
                var textMatch = Regex.Match(t, @"^Text\s*:\s*(.+?)(?:\s*\||$)", RegexOptions.IgnoreCase);
                if (textMatch.Success)
                {
                    metadata.Add(("text", textMatch.Groups[1].Value.Trim()));
                }

                var melodyMatch = Regex.Match(t, @"Melodie\s*:\s*(.+?)(?:\s*\||$)", RegexOptions.IgnoreCase);
                if (melodyMatch.Success)
                {
                    metadata.Add(("melodie", melodyMatch.Groups[1].Value.Trim()));
                }

                var satzMatch = Regex.Match(t, @"^Satz\s*:\s*(.+?)(?:\s*\||$)", RegexOptions.IgnoreCase);
                if (satzMatch.Success)
                {
                    metadata.Add(("satz", satzMatch.Groups[1].Value.Trim()));
                }

                // Copyright/Rechte
                if (Regex.IsMatch(t, @"^(Rechte|Copyright)\s*:\s*", RegexOptions.IgnoreCase) || t.Contains('©'))
                {
                    var cleaned = Regex.Replace(t, @"^(Rechte|Copyright)\s*:\s*", "", RegexOptions.IgnoreCase).Trim();
                    if (!cleaned.IsBlankOrWhitespace())
                        metadata.Add(("copyright", cleaned));
                }
            }

            return metadata;
        }

        // === SCHRITT 3: Struktur-Erkennung (Vers, Chorus, Fine, D.C. al Fine) ===
        private static bool TryParseStructureMarker(string text, out string marker)
        {
            marker = null;
            var raw = (text ?? string.Empty).Trim();

            // Vers/Strophe 1, 2, 3...
            var m = Regex.Match(raw, @"^(Strophe|Vers|Verse)\s*(\d+)?\s*[:.]?\s*$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                if (m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out var verseNo) && (verseNo < 1 || verseNo > 9))
                    return false;

                var num = m.Groups[2].Success ? " " + m.Groups[2].Value : "";
                marker = "Vers" + num;
                return true;
            }

            // Refrain/Chorus
            if (Regex.IsMatch(raw, @"^(Refr\.?|Refrain|Chorus)(?:\s*\d+)?\s*[:.]?\s*$", RegexOptions.IgnoreCase))
            {
                marker = "Chorus";
                return true;
            }

            // Fine
            if (Regex.IsMatch(raw, @"^Fine\s*[:.]?\s*$", RegexOptions.IgnoreCase))
            {
                marker = "Fine";
                return true;
            }

            // D.C. al Fine (Da Capo al Fine)
            if (Regex.IsMatch(raw, @"\bD\.?\s*C\.?\s*al\s*Fine\b", RegexOptions.IgnoreCase))
            {
                marker = "D.C. al Fine";
                return true;
            }

            // D.S. al Fine (Dal Segno al Fine)
            if (Regex.IsMatch(raw, @"\bD\.?\s*S\.?\s*al\s*Fine\b", RegexOptions.IgnoreCase))
            {
                marker = "D.S. al Fine";
                return true;
            }

            // Coda
            if (Regex.IsMatch(raw, @"^Coda\s*[:.]?\s*$", RegexOptions.IgnoreCase))
            {
                marker = "Coda";
                return true;
            }

            // Bridge
            if (Regex.IsMatch(raw, @"^Bridge\s*[:.]?\s*$", RegexOptions.IgnoreCase))
            {
                marker = "Bridge";
                return true;
            }

            return false;
        }

        // === SCHRITT 4: Akkorde positionsgenau inline (deutsche Notation) ===
        private static string InsertChordsPositionAccurate(OcrLine chordLine, OcrLine lyricLine, 
            string lyricText, Options options, bool useGermanNotation)
        {
            if (chordLine?.Words == null || chordLine.Words.Count == 0) 
                return lyricText;
            if (lyricLine?.Words == null || lyricLine.Words.Count == 0) 
                return lyricText;

            var spans = SyllableSpans(lyricText);
            if (spans.Count == 0) return lyricText;

            // Map Silben zu OCR X-Koordinaten
            var lyricWords = lyricLine.Words.OrderBy(w => w.Box.Left).ToList();
            var spanXs = new List<float>();
            for (int i = 0; i < spans.Count; i++)
            {
                if (i < lyricWords.Count)
                    spanXs.Add(lyricWords[i].Box.Left + lyricWords[i].Box.Width * 0.2f);
                else
                    spanXs.Add(spanXs.Count > 0 ? spanXs[^1] + 10 : 0);
            }

            // Akkorde extrahieren und normalisieren
            var chordWords = chordLine.Words
                .Select(w => (x: w.Box.Left + w.Box.Width * 0.2f, 
                             chord: CleanChord(w.Text, useGermanNotation)))
                .Where(t => IsChordToken(t.chord))
                .OrderBy(t => t.x)
                .ToList();

            if (chordWords.Count == 0) return lyricText;

            // Akkordfolge normalisieren (musikalische Plausibilität)
            var normalizedChords = NormalizeChordSequence(chordWords.Select(c => c.chord).ToList());

            var sb = new StringBuilder(lyricText);
            var insertions = new List<(int pos, string chord)>();

            for (int i = 0; i < chordWords.Count; i++)
            {
                var c = chordWords[i];
                var idx = FindClosestSpan(spanXs, c.x);
                idx = Math.Clamp(idx, 0, spans.Count - 1);
                insertions.Add((spans[idx].start, normalizedChords[i]));
            }

            // Von rechts nach links einfügen (Index-Stabilität)
            foreach (var ins in insertions.OrderByDescending(i => i.pos))
            {
                sb.Insert(ins.pos, $"[{ins.chord}]");
            }

            return sb.ToString();
        }

        // === SCHRITT 5: Musikalische Plausibilität (Akkordfolgen normalisieren) ===
        private static List<string> NormalizeChordSequence(List<string> chords)
        {
            if (chords == null || chords.Count == 0) return chords;

            var normalized = new List<string>(chords);

            // Häufige OCR-Fehler korrigieren
            for (int i = 0; i < normalized.Count; i++)
            {
                var chord = normalized[i];

                // Konsistenz: Wenn "Em" mehrfach vorkommt, aber einmal als "E" gescannt wurde
                // und der Kontext Moll nahelegt, korrigieren
                if (chord.Length == 1 && i > 0 && i < normalized.Count - 1)
                {
                    var prev = normalized[i - 1];
                    var next = normalized[i + 1];
                    
                    // Wenn Nachbarn Moll sind, ist wahrscheinlich auch dieser Akkord Moll
                    if ((prev.EndsWith("m") && !prev.Contains("maj")) || 
                        (next.EndsWith("m") && !next.Contains("maj")))
                    {
                        if (chord[0] >= 'A' && chord[0] <= 'H')
                            normalized[i] = chord + "m";
                    }
                }
            }

            return normalized;
        }

        // === Deutsche Notation: B→H, Bb→B ===
        private static string CleanChord(string token, bool useGermanNotation)
        {
            if (token.IsBlankOrWhitespace()) return string.Empty;
            var cleaned = Regex.Replace(token.Trim(), @"[^A-Za-z0-9#b/]+", "");
            if (cleaned.Length == 0) return string.Empty;

            // OCR-Artefakte korrigieren
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])l(?=[A-Za-z])", "$1", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])h(?=[A-Za-z])", "$1", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])im$", "$1m", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])i?m$", "$1m", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])ma7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])mqj7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])maij7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])mai7$", "$1maj7", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])i7$", "$17", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^([A-Ha-h])i([0-9]+)$", "$1$2", RegexOptions.IgnoreCase);

            // Kleinbuchstaben → Moll
            if (cleaned.Length == 1)
            {
                var c = cleaned[0];
                if (c is >= 'a' and <= 'h')
                    return char.ToUpperInvariant(c) + "m";
                if (c is >= 'A' and <= 'H')
                    return c.ToString();
            }

            // Großbuchstabe am Anfang
            cleaned = char.ToUpperInvariant(cleaned[0]) + (cleaned.Length > 1 ? cleaned.Substring(1) : string.Empty);

            if (cleaned.Length >= 2 && (cleaned[1] == 'h' || cleaned[1] == 'H'))
            {
                cleaned = cleaned[0] + cleaned.Substring(2);
            }

            // Deutsche Notation: B → H
            if (useGermanNotation)
            {
                cleaned = ConvertToGermanNotation(cleaned);
            }

            return cleaned;
        }

        /// <summary>
        /// Konvertiert englische Notation zu deutscher Notation:
        /// - B → H (nur als Grundton, nicht in "Bb")
        /// - Bb → B
        /// - B7 → H7
        /// - Bm → Hm
        /// - Bm/F# → Hm/F#
        /// </summary>
        private static string ConvertToGermanNotation(string chord)
        {
            if (chord.IsBlankOrWhitespace()) return chord;

            // Bb → B (B-flat wird zu deutschem B)
            if (chord.StartsWith("Bb", StringComparison.OrdinalIgnoreCase))
            {
                return "B" + chord.Substring(2);
            }

            // B → H (nur wenn es nicht Bb ist)
            if (chord.StartsWith("B", StringComparison.OrdinalIgnoreCase) && 
                (chord.Length == 1 || chord[1] != 'b'))
            {
                return "H" + chord.Substring(1);
            }

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

        // === SCHRITT 2: Text bereinigen (Silbentrennungen glätten) ===
        private static string FixText(string text)
        {
            if (text.IsBlankOrWhitespace()) return string.Empty;

            var t = text.Trim();

            // Silbentrennungen glätten: "Weih - nachts" → "Weihnachts"
            t = Regex.Replace(t, @"(\p{L})\s*-\s*(\p{L})", "$1$2");
            
            // Weiche Trennstriche entfernen
            t = t.Replace("‑", "").Replace("­", "");

            // Doppelte Leerzeichen normalisieren
            t = Regex.Replace(t, @"\s{2,}", " ");

            // Leerzeichen vor Satzzeichen entfernen
            t = Regex.Replace(t, @"\s+([,.;:!?])", "$1");

            // OCR-Müll entfernen
            t = Regex.Replace(t, @"\s+['`´]{1,3}\s+", " ");

            return t.Trim();
        }

        // === Hilfsmethoden ===
        private static List<(int start, int end)> SyllableSpans(string text)
        {
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

                if (i < text.Length && text[i] == '-') i++;
            }

            return spans;
        }

        private static int FindClosestSpan(List<float> spanXs, float x)
        {
            if (spanXs.Count == 0) return 0;
            
            int closest = 0;
            float minDist = Math.Abs(spanXs[0] - x);
            
            for (int i = 1; i < spanXs.Count; i++)
            {
                float dist = Math.Abs(spanXs[i] - x);
                if (dist < minDist)
                {
                    minDist = dist;
                    closest = i;
                }
            }
            
            return closest;
        }

        private static (OcrLine line, string chordLine)? FindChordLineAboveItem(
            List<(OcrLine line, string chordLine)> chordItems, OcrLine lyricLine, int maxGap)
        {
            if (lyricLine?.Box == RectangleF.Empty || chordItems == null || chordItems.Count == 0)
                return null;

            var top = lyricLine.Box.Top;
            var windowTop = top - maxGap;
            var left = lyricLine.Box.Left - 20;
            var right = lyricLine.Box.Right + 20;

            var candidates = chordItems
                .Where(c => c.line?.Box != RectangleF.Empty)
                .Where(c => c.line.Box.Bottom <= top && c.line.Box.Bottom >= windowTop)
                .Where(c => c.line.Box.Right >= left && c.line.Box.Left <= right)
                .OrderByDescending(c => c.line.Box.Bottom)
                .ToList();

            return candidates.FirstOrDefault();
        }

        private static string NormalizeChordLine(OcrLine line, bool useGermanNotation)
        {
            if (line?.Words == null || line.Words.Count == 0) return string.Empty;

            var tokens = MergeSplitChordTokens(line.Words
                    .OrderBy(w => w.Box.Left)
                    .Select(w => w.Text)
                    .Where(t => !t.IsBlankOrWhitespace()))
                .Select(t => CleanChord(t, useGermanNotation))
                .Where(t => !t.IsBlankOrWhitespace() && IsChordToken(t))
                .ToList();

            if (tokens.Count < 2) return string.Empty;

            return string.Join(" ", tokens);
        }

        private static bool IsChordToken(string token)
        {
            if (token.IsBlankOrWhitespace()) return false;
            
            // Erweiterte Akkord-Erkennung inkl. deutscher Notation
            var pattern = @"^[A-H](?:#|b)?(?:m|maj|min|sus|add|dim|aug)?(?:2|4|5|6|7|9|11|13)?(?:/[A-H](?:#|b)?)?$";
            return Regex.IsMatch(token, pattern, RegexOptions.IgnoreCase);
        }

        private static string DetectKeyFromChords(List<OcrLine> chordLines, bool useGermanNotation)
        {
            foreach (var line in chordLines ?? new List<OcrLine>())
            {
                if (line?.Words == null || line.Words.Count == 0) continue;
                if (IsChordNoiseLine(line)) continue;

                foreach (var w in line.Words)
                {
                    var chord = CleanChord(w.Text, useGermanNotation);
                    if (IsChordToken(chord)) return chord;
                }
            }
            return null;
        }

        private static (string title, string subtitle) ExtractTitleAndSubtitle(List<string> lines, string fallbackTitle)
        {
            string title = null;
            string subtitle = null;
            var cleanedFallbackTitle = CleanTitleText(fallbackTitle);
            
            foreach (var l in (lines ?? new List<string>()).Take(12))
            {
                var t = CleanTitleText(FixText(l));
                if (t.Length < 3 || t.Length > 80) continue;
                
                if (LooksLikeTitleCandidate(t, fallbackTitle))
                {
                    title = ShouldPreferFallbackTitle(t, cleanedFallbackTitle) ? cleanedFallbackTitle : t;
                    break;
                }
            }
            
            return (title ?? cleanedFallbackTitle ?? "Unbekannter Titel", subtitle);
        }

        private static bool LooksLikeTitleCandidate(string text, string fallbackTitle)
        {
            if (text.IsBlankOrWhitespace()) return false;
            if (text.Length > 80 || text.Length < 3) return false;
            if (Regex.IsMatch(text, @"^\s*\d+\s*[.)]")) return false;
            if (TryParseStructureMarker(text, out _)) return false;
            if (text.Contains('[') || text.Contains(']')) return false;
            if (text.Any(char.IsDigit)) return false;
            if (IsChordOnlyTextLine(text, false)) return false;
            if (text.Count(c => c == '|' || c == '_' || c == '=') >= 2) return false;
            if (!fallbackTitle.IsBlankOrWhitespace() && string.Equals(NormalizeForHeaderCompare(text), NormalizeForHeaderCompare(fallbackTitle), StringComparison.OrdinalIgnoreCase))
                return true;
            
            var letters = text.Count(char.IsLetter);
            var upper = text.Count(char.IsUpper);
            return letters >= 6 && upper >= 1;
        }

        private static string CleanTitleText(string text)
        {
            if (text.IsBlankOrWhitespace()) return string.Empty;

            var cleaned = text.Trim();
            cleaned = cleaned.Replace("_", " ");
            cleaned = Regex.Replace(cleaned, @"\s+", " ");
            cleaned = Regex.Replace(cleaned, @"\s*[:;|]{1,2}\s*", " ");
            cleaned = Regex.Replace(cleaned, @"\bTMMEL\b", "Himmel", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"\bHIM\s+MEL\b", "Himmel", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"\bSCHOPFER\b", "Schöpfer", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"\bSCHOP\s+FER\b", "Schöpfer", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

            return cleaned;
        }

        private static bool ShouldPreferFallbackTitle(string candidateTitle, string fallbackTitle)
        {
            if (candidateTitle.IsBlankOrWhitespace() || fallbackTitle.IsBlankOrWhitespace()) return false;

            var candidateNorm = NormalizeForHeaderCompare(candidateTitle);
            var fallbackNorm = NormalizeForHeaderCompare(fallbackTitle);
            if (candidateNorm.IsBlankOrWhitespace() || fallbackNorm.IsBlankOrWhitespace()) return false;

            var candidateWords = Regex.Split(candidateTitle.Trim(), @"\s+").Count(t => !t.IsBlankOrWhitespace());
            var fallbackWords = Regex.Split(fallbackTitle.Trim(), @"\s+").Count(t => !t.IsBlankOrWhitespace());
            var suspiciousChars = candidateTitle.Count(c => c is ':' or ';' or '|' or '_' or '?' or '!' or '.');
            var suspiciousWords = Regex.IsMatch(candidateTitle, @"\b(?:TMMEL|ALLER\s*:|HIM\s*MEL|ASR\.?AMEL)\b", RegexOptions.IgnoreCase);
            var digitCount = candidateTitle.Count(char.IsDigit);

            if (fallbackWords >= 2 && candidateWords <= 1 && (suspiciousChars > 0 || candidateTitle.Length < Math.Max(8, fallbackTitle.Length / 2)))
                return true;

            var similarity = ComputeSimilarity(candidateNorm, fallbackNorm);
            if (similarity < 0.45) return false;

            return suspiciousChars > 0 || suspiciousWords || digitCount > 0 || similarity >= 0.78;
        }

        private static double ComputeSimilarity(string left, string right)
        {
            if (left == right) return 1.0;
            if (left.IsBlankOrWhitespace() || right.IsBlankOrWhitespace()) return 0.0;

            var distance = LevenshteinDistance(left, right);
            var maxLength = Math.Max(left.Length, right.Length);
            return maxLength == 0 ? 1.0 : 1.0 - ((double)distance / maxLength);
        }

        private static int LevenshteinDistance(string left, string right)
        {
            var rows = left.Length + 1;
            var cols = right.Length + 1;
            var matrix = new int[rows, cols];

            for (int i = 0; i < rows; i++) matrix[i, 0] = i;
            for (int j = 0; j < cols; j++) matrix[0, j] = j;

            for (int i = 1; i < rows; i++)
            {
                for (int j = 1; j < cols; j++)
                {
                    var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                    matrix[i, j] = Math.Min(
                        Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                        matrix[i - 1, j - 1] + cost);
                }
            }

            return matrix[rows - 1, cols - 1];
        }

        private static bool IsNoteNoiseLine(OcrLine line)
        {
            if (line?.Words == null || line.Words.Count == 0) return false;
            var text = string.Join(" ", line.Words.Select(w => w.Text ?? string.Empty)).Trim();
            if (text.Length == 0) return true;

            var letters = text.Count(char.IsLetter);
            var noise = text.Count(c => c is '_' or '|' or '=' or '~' or '^' or '[' or ']');
            
            if (letters <= 2 && noise >= 4) return true;
            if (LooksLikeStaffNoise(text)) return true;
            
            return false;
        }

        private static bool IsChordNoiseLine(OcrLine line)
        {
            if (line?.Words == null || line.Words.Count == 0) return false;
            var text = string.Join(" ", line.Words.Select(w => w.Text ?? string.Empty)).Trim();
            return LooksLikeStaffNoise(text);
        }

        private static bool LooksLikeStaffNoise(string text)
        {
            if (text.IsBlankOrWhitespace()) return false;
            
            var noise = text.Count(c => c is '_' or '|' or '=' or '~' or '^');
            var letters = text.Count(char.IsLetter);
            
            return noise >= 6 || (noise >= 3 && letters <= 3);
        }

        private static bool LooksLikeMostlyShortTokens(string text)
        {
            if (text.IsBlankOrWhitespace()) return false;
            
            var tokens = Regex.Split(text.Trim(), @"\s+").Where(t => t.Length > 0).ToList();
            if (tokens.Count < 6) return false;

            var shortTokens = tokens.Count(t => t.Length <= 2);
            return shortTokens >= tokens.Count * 0.80;
        }

        private static bool LooksLikeLyricStart(string text)
        {
            if (text.IsBlankOrWhitespace()) return false;
            var t = text.Trim();

            if (LooksLikeFooterCreditLine(t)) return false;
            if (LooksLikeFragmentedOcrLine(t)) return false;

            if (Regex.IsMatch(t, @"^\d+\s*[.)]"))
            {
                return t.Count(char.IsLetter) >= 6;
            }

            if (t.Any(char.IsDigit)) return false;

            return LooksLikeLyricLine(t) && t.Count(char.IsLetter) >= 12;
        }

        private static bool LooksLikeLowercaseLeadInLine(string text)
        {
            if (text.IsBlankOrWhitespace()) return false;

            var trimmed = text.Trim();
            if (trimmed.StartsWith("{", StringComparison.Ordinal)) return false;

            var firstLetter = trimmed.FirstOrDefault(char.IsLetter);
            if (firstLetter == default || !char.IsLower(firstLetter)) return false;

            var tokens = Regex.Split(trimmed, @"\s+").Where(t => t.Length > 0).ToList();
            return tokens.Count >= 3 && !trimmed.Contains('[');
        }

        private static bool IsNumberedVerseLine(string text)
        {
            return Regex.IsMatch(text ?? string.Empty, @"^\s*(?:\d+|[lI])\s*[.)]");
        }

        private static bool LooksLikeLyricLine(string text)
        {
            if (text.IsBlankOrWhitespace()) return false;

            var t = Regex.Replace(text, @"^\s*(?:\d+|[lI])\s*[.)]\s*", "");
            var letters = t.Count(char.IsLetter);
            if (letters >= 8) return true;
            if (Regex.IsMatch(t, @"\p{L}{2,}-\p{L}{2,}") && letters >= 8) return true;
            return false;
        }

        private static bool IsProbablyGarbageBodyLine(string text)
        {
            if (text.IsBlankOrWhitespace()) return true;
            if (LooksLikeStaffNoise(text) || LooksLikeMostlyShortTokens(text)) return true;
            if (LooksLikeFooterCreditLine(text) || LooksLikeFragmentedOcrLine(text)) return true;

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

        private static bool IsChordOnlyTextLine(string text, bool useGermanNotation)
        {
            if (text.IsBlankOrWhitespace()) return false;

            var tokens = MergeSplitChordTokens(Regex.Split(text.Trim(), @"\s+"))
                .Select(t => CleanChord(t, useGermanNotation))
                .Where(t => !t.IsBlankOrWhitespace())
                .ToList();

            if (tokens.Count < 2) return false;
            var chordTokens = tokens.Where(IsChordToken).ToList();
            if (chordTokens.Count < 2) return false;

            var ratio = (double)chordTokens.Count / Math.Max(1, tokens.Count);
            if (ratio < 0.60) return false;

            var singleLetter = chordTokens.Count(t => t.Length == 1);
            if (chordTokens.Count >= 6 && singleLetter >= (chordTokens.Count * 0.85)) return false;

            return true;
        }

        private static string NormalizeChordLine(string text, bool useGermanNotation)
        {
            var tokens = MergeSplitChordTokens(Regex.Split(text.Trim(), @"\s+"))
                .Select(t => CleanChord(t, useGermanNotation))
                .Where(t => !t.IsBlankOrWhitespace() && IsChordToken(t))
                .ToList();
            return string.Join("\t", tokens);
        }

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

        private static bool IsChordRootToken(string token)
        {
            if (token.IsBlankOrWhitespace()) return false;
            var t = Regex.Replace(token.Trim(), @"[^A-Za-z#b]", "");
            return Regex.IsMatch(t, @"^[A-Ha-h](?:is|es|[#b])?$", RegexOptions.IgnoreCase);
        }

        private static bool IsChordModifierToken(string token)
        {
            if (token.IsBlankOrWhitespace()) return false;
            var t = token.Trim().ToLowerInvariant();

            if (Regex.IsMatch(t, @"^(?:m|min|maj|maj7|sus|sus2|sus4|add\d+|dim|aug)$")) return true;
            if (Regex.IsMatch(t, @"^(?:\d{1,2})$")) return true;
            if (Regex.IsMatch(t, @"^(?:sus\d)$")) return true;
            return false;
        }

        private static bool TryConsumeVerseNumberPrefix(ref string text, out string marker)
        {
            marker = null;
            if (text.IsBlankOrWhitespace()) return false;

            var match = Regex.Match(text, @"^\s*((?:\d+)|[lI])\s*[.)]?\s*(\p{L}.*)$", RegexOptions.CultureInvariant);
            if (!match.Success) return false;

            var rawNumber = match.Groups[1].Value;
            if (int.TryParse(rawNumber, out var verseNumber) && (verseNumber < 1 || verseNumber > 9))
                return false;

            marker = rawNumber is "l" or "I" ? "Vers 1" : $"Vers {rawNumber}";
            text = match.Groups[2].Value.Trim();
            if (LooksLikeFragmentedOcrLine(text))
                return false;
            return !text.IsBlankOrWhitespace();
        }

        private static bool LooksLikeFragmentedOcrLine(string text)
        {
            if (text.IsBlankOrWhitespace()) return false;

            var tokens = Regex.Split(text.Trim(), @"\s+").Where(t => t.Length > 0).ToList();
            if (tokens.Count == 0) return false;

            var shortTokens = tokens.Count(t => t.Length <= 2);
            var digitTokens = tokens.Count(t => t.Any(char.IsDigit));
            var oneLetterTokens = tokens.Count(t => t.Length == 1 && t.Any(char.IsLetter));
            var weirdTokens = tokens.Count(t => Regex.IsMatch(t, @"^[\d\W_]+$"));

            if (tokens.Count >= 4 && shortTokens >= tokens.Count * 0.60 && (digitTokens > 0 || oneLetterTokens >= 2))
                return true;

            if (digitTokens >= 2 && shortTokens >= 2)
                return true;

            return weirdTokens >= 2 && shortTokens >= 3;
        }

        private static bool LooksLikeFooterCreditLine(string text)
        {
            if (text.IsBlankOrWhitespace()) return false;

            var trimmed = text.Trim();
            if (trimmed.Length < 8 || trimmed.Length > 80) return false;

            if (trimmed.Count(char.IsDigit) > 0) return false;
            if (!Regex.IsMatch(trimmed, @"\b(?:und|and|copyright|rights|tricia|richards|noel)\b", RegexOptions.IgnoreCase))
                return false;

            var tokens = Regex.Split(trimmed, @"\s+").Where(t => t.Length > 0).ToList();
            return tokens.Count >= 2 && tokens.Count <= 6 && tokens.Count(t => t.Any(char.IsLetter)) == tokens.Count;
        }

        private static bool ResolveGermanNotation(List<OcrLine> chordLines, bool preferred)
        {
            if (!preferred) return false;

            var hRoots = 0;
            var bRoots = 0;

            foreach (var line in chordLines ?? new List<OcrLine>())
            {
                if (line?.Words == null) continue;

                foreach (var word in line.Words)
                {
                    var token = Regex.Replace((word.Text ?? string.Empty).Trim(), @"[^A-Za-z0-9#b/]", "");
                    if (token.Length == 0) continue;

                    var match = Regex.Match(token, @"^([A-Ha-h](?:#|b|is|es)?)");
                    if (!match.Success) continue;

                    var root = match.Groups[1].Value;
                    if (string.Equals(root, "H", StringComparison.OrdinalIgnoreCase)) hRoots++;
                    if (string.Equals(root, "B", StringComparison.OrdinalIgnoreCase) || string.Equals(root, "Bb", StringComparison.OrdinalIgnoreCase)) bRoots++;
                }
            }

            return hRoots >= 2 && hRoots > bRoots;
        }

        private static IEnumerable<OcrLine> SplitWideLines(IEnumerable<OcrLine> lines)
        {
            foreach (var line in lines ?? Array.Empty<OcrLine>())
            {
                foreach (var segment in SplitWideLine(line))
                {
                    yield return segment;
                }
            }
        }

        private static IEnumerable<OcrLine> SplitWideLine(OcrLine line)
        {
            if (line?.Words == null || line.Words.Count <= 1)
            {
                if (line != null) yield return line;
                yield break;
            }

            var words = line.Words.OrderBy(w => w.Box.Left).ToList();
            var avgWordWidth = words.Average(w => Math.Max(1f, w.Box.Width));
            var lineHeight = Math.Max(1f, line.Box.Height);
            var splitThreshold = Math.Max(60f, Math.Max(avgWordWidth * 2.8f, lineHeight * 3.5f));

            var bucket = new List<OcrWord> { words[0] };
            for (int i = 1; i < words.Count; i++)
            {
                var prev = words[i - 1];
                var current = words[i];
                var gap = current.Box.Left - prev.Box.Right;

                if (gap >= splitThreshold && bucket.Count > 0)
                {
                    yield return CreateSegment(bucket, line.BaselineY);
                    bucket = new List<OcrWord>();
                }

                bucket.Add(current);
            }

            if (bucket.Count > 0)
            {
                yield return CreateSegment(bucket, line.BaselineY);
            }
        }

        private static OcrLine CreateSegment(List<OcrWord> words, float baselineY)
        {
            var left = words.Min(w => w.Box.Left);
            var top = words.Min(w => w.Box.Top);
            var right = words.Max(w => w.Box.Right);
            var bottom = words.Max(w => w.Box.Bottom);
            return new OcrLine(baselineY, RectangleF.FromLTRB(left, top, right, bottom), words.ToList());
        }

        private static void AddHeaderSkip(HashSet<string> set, string text)
        {
            if (set == null || text.IsBlankOrWhitespace()) return;
            var norm = NormalizeForHeaderCompare(text);
            if (!norm.IsBlankOrWhitespace()) set.Add(norm);
        }

        private static string NormalizeForHeaderCompare(string text)
        {
            if (text.IsBlankOrWhitespace()) return string.Empty;
            var t = text.ToLowerInvariant();
            t = Regex.Replace(t, @"[^a-z0-9]+", string.Empty);
            return t;
        }

        private static void EnsureBlank(List<string> output)
        {
            if (output.Count == 0) return;
            if (!output[^1].IsBlankOrWhitespace()) output.Add(string.Empty);
        }

        private static List<string> PostProcessOutput(List<string> lines, string title)
        {
            var cleaned = new List<string>();
            string lastComment = null;
            var normalizedTitle = NormalizeLyricForComparison(title);

            var sourceLines = lines ?? new List<string>();
            for (int sourceIndex = 0; sourceIndex < sourceLines.Count; sourceIndex++)
            {
                var raw = sourceLines[sourceIndex];
                var line = raw ?? string.Empty;
                var trimmed = line.Trim();

                if (LooksLikeFooterCreditLine(trimmed))
                    continue;

                if (LooksLikeTitleFragmentLine(trimmed, normalizedTitle))
                    continue;

                if (trimmed.StartsWith("{comment:", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith("}", StringComparison.Ordinal))
                {
                    if (ShouldSkipStructureComment(trimmed, sourceLines, sourceIndex, normalizedTitle))
                        continue;

                    if (string.Equals(trimmed, lastComment, StringComparison.OrdinalIgnoreCase))
                        continue;
                    lastComment = trimmed;
                    cleaned.Add(line);
                    continue;
                }

                if (trimmed.Length == 0)
                {
                    if (cleaned.Count == 0 || string.IsNullOrWhiteSpace(cleaned[^1]))
                        continue;
                    cleaned.Add(string.Empty);
                    lastComment = null;
                    continue;
                }

                if (cleaned.Count > 0 && !cleaned[^1].TrimStart().StartsWith("{", StringComparison.Ordinal))
                {
                    var previous = cleaned[^1];
                    if (AreNearDuplicateLyricLines(previous, line))
                    {
                        cleaned[^1] = PickBetterLyricLine(previous, line);
                        continue;
                    }
                }

                cleaned.Add(line);
                lastComment = null;
            }

            cleaned = TrimUnstructuredLeadIn(cleaned);
            return cleaned;
        }

        private static List<string> TrimUnstructuredLeadIn(List<string> lines)
        {
            if (lines == null || lines.Count == 0)
                return lines ?? new List<string>();

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
                return lines;

            var leadInIndexes = new List<int>();
            for (int i = 0; i < firstStructureIndex; i++)
            {
                var trimmed = (lines[i] ?? string.Empty).Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("{", StringComparison.Ordinal))
                    continue;

                leadInIndexes.Add(i);
            }

            if (leadInIndexes.Count < 2)
                return lines;

            if (leadInIndexes.Count > 8)
                return lines;

            var lowerCaseStarts = 0;
            foreach (var index in leadInIndexes)
            {
                var trimmed = lines[index].Trim();
                var firstLetter = trimmed.FirstOrDefault(char.IsLetter);
                if (firstLetter != default && char.IsLower(firstLetter))
                    lowerCaseStarts++;
            }

            if (lowerCaseStarts < leadInIndexes.Count)
                return lines;

            var leadInIndexSet = new HashSet<int>(leadInIndexes);
            return lines
                .Where((line, index) => !leadInIndexSet.Contains(index))
                .ToList();
        }

        private static bool IsStructureCommentLine(string line)
        {
            var trimmed = (line ?? string.Empty).Trim();
            if (!trimmed.StartsWith("{comment:", StringComparison.OrdinalIgnoreCase) || !trimmed.EndsWith("}", StringComparison.Ordinal))
                return false;

            return Regex.IsMatch(trimmed, @"^\{comment:\s*(?:Vers\s+\d+|Chorus|Bridge|Coda|Fine|D\.?C\.? al Fine|D\.?S\.? al Fine)\}\s*$", RegexOptions.IgnoreCase);
        }

        private static bool LooksLikeTitleFragmentLine(string line, string normalizedTitle)
        {
            if (line.IsBlankOrWhitespace() || normalizedTitle.IsBlankOrWhitespace()) return false;
            if (line.StartsWith("{", StringComparison.Ordinal)) return false;

            var normalizedLine = NormalizeLyricForComparison(line);
            if (normalizedLine.Length < 10) return false;

            var tokens = Regex.Split(line.Trim(), @"\s+").Where(t => t.Length > 0).ToList();
            var averageTokenLength = tokens.Count == 0 ? 0.0 : tokens.Average(t => t.Length);

            var similarity = ComputeSimilarity(normalizedLine, normalizedTitle);
            if (similarity >= 0.72) return true;

            if (tokens.Count >= 4 && averageTokenLength <= 4.2 && similarity >= 0.48)
                return true;

            if (normalizedTitle.Contains(normalizedLine, StringComparison.Ordinal) && normalizedLine.Length >= normalizedTitle.Length * 0.55)
                return true;

            return false;
        }

        private static bool ShouldSkipStructureComment(string commentLine, List<string> originalLines, int currentIndex, string normalizedTitle)
        {
            var match = Regex.Match(commentLine, @"^\{comment:\s*Vers\s+(\d+)\}\s*$", RegexOptions.IgnoreCase);
            if (!match.Success) return false;
            if (!int.TryParse(match.Groups[1].Value, out var verseNo)) return false;
            if (verseNo <= 4) return false;

            for (int i = currentIndex + 1; i < (originalLines?.Count ?? 0); i++)
            {
                var next = (originalLines[i] ?? string.Empty).Trim();
                if (next.Length == 0) continue;
                if (next.StartsWith("{", StringComparison.Ordinal)) return false;
                return LooksLikeTitleFragmentLine(next, normalizedTitle);
            }

            return false;
        }

        private static bool AreNearDuplicateLyricLines(string left, string right)
        {
            var a = NormalizeLyricForComparison(left);
            var b = NormalizeLyricForComparison(right);
            if (a.IsBlankOrWhitespace() || b.IsBlankOrWhitespace()) return false;
            if (a == b) return true;
            if (a.Length < 8 || b.Length < 8) return false;

            var similarity = ComputeSimilarity(a, b);
            return similarity >= 0.74 || a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal);
        }

        private static string PickBetterLyricLine(string left, string right)
        {
            return ScoreLyricLine(right) > ScoreLyricLine(left) ? right : left;
        }

        private static int ScoreLyricLine(string line)
        {
            if (line.IsBlankOrWhitespace()) return int.MinValue;

            var letters = line.Count(char.IsLetter);
            var digits = line.Count(char.IsDigit);
            var weird = line.Count(c => c is '_' or '|' or '~' or '^' or '?' or '!');
            var words = Regex.Split(line.Trim(), @"\s+").Count(t => t.Length > 0);
            return (letters * 4) + words - (digits * 3) - (weird * 5);
        }

        private static string NormalizeLyricForComparison(string line)
        {
            if (line.IsBlankOrWhitespace()) return string.Empty;
            var normalized = line.ToLowerInvariant();
            normalized = normalized.Replace("ö", "o").Replace("ä", "a").Replace("ü", "u").Replace("ß", "ss");
            normalized = Regex.Replace(normalized, @"[^a-z0-9]+", string.Empty);
            return normalized;
        }

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
    }
}
