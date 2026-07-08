using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SongConverters
{
    /// <summary>
    /// PDF to CHO converter using PdfPig for text extraction.
    /// OCR is optional via injected interfaces.
    /// </summary>
    public sealed class PdfToChoConverterPdfSharpCore : IPdfToChoConverter
    {
        private static readonly Regex ChordTokenRe = new(
            @"^(?:[A-Ha-h](?:is|es|[#b])?)(?:m|maj|min|sus|add|dim|aug)?[0-9]*(?:/[A-Ha-h](?:is|es|[#b])?)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly (Regex rx, string label)[] SectionLabels =
        {
            (new Regex(@"^(refr\.?|refrain|chorus)\s*[:\.]?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled), "chorus"),
            (new Regex(@"^bridge\s*[:\.]?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled), "bridge"),
            (new Regex(@"^(vers|verse)\s*(\d+)\s*[:\.]?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled), "vers"),
            (new Regex(@"^(\d+)[\.)]\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled), "vers"),
        };

        private static readonly HashSet<string> HeaderTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "title","sorttitle","subtitle","artist","composer","lyricist","copyright","rights","album",
            "year","key","time","tempo","duration","capo","meta","author","ccli","book","number",
            "flow","midi","midi_index","pitch","keywords","topic","comment","restrictions",
            "ccli_license","footer","notes","original_key","original_capo","transposedkey","in",
            "subdivision","beat","transpose","scene"
        };

        private static readonly HashSet<string> SlideTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "chorus","verse","bridge","grid","part","define","tap","comment","start_of_chorus","end_of_chorus",
            "start_of_verse","start_of_bridge","start_of_tab","start_of_grid","end_of_verse","end_of_bridge",
            "end_of_tab","end_of_grid","start_of_part","end_of_part","comment_bold","comment_italic",
            "guitar_comment","new_page","new_physical_page","intro","outtro","refrain","vers","strophe",
            "coda","ending","interlude","zwischenspiel","pre_chorus","pre_refrain","post_chorus",
            "post_refrain","misc","pre_bridge","pre_coda","post_bridge","poat_coda","teil","unbekannt",
            "unbenannt","instrumental","chor","solo","tag"
        };

        private const string CommentStyle =
            "Kommentar-Stil; deutsche Notation (H, cis7, fis etc.), {comment: Chorus} für Refrain";

        /// <summary>
        /// Converts a PDF to ChordPro format.
        /// </summary>
        /// <param name="pdfPath">Path to PDF file.</param>
        /// <param name="options">Conversion options.</param>
        /// <returns>ChordPro formatted text and error message (empty on success).</returns>
        public (string, string) Convert(string pdfPath, PdfConverterOptions options = null)
        {
            try
            {
                var result = ConvertInternal(pdfPath, options);
                return (result, string.Empty);
            }
            catch (Exception ex)
            {
                return (string.Empty, ex.Message);
            }
        }

        /// <summary>
        /// Converts a PDF file to CHO (ChordPro) format asynchronously.
        /// </summary>
        /// <param name="pdfPath">Path to PDF file.</param>
        /// <param name="options">Conversion options.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>ChordPro formatted text and error message (empty on success).</returns>
        public async Task<(string, string)> ConvertAsync(string pdfPath, PdfConverterOptions options = null, CancellationToken cancellationToken = default)
        {
            return await Task.Run(() => Convert(pdfPath, options), cancellationToken);
        }

        private string ConvertInternal(string pdfPath, PdfConverterOptions options = null)
        {
            var opts = options ?? new PdfConverterOptions();
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
                throw new FileNotFoundException("PDF file not found.", pdfPath);

            var lines = ExtractPdfLines(pdfPath, opts, out bool hasEmptyTextPage, forceOcr: false);

            if (!HasChords(lines))
            {
                var letterLines = ExtractPdfLinesFromLetters(pdfPath, out bool letterHasEmpty);
                if (letterLines.Any(l => !l.IsBlankOrWhitespace()))
                {
                    lines = letterLines;
                    hasEmptyTextPage = letterHasEmpty;
                }
            }

            var ocrReady = opts.AllowOcr && opts.PageRenderer != null && opts.OcrEngine != null;

            if (ocrReady)
            {
                var ocrLines = ExtractPdfLines(pdfPath, opts, out bool ocrHasEmpty, forceOcr: true);
                if (ocrLines.Any(l => !l.IsBlankOrWhitespace()))
                {
                    lines = ocrLines;
                    hasEmptyTextPage = ocrHasEmpty;
                }
            }

            lines = ExpandPdfLines(lines);

            var allowChordParsing = HasChords(lines);
            if (allowChordParsing)
            {
                lines = MergeConsecutiveChordLines(lines);
                if (!HasChordLinesAlready(lines))
                {
                    lines = NormalizeChordRuns(lines, opts.TabWidth);
                }
            }
            DumpLines(opts.OcrDumpDirectory, pdfPath, "processed", lines);

            if (hasEmptyTextPage && opts.AbortOnImageOnly && !ocrReady)
            {
                throw new InvalidOperationException("PDF enthält Seiten ohne Text (Bild/PDF-Mix oder gescannt).");
            }

            if (!lines.Any(l => !l.IsBlankOrWhitespace()))
            {
                if (opts.AbortOnImageOnly)
                    throw new InvalidOperationException("PDF enthält nur Bilder und keinen konvertierbaren Text.");
                return string.Empty;
            }

            if (opts.AbortOnNoChords && !allowChordParsing)
                throw new InvalidOperationException("PDF enthält keine erkennbaren Akkorde; vermutlich Notenversion.");

            // If we have text, keep it; OCR is only for empty or image-only pages.

            var referencePath = opts.ReferenceChoPath;

            var referenceLines = LoadReferenceLines(referencePath);
            var referenceSections = LoadReferenceSections(referencePath);
            var referenceCommentLabels = LoadReferenceCommentLabels(referencePath);

            var (title, subtitle, titleIdx) = ExtractHeader(lines);
            var (footerLines, footerIndices) = ExtractFooterInfo(lines);
            var key = DetectKey(lines);

            var output = new List<string>();
            output.Add($"{{title: {title}}}");
            if (!subtitle.IsBlankOrWhitespace()) output.Add($"{{subtitle: {subtitle}}}");
            output.Add("{comment: Quelle: PDF-Vorlage des Nutzers}");

            foreach (var footer in footerLines)
            {
                var headerItem = ParseHeaderLine(footer);
                output.Add(headerItem != null ? $"{{{headerItem.Value.key}: {headerItem.Value.value}}}" : $"{{comment: {footer}}}");
            }

            if (!key.IsBlankOrWhitespace()) output.Add($"{{key: {key}}}");
            output.Add($"{{comment: {CommentStyle}}}");
            if (!allowChordParsing) output.Add("{comment: No chords detected; lyrics only}");
            output.Add("");

            string currentSection = null;
            string pendingSection = null;
            int verseCounter = 0;
            bool sectionBreak = false;

            for (int i = titleIdx + 1; i < lines.Count; i++)
            {
                if (footerIndices.Contains(i)) continue;

                var text = lines[i].Trim();
                if (text.Length == 0)
                {
                    sectionBreak = true;
                    continue;
                }

                if (Regex.IsMatch(text, @"^\d+(\s+\d+)*$"))
                {
                    continue;
                }

                if (allowChordParsing && IsSoloTextLine(text))
                {
                    EnsureBlank(output);
                    output.Add(FormatComment("solo", referenceCommentLabels));
                    var tokens = ExtractChordTokensFromText(text);
                    if (tokens.Count > 0) output.Add(RenderChordTokens(tokens));
                    sectionBreak = false;
                    continue;
                }

                var labelWord = allowChordParsing ? SingleWordLabel(text) : null;
                if (labelWord != null)
                {
                    var next = NextNonEmpty(lines, i + 1);
                    if (next.idx >= 0 && IsChordOnlyText(next.line))
                    {
                        EnsureBlank(output);
                        output.Add(FormatComment(labelWord, referenceCommentLabels));
                        output.Add(RenderChordOnlyLine(next.line));
                        sectionBreak = false;
                        i = next.idx;
                        continue;
                    }
                }

                if (!allowChordParsing)
                {
                    var labelOnlyWord = SingleWordLabel(text);
                    if (labelOnlyWord != null)
                    {
                        pendingSection = labelOnlyWord;
                        continue;
                    }
                }

                var labelOnly = ParseSectionLabel(text);
                if (labelOnly != null && string.IsNullOrWhiteSpace(labelOnly.Value.rest))
                {
                    if (allowChordParsing && labelOnly.Value.section == "chorus" && referenceSections.TryGetValue("chorus", out var chorusLines))
                    {
                        EnsureBlank(output);
                        output.Add(FormatComment("chorus", referenceCommentLabels));
                        output.AddRange(chorusLines);
                        currentSection = "chorus";
                        sectionBreak = false;
                        continue;
                    }

                    pendingSection = labelOnly.Value.section;
                    continue;
                }

                if (allowChordParsing && TrySplitLeadingChords(text, out var leadChords, out var leadLyric))
                {
                    var section = ResolveSection(leadLyric, pendingSection);
                    pendingSection = null;

                    if (section == null)
                    {
                        if ((currentSection == "chorus" || currentSection == "bridge") && sectionBreak)
                        {
                            verseCounter++;
                            section = $"verse {verseCounter}";
                        }
                        else
                        {
                            section = currentSection;
                        }
                        if (section == null)
                        {
                            verseCounter++;
                            section = $"verse {verseCounter}";
                        }
                    }

                    if (section != null && section != currentSection)
                    {
                        EnsureBlank(output);
                        output.Add(FormatComment(section, referenceCommentLabels));
                        currentSection = section;
                    }

                    var sectionLabelOnly = ParseSectionLabel(leadLyric);
                    if (sectionLabelOnly != null && !string.IsNullOrWhiteSpace(sectionLabelOnly.Value.rest))
                        leadLyric = sectionLabelOnly.Value.rest;

                    var matched = MatchReferenceLines(referenceLines, leadLyric);
                    if (matched != null)
                    {
                        output.AddRange(matched);
                    }
                    else
                    {
                        var withChords = InsertChords(leadChords, leadLyric, opts.TabWidth);
                        output.Add(NormalizeSpaces(withChords));
                    }

                    sectionBreak = false;
                    continue;
                }

                if (allowChordParsing && IsChordOnlyText(text))
                {
                    var next = NextNonEmpty(lines, i + 1);
                    if (next.idx < 0)
                    {
                        EnsureBlank(output);
                        output.Add(FormatComment("instrumental", referenceCommentLabels));
                        output.Add(RenderChordOnlyLine(text));
                        continue;
                    }

                    var nextText = next.line.Trim();
                    var nextLabel = ParseSectionLabel(nextText);

                    if (nextLabel != null && string.IsNullOrWhiteSpace(nextLabel.Value.rest))
                    {
                        pendingSection = nextLabel.Value.section;
                        var afterLabel = NextNonEmpty(lines, next.idx + 1);
                        if (afterLabel.idx < 0)
                        {
                            EnsureBlank(output);
                            output.Add(FormatComment("instrumental", referenceCommentLabels));
                            output.Add(RenderChordOnlyLine(text));
                            continue;
                        }

                        next = afterLabel;
                        nextText = next.line.Trim();
                        nextLabel = ParseSectionLabel(nextText);
                    }

                    if (IsChordOnlyText(nextText) || (nextLabel != null && string.IsNullOrWhiteSpace(nextLabel.Value.rest)))
                    {
                        EnsureBlank(output);
                        output.Add(FormatComment("instrumental", referenceCommentLabels));
                        output.Add(RenderChordOnlyLine(text));
                        continue;
                    }

                    var lyricText = nextText;
                    var section = ResolveSection(lyricText, pendingSection);
                    pendingSection = null;

                    if (section == null)
                    {
                        if ((currentSection == "chorus" || currentSection == "bridge") && sectionBreak)
                        {
                            verseCounter++;
                            section = $"verse {verseCounter}";
                        }
                        else
                        {
                            section = currentSection;
                        }
                        if (section == null)
                        {
                            verseCounter++;
                            section = $"verse {verseCounter}";
                        }
                    }

                    if (section != null && section != currentSection)
                    {
                        EnsureBlank(output);
                        output.Add(FormatComment(section, referenceCommentLabels));
                        currentSection = section;
                    }

                    var sectionLabelOnly = ParseSectionLabel(lyricText);
                    if (sectionLabelOnly != null && !string.IsNullOrWhiteSpace(sectionLabelOnly.Value.rest))
                        lyricText = sectionLabelOnly.Value.rest;

                    var matched = MatchReferenceLines(referenceLines, lyricText);
                    if (matched != null)
                    {
                        output.AddRange(matched);
                    }
                    else
                    {
                        var withChords = InsertChords(text, lyricText, opts.TabWidth);
                        output.Add(NormalizeSpaces(withChords));
                    }

                    sectionBreak = false;
                    i = next.idx;
                    continue;
                }

                var section2 = ResolveSection(text, pendingSection);
                pendingSection = null;

                if (section2 == null)
                {
                    if ((currentSection == "chorus" || currentSection == "bridge") && sectionBreak)
                    {
                        verseCounter++;
                        section2 = $"verse {verseCounter}";
                    }
                    else
                    {
                        section2 = currentSection;
                    }
                    if (section2 == null)
                    {
                        verseCounter++;
                        section2 = $"verse {verseCounter}";
                    }
                }

                if (section2 != null && section2 != currentSection)
                {
                    EnsureBlank(output);
                    output.Add(FormatComment(section2, referenceCommentLabels));
                    currentSection = section2;
                }

                output.Add(NormalizeSpaces(text));
                sectionBreak = false;
            }

            return string.Join("\n", output).Trim() + "\n";
        }

        /// <summary>
        /// Expands long lines (over 120 chars) that appear to be two text columns joined together
        /// by splitting on runs of 2+ spaces when no chord bracket or chord tokens are present.
        /// </summary>
        /// <param name="lines">Lines to process.</param>
        /// <returns>Expanded line list.</returns>
        private static List<string> ExpandPdfLines(List<string> lines)
        {
            if (lines == null || lines.Count == 0) return lines ?? new List<string>();

            var expanded = new List<string>();
            foreach (var line in lines)
            {
                var raw = line ?? string.Empty;
                if (raw.Length > 120)
                {
                    var chordTokens = ExtractChordTokensFromText(raw);
                    if (chordTokens.Count == 0 && !raw.Contains("["))
                    {
                        var parts = Regex.Split(raw, @"\s{2,}")
                            .Select(p => p.Trim())
                            .Where(p => p.Length > 0)
                            .ToList();
                        if (parts.Count > 1)
                        {
                            expanded.AddRange(parts);
                            continue;
                        }
                    }
                }
                expanded.Add(raw);
            }

            return expanded;
        }

        /// <summary>
        /// Detects and splits lines that contain interleaved chord and lyric tokens (chord run + lyric segments)
        /// and inserts the chords inline into the corresponding lyric text.
        /// </summary>
        /// <param name="lines">Lines to normalize.</param>
        /// <param name="tabWidth">Tab width for positional chord insertion.</param>
        /// <returns>Lines with chord-run/lyric combinations converted to inline-chord format.</returns>
        private static List<string> NormalizeChordRuns(List<string> lines, int tabWidth)
        {
            var output = new List<string>();
            foreach (var line in lines ?? Enumerable.Empty<string>())
            {
                if (line.IsBlankOrWhitespace())
                {
                    output.Add(line);
                    continue;
                }

                if (line.Contains("["))
                {
                    output.Add(line);
                    continue;
                }

                if (TrySplitChordRuns(line, out var segments))
                {
                    foreach (var seg in segments)
                    {
                        if (seg.chords.Count > 0 && seg.lyrics.Count > 0)
                        {
                            var chordLine = string.Join(" ", seg.chords);
                            var lyricLine = string.Join(" ", seg.lyrics);
                            output.Add(InsertChords(chordLine, lyricLine, tabWidth));
                        }
                        else if (seg.chords.Count > 0)
                        {
                            output.Add(string.Join(" ", seg.chords));
                        }
                        else if (seg.lyrics.Count > 0)
                        {
                            output.Add(string.Join(" ", seg.lyrics));
                        }
                    }
                    continue;
                }

                output.Add(line);
            }

            return output;
        }

        /// <summary>
        /// Merges sequences of consecutive chord-only lines (possibly separated by blank lines)
        /// into a single combined chord line.
        /// </summary>
        /// <param name="lines">Line list to process.</param>
        /// <returns>Line list with consecutive chord lines merged.</returns>
        private static List<string> MergeConsecutiveChordLines(List<string> lines)
        {
            if (lines == null || lines.Count == 0) return lines ?? new List<string>();

            var merged = new List<string>();
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (!IsChordOnlyText(line))
                {
                    merged.Add(line);
                    continue;
                }

                var tokens = ExtractChordTokensFromText(line);
                int j = i + 1;
                while (j < lines.Count && (lines[j].IsBlankOrWhitespace() || IsChordOnlyText(lines[j])))
                {
                    if (IsChordOnlyText(lines[j]))
                    {
                        tokens.AddRange(ExtractChordTokensFromText(lines[j]));
                    }
                    j++;
                }

                merged.Add(string.Join(" ", tokens));
                i = j - 1;
            }

            return merged;
        }


        /// <summary>
        /// Splits a mixed chord+lyric line into segments of (chords, lyrics) based on token classification.
        /// Returns <c>false</c> if no mixed chord/lyric pattern is detected.
        /// </summary>
        /// <param name="line">Line to split.</param>
        /// <param name="segments">Resulting (chord list, lyric list) segment pairs.</param>
        /// <returns><c>true</c> if the line contains both chord and lyric tokens.</returns>
        private static bool TrySplitChordRuns(string line, out List<(List<string> chords, List<string> lyrics)> segments)
        {
            segments = new List<(List<string> chords, List<string> lyrics)>();
            var tokens = TokenizeLine(line);

            if (tokens.Count == 0) return false;

            var currentChords = new List<string>();
            var currentLyrics = new List<string>();
            bool sawChord = false;
            bool sawLyric = false;

            int i = 0;
            while (i < tokens.Count)
            {
                var token = tokens[i];
                var isChord = ChordTokenRe.IsMatch(CleanToken(token));
                if (isChord)
                {
                    sawChord = true;
                    if (currentLyrics.Count > 0)
                    {
                        segments.Add((new List<string>(currentChords), new List<string>(currentLyrics)));
                        currentChords.Clear();
                        currentLyrics.Clear();
                    }
                    currentChords.Add(CleanToken(token));
                }
                else
                {
                    sawLyric = true;
                    currentLyrics.Add(token);
                }
                i++;
            }

            if (currentChords.Count > 0 || currentLyrics.Count > 0)
            {
                segments.Add((currentChords, currentLyrics));
            }

            return sawChord && sawLyric;
        }

        /// <summary>
        /// Tokens a PDF text line into individual tokens, also merging adjacent tokens
        /// that form chord roots with modifiers (e.g. "C" + "is" → "Cis", chord + number, chord + slash-bass).
        /// </summary>
        /// <param name="line">The text line to tokenize.</param>
        /// <returns>List of merged token strings.</returns>
        private static List<string> TokenizeLine(string line)
        {
            var tokens = new List<string>();
            foreach (var raw in Regex.Split(line ?? string.Empty, @"\s+"))
            {
                if (raw.IsBlankOrWhitespace()) continue;

                var token = raw.Trim();
                tokens.Add(token);
            }

            for (int i = 0; i < tokens.Count - 1; i++)
            {
                var current = CleanToken(tokens[i]);
                var next = CleanToken(tokens[i + 1]);
                if (Regex.IsMatch(current, @"^[A-Ha-h]$") && Regex.IsMatch(next, @"^(is|es)$", RegexOptions.IgnoreCase))
                {
                    tokens[i] = current + next;
                    tokens.RemoveAt(i + 1);
                    i--;
                }
                else if (ChordTokenRe.IsMatch(current) && Regex.IsMatch(next, @"^\d+$"))
                {
                    tokens[i] = current + next;
                    tokens.RemoveAt(i + 1);
                    i--;
                }
                else if (ChordTokenRe.IsMatch(current) && Regex.IsMatch(next, @"^/[A-Ha-h](?:is|es|[#b])?$"))
                {
                    tokens[i] = current + next;
                    tokens.RemoveAt(i + 1);
                    i--;
                }
            }

            return tokens;
        }

        /// <summary>
        /// Attempts to find an auto-detected reference .cho file in the same directory as the PDF.
        /// Looks for files with the same base name and the extensions ".converted.cho" or ".docx.converted.cho".
        /// </summary>
        /// <param name="pdfPath">Path to the source PDF.</param>
        /// <returns>Path to the reference file if found, or <c>null</c>.</returns>
        private static string AutoReferencePath(string pdfPath)
        {
            if (pdfPath.IsBlankOrWhitespace()) return null;

            var directory = Path.GetDirectoryName(pdfPath) ?? string.Empty;
            var baseName = Path.GetFileNameWithoutExtension(pdfPath);
            if (baseName.IsBlankOrWhitespace()) return null;

            var candidate1 = Path.Combine(directory, baseName + ".converted.cho");
            if (File.Exists(candidate1)) return candidate1;

            var candidate2 = Path.Combine(directory, baseName + ".docx.converted.cho");
            if (File.Exists(candidate2)) return candidate2;

            return null;
        }

        #region PDF text extraction and OCR hooks

        /// <summary>
        /// Extracts text lines from all pages of a PDF file. If the page yields no text words
        /// and OCR is configured, falls back to the configured <see cref="IPdfPageRenderer"/> and
        /// <see cref="IOcrEngine"/> to extract content from the rendered page image.
        /// </summary>
        /// <param name="pdfPath">Path to the source PDF.</param>
        /// <param name="opts">Converter options including optional OCR components.</param>
        /// <param name="hasEmptyTextPage">Set to <c>true</c> if at least one page had no extractable text.</param>
        /// <param name="forceOcr">When <c>true</c>, always uses OCR even if text is available.</param>
        /// <returns>Flat list of text lines across all pages.</returns>
        private static List<string> ExtractPdfLines(string pdfPath, PdfConverterOptions opts, out bool hasEmptyTextPage, bool forceOcr)
        {
            hasEmptyTextPage = false;
            var lines = new List<string>();
            var ocrReady = opts.AllowOcr && opts.PageRenderer != null && opts.OcrEngine != null;

            using var document = UglyToad.PdfPig.PdfDocument.Open(pdfPath);
            var pages = document.GetPages().ToList();
            for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
            {
                var page = pages[pageIndex];
                var words = (forceOcr && ocrReady) ? new List<Word>() : page.GetWords().ToList();
                bool useOcr = forceOcr && ocrReady;

                if (words.Count == 0 && ocrReady)
                {
                    useOcr = true;
                }

                if (useOcr)
                {
                    var ocrText = ExtractOcrText(pdfPath, pageIndex, opts);
                    DumpOcrText(opts.OcrDumpDirectory, pdfPath, pageIndex, ocrText);
                    var ocrLines = ocrText.Replace("\r\n", "\n").Split('\n');
                    if (ocrLines.All(l => l.IsBlankOrWhitespace()))
                    {
                        hasEmptyTextPage = true;
                        continue;
                    }

                    foreach (var line in ocrLines)
                    {
                        var normalized = NormalizeOcrLine(line.TrimEnd());
                        lines.Add(normalized);
                    }

                    lines.Add(string.Empty);
                    continue;
                }

                if (words.Count == 0)
                {
                    hasEmptyTextPage = true;
                    continue;
                }

                var ordered = words
                    .OrderByDescending(w => w.BoundingBox.Bottom)
                    .ThenBy(w => w.BoundingBox.Left)
                    .ToList();

                var lineWords = new List<Word>();
                double? currentY = null;
                const double lineTolerance = 2.0;

                foreach (var word in ordered)
                {
                    var y = word.BoundingBox.Bottom;
                    if (currentY == null || Math.Abs(y - currentY.Value) <= lineTolerance)
                    {
                        lineWords.Add(word);
                        currentY = currentY == null ? y : (currentY.Value * (lineWords.Count - 1) + y) / lineWords.Count;
                    }
                    else
                    {
                        if (lineWords.Count > 0)
                        {
                            var lineText = string.Join(" ", lineWords.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text));
                            lines.Add(lineText);
                        }
                        lineWords.Clear();
                        lineWords.Add(word);
                        currentY = y;
                    }
                }

                if (lineWords.Count > 0)
                {
                    var lineText = string.Join(" ", lineWords.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text));
                    lines.Add(lineText);
                }

                lines.Add(string.Empty);
            }

            return lines;
        }

        /// <summary>
        /// Writes raw OCR text for a single page to a debug dump file under <paramref name="dumpDir"/>.
        /// Does nothing when <paramref name="dumpDir"/> is blank or <paramref name="text"/> is <c>null</c>.
        /// </summary>
        /// <param name="dumpDir">Directory to write debug files into.</param>
        /// <param name="pdfPath">Source PDF path, used to build the output file name.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="text">OCR text to dump.</param>
        private static void DumpOcrText(string dumpDir, string pdfPath, int pageIndex, string text)
        {
            if (dumpDir.IsBlankOrWhitespace() || text == null) return;
            Directory.CreateDirectory(dumpDir);

            var baseName = Path.GetFileName(pdfPath);
            var safeName = string.Join("_", baseName.Split(Path.GetInvalidFileNameChars()));
            var fileName = $"{safeName}.ocr.page{pageIndex + 1}.txt";
            var target = Path.Combine(dumpDir, fileName);
            File.WriteAllText(target, text);
        }

        /// <summary>
        /// Writes a list of processed lines to a debug dump file under <paramref name="dumpDir"/>.
        /// Does nothing when <paramref name="dumpDir"/> is blank or <paramref name="lines"/> is <c>null</c>.
        /// </summary>
        /// <param name="dumpDir">Directory to write debug files into.</param>
        /// <param name="pdfPath">Source PDF path, used to build the output file name.</param>
        /// <param name="suffix">File name suffix identifying the processing stage.</param>
        /// <param name="lines">Lines to write.</param>
        private static void DumpLines(string dumpDir, string pdfPath, string suffix, List<string> lines)
        {
            if (dumpDir.IsBlankOrWhitespace() || lines == null) return;
            Directory.CreateDirectory(dumpDir);

            var baseName = Path.GetFileName(pdfPath);
            var safeName = string.Join("_", baseName.Split(Path.GetInvalidFileNameChars()));
            var fileName = $"{safeName}.{suffix}.txt";
            var target = Path.Combine(dumpDir, fileName);
            File.WriteAllLines(target, lines);
        }

        /// <summary>
        /// Applies OCR post-processing corrections to a single line: fixes common word mis-reads
        /// (e.g. "schopfer" → "schöpfer") and collapses multiple whitespace runs.
        /// </summary>
        /// <param name="line">Raw OCR output line.</param>
        /// <returns>Corrected line text.</returns>
        private static string NormalizeOcrLine(string line)
        {
            if (line.IsBlankOrWhitespace()) return line;

            var normalized = line;
            normalized = ReplaceWord(normalized, "schopfer", "schöpfer");
            normalized = ReplaceWord(normalized, "heildt", "heißt");
            normalized = ReplaceWord(normalized, "heil3t", "heißt");
            normalized = ReplaceWord(normalized, "heiRt", "heißt");
            normalized = ReplaceWord(normalized, "heift", "heißt");
            normalized = ReplaceWord(normalized, "gluck", "glück");
            normalized = ReplaceWord(normalized, "grore", "große");
            normalized = ReplaceWord(normalized, "grobe", "große");

            normalized = Regex.Replace(normalized, @"\s{2,}", " ");
            return normalized.TrimEnd();
        }

        /// <summary>
        /// Replaces a whole-word occurrence of <paramref name="word"/> in <paramref name="input"/>
        /// with <paramref name="replacement"/>, preserving the original capitalisation of the first letter.
        /// </summary>
        /// <param name="input">Source string.</param>
        /// <param name="word">Word to find (case-insensitive).</param>
        /// <param name="replacement">Replacement text.</param>
        /// <returns>String with the word replaced.</returns>
        private static string ReplaceWord(string input, string word, string replacement)
        {
            return Regex.Replace(
                input,
                $@"\b{Regex.Escape(word)}\b",
                m => char.IsUpper(m.Value[0]) ? char.ToUpperInvariant(replacement[0]) + replacement.Substring(1) : replacement,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        /// <summary>
        /// Extracts text lines from a PDF by grouping individual letter glyphs rather than words,
        /// detecting word boundaries via character gap analysis. Used as an alternative extraction
        /// path when the word-level PdfPig API misses spacing.
        /// </summary>
        /// <param name="pdfPath">Path to the source PDF.</param>
        /// <param name="hasEmptyTextPage">Set to <c>true</c> if any page contained no letter glyphs.</param>
        /// <returns>Flat list of text lines across all pages.</returns>
        private static List<string> ExtractPdfLinesFromLetters(string pdfPath, out bool hasEmptyTextPage)
        {
            hasEmptyTextPage = false;
            var lines = new List<string>();

            using var document = UglyToad.PdfPig.PdfDocument.Open(pdfPath);
            foreach (var page in document.GetPages())
            {
                var letters = page.Letters?.ToList() ?? new List<Letter>();
                if (letters.Count == 0)
                {
                    hasEmptyTextPage = true;
                    continue;
                }

                var grouped = letters
                    .GroupBy(l => Math.Round(l.GlyphRectangle.Bottom, 1))
                    .OrderByDescending(g => g.Key)
                    .Select(g => g.OrderBy(l => l.GlyphRectangle.Left).ToList())
                    .ToList();

                foreach (var lineLetters in grouped)
                {
                    if (lineLetters.Count == 0) continue;
                    var avgWidth = lineLetters.Average(l => l.GlyphRectangle.Width);
                    var spaceThreshold = Math.Max(1.0, avgWidth * 1.6);

                    var sb = new StringBuilder();
                    double lastRight = lineLetters[0].GlyphRectangle.Right;
                    sb.Append(lineLetters[0].Value);

                    for (int i = 1; i < lineLetters.Count; i++)
                    {
                        var letter = lineLetters[i];
                        var gap = letter.GlyphRectangle.Left - lastRight;
                        if (gap > spaceThreshold) sb.Append(' ');
                        sb.Append(letter.Value);
                        lastRight = letter.GlyphRectangle.Right;
                    }

                    lines.Add(sb.ToString());
                }

                lines.Add(string.Empty);
            }

            return lines;
        }

        /// <summary>
        /// Renders a single PDF page to a PNG stream using the configured page renderer and
        /// passes it to the configured OCR engine to obtain raw text.
        /// </summary>
        /// <param name="pdfPath">Path to the source PDF.</param>
        /// <param name="pageIndex">Zero-based page index to render.</param>
        /// <param name="opts">Converter options that supply the renderer and OCR engine.</param>
        /// <returns>Recognised text from the page, or an empty string if OCR is unavailable.</returns>
        private static string ExtractOcrText(string pdfPath, int pageIndex, PdfConverterOptions opts)
        {
            if (opts.PageRenderer == null || opts.OcrEngine == null)
                return string.Empty;

            using var png = opts.PageRenderer.RenderPageAsPng(pdfPath, pageIndex, opts.OcrDpi);
            return opts.OcrEngine.ReadText(png) ?? string.Empty;
        }

        #endregion

        #region Parsing helpers (line-based)

        /// <summary>
        /// Returns <c>true</c> if any line in the list contains chord information
        /// (bracket notation, or two or more chord tokens on a chord-only line, or tab-delimited chords).
        /// </summary>
        /// <param name="lines">Lines to search.</param>
        /// <returns><c>true</c> if at least one chord-bearing line is found.</returns>
        private static bool HasChords(List<string> lines)
        {
            foreach (var line in lines)
            {
                if (line.IsBlankOrWhitespace()) continue;
                var raw = line.Trim();
                if (raw.Contains("[") && raw.Contains("]")) return true;

                var tokens = ExtractChordTokensFromText(raw);
                if (tokens.Count >= 2 && IsChordOnlyText(raw)) return true;
                if (tokens.Count >= 2 && raw.Contains("\t")) return true;
            }
            return false;
        }

        /// <summary>
        /// Scans the line list for the first non-empty, non-chord line and uses it as the song title.
        /// Extracts an optional subtitle from parenthesised text on the same line.
        /// </summary>
        /// <param name="lines">Full list of extracted PDF lines.</param>
        /// <returns>Tuple of (title, subtitle-or-null, index-of-title-line).</returns>
        private static (string title, string subtitle, int idx) ExtractHeader(List<string> lines)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                var text = lines[i].Trim();
                if (text.Length == 0) continue;
                if (IsLikelyChordLine(text)) continue;

                var m = Regex.Match(text, @"^(.*?)\s*(\(.*?\))\s*(?:-\s*.*)?$");
                var title = m.Success ? m.Groups[1].Value.Trim() : text;
                var subtitle = m.Success ? m.Groups[2].Value.Trim() : null;
                return (title, subtitle, i);
            }
            return ("Unbekannter Titel", null, 0);
        }

        /// <summary>
        /// Scans the line list from the bottom upward for copyright/attribution metadata lines
        /// (matching keywords such as "text", "melodie", "ccli", "copyright", etc.) and returns
        /// them together with the set of line indices they occupied.
        /// </summary>
        /// <param name="lines">Full list of extracted PDF lines.</param>
        /// <returns>Tuple of (collected footer lines, set of removed line indices).</returns>
        private static (List<string> lines, HashSet<int> indices) ExtractFooterInfo(List<string> lines)
        {
            var keywordRe = new Regex(@"\b(text|melodie|rechte|copyright|quelle|ccli|autor|composer|komponist)\b",
                                      RegexOptions.IgnoreCase | RegexOptions.Compiled);

            var result = new List<string>();
            var indices = new HashSet<int>();
            bool collecting = false;

            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var text = lines[i].Trim();
                if (text.Length == 0) continue;

                if (keywordRe.IsMatch(text))
                {
                    collecting = true;
                    result.Add(text);
                    indices.Add(i);
                    continue;
                }

                if (collecting)
                {
                    if (IsChordOnlyText(text) || ParseSectionLabel(text) != null || text.Contains("\t") || Regex.IsMatch(text, @"\[[^\]]+\]"))
                        break;

                    result.Add(text);
                    indices.Add(i);
                    continue;
                }
            }

            result.Reverse();
            return (result, indices);
        }

        /// <summary>
        /// Parses a "Key: Value" header line and maps the key to a known ChordPro directive name.
        /// Recognised keys include <c>title</c>, <c>key</c>, <c>ccli</c>, <c>text</c>, <c>melodie</c>, etc.
        /// </summary>
        /// <param name="text">Candidate header line.</param>
        /// <returns>Normalised (key, value) pair if recognised; <c>null</c> otherwise.</returns>
        private static (string key, string value)? ParseHeaderLine(string text)
        {
            var m = Regex.Match(text ?? string.Empty, @"^\s*([^:]+)\s*:\s*(.+)\s*$");
            if (!m.Success) return null;
            var key = NormalizeLabel(m.Groups[1].Value);
            var value = m.Groups[2].Value;
            if (HeaderTypes.Contains(key)) return (key, value);

            var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["text"] = "lyricist",
                ["melodie"] = "composer",
                ["rechte"] = "rights",
                ["urheber"] = "copyright",
                ["autor"] = "author",
                ["original_key"] = "original_key",
                ["original_capo"] = "original_capo",
            };
            if (mapping.TryGetValue(key, out var mapped)) return (mapped, value);
            return null;
        }

        /// <summary>
        /// Finds the first chord-only line in the list and returns its first token (without a slash-bass) as the song key.
        /// </summary>
        /// <param name="lines">Lines to scan.</param>
        /// <returns>Detected key string, or <c>null</c> if no chord-only line is found.</returns>
        private static string DetectKey(List<string> lines)
        {
            foreach (var line in lines)
            {
                var text = line.Trim();
                if (!IsChordOnlyText(text)) continue;
                var tokens = Regex.Split(text, @"\s+").Where(t => !t.IsBlankOrWhitespace()).ToList();
                if (tokens.Count > 0) return tokens[0].Split('/')[0];
            }
            return null;
        }

        /// <summary>
        /// Returns <c>true</c> when every non-blank token in <paramref name="text"/> is a valid chord token
        /// according to <see cref="ChordTokenRe"/>.
        /// </summary>
        /// <param name="text">Text to test.</param>
        /// <returns><c>true</c> if all tokens are chords.</returns>
        private static bool IsChordOnlyText(string text)
        {
            var tokens = Regex.Split(text ?? string.Empty, @"\s+")
                .Select(CleanToken)
                .Where(t => !t.IsBlankOrWhitespace())
                .ToList();

            if (tokens.Count == 0) return false;
            return tokens.All(t => ChordTokenRe.IsMatch(t));
        }

        /// <summary>
        /// Returns <c>true</c> if at least one non-blank line in <paramref name="lines"/> is a chord-only line.
        /// </summary>
        /// <param name="lines">Lines to inspect.</param>
        /// <returns><c>true</c> if any chord-only line is present.</returns>
        private static bool HasChordLinesAlready(List<string> lines)
        {
            return lines.Any(l => !l.IsBlankOrWhitespace() && IsChordOnlyText(l));
        }

        /// <summary>
        /// Formats a chord-only text line as a bracketed chord token list followed by " ----".
        /// </summary>
        /// <param name="text">Chord-only text line to render.</param>
        /// <returns>Formatted chord line string.</returns>
        private static string RenderChordOnlyLine(string text)
        {
            var tokens = ExtractChordTokensFromText(text);
            return RenderChordTokens(tokens);
        }

        /// <summary>
        /// Formats a list of chord token strings as <c>[Chord1] [Chord2] ... ----</c>.
        /// </summary>
        /// <param name="tokens">Chord tokens to render.</param>
        /// <returns>Formatted chord line string.</returns>
        private static string RenderChordTokens(List<string> tokens)
        {
            return string.Join(" ", tokens.Select(t => $"[{t}]")) + " ----";
        }

        /// <summary>
        /// Extracts only the chord tokens from a piece of text, cleaning each token and
        /// filtering by <see cref="ChordTokenRe"/>.
        /// </summary>
        /// <param name="text">Source text.</param>
        /// <returns>List of cleaned chord token strings.</returns>
        private static List<string> ExtractChordTokensFromText(string text)
        {
            return Regex.Split(text ?? string.Empty, @"\s+")
                .Select(CleanToken)
                .Where(t => !t.IsBlankOrWhitespace() && ChordTokenRe.IsMatch(t))
                .ToList();
        }

        /// <summary>
        /// Strips surrounding punctuation and non-alphanumeric characters from a token,
        /// retaining chord-relevant characters (<c>#</c>, <c>/</c>, <c>+</c>, <c>-</c>).
        /// </summary>
        /// <param name="token">Token to clean.</param>
        /// <returns>Cleaned token string.</returns>
        private static string CleanToken(string token)
        {
            var raw = (token ?? string.Empty).Trim('(', ')', '[', ']', '{', '}', '.', ',', ':', ';', '…');
            raw = raw.Replace('’', ' ').Replace('“', ' ').Replace('”', ' ').Replace('„', ' ').Replace('`', ' ');
            raw = Regex.Replace(raw, @"[^A-Za-z0-9#/+-]", "");
            return raw;
        }

        /// <summary>
        /// Heuristically determines whether a text line is mostly composed of chord tokens.
        /// Returns <c>true</c> when the number of chord tokens equals or exceeds one less than
        /// the total token count.
        /// </summary>
        /// <param name="text">Line to evaluate.</param>
        /// <returns><c>true</c> if the line is likely a chord line.</returns>
        private static bool IsLikelyChordLine(string text)
        {
            var tokens = Regex.Split(text ?? string.Empty, @"\s+")
                .Where(t => !t.IsBlankOrWhitespace())
                .ToList();
            if (tokens.Count == 0) return true;

            var chordCount = tokens.Count(t => ChordTokenRe.IsMatch(CleanToken(t)));
            return chordCount >= Math.Max(1, tokens.Count - 1);
        }

        /// <summary>
        /// Returns the normalised section label if <paramref name="text"/> is a single word that
        /// matches a known slide type; otherwise returns <c>null</c>.
        /// </summary>
        /// <param name="text">Candidate text.</param>
        /// <returns>Normalised label string, or <c>null</c>.</returns>
        private static string SingleWordLabel(string text)
        {
            var raw = (text ?? string.Empty).Trim();
            if (raw.IsBlankOrWhitespace() || Regex.IsMatch(raw, @"\s")) return null;
            if (IsChordOnlyText(raw)) return null;
            var cleaned = Regex.Replace(raw, @"[^a-zA-ZäöüÄÖÜß]", string.Empty);
            if (cleaned.IsBlankOrWhitespace()) return null;
            var label = NormalizeLabel(cleaned);
            return SlideTypes.Contains(label) ? label : null;
        }

        /// <summary>
        /// Normalises a label string to lowercase-with-underscores, trimming surrounding whitespace.
        /// </summary>
        /// <param name="label">Label to normalise.</param>
        /// <returns>Normalised label string.</returns>
        private static string NormalizeLabel(string label)
        {
            return Regex.Replace((label ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", "_");
        }

        /// <summary>
        /// Returns <c>true</c> if the line contains the word "solo" and also has at least two chord tokens,
        /// indicating it describes a solo chord passage.
        /// </summary>
        /// <param name="text">Line to test.</param>
        /// <returns><c>true</c> for a solo chord line, <c>false</c> otherwise.</returns>
        private static bool IsSoloTextLine(string text)
        {
            if (!text.ToLowerInvariant().Contains("solo")) return false;
            return ExtractChordTokensFromText(text).Count >= 2;
        }

        /// <summary>
        /// Returns the first non-blank line and its index at or after <paramref name="startIdx"/>.
        /// </summary>
        /// <param name="lines">Line list to search.</param>
        /// <param name="startIdx">Index to start from (inclusive).</param>
        /// <returns>Tuple of (line text, index), or (<c>null</c>, -1) if none found.</returns>
        private static (string line, int idx) NextNonEmpty(List<string> lines, int startIdx)
        {
            for (int i = startIdx; i < lines.Count; i++)
            {
                if (!lines[i].IsBlankOrWhitespace()) return (lines[i], i);
            }
            return (null, -1);
        }

        /// <summary>
        /// Parses a section label from the start of a text line. Recognises patterns such as "Refrain",
        /// "Vers 1", "Bridge", "Intro" and similar; also handles a generic "Label:" pattern
        /// for known slide types.
        /// </summary>
        /// <param name="text">Text to parse.</param>
        /// <returns>Tuple of (section name, raw matched text, remaining text after label),
        /// or <c>null</c> if no section label is found.</returns>
        private static (string section, string raw, string rest)? ParseSectionLabel(string text)
        {
            var raw = (text ?? string.Empty).Trim();
            if (raw.ToLowerInvariant().StartsWith("ain"))
            {
                var rest = raw.Substring(3).TrimStart(' ', ':', '.', '-').Trim();
                return ("chorus", raw, rest);
            }

            foreach (var (rx, label) in SectionLabels)
            {
                var m = rx.Match(raw);
                if (!m.Success) continue;

                if (label == "vers")
                {
                    var num = m.Groups.Count > 2 && !m.Groups[2].Value.IsBlankOrWhitespace() ? m.Groups[2].Value : m.Groups[1].Value;
                    return ($"verse {num}", raw, raw.Substring(m.Length).Trim());
                }

                return (label, raw, raw.Substring(m.Length).Trim());
            }

            var generic = Regex.Match(raw, @"^([A-Za-zÄÖÜäöüß_]+)\s*[:\.]\s*(.*)$");
            if (generic.Success)
            {
                var label = NormalizeLabel(generic.Groups[1].Value);
                var rest = generic.Groups[2].Value.Trim();
                if (SlideTypes.Contains(label)) return (label, raw, rest);
                return null;
            }

            return null;
        }

        /// <summary>
        /// Returns a resolved section label for the given line: parses it as a section label if
        /// possible, otherwise falls back to <paramref name="pendingSection"/>.
        /// </summary>
        /// <param name="text">Line text to test.</param>
        /// <param name="pendingSection">Fallback section label from context.</param>
        /// <returns>Resolved section label string.</returns>
        private static string ResolveSection(string text, string pendingSection)
        {
            var parsed = ParseSectionLabel(text);
            if (parsed != null) return parsed.Value.section;
            return pendingSection;
        }

        /// <summary>
        /// Splits a line that starts with chord tokens followed by lyric text into separate
        /// chord and lyric parts. Returns <c>false</c> if fewer than two leading chord tokens are found
        /// or no lyric text remains.
        /// </summary>
        /// <param name="line">Line to split.</param>
        /// <param name="chordLine">Receives the leading chord tokens joined by spaces.</param>
        /// <param name="lyricText">Receives the remaining lyric text.</param>
        /// <returns><c>true</c> if the line was successfully split.</returns>
        private static bool TrySplitLeadingChords(string line, out string chordLine, out string lyricText)
        {
            chordLine = null;
            lyricText = null;

            var tokens = TokenizeLine(line);

            if (tokens.Count < 3) return false;

            int idx = 0;
            while (idx < tokens.Count && ChordTokenRe.IsMatch(CleanToken(tokens[idx])))
            {
                idx++;
            }

            if (idx >= 2 && idx < tokens.Count)
            {
                chordLine = string.Join(" ", tokens.Take(idx));
                lyricText = string.Join(" ", tokens.Skip(idx));
                return true;
            }
            return false;
        }

        /// <summary>
        /// Inserts chords from a separate chord line into the corresponding lyric line using
        /// positional alignment. Supports both space-separated and tab-delimited chord lines.
        /// Each chord is inserted as a <c>[Chord]</c> bracket before the nearest lyric word.
        /// </summary>
        /// <param name="chordLine">The chord-only source line.</param>
        /// <param name="lyricLine">The lyric line to annotate.</param>
        /// <param name="tabWidth">Number of columns per tab stop, used for positional mapping.</param>
        /// <returns>Lyric line with inline chord brackets inserted.</returns>
        private static string InsertChords(string chordLine, string lyricLine, int tabWidth)
        {
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

            var chords = ChordPositions(chordLine, tabWidth);
            var spans = WordSpans(lyricLine);
            var output = lyricLine;

            for (int i = chords.Count - 1; i >= 0; i--)
            {
                var (pos, chord) = chords[i];
                var target = (int)Math.Round((pos / Math.Max(chordLine.Length, 1.0)) * lyricLine.Length);
                int insertPos;

                var spanIdx = spans.FindIndex(s => s.start <= target && target <= s.end);
                if (spanIdx >= 0)
                {
                    var span = spans[spanIdx];
                    var wordLen = span.end - span.start + 1;
                    insertPos = (wordLen <= 3 && spanIdx + 1 < spans.Count) ? spans[spanIdx + 1].start : span.start;
                }
                else
                {
                    var prev = spans.Where(s => s.start <= target).ToList();
                    var next = spans.Where(s => s.start >= target).ToList();
                    if (prev.Count > 0 && next.Count > 0)
                        insertPos = (target - prev.Last().end) <= 4 ? prev.Last().start : next.First().start;
                    else if (prev.Count > 0)
                        insertPos = prev.Last().start;
                    else
                        insertPos = next.Count > 0 ? next.First().start : 0;
                }

                if (i == 0 && insertPos > 0) insertPos = 0;
                output = output.Insert(insertPos, $"[{chord}]");
            }

            return output;
        }

        /// <summary>
        /// Parses a chord line and returns a list of (column-position, chord) pairs.
        /// Handles both space-separated chord lines and tab-delimited chord lines, advancing
        /// the column counter by <paramref name="tabWidth"/> columns at each tab stop.
        /// </summary>
        /// <param name="chordLine">The chord source line.</param>
        /// <param name="tabWidth">Number of columns per tab stop.</param>
        /// <returns>Ordered list of (position, chord) tuples.</returns>
        private static List<(int pos, string chord)> ChordPositions(string chordLine, int tabWidth)
        {
            if (string.IsNullOrWhiteSpace(chordLine)) return new List<(int pos, string chord)>();

            if (chordLine.Contains(' '))
            {
                var spaced = new List<(int pos, string chord)>();
                foreach (Match m in Regex.Matches(chordLine, @"\S+"))
                {
                    var chordToken = CleanToken(m.Value);
                    if (!chordToken.IsBlankOrWhitespace() && ChordTokenRe.IsMatch(chordToken))
                    {
                        spaced.Add((m.Index, chordToken));
                    }
                }
                if (spaced.Count > 0) return spaced;
            }

            int pos = 0;
            var chords = new List<(int pos, string chord)>();
            string token = null;
            int tokenStart = -1;

            foreach (var ch in chordLine ?? string.Empty)
            {
                if (ch == '\t')
                {
                    if (!string.IsNullOrEmpty(token))
                    {
                        var cleaned = CleanToken(token);
                        if (!cleaned.IsBlankOrWhitespace() && ChordTokenRe.IsMatch(cleaned))
                            chords.Add((tokenStart < 0 ? 0 : tokenStart, cleaned));
                        token = null;
                        tokenStart = -1;
                    }
                    pos = ((pos / tabWidth) + 1) * tabWidth;
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
                var cleaned = CleanToken(token);
                if (!cleaned.IsBlankOrWhitespace() && ChordTokenRe.IsMatch(cleaned))
                    chords.Add((tokenStart < 0 ? 0 : tokenStart, cleaned));
            }

            return chords;
        }

        /// <summary>
        /// Returns the start and end character indices for every non-whitespace word in <paramref name="text"/>.
        /// </summary>
        /// <param name="text">Text to scan.</param>
        /// <returns>List of (start, end) index pairs (both inclusive).</returns>
        private static List<(int start, int end)> WordSpans(string text)
        {
            return Regex.Matches(text ?? string.Empty, @"\S+")
                .Select(m => (m.Index, m.Index + m.Length - 1))
                .ToList();
        }

        /// <summary>
        /// Collapses all whitespace runs in <paramref name="text"/> to single spaces and trims the result.
        /// </summary>
        /// <param name="text">Input text.</param>
        /// <returns>Whitespace-normalised string.</returns>
        private static string NormalizeSpaces(string text)
        {
            return Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        }

        /// <summary>
        /// Appends an empty string to <paramref name="lines"/> if the last element is not already blank,
        /// ensuring a trailing blank line after the current block.
        /// </summary>
        /// <param name="lines">Line list to update in place.</param>
        private static void EnsureBlank(List<string> lines)
        {
            if (lines.Count > 0 && lines[^1] != "") lines.Add("");
        }

        /// <summary>
        /// Formats a section label as a <c>{comment: ...}</c> ChordPro directive.  When a reference
        /// label map is provided, the mapped display text is used instead of the raw label.
        /// Handles aliases for "chorus" / "refrain" and "verse" / "vers".
        /// </summary>
        /// <param name="label">Section label to format.</param>
        /// <param name="referenceLabels">Optional mapping from normalised labels to display text.</param>
        /// <returns>Formatted <c>{comment: ...}</c> directive string.</returns>
        private static string FormatComment(string label, Dictionary<string, string> referenceLabels)
        {
            var labelNorm = NormalizeLabel(label);
            if (referenceLabels != null && referenceLabels.TryGetValue(labelNorm, out var mapped))
                return $"{{comment: {mapped}}}";

            if (labelNorm.StartsWith("verse_") && referenceLabels != null)
            {
                var alt = "vers_" + labelNorm.Substring("verse_".Length);
                if (referenceLabels.TryGetValue(alt, out var mappedAlt))
                    return $"{{comment: {mappedAlt}}}";
            }

            if (labelNorm == "chorus" && referenceLabels != null)
            {
                if (referenceLabels.TryGetValue("refrain", out var refr)) return $"{{comment: {refr}}}";
                if (referenceLabels.TryGetValue("chorus", out var cho)) return $"{{comment: {cho}}}";
            }

            return $"{{comment: {labelNorm.Replace('_', ' ')}}}";
        }

        /// <summary>
        /// Loads all chord-bearing lines from a reference <c>.cho</c> file, pairing each with its
        /// plain-text (chord-stripped, whitespace-normalised) equivalent for later matching.
        /// </summary>
        /// <param name="referenceChoPath">Path to the reference ChordPro file.</param>
        /// <returns>List of (plainText, originalLine) pairs; empty if the file does not exist.</returns>
        private static List<(string plain, string line)> LoadReferenceLines(string referenceChoPath)
        {
            if (referenceChoPath.IsBlankOrWhitespace() || !File.Exists(referenceChoPath))
                return new List<(string plain, string line)>();

            var list = new List<(string plain, string line)>();
            foreach (var line in File.ReadAllLines(referenceChoPath))
            {
                if (line.Trim().StartsWith("["))
                {
                    var plain = NormalizeSpaces(StripChords(line));
                    list.Add((plain, line.Trim()));
                }
            }
            return list;
        }

        /// <summary>
        /// Reads all <c>{comment: ...}</c> directives from a reference <c>.cho</c> file and returns them
        /// as a mapping from normalised label to the original display text.
        /// </summary>
        /// <param name="referenceChoPath">Path to the reference ChordPro file.</param>
        /// <returns>Dictionary of normalised-label → display-text; empty if the file does not exist.</returns>
        private static Dictionary<string, string> LoadReferenceCommentLabels(string referenceChoPath)
        {
            var map = new Dictionary<string, string>();
            if (referenceChoPath.IsBlankOrWhitespace() || !File.Exists(referenceChoPath))
                return map;

            foreach (var line in File.ReadAllLines(referenceChoPath))
            {
                var m = Regex.Match(line.Trim(), @"\{comment:\s*([^}]+)\}");
                if (m.Success)
                {
                    var raw = m.Groups[1].Value.Trim();
                    var norm = NormalizeLabel(raw);
                    if (!map.ContainsKey(norm)) map[norm] = raw;
                }
            }
            return map;
        }

        /// <summary>
        /// Parses a reference <c>.cho</c> file into a dictionary mapping each section label
        /// (from <c>{comment:}</c> directives) to its associated chord lines.  Duplicate sections
        /// are skipped after the first occurrence.
        /// </summary>
        /// <param name="referenceChoPath">Path to the reference ChordPro file.</param>
        /// <returns>Dictionary of section-label → chord lines; empty if the file does not exist.</returns>
        private static Dictionary<string, List<string>> LoadReferenceSections(string referenceChoPath)
        {
            var sections = new Dictionary<string, List<string>>();
            if (referenceChoPath.IsBlankOrWhitespace() || !File.Exists(referenceChoPath))
                return sections;

            string current = null;
            foreach (var line in File.ReadAllLines(referenceChoPath))
            {
                var stripped = line.Trim();
                if (stripped.Length == 0)
                {
                    current = null;
                    continue;
                }

                var m = Regex.Match(stripped, @"\{comment:\s*([^}]+)\}");
                if (m.Success)
                {
                    var label = NormalizeLabel(m.Groups[1].Value);
                    if (sections.ContainsKey(label) && sections[label].Count > 0)
                    {
                        current = "__ignore__";
                    }
                    else
                    {
                        current = label;
                        if (!sections.ContainsKey(label)) sections[label] = new List<string>();
                    }
                    continue;
                }

                if (current != null && current != "__ignore__" && stripped.StartsWith("["))
                {
                    sections[current].Add(stripped);
                }
            }

            return sections;
        }

        /// <summary>
        /// Tries to find a reference line (or set of lines) whose stripped text matches the provided
        /// lyric text. Matched lines are removed from <paramref name="referenceLines"/> to avoid reuse.
        /// Also handles multi-column lyric text split by two or more spaces.
        /// </summary>
        /// <param name="referenceLines">Pool of available reference lines (modified in place).</param>
        /// <param name="lyricText">Lyric text to search for.</param>
        /// <returns>List of matching chord lines, or <c>null</c> if no match is found.</returns>
        private static List<string> MatchReferenceLines(List<(string plain, string line)> referenceLines, string lyricText)
        {
            if (referenceLines == null || referenceLines.Count == 0) return null;

            var target = NormalizeSpaces(lyricText);
            for (int i = 0; i < referenceLines.Count; i++)
            {
                if (referenceLines[i].plain == target)
                {
                    var line = referenceLines[i].line;
                    referenceLines.RemoveAt(i);
                    return new List<string> { line };
                }
            }

            var parts = Regex.Split(lyricText ?? string.Empty, @"\s{2,}")
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();

            if (parts.Count <= 1) return null;

            var matchedIndices = new List<int>();
            var matchedLines = new List<string>();

            foreach (var part in parts)
            {
                var partNorm = NormalizeSpaces(part);
                var idx = referenceLines.FindIndex(t => t.plain == partNorm);
                if (idx < 0) return null;
                matchedIndices.Add(idx);
                matchedLines.Add(referenceLines[idx].line);
            }

            foreach (var idx in matchedIndices.OrderByDescending(x => x))
                referenceLines.RemoveAt(idx);

            return matchedLines;
        }

        /// <summary>
        /// Removes all <c>[chord]</c> inline brackets from a ChordPro line, returning plain lyrics.
        /// </summary>
        /// <param name="line">ChordPro line to strip.</param>
        /// <returns>Line with all bracket notations removed.</returns>
        private static string StripChords(string line)
        {
            return Regex.Replace(line ?? string.Empty, @"\[[^\]]+\]", "");
        }

        #endregion
    }
}
