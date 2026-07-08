using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using SongConverters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SongConverters
{
    /// <summary>
    /// Converter class to transform DOCX files into CHO (ChordPro) format.
    /// Uses DocumentFormat.OpenXml - no external dependencies like Xceed.
    /// 100% compatible API with DocxToChoConverter.
    /// </summary>
    public sealed class DocxToChoConverterOpenXml : IDocxToChoConverter
    {
        /// <summary>
        /// Chord token regex for parsing.
        /// </summary>
        private static readonly Regex ChordTokenRe = new(
            @"^(?:[A-Ha-h](?:is|es|[#b])?)(?:m|maj|min|sus|add|dim|aug)?[0-9]*(?:/[A-Ha-h](?:is|es|[#b])?)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Section label regexes for parsing.
        /// </summary>
        private static readonly (Regex rx, string label)[] SectionLabels =
        {
            (new Regex(@"^(refr\.?|refrain|chorus)\s*[:\.]?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled), "chorus"),
            (new Regex(@"^bridge\s*[:\.]?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled), "bridge"),
            (new Regex(@"^(vers|verse)\s*(\d+)\s*[:\.]?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled), "vers"),
            (new Regex(@"^(\d+)[\.)]\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled), "vers"),
        };

        /// <summary>
        /// Header types for metadata extraction.
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
        /// Slide types for section labeling.
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
        /// Comment style description.
        /// </summary>
        private const string CommentStyle =
            "Kommentar-Stil; deutsche Notation (H, cis7, fis etc.), {comment: Chorus} f�r Refrain";

        /// <summary>
        /// Represents conversion options.
        /// </summary>
        public sealed record Options(int TabWidth = 12, string ReferenceChoPath = null);

        /// <summary>
        /// Converts a DOCX file to CHO format using OpenXML.
        /// </summary>
        public string Convert(string docxPath, ConverterOptions options = null)
        {
            // Support both ConverterOptions (interface) and Options (legacy)
            var opts = options ?? new ConverterOptions();
            return ConvertInternal(docxPath, opts.TabWidth, opts.ReferenceChoPath);
        }

        /// <summary>
        /// Converts a DOCX file to CHO format using OpenXML asynchronously.
        /// </summary>
        /// <param name="docxPath">Path to the DOCX file.</param>
        /// <param name="options">Conversion options.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>CHO format string.</returns>
        public async Task<string> ConvertAsync(string docxPath, ConverterOptions options = null, 
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(() => Convert(docxPath, options), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Legacy method signature for backward compatibility.
        /// </summary>
        public string Convert(string docxPath, Options options = null)
        {
            var opts = options ?? new Options();
            return ConvertInternal(docxPath, opts.TabWidth, opts.ReferenceChoPath);
        }

        /// <summary>
        /// Internal conversion implementation.
        /// </summary>
        private string ConvertInternal(string docxPath, int tabWidth, string referenceChoPath)
        {
            WordprocessingDocument doc = null;

            try
            {
                doc = WordprocessingDocument.Open(docxPath, false);
            }
            catch (DocumentFormat.OpenXml.Packaging.OpenXmlPackageException ex) when (ex.Message.Contains("Encrypted", StringComparison.OrdinalIgnoreCase))
            {
                // Encrypted DOCX files are not supported - silently return empty
                return string.Empty;
            }
            catch (Exception)
            {
                // Other errors (corrupted file, invalid format, etc.) - return empty
                return string.Empty;
            }

            using (doc)
            {
                var body = doc?.MainDocumentPart?.Document?.Body;

                if (body == null)
                {
                    return string.Empty;
                }

                var paragraphs = body.Elements<Paragraph>().ToList();

                if (DocumentHasImagesOnly(doc))
                {
                    return string.Empty;
                }

                var allowChordParsing = DocumentHasChordLines(paragraphs);

                var (footerLines, footerIndices) = ExtractFooterInfo(paragraphs);
                var referenceLines = LoadReferenceLines(referenceChoPath);
                var referenceSections = LoadReferenceSections(referenceChoPath);
                var referenceCommentLabels = LoadReferenceCommentLabels(referenceChoPath);

                var lines = new List<string>();

                // Header (title/subtitle/link)
                var (title, subtitle, link) = ExtractHeader(doc, paragraphs);
                lines.Add($"{{title: {title}}}");

                if (!string.IsNullOrWhiteSpace(subtitle))
                {
                    lines.Add($"{{subtitle: {subtitle}}}");
                }

                lines.Add("{comment: Quelle: Word-Vorlage des Nutzers}");

                if (!string.IsNullOrWhiteSpace(link))
                {
                    lines.Add($"{{comment: Link: {link}}}");
                }

                foreach (var footer in footerLines)
                {
                    var headerItem = ParseHeaderLine(footer);
                    lines.Add(headerItem != null ? $"{{{headerItem.Value.key}: {headerItem.Value.value}}}" : $"{{comment: {footer}}}");
                }

                var key = DetectKey(paragraphs);

                if (!string.IsNullOrWhiteSpace(key))
                {
                    lines.Add($"{{key: {key}}}");
                }

                lines.Add($"{{comment: {CommentStyle}}}");
                if (!allowChordParsing)
                {
                    lines.Add("{comment: No chords detected; lyrics only}");
                }
                lines.Add("");

                string currentSection = null;
                string pendingSection = null;
                int verseCounter = 0;
                bool sectionBreak = false;
                List<string> lastChorusLines = new();

                for (int i = 1; i < paragraphs.Count; i++)
                {
                    if (footerIndices.Contains(i))
                    {
                        continue;
                    }

                    var p = paragraphs[i];
                    if (ParagraphHasImage(p))
                    {
                        sectionBreak = true;
                        continue;
                    }

                    var text = GetParagraphText(p).Trim();

                    if (text.Length == 0)
                    {
                        sectionBreak = true;
                        continue;
                    }

                    if (allowChordParsing && IsSoloTextLine(text))
                    {
                        EnsureBlank(lines);
                        lines.Add(FormatComment("solo", referenceCommentLabels));
                        var tokens = ExtractChordTokensFromText(text);
                        if (tokens.Count > 0) lines.Add(RenderChordTokens(tokens));
                        sectionBreak = false;
                        continue;
                    }

                    var labelWord = allowChordParsing ? SingleWordLabel(text) : null;

                    if (labelWord != null)
                    {
                        var next = NextNonEmpty(paragraphs, i + 1);
                        if (next.idx >= 0 && IsChordOnlyText(GetParagraphText(next.par)))
                        {
                            EnsureBlank(lines);
                            lines.Add(FormatComment(labelWord, referenceCommentLabels));
                            lines.Add(RenderChordOnlyLine(GetParagraphText(next.par)));
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
                            EnsureBlank(lines);
                            lines.Add(FormatComment("chorus", referenceCommentLabels));
                            lines.AddRange(chorusLines);
                            currentSection = "chorus";
                            lastChorusLines = new List<string>(chorusLines);
                            sectionBreak = false;
                            continue;
                        }

                        if (labelOnly.Value.section == "chorus" && lastChorusLines.Count > 0)
                        {
                            EnsureBlank(lines);
                            lines.Add(FormatComment("chorus", referenceCommentLabels));
                            lines.AddRange(lastChorusLines);
                            currentSection = "chorus";
                            sectionBreak = false;
                            continue;
                        }

                        pendingSection = labelOnly.Value.section;
                        continue;
                    }

                    var chordLine = text;
                    bool isChordLine = TryGetChordLine(p, out chordLine) || IsChordOnlyText(text);

                    if (isChordLine)
                    {
                        var next = NextNonEmpty(paragraphs, i + 1);

                        if (next.idx < 0)
                        {
                            EnsureBlank(lines);
                            lines.Add(FormatComment("instrumental", referenceCommentLabels));
                            lines.Add(RenderChordOnlyLine(chordLine));
                            continue;
                        }

                        var nextText = GetParagraphText(next.par).Trim();
                        var nextLabel = ParseSectionLabel(nextText);

                        if (TryGetChordLine(next.par, out _) || IsChordOnlyText(nextText) || (nextLabel != null && string.IsNullOrWhiteSpace(nextLabel.Value.rest)))
                        {
                            EnsureBlank(lines);
                            lines.Add(FormatComment("instrumental", referenceCommentLabels));
                            lines.Add(RenderChordOnlyLine(chordLine));
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
                                verseCounter++; section = $"verse {verseCounter}";
                            }
                        }

                        if (section != null && section != currentSection)
                        {
                            EnsureBlank(lines);
                            lines.Add(FormatComment(section, referenceCommentLabels));
                            currentSection = section;
                            if (section == "chorus")
                            {
                                lastChorusLines = new List<string>();
                            }
                        }

                        var sectionLabelOnly = ParseSectionLabel(lyricText);
                        if (sectionLabelOnly != null && !string.IsNullOrWhiteSpace(sectionLabelOnly.Value.rest))
                        {
                            lyricText = sectionLabelOnly.Value.rest;
                        }

                        var matched = MatchReferenceLines(referenceLines, lyricText);

                        if (matched != null)
                        {
                            lines.AddRange(matched);
                        }
                        else
                        {
                            var withChords = InsertChords(chordLine, lyricText, tabWidth);
                            var normalized = NormalizeSpaces(withChords);
                            lines.Add(normalized);
                            if (currentSection == "chorus")
                            {
                                lastChorusLines.Add(normalized);
                            }
                        }

                        sectionBreak = false;
                        i = next.idx;
                        continue;
                    }

                    // non-chord lyric
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
                            verseCounter++; section2 = $"verse {verseCounter}";
                        }
                    }

                    if (section2 != null && section2 != currentSection)
                    {
                        EnsureBlank(lines);
                        lines.Add(FormatComment(section2, referenceCommentLabels));
                        currentSection = section2;
                        if (section2 == "chorus")
                        {
                            lastChorusLines = new List<string>();
                        }
                    }

                    var normalizedText = NormalizeSpaces(text);
                    lines.Add(normalizedText);
                    if (currentSection == "chorus")
                    {
                        lastChorusLines.Add(normalizedText);
                    }
                    sectionBreak = false;
                }

                return string.Join("\n", lines).Trim() + "\n";
            } // end using (doc)
        }

        /// <summary>
        /// Checks if the document contains any chord lines.
        /// Uses existing TryGetChordLine() and IsChordOnlyText() methods.
        /// </summary>
        private static bool DocumentHasChordLines(List<Paragraph> paragraphs)
        {
            if (paragraphs == null || paragraphs.Count == 0) return false;

            foreach (var p in paragraphs)
            {
                // Method 1: Check if paragraph is a valid chord line
                if (TryGetChordLine(p, out var chordLine))
                {
                    var tokens = ExtractChordTokensFromText(chordLine);
                    if (tokens.Count >= 2)
                    {
                        return true;
                    }
                }

                // Method 2: Check if text is chord-only (e.g., "D A fis E")
                var text = GetParagraphText(p).Trim();
                if (!string.IsNullOrWhiteSpace(text) && IsChordOnlyText(text))
                {
                    var tokens = ExtractChordTokensFromText(text);
                    if (tokens.Count >= 2)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        #region OpenXML Helper Methods

        /// <summary>
        /// Get text from OpenXML paragraph.
        /// </summary>
        private static string GetParagraphText(Paragraph paragraph)
        {
            if (paragraph == null) return string.Empty;
            var parts = new List<string>();
            foreach (var run in paragraph.Descendants<Run>())
            {
                foreach (var child in run.ChildElements)
                {
                    if (child is Text t)
                    {
                        parts.Add(t.Text ?? string.Empty);
                    }
                    else if (child is TabChar)
                    {
                        parts.Add("\t");
                    }
                }
            }

            return string.Concat(parts);
        }

        /// <summary>
        /// Extracts title, subtitle, and link from the first paragraph.
        /// </summary>
        private static (string title, string subtitle, string link) ExtractHeader(WordprocessingDocument doc, List<Paragraph> paragraphs)
        {
            var first = paragraphs.FirstOrDefault(p => !GetParagraphText(p).IsBlankOrWhitespace());
            var text = GetParagraphText(first).Trim();
            var m = Regex.Match(text, @"^(.*?)\s*(\(.*?\))\s*(?:-\s*.*)?$");
            string title = m.Success ? m.Groups[1].Value.Trim() : text;
            string subtitle = m.Success ? m.Groups[2].Value.Trim() : null;

            if (title.IsBlankOrWhitespace())
            {
                title = "Unbekannter Titel";
            }

            string link = null;
            if (first != null && doc?.MainDocumentPart != null)
            {
                var hyperlink = first.Descendants<Hyperlink>().FirstOrDefault();
                var relId = hyperlink?.Id?.Value;
                if (!string.IsNullOrWhiteSpace(relId))
                {
                    var rel = doc.MainDocumentPart.HyperlinkRelationships
                        .FirstOrDefault(r => r.Id == relId);
                    if (rel?.Uri != null)
                    {
                        link = rel.Uri.ToString();
                    }
                }
            }

            return (title, subtitle, link);
        }

        /// <summary>
        /// Extracts footer information from the document paragraphs.
        /// </summary>
        private static (List<string> lines, HashSet<int> indices) ExtractFooterInfo(List<Paragraph> paragraphs)
        {
            var keywordRe = new Regex(@"\b(text|melodie|rechte|copyright|quelle|ccli|autor|composer|komponist)\b",
                                      RegexOptions.IgnoreCase | RegexOptions.Compiled);

            var lines = new List<string>();
            var indices = new HashSet<int>();
            bool collecting = false;

            for (int i = paragraphs.Count - 1; i >= 0; i--)
            {
                var text = GetParagraphText(paragraphs[i]).Trim();
                if (text.Length == 0)
                {
                    continue;
                }
                if (keywordRe.IsMatch(text))
                {
                    collecting = true;
                    lines.Add(text);
                    indices.Add(i);
                    continue;
                }

                if (collecting)
                {
                    if (IsChordOnlyText(text) || ParseSectionLabel(text) != null || text.Contains('\t') || Regex.IsMatch(text, @"\[[^\]]+\]"))
                    {
                        break;
                    }

                    lines.Add(text);
                    indices.Add(i);
                }
            }

            lines.Reverse();
            return (lines, indices);
        }

        /// <summary>
        /// Detects the key of the song from chord paragraphs.
        /// </summary>
        private static string DetectKey(List<Paragraph> paragraphs)
        {
            foreach (var p in paragraphs)
            {
                if (TryGetChordLine(p, out var chordLine))
                {
                    var tokens = Regex.Split(chordLine, @"\s+").Where(t => t.Length > 0).ToList();
                    if (tokens.Count > 0) return tokens[0].Split('/')[0];
                }
            }

            return null;
        }

        /// <summary>
        /// Indicates whether the given paragraph is likely to contain chords.
        /// Checks for bold/italic formatting as indicator.
        /// </summary>
        private static bool IsChordParagraph(Paragraph p)
        {
            return TryGetChordLine(p, out _);
        }

        private static bool TryGetChordLine(Paragraph p, out string chordLine)
        {
            chordLine = null;
            var text = GetParagraphText(p).Trim();
            if (text.Length == 0) return false;

            var tokens = Regex.Split(text, @"\s+").Where(t => t.Length > 0).ToList();
            if (tokens.Count > 0 && tokens.All(t => ChordTokenRe.IsMatch(t)))
            {
                chordLine = text;
                return true;
            }

            // Fallback: treat each run as a chord token when possible
            var runTokens = new List<string>();
            foreach (var run in p.Descendants<Run>())
            {
                var runText = string.Concat(run.Descendants<Text>().Select(t => t.Text)).Trim();
                if (runText.Length == 0) continue;
                if (ChordTokenRe.IsMatch(runText))
                {
                    runTokens.Add(runText);
                    continue;
                }
                // If any run is non-chord text, abort fallback
                return false;
            }

            if (runTokens.Count >= 2)
            {
                chordLine = string.Join("\t", runTokens);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Indicates whether paragraph contains an image.
        /// </summary>
        private static bool ParagraphHasImage(Paragraph p)
        {
            return p.Descendants<DocumentFormat.OpenXml.Drawing.Picture>().Any() ||
                   p.Descendants<DocumentFormat.OpenXml.Wordprocessing.Drawing>().Any();
        }

        /// <summary>
        /// Detects if the document has only images and no text.
        /// </summary>
        private static bool DocumentHasImagesOnly(WordprocessingDocument doc)
        {
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body == null) return false;

            bool hasImage = body.Descendants<DocumentFormat.OpenXml.Drawing.Picture>().Any() ||
                           body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Drawing>().Any();
            bool hasText = body.Descendants<Text>().Any(t => !string.IsNullOrWhiteSpace(t.Text));

            return hasImage && !hasText;
        }

        #endregion

        #region Shared Parsing Logic (identical to Xceed version)

        private static (string key, string value)? ParseHeaderLine(string text)
        {
            var m = Regex.Match(text, @"^\s*([^:]+)\s*:\s*(.+)\s*$");

            if (!m.Success)
            {
                return null;
            }

            var key = NormalizeLabel(m.Groups[1].Value);
            var value = m.Groups[2].Value;

            if (HeaderTypes.Contains(key))
            {
                return (key, value);
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["text"] = "lyricist",
                ["melodie"] = "composer",
                ["rechte"] = "rights",
                ["urheber"] = "copyright",
                ["autor"] = "author",
                ["original_key"] = "original_key",
                ["original_capo"] = "original_capo"
            };

            return map.TryGetValue(key, out var k) ? (k, value) : null;
        }

        private static bool IsChordOnlyText(string text)
        {
            var tokens = Regex.Split(text.Trim(), @"\s+")
                .Select(CleanToken).Where(t => t.Length > 0).ToList();

            return tokens.Count > 0 && tokens.All(t => ChordTokenRe.IsMatch(t));
        }

        private static bool IsSoloTextLine(string text)
            => text.Contains("solo", StringComparison.OrdinalIgnoreCase)
               && ExtractChordTokensFromText(text).Count >= 2;

        private static string SingleWordLabel(string text)
        {
            var raw = text.Trim();

            if (raw.Length == 0 || Regex.IsMatch(raw, @"\s") || IsChordOnlyText(raw))
            {
                return null;
            }

            var cleaned = Regex.Replace(raw, @"[^a-zA-Z�������]", "");

            if (cleaned.Length == 0)
            {
                return null;
            }

            var label = NormalizeLabel(cleaned);
            return SlideTypes.Contains(label) ? label : null;
        }

        private static (string section, string raw, string rest)? ParseSectionLabel(string text)
        {
            var raw = text.Trim();

            if (raw.StartsWith("ain", StringComparison.OrdinalIgnoreCase))
            {
                return ("chorus", raw, raw[3..].Trim(' ', ':', '.', '-'));
            }

            foreach (var (rx, label) in SectionLabels)
            {
                var m = rx.Match(raw);

                if (!m.Success)
                {
                    continue;
                }

                if (label == "vers")
                {
                    var num = m.Groups.Count > 2 && m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value;
                    return ($"verse {num}", raw, raw[m.Length..].Trim());
                }

                return (label.ToLowerInvariant(), raw, raw[m.Length..].Trim());
            }

            var m2 = Regex.Match(raw, @"^([A-Za-z�������_]+)\s*[:\.]\s*(.*)$");

            if (m2.Success)
            {
                var label = NormalizeLabel(m2.Groups[1].Value);
                var rest = m2.Groups[2].Value.Trim();

                if (SlideTypes.Contains(label))
                {
                    return (label, raw, rest);
                }

                return null;
            }

            return null;
        }

        private static string ResolveSection(string text, string pendingSection)
        {
            var parsed = ParseSectionLabel(text);
            return parsed?.section ?? pendingSection;
        }

        private static string InsertChords(string chordLine, string lyricLine, int tabWidth)
        {
            var (chords, width) = ChordPositions(chordLine, tabWidth);
            var spans = WordSpans(lyricLine);

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
                    {
                        insertPos = (target - prev.end) <= 4 ? prev.start : next.start;
                    }
                    else if (prev != default)
                    {
                        insertPos = prev.start;
                    }
                    else
                    {
                        insertPos = next.start;
                    }
                }

                if (i == ordered.Count - 1 && insertPos > 0)
                {
                    insertPos = 0;
                }

                output = output.Insert(insertPos, $"[{chord}]");
            }

            return output;
        }

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
                        chords.Add((tokenStart < 0 ? 0 : tokenStart, token));
                        token = null;
                        tokenStart = -1;
                    }

                    if (ch == '\t')
                    {
                        pos = ((pos / tab) + 1) * tab;
                    }
                    else
                    {
                        pos++;
                    }
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
                chords.Add((tokenStart < 0 ? 0 : tokenStart, token));
            }

            return (chords, pos);
        }

        private static List<(int start, int end)> WordSpans(string text)
        {
            var spans = new List<(int, int)>();

            foreach (Match m in Regex.Matches(text, @"\S+"))
            {
                spans.Add((m.Index, m.Index + m.Length - 1));
            }

            return spans;
        }

        private static string NormalizeSpaces(string s)
            => Regex.Replace(s, @"\s+", " ").Trim();

        private static void EnsureBlank(List<string> lines)
        {
            if (lines.Count > 0 && lines[^1] != "")
            {
                lines.Add("");
            }
        }

        private static string RenderChordOnlyLine(string text)
            => RenderChordTokens(ExtractChordTokensFromText(text));

        private static string RenderChordTokens(List<string> tokens)
            => string.Join(" ", tokens.Select(t => $"[{t}]")) + " ----";

        private static List<string> ExtractChordTokensFromText(string text)
            => Regex.Split(text.Trim(), @"\s+")
                    .Select(CleanToken)
                    .Where(t => t.Length > 0 && ChordTokenRe.IsMatch(t))
                    .ToList();

        private static string CleanToken(string token)
            => token.Trim("()[]{}.,:;?".ToCharArray());

        private static string NormalizeLabel(string label)
            => Regex.Replace(label.Trim().ToLowerInvariant(), @"\s+", "_");

        private static List<(string plain, string line)> LoadReferenceLines(string referenceChoPath)
        {
            if (string.IsNullOrWhiteSpace(referenceChoPath) || !File.Exists(referenceChoPath))
            {
                return new();
            }
            var list = new List<(string, string)>();

            foreach (var line in File.ReadAllLines(referenceChoPath))
            {
                if (line.TrimStart().StartsWith("["))
                {
                    var plain = NormalizeSpaces(Regex.Replace(line, @"\[[^\]]+\]", ""));
                    list.Add((plain, line.Trim()));
                }
            }

            return list;
        }

        private static Dictionary<string, List<string>> LoadReferenceSections(string referenceChoPath)
        {
            var dict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(referenceChoPath) || !File.Exists(referenceChoPath))
            {
                return dict;
            }
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

                    if (dict.ContainsKey(label) && dict[label].Count > 0)
                    {
                        current = null;
                        continue;
                    }

                    dict[label] = dict.TryGetValue(label, out var l) ? l : new List<string>();
                    current = label;
                    continue;
                }

                if (current != null && stripped.StartsWith("["))
                {
                    dict[current].Add(stripped);
                }
            }

            return dict;
        }

        private static Dictionary<string, string> LoadReferenceCommentLabels(string referenceChoPath)
        {
            var map = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(referenceChoPath) || !File.Exists(referenceChoPath))
            {
                return map;
            }

            foreach (var line in File.ReadAllLines(referenceChoPath))
            {
                var m = Regex.Match(line.Trim(), @"\{comment:\s*([^}]+)\}");

                if (!m.Success)
                {
                    continue;
                }

                var raw = m.Groups[1].Value.Trim();
                var norm = NormalizeLabel(raw);

                if (!map.ContainsKey(norm)) map[norm] = raw;
            }

            return map;
        }

        private static string FormatComment(string label, Dictionary<string, string> refLabels)
        {
            var norm = NormalizeLabel(label);

            if (!refLabels.TryGetValue(norm, out var outLabel) && norm.StartsWith("verse_"))
            {
                var num = norm.Split("verse_")[1];

                if (refLabels.TryGetValue($"vers_{num}", out var v))
                {
                    outLabel = v;
                }
                else
                {
                    outLabel = $"Vers {num}";
                }
            }

            if (outLabel == null && norm == "chorus")
            {
                refLabels.TryGetValue("refrain", out outLabel);
                outLabel ??= "Chorus";
            }

            outLabel ??= norm;
            return $"{{comment: {outLabel}}}";
        }

        private static List<string> MatchReferenceLines(List<(string plain, string line)> referenceLines, string lyric)
        {
            if (referenceLines == null || referenceLines.Count == 0)
            {
                return null;
            }

            var target = NormalizeSpaces(lyric);

            for (int i = 0; i < referenceLines.Count; i++)
            {
                if (referenceLines[i].plain == target)
                {
                    var line = referenceLines[i].line;
                    referenceLines.RemoveAt(i);
                    return new List<string> { line };
                }
            }

            var parts = Regex.Split(lyric, @"\s{2,}").Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

            if (parts.Count <= 1)
            {
                return null;
            }

            var indices = new List<int>();
            var lines = new List<string>();

            foreach (var p in parts)
            {
                var norm = NormalizeSpaces(p);
                var idx = referenceLines.FindIndex(x => x.plain == norm);

                if (idx < 0)
                {
                    return null;
                }

                indices.Add(idx);
                lines.Add(referenceLines[idx].line);
            }

            foreach (var idx in indices.OrderByDescending(x => x))
            {
                referenceLines.RemoveAt(idx);
            }

            return lines;
        }

        private static (Paragraph par, int idx) NextNonEmpty(List<Paragraph> paragraphs, int start)
        {
            for (int i = start; i < paragraphs.Count; i++)
            {
                var text = GetParagraphText(paragraphs[i]);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return (paragraphs[i], i);
                }
            }

            return (null, -1);
        }

        #endregion
    }
}
