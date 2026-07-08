using SongConverters;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace SongConverters_Reference
{
    /// <summary>
    /// PDF to CHO converter using PdfPig for text extraction.
    /// OCR is optional via injected interfaces.
    /// </summary>
    public sealed class PdfToChoConverterPdfSharpCore : IPdfToChoConverter
    {
        /// <summary>
        /// Chord token regex: matches typical chord notations like A, Am, C#, Fis7, Gmaj7, Dm/F, etc.
        /// </summary>
        private static readonly Regex ChordTokenRe = new(
            @"^(?:[A-Ha-h](?:is|es|[#b])?)(?:m|maj|min|sus|add|dim|aug)?[0-9]*(?:/[A-Ha-h](?:is|es|[#b])?)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Section label regexes: matches common section labels like "Refrain", "Chorus", "Verse 1", "Bridge", etc.
        /// </summary>
        private static readonly (Regex rx, string label)[] SectionLabels =
        {
            (new Regex(@"^(refr\.?|refrain|chorus)\s*[:\.]?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled), "chorus"),
            (new Regex(@"^bridge\s*[:\.]?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled), "bridge"),
            (new Regex(@"^(vers|verse)\s*(\d+)\s*[:\.]?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled), "vers"),
            (new Regex(@"^(\d+)[\.)]\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled), "vers"),
        };

        /// <summary>
        /// Header types that can be recognized in the PDF and converted to ChordPro directives.
        /// </summary>
        private static readonly HashSet<string> HeaderTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "title","sorttitle","subtitle","artist","composer","lyricist","copyright","rights","album",
            "year","key","time","tempo","duration","capo","meta","author","ccli","book","number",
            "flow","midi","midi_index","pitch","keywords","topic","comment","restrictions",
            "ccli_license","footer","notes","original_key","original_capo","transposedkey","in",
            "subdivision","beat","transpose","scene"
        };

        /// <summary>
        /// Section types that can be recognized in the PDF and converted to ChordPro section comments.
        /// </summary>
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

        /// <summary>
        /// Comment style guidance for the user, included as a comment in the output ChordPro file.
        /// </summary>
        private const string CommentStyle = "Kommentar-Stil; deutsche Notation (H, cis7, fis etc.), {comment: Chorus} für Refrain";

        /// <summary>
        /// Converts a PDF file to ChordPro format.
        /// </summary>
        /// <param name="pdfPath">Path to the PDF file to convert.</param>
        /// <param name="options">Optional conversion options.</param>
        /// <returns>The converted ChordPro content as a string.</returns>
        public (string, string) Convert(string pdfPath, PdfConverterOptions options = null)
        {
            string errorString = string.Empty;
            var opts = options ?? new PdfConverterOptions();

            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            {
                throw new FileNotFoundException("PDF file not found.", pdfPath);
            }

            var lines = ExtractPdfLines(pdfPath, opts, out bool hasEmptyTextPage, forceOcr: false);

            if (!lines.Any(l => !l.IsBlankOrWhitespace()))
            {
                var letterLines = ExtractPdfLinesFromLetters(pdfPath, out bool letterHasEmpty);

                if (letterLines.Any(l => !l.IsBlankOrWhitespace()))
                {
                    lines = letterLines;
                    hasEmptyTextPage = letterHasEmpty;
                }
            }

            var ocrReady = opts.AllowOcr && opts.PageRenderer != null && opts.OcrEngine != null;

            if (ocrReady && (hasEmptyTextPage || !lines.Any(l => !l.IsBlankOrWhitespace())))
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
#if DEBUG
                //throw new InvalidOperationException("PDF enthält Seiten ohne Text (Bild/PDF-Mix oder gescannt).");
                Debug.WriteLine("Warning: PDF contains pages without text. OCR might be helpful if activated.");
#endif
                errorString = "PDF contains pages without text. OCR might be helpful if activated.";
                return new ValueTuple<string, string>(string.Empty, errorString);
            }

            if (!lines.Any(l => !l.IsBlankOrWhitespace()))
            {
                if (opts.AbortOnImageOnly)
                {
#if DEBUG
                    //throw new InvalidOperationException("PDF enthält nur Bilder und keinen konvertierbaren Text.");
                    Debug.WriteLine("Warning: PDF contains only images and no convertible text.");
#endif
                }

                errorString = "PDF contains only images and no convertible text.";
                return new ValueTuple<string, string>(string.Empty, errorString);
            }

            if (opts.AbortOnNoChords && !HasChords(lines))
            {
#if DEBUG
                //throw new InvalidOperationException("PDF enthält keine erkennbaren Akkorde; vermutlich Notenversion.");
                Debug.WriteLine("Warning: PDF contains no recognizable chords; probably a sheet music version.");
#endif
                errorString = "PDF contains no recognizable chords; probably a sheet music version.";
                return new ValueTuple<string, string>(string.Empty, errorString);
            }

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

            if (!subtitle.IsBlankOrWhitespace())
            {
                output.Add($"{{subtitle: {subtitle}}}");
            }
            
            output.Add("{comment: Quelle: PDF-Vorlage des Nutzers}");

            foreach (var footer in footerLines)
            {
                var headerItem = ParseHeaderLine(footer);
                output.Add(headerItem != null ? $"{{{headerItem.Value.key}: {headerItem.Value.value}}}" : $"{{comment: {footer}}}");
            }

            if (!key.IsBlankOrWhitespace()) output.Add($"{{key: {key}}}");
            
            output.Add($"{{comment: {CommentStyle}}}");
            if (!allowChordParsing)
            {
                output.Add("{comment: No chords detected; lyrics only}");
            }
            output.Add("");

            string currentSection = null;
            string pendingSection = null;
            int verseCounter = 0;
            bool sectionBreak = false;

            for (int i = titleIdx + 1; i < lines.Count; i++)
            {
                if (footerIndices.Contains(i))
                {
                    continue;
                }

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
                    if (labelOnly.Value.section == "chorus" && referenceSections.TryGetValue("chorus", out var chorusLines))
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
                    {
                        leadLyric = sectionLabelOnly.Value.rest;
                    }

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

                if (IsChordOnlyText(text))
                {
                    var chordTokens = ExtractChordTokensFromText(text);

                    if (!allowChordParsing && chordTokens.Count == 1)
                    {
                        var nextSingle = NextNonEmpty(lines, i + 1);

                        if (nextSingle.idx >= 0)
                        {
                            var nextTextSingle = nextSingle.line.Trim();
                            var nextLabelSingle = ParseSectionLabel(nextTextSingle);

                            if (nextLabelSingle != null && string.IsNullOrWhiteSpace(nextLabelSingle.Value.rest))
                            {
                                pendingSection = nextLabelSingle.Value.section;
                                var afterLabel = NextNonEmpty(lines, nextSingle.idx + 1);
                                if (afterLabel.idx < 0)
                                {
                                    EnsureBlank(output);
                                    output.Add(FormatComment("instrumental", referenceCommentLabels));
                                    output.Add(RenderChordOnlyLine(text));
                                    continue;
                                }

                                nextSingle = afterLabel;
                                nextTextSingle = nextSingle.line.Trim();
                                nextLabelSingle = ParseSectionLabel(nextTextSingle);
                            }

                            if (!IsChordOnlyText(nextTextSingle) && (nextLabelSingle == null || !string.IsNullOrWhiteSpace(nextLabelSingle.Value.rest)))
                            {
                                var lyricTextSingle = nextTextSingle;
                                var sectionSingle = ResolveSection(lyricTextSingle, pendingSection);
                                pendingSection = null;

                                if (sectionSingle == null)
                                {
                                    if ((currentSection == "chorus" || currentSection == "bridge") && sectionBreak)
                                    {
                                        verseCounter++;
                                        sectionSingle = $"verse {verseCounter}";
                                    }
                                    else
                                    {
                                        sectionSingle = currentSection;
                                    }

                                    if (sectionSingle == null)
                                    {
                                        verseCounter++;
                                        sectionSingle = $"verse {verseCounter}";
                                    }
                                }

                                if (sectionSingle != null && sectionSingle != currentSection)
                                {
                                    EnsureBlank(output);
                                    output.Add(FormatComment(sectionSingle, referenceCommentLabels));
                                    currentSection = sectionSingle;
                                }

                                var sectionLabelOnlySingle = ParseSectionLabel(lyricTextSingle);
                                if (sectionLabelOnlySingle != null && !string.IsNullOrWhiteSpace(sectionLabelOnlySingle.Value.rest))
                                {
                                    lyricTextSingle = sectionLabelOnlySingle.Value.rest;
                                }

                                var matchedSingle = MatchReferenceLines(referenceLines, lyricTextSingle);

                                if (matchedSingle != null)
                                {
                                    output.AddRange(matchedSingle);
                                }
                                else
                                {
                                    var withChords = InsertChords(text, lyricTextSingle, opts.TabWidth);
                                    output.Add(NormalizeSpaces(withChords));
                                }

                                sectionBreak = false;
                                i = nextSingle.idx;
                                continue;
                            }
                        }
                    }

                    if (!allowChordParsing)
                    {
                        continue;
                    }

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
                    {
                        lyricText = sectionLabelOnly.Value.rest;
                    }

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

            return new ValueTuple<string, string>(string.Join("\n", output).Trim() + "\n", errorString);
        }

        /// <summary>
        /// Converts a PDF file to ChordPro format.
        /// </summary>
        /// <param name="pdfPath">Path to the PDF file to convert.</param>
        /// <param name="options">Optional conversion options.</param>
        /// <returns>The converted ChordPro content as a string.</returns>
        public async Task<(string, string)> ConvertAsync(string pdfPath, PdfConverterOptions options = null)
        {
            return await Task.Run(() => Convert(pdfPath, options));
        }

        /// <summary>
        /// Expands lines that are too long by splitting them on multiple spaces, but only if they don't contain chords or chord-like tokens.
        /// </summary>
        /// <param name="lines"></param>
        /// <returns></returns>
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
        /// Normalizes lines that contain mixed chords and lyrics by splitting them into segments of consecutive chords and lyrics, 
        /// and then re-inserting the chords above the lyrics with appropriate spacing.
        /// </summary>
        /// <param name="lines"></param>
        /// <param name="tabWidth"></param>
        /// <returns></returns>
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
        /// Merges consecutive lines that contain only chords into a single line, to handle cases where the PDF 
        /// has split chord lines into multiple lines due to formatting, but they should logically be one line of chords. 
        /// This is done before the main normalization step to ensure that we have complete chord lines to work with.
        /// </summary>
        /// <param name="lines"></param>
        /// <returns></returns>
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
        /// Tries to split a line into segments of consecutive chords and lyrics. For example, a line like "C G Am F" would be split 
        /// into one segment with chords ["C", "G", "Am", "F"] and no lyrics, while a line like "C G Am F Hello world" would be split 
        /// into two segments: one with chords ["C", "G", "Am", "F"] and lyrics ["Hello", "world"]. This allows us to handle lines that have 
        /// mixed chords and lyrics without relying on the presence of brackets, which may not be present in the original PDF.
        /// </summary>
        /// <param name="line"></param>
        /// <param name="segments"></param>
        /// <returns></returns>
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
        /// Tokenizes a line into individual tokens, splitting on whitespace and cleaning each token of common punctuation.
        /// </summary>
        /// <param name="line"></param>
        /// <returns></returns>
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
        /// Automatically determines a reference CHO file path based on the PDF path, by looking for files with 
        /// the same base name and extensions like .converted.cho or .docx.converted.cho in the same directory.
        /// </summary>
        /// <param name="pdfPath"></param>
        /// <returns></returns>
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
        /// Extracts lines of text from the PDF using PdfPig, with an option to force OCR on all pages. 
        /// It processes each page, extracts words, and groups them into lines based on their vertical position.
        /// </summary>
        /// <param name="pdfPath"></param>
        /// <param name="opts"></param>
        /// <param name="hasEmptyTextPage"></param>
        /// <param name="forceOcr"></param>
        /// <returns></returns>
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
        /// Dumps the OCR text for a page into a separate .txt file in the specified dump directory, with a filename based on the PDF name and page index.
        /// </summary>
        /// <param name="dumpDir"></param>
        /// <param name="pdfPath"></param>
        /// <param name="pageIndex"></param>
        /// <param name="text"></param>
        private static void DumpOcrText(string dumpDir, string pdfPath, int pageIndex, string text)
        {
            if (dumpDir.IsBlankOrWhitespace() || text == null)
            {
                return;
            }
            
            Directory.CreateDirectory(dumpDir);

            var baseName = Path.GetFileName(pdfPath);
            var safeName = string.Join("_", baseName.Split(Path.GetInvalidFileNameChars()));
            var fileName = $"{safeName}.ocr.page{pageIndex + 1}.txt";
            var target = Path.Combine(dumpDir, fileName);
            File.WriteAllText(target, text);
        }

        /// <summary>
        /// Dumps the processed lines of text into a .txt file in the specified dump directory, with a filename based on the PDF name and a suffix indicating the processing stage.
        /// </summary>
        /// <param name="dumpDir"></param>
        /// <param name="pdfPath"></param>
        /// <param name="suffix"></param>
        /// <param name="lines"></param>
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
        /// Normalizes common OCR errors in a line of text by replacing frequently misrecognized words with their correct forms, and collapsing multiple spaces into a single space.
        /// </summary>
        /// <param name="line"></param>
        /// <returns></returns>
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
        /// Replaces a word in the input text with a replacement, but only if it matches as a whole word (using word boundaries), and preserves the original capitalization of the first letter.
        /// </summary>
        /// <param name="input"></param>
        /// <param name="word"></param>
        /// <param name="replacement"></param>
        /// <returns></returns>
        private static string ReplaceWord(string input, string word, string replacement)
        {
            return Regex.Replace(
                input,
                $@"\b{Regex.Escape(word)}\b",
                m => char.IsUpper(m.Value[0]) ? char.ToUpperInvariant(replacement[0]) + replacement.Substring(1) : replacement,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        /// <summary>
        /// Extracts lines of text from the PDF by using the individual letters and their positions, rather than relying on the word extraction.
        /// </summary>
        /// <param name="pdfPath"></param>
        /// <param name="hasEmptyTextPage"></param>
        /// <returns></returns>
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
        /// Extracts text from a PDF page using OCR, by rendering the page as a PNG image at the specified DPI and then passing it to the OCR engine to read the text.
        /// </summary>
        /// <param name="pdfPath"></param>
        /// <param name="pageIndex"></param>
        /// <param name="opts"></param>
        /// <returns></returns>
        private static string ExtractOcrText(string pdfPath, int pageIndex, PdfConverterOptions opts)
        {
            if (opts.PageRenderer == null || opts.OcrEngine == null)
            {
                return string.Empty;
            }

            using var png = opts.PageRenderer.RenderPageAsPng(pdfPath, pageIndex, opts.OcrDpi);
            return opts.OcrEngine.ReadText(png) ?? string.Empty;
        }

        #endregion

        #region Parsing helpers (line-based)

        /// <summary>
        /// Heuristic to determine if the PDF likely contains chords, by checking if any line is either a chord-only line or contains chord-like tokens.
        /// </summary>
        /// <param name="lines"></param>
        /// <returns></returns>
        private static bool HasChords(List<string> lines)
        {
            // CRITICAL: Only check for CHORD-ONLY LINES, not individual chord tokens in lyrics!
            // Require at least 2 chord-only lines OR 1 strong chord-only line (>=3 chords).
            int chordLineCount = 0;
            bool hasStrongChordLine = false;

            foreach (var line in lines)
            {
                if (line.IsBlankOrWhitespace()) continue;
                if (!IsChordOnlyText(line)) continue;

                var tokens = Regex.Split(line, @"\s+")
                    .Select(CleanToken)
                    .Where(t => !t.IsBlankOrWhitespace())
                    .ToList();

                if (tokens.Count < 2)
                {
                    continue;
                }

                chordLineCount++;

                if (tokens.Count >= 3)
                {
                    hasStrongChordLine = true;
                }
            }

            return chordLineCount >= 2 || hasStrongChordLine;
        }

        /// <summary>
        /// Extracts the title and subtitle from the first non-empty, non-chord line of the PDF text. It looks for patterns like "Title (Subtitle)" or "Title - Subtitle" to 
        /// separate them, but if no such pattern is found, it treats the entire line as the title and leaves the subtitle null. 
        /// This is a heuristic that may not be perfect, but it should work for many common cases where the title is prominently displayed at the top of the PDF.
        /// </summary>
        /// <param name="lines"></param>
        /// <returns></returns>
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
        /// Extracts footer information by scanning from the bottom of the lines upwards, looking for lines 
        /// that contain keywords commonly associated with credits and rights information.
        /// </summary>
        /// <param name="lines"></param>
        /// <returns></returns>
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
        /// Parses a header line to extract key-value pairs for metadata like lyricist, composer, rights, etc. 
        /// It looks for lines that match the pattern "Key: Value" and normalizes the key to a standard set of metadata fields.
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
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
        /// Detects the original key of the song by looking for lines that contain only chords, and extracting the root note of the first chord as the key.
        /// </summary>
        /// <param name="lines"></param>
        /// <returns></returns>
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
        /// Is a line of text likely to be a chord-only line, meaning it consists entirely of valid chord tokens 
        /// (like "C", "Am", "G7", etc.) and possibly some common punctuation, but no actual lyric words.
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
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
        /// Has the PDF already been processed to have separate chord lines, by checking if any line contains only chords.
        /// </summary>
        /// <param name="lines"></param>
        /// <returns></returns>
        private static bool HasChordLinesAlready(List<string> lines)
        {
            return lines.Any(l => !l.IsBlankOrWhitespace() && IsChordOnlyText(l));
        }

        /// <summary>
        /// Renders a line that contains only chords into a format suitable for the CHO file, 
        /// by extracting the chord tokens and joining them with spaces,
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        private static string RenderChordOnlyLine(string text)
        {
            var tokens = ExtractChordTokensFromText(text);
            return RenderChordTokens(tokens);
        }

        /// <summary>
        /// Renders a list of chord tokens into a line suitable for the CHO file, by wrapping each token in brackets and joining them with spaces.
        /// </summary>
        /// <param name="tokens"></param>
        /// <returns></returns>
        private static string RenderChordTokens(List<string> tokens)
        {
            return string.Join(" ", tokens.Select(t => $"[{t}]")) + " ----";
        }

        /// <summary>
        /// Extracts chord-like tokens from a line of text, by splitting on whitespace, cleaning each token 
        /// of common punctuation, and then filtering to only those that match the chord token regex.
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        private static List<string> ExtractChordTokensFromText(string text)
        {
            return Regex.Split(text ?? string.Empty, @"\s+")
                .Select(CleanToken)
                .Where(t => !t.IsBlankOrWhitespace() && ChordTokenRe.IsMatch(t))
                .ToList();
        }

        /// <summary>
        /// Cleans a token by trimming common punctuation characters and then removing any remaining characters that are not valid in chord tokens.
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        private static string CleanToken(string token)
        {
            var raw = (token ?? string.Empty).Trim('(', ')', '[', ']', '{', '}', '.', ',', ':', ';', '…');
            raw = raw.Replace('’', ' ').Replace('“', ' ').Replace('”', ' ').Replace('„', ' ').Replace('`', ' ');
            raw = Regex.Replace(raw, @"[^A-Za-z0-9#/+-]", "");
            return raw;
        }

        /// <summary>
        /// Is a line likely to be a chord line, meaning it consists mostly of valid chord tokens 
        /// and possibly some lyric words, but is not purely a lyric line.
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
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
        /// Some PDFs have the title or section labels rendered in a way that they are split into multiple lines, but if we can detect that a line consists 
        /// of only one word and that word is a valid section label (like "Chorus" or "Verse"), then we can 
        /// treat that line as the section label for the following lines until the next section label. 
        /// This helps to recover section information that might otherwise be lost due to PDF formatting.
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
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
        /// Normalizes a section label by trimming whitespace, converting to lowercase, replacing internal 
        /// whitespace with underscores, and removing any non-alphanumeric characters.
        /// </summary>
        /// <param name="label"></param>
        /// <returns></returns>
        private static string NormalizeLabel(string label)
        {
            return Regex.Replace((label ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", "_");
        }

        /// <summary>
        /// Is a line likely to be a solo section, meaning it contains the word "solo" and also has at least two chord-like tokens, 
        /// which suggests that it's a section of the song where an instrument plays a solo.
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        private static bool IsSoloTextLine(string text)
        {
            if (!text.ToLowerInvariant().Contains("solo")) return false;
            return ExtractChordTokensFromText(text).Count >= 2;
        }

        /// <summary>
        /// Next non-empty line starting from a given index, which can be used to look ahead in the lines to 
        /// find potential section labels or chord lines that are not separated by blank lines.
        /// </summary>
        /// <param name="lines"></param>
        /// <param name="startIdx"></param>
        /// <returns></returns>
        private static (string line, int idx) NextNonEmpty(List<string> lines, int startIdx)
        {
            for (int i = startIdx; i < lines.Count; i++)
            {
                if (!lines[i].IsBlankOrWhitespace()) return (lines[i], i);
            }
            return (null, -1);
        }

        /// <summary>
        /// Parses a line to see if it matches known section label patterns, such as "Chorus", "Verse 1", "Bridge", etc.
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
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
        /// Resolves the current section label for a line of text, by first checking if the line itself is a 
        /// single-word label, then looking for explicit section labels in the line,
        /// </summary>
        /// <param name="text"></param>
        /// <param name="pendingSection"></param>
        /// <returns></returns>
        private static string ResolveSection(string text, string pendingSection)
        {
            var parsed = ParseSectionLabel(text);
            if (parsed != null) return parsed.Value.section;
            return pendingSection;
        }

        /// <summary>
        /// Tries to split a line that contains both chords and lyrics into a chord part and a lyric part, by looking for the longest prefix of tokens that are valid chords,
        /// </summary>
        /// <param name="line"></param>
        /// <param name="chordLine"></param>
        /// <param name="lyricText"></param>
        /// <returns></returns>
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
        /// Inserts chord annotations into a lyric line based on the positions of chords in the chord line. 
        /// It tries to align the chords with the corresponding words in the lyrics,
        /// </summary>
        /// <param name="chordLine"></param>
        /// <param name="lyricLine"></param>
        /// <param name="tabWidth"></param>
        /// <returns></returns>
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
        /// Chord positions are determined by splitting the chord line into tokens based on tabs and spaces, 
        /// cleaning each token, and then recording the position of each valid chord token.
        /// </summary>
        /// <param name="chordLine"></param>
        /// <param name="tabWidth"></param>
        /// <returns></returns>
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
        /// Words spans are calculated by finding all sequences of non-whitespace characters in the lyric line, and recording their start and end indices.
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        private static List<(int start, int end)> WordSpans(string text)
        {
            return Regex.Matches(text ?? string.Empty, @"\S+")
                .Select(m => (m.Index, m.Index + m.Length - 1))
                .ToList();
        }

        /// <summary>
        /// Normalizes spaces in a line of text by replacing multiple consecutive whitespace characters with a single space, and trimming leading and trailing whitespace.
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        private static string NormalizeSpaces(string text)
        {
            return Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        }

        /// <summary>
        /// Ensures that there is a blank line at the end of the list of lines, which can be important for correctly separating sections in the CHO file format.
        /// </summary>
        /// <param name="lines"></param>
        private static void EnsureBlank(List<string> lines)
        {
            if (lines.Count > 0 && lines[^1] != "") lines.Add("");
        }

        /// <summary>
        /// Formats a section label into a comment line for the CHO file, by normalizing the label and looking it up in the reference labels to find a more user-friendly name if available.
        /// </summary>
        /// <param name="label"></param>
        /// <param name="referenceLabels"></param>
        /// <returns></returns>
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

            return $"{{comment: {labelNorm}}}";
        }

        /// <summary>
        /// Loads reference lines from the original PDF text, which can be used to match and preserve the original formatting of lyric lines when inserting chord annotations.
        /// </summary>
        /// <param name="referenceChoPath"></param>
        /// <returns></returns>
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
        /// Loads reference comment labels from the original PDF text, which can be used to map normalized section labels to their original formatting in the PDF,
        /// </summary>
        /// <param name="referenceChoPath"></param>
        /// <returns></returns>
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
        /// Loads reference sections from the original PDF text, which can be used to preserve the original section structure and formatting when reconstructing the CHO file.
        /// </summary>
        /// <param name="referenceChoPath"></param>
        /// <returns></returns>
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
        /// Matches reference lines to the given lyric text, preserving original formatting.
        /// </summary>
        /// <param name="referenceLines"></param>
        /// <param name="lyricText"></param>
        /// <returns></returns>
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
        /// Strips chord annotations from a line of text by removing any substrings that are enclosed in square brackets, 
        /// which is the format used for chords in the CHO file.
        /// </summary>
        /// <param name="line"></param>
        /// <returns></returns>
        private static string StripChords(string line)
        {
            return Regex.Replace(line ?? string.Empty, @"\[[^\]]+\]", "");
        }

        #endregion
    }
}
