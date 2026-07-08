using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using SongConverters;
using CHOConverterCLI;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;
var utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

var enableOcr = ParseEnableOcr(args);
var requestedOcrBackend = ParseOcrBackend(args);
var useFastOcr = ParseFastOcr(args);

var (inputFiles, outputDir) = ResolveInputs(args);
if (inputFiles == null || inputFiles.Count == 0 || outputDir == null)
{
    Console.WriteLine("Keine Eingabedateien oder kein Zielordner gewählt.");
    return;
}

Directory.CreateDirectory(outputDir);

var docxConverter = new DocxToChoConverterOpenXml();
var pdfConverter = new PdfToChoConverterPdfSharpCore();

// Default: no OCR (fast, avoids native OCR engine lifetime issues in batch runs).
var pdfOptions = new PdfConverterOptions(AbortOnNoChords: true, AbortOnImageOnly: true);

// OCR is initialized lazily (DOCX fallback always allowed if OCR data exists; PDF OCR only when --ocr is passed).
DocxToChoConverterOpenXmlImageOcr docxOcrConverter = null;
DocxToChoConverterOpenXmlImageOcrDualLayout docxLayoutConverter = null;
PdfConverterOptions pdfOcrOptions = null;
IPdfToChoConverter pdfLayoutConverter = null;
IDisposable ocrCleanup = null;
IDisposable pdfLayoutCleanup = null;

int ok = 0, skip = 0, fail = 0;

foreach (var file in inputFiles.OrderBy(p => p))
{
    try
    {
    if (IsLikelyNotenFile(file))
    {
        WriteStatus("SKIP", file, "Noten-Datei (per Dateiname)");
        skip++;
        continue;
    }

    // Sheet-music detection: flag files that contain staff systems.
    // We no longer skip them outright — many chord sheets have notation lines
    // mixed with chords and lyrics. Instead, mark them and let the converter
    // try OCR; the resulting CHO is post-checked for garbage later.
    var ext0 = Path.GetExtension(file)?.ToLowerInvariant();
    if (ext0 == ".docx" && LooksLikeSheetMusicDocx(file))
    {
    }
    if (ext0 == ".pdf" && LooksLikeSheetMusicPdf(file))
    {
    }

    var ext = Path.GetExtension(file)?.ToLowerInvariant();
    if (ext == ".docx")
    {
        var (status, message) = ProcessDocx(
            file,
            outputDir,
            docxConverter,
            ref docxOcrConverter,
            ref docxLayoutConverter,
            EnsureOcrInitialized,
            utf8Bom);
        WriteStatus(status, file, message);
        if (status == "OK") ok++;
        else if (status == "SKIP") skip++;
        else fail++;
        continue;
    }

    if (ext == ".pdf")
    {
        var (status, message) = ProcessPdf(
            file,
            outputDir,
            pdfConverter,
            enableOcr,
            requestedOcrBackend,
            ref pdfLayoutConverter,
            pdfOptions,
            ref pdfOcrOptions,
            EnsureOcrInitialized,
            utf8Bom);
        WriteStatus(status, file, message);
        if (status == "OK") ok++;
        else if (status == "SKIP") skip++;
        else fail++;
        continue;
    }

    WriteStatus("SKIP", file, "Unsupported extension");
    skip++;

    } // end try
    catch (Exception ex)
    {
        // Catch severe errors (including SEHException from native Tesseract crashes)
        // to prevent a single file from killing the entire batch run.
        WriteStatus("FAIL", file, $"Schwerer Fehler: {ex.GetType().Name}: {ex.Message}");
        fail++;
    }
}

Console.WriteLine($"Summary: OK={ok}, SKIP={skip}, FAIL={fail}");
Environment.ExitCode = 0;

// Flush stdout before native cleanup — if Tesseract's Dispose triggers a native
// crash (Access Violation in tesseract50.dll / leptonica), the process may die
// during cleanup. All conversion results are already written to disk at this point.
Console.Out.Flush();

// Set desired exit code. If Dispose below triggers a native crash, the OS may
// report a different (non-zero) exit code, but all output files will be complete.
var desiredExitCode = 0;

// Attempt to dispose native OCR engines. If this crashes natively, results are
// already safely on disk and the Summary line has been printed.
try
{
    ocrCleanup?.Dispose();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Warning: OCR engine cleanup failed: {ex.Message}");
}

try
{
    pdfLayoutCleanup?.Dispose();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Warning: Layout OCR cleanup failed: {ex.Message}");
}

Environment.ExitCode = desiredExitCode;

/// <summary>
/// Lazily initializes all OCR engines (Tesseract, layout OCR, PDF OCR) if they have not yet been created.
/// Searches for a valid OCR data directory before initialization.
/// </summary>
/// <returns><c>true</c> if OCR was successfully initialized or was already initialized; <c>false</c> if OCR data is missing.</returns>
bool EnsureOcrInitialized()
{
    if (docxOcrConverter != null || docxLayoutConverter != null || pdfOcrOptions != null) return true;

    var ocrDataPath = FindOcrDataDir();
    if (ocrDataPath == null || !HasOcrDataFiles(ocrDataPath)) return false;

    var renderer = new LoggingPdfRenderer(new DocnetPdfRenderer());

    try
    {
        var ocrDpi = useFastOcr ? 220 : 300;

        var runtime = OcrRuntimeFactory.Create(new OcrBackendOptions(
            Backend: requestedOcrBackend,
            Language: "deu+eng",
            DataPath: ocrDataPath,
            ScalePercent: 200,
            PreferDenseLayout: true,
            RemoveHorizontalLines: true,
            HorizontalLineMinPct: 35,
            RemoveStaffSystems: true));

        ocrCleanup = runtime.Cleanup;
        docxOcrConverter = new DocxToChoConverterOpenXmlImageOcr(runtime.TextEngine);
        docxLayoutConverter = new DocxToChoConverterOpenXmlImageOcrDualLayout(runtime.ChordLayoutEngine, runtime.LyricLayoutEngine);
        pdfLayoutCleanup = null;

        try
        {
            var renderer2 = new LoggingPdfRenderer(new DocnetPdfRenderer());
            pdfLayoutConverter = new PdfToChoConverterImproved(renderer2, runtime.ChordLayoutEngine, runtime.LyricLayoutEngine);
        }
        catch
        {
            pdfLayoutConverter = null;
        }

        pdfOcrOptions = new PdfConverterOptions(
            AllowOcr: true,
            OcrDpi: ocrDpi,
            OcrDumpDirectory: outputDir,
            AbortOnNoChords: true,
            AbortOnImageOnly: false,
            PageRenderer: renderer,
            OcrEngine: runtime.TextEngine);

        Console.WriteLine($"OCR-Backend aktiv: {runtime.Description}");
        Console.WriteLine($"OCR-Modus: {(useFastOcr ? "fast" : "quality")} ({ocrDpi} DPI)");

        return true;
    }
    catch (NotSupportedException ex) when (requestedOcrBackend != OcrBackend.Tesseract)
    {
        Console.WriteLine($"Warnung: OCR-Backend '{requestedOcrBackend}' ist noch nicht aktiv: {ex.Message}");
        Console.WriteLine("Fallback auf Tesseract.");

        requestedOcrBackend = OcrBackend.Tesseract;
        return EnsureOcrInitialized();
    }
    catch
    {
        // best-effort; plain OCR fallback still works
        docxLayoutConverter = null;
        pdfLayoutConverter = null;
        pdfOcrOptions = null;
        docxOcrConverter = null;
        ocrCleanup = null;
        return false;
    }
}

/// <summary>
/// Returns <c>true</c> if the given CHO text contains no meaningful content beyond directives.
/// </summary>
/// <param name="cho">The ChordPro text to inspect.</param>
static bool IsEffectivelyEmptyCho(string cho)
{
    if (string.IsNullOrWhiteSpace(cho)) return true;

    int contentLines = 0;
    int totalContentChars = 0;
    foreach (var raw in cho.Replace("\r\n", "\n").Split('\n'))
    {
        var line = raw?.Trim() ?? string.Empty;
        if (line.Length == 0) continue;
        if (line.StartsWith("{", StringComparison.Ordinal) && line.EndsWith("}", StringComparison.Ordinal))
            continue;
        contentLines++;
        totalContentChars += line.Length;
    }

    if (contentLines == 0) return true;
    if (totalContentChars < 20) return true;
    if ((double)totalContentChars / contentLines < 3.0) return true;

    return false;
}

/// <summary>
/// Resolves the list of input files and the output directory from command-line arguments.
/// Falls back to folder-browser dialogs when arguments are missing.
/// </summary>
/// <param name="args">Command-line arguments passed to the process.</param>
/// <returns>A tuple of the resolved file list and the output directory path.</returns>
static (List<string> inputFiles, string outputDir) ResolveInputs(string[] args)
{
    var inputs = new List<string>();
    string inputDir = null;
    string outputDir = null;

    for (int i = 0; i < (args?.Length ?? 0); i++)
    {
        var a = args[i];
        if (string.IsNullOrWhiteSpace(a)) continue;

        if (string.Equals(a, "-o", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--output", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 < args.Length)
            {
                outputDir = args[i + 1];
                i++;
            }
            continue;
        }

        if (File.Exists(a))
        {
            inputs.Add(Path.GetFullPath(a));
            continue;
        }

        if (Directory.Exists(a))
        {
            if (inputDir == null) inputDir = Path.GetFullPath(a);
            else if (outputDir == null) outputDir = Path.GetFullPath(a);
            continue;
        }
    }

    if (inputs.Count == 0)
    {
        if (string.IsNullOrWhiteSpace(inputDir) || !Directory.Exists(inputDir))
        {
            inputDir = PromptForFolder("Quell-Ordner auswählen");
        }

        if (!string.IsNullOrWhiteSpace(inputDir) && Directory.Exists(inputDir))
        {
            inputs.AddRange(Directory.EnumerateFiles(inputDir, "*.docx"));
            inputs.AddRange(Directory.EnumerateFiles(inputDir, "*.pdf"));
        }
    }

    if (string.IsNullOrWhiteSpace(outputDir))
    {
        outputDir = PromptForFolder("Ziel-Ordner auswählen");
    }

    if (!string.IsNullOrWhiteSpace(outputDir) && !Directory.Exists(outputDir))
        Directory.CreateDirectory(outputDir);

    // De-dup.
    inputs = inputs.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .ToList();

    return (inputs, outputDir);
}

/// <summary>Writes a formatted status line (<c>STATUS: filename - message</c>) to stdout.</summary>
/// <param name="status">Status token such as <c>"OK"</c>, <c>"SKIP"</c>, or <c>"FAIL"</c>.</param>
/// <param name="filePath">Full path of the file being processed; only the filename part is printed.</param>
/// <param name="message">Optional detail message appended after a dash separator.</param>
static void WriteStatus(string status, string filePath, string message)
{
    var name = Path.GetFileName(filePath);
    if (string.IsNullOrWhiteSpace(message))
        Console.WriteLine($"{status}: {name}");
    else
        Console.WriteLine($"{status}: {name} - {message}");
}

/// <summary>
/// Converts a single DOCX file to ChordPro format, falling back to OCR if the text extraction yields an empty result.
/// </summary>
/// <returns>A tuple of the status token (<c>"OK"</c>, <c>"SKIP"</c>, <c>"FAIL"</c>) and a detail message.</returns>
static (string status, string message) ProcessDocx(
    string docxPath,
    string outputDir,
    DocxToChoConverterOpenXml docxConverter,
    ref DocxToChoConverterOpenXmlImageOcr docxOcrConverter,
    ref DocxToChoConverterOpenXmlImageOcrDualLayout docxLayoutConverter,
    Func<bool> ensureOcrInitialized,
    Encoding encoding)
{
    try
    {
        var baseName = Path.GetFileNameWithoutExtension(docxPath);
        var text = docxConverter.Convert(docxPath, new ConverterOptions());

        if (string.IsNullOrWhiteSpace(text) || IsEffectivelyEmptyCho(text))
        {
            // Lazy OCR fallback for image-only DOCX.
            if (docxOcrConverter == null && ensureOcrInitialized != null)
            {
                if (!ensureOcrInitialized())
                {
                    return ("SKIP", "DOCX enthält keine konvertierbaren Texte (OCR nicht verfügbar)");
                }
            }

            // Prefer layout OCR (better chord placement), fallback to plain OCR.
            if (docxLayoutConverter != null)
            {
                text = docxLayoutConverter.Convert(docxPath, new ConverterOptions());
                text = OcrOutputCleaner.Clean(text);
            }

            if ((string.IsNullOrWhiteSpace(text) || IsEffectivelyEmptyCho(text)) && docxOcrConverter != null)
            {
                text = docxOcrConverter.Convert(docxPath, new ConverterOptions());
                text = OcrOutputCleaner.Clean(text);
            }
        }

        if (string.IsNullOrWhiteSpace(text) || IsEffectivelyEmptyCho(text))
        {
            return IsLikelyNotenFile(docxPath)
                ? ("SKIP", "Notenversion (kein konvertierbarer Text/Akkorde)")
                : ("FAIL", "CHO ist leer");
        }

        var docxOut = Path.Combine(outputDir, baseName + ".docx.converted.cho");
        var baseOut = Path.Combine(outputDir, baseName + ".converted.cho");
        File.WriteAllText(docxOut, text, encoding);
        File.WriteAllText(baseOut, text, encoding);
        return ("OK", Path.GetFileName(docxOut));
    }
    catch (Exception ex)
    {
        if (IsNotenLikeException(ex) || IsLikelyNotenFile(docxPath))
            return ("SKIP", ex.Message);
        return ("FAIL", ex.Message);
    }
}

/// <summary>
/// Converts a single PDF file to ChordPro format. Uses OCR and layout-OCR as fallbacks
/// depending on the <paramref name="enableOcr"/> flag and whether text extraction succeeds.
/// </summary>
/// <returns>A tuple of the status token (<c>"OK"</c>, <c>"SKIP"</c>, <c>"FAIL"</c>) and a detail message.</returns>
static (string status, string message) ProcessPdf(
    string pdfPath,
    string outputDir,
    PdfToChoConverterPdfSharpCore pdfConverter,
    bool enableOcr,
    OcrBackend requestedOcrBackend,
    ref IPdfToChoConverter pdfLayoutConverter,
    PdfConverterOptions pdfOptions,
    ref PdfConverterOptions pdfOcrOptions,
    Func<bool> ensureOcrInitialized,
    Encoding encoding)
{
    try
    {
        var baseName = Path.GetFileNameWithoutExtension(pdfPath);
        string text = null;
        string errorMsg = null;

        // Primary: PdfSharpCore text-layer extraction (fast, works for text-based PDFs)
        (text, errorMsg) = pdfConverter.Convert(pdfPath, pdfOptions);

        // OCR Fallback: only when PdfSharpCore returned empty (true image-only PDF) and --ocr flag is set
        if ((string.IsNullOrWhiteSpace(text) || IsEffectivelyEmptyCho(text)) && enableOcr)
        {
            if (ensureOcrInitialized != null && ensureOcrInitialized())
            {
                var candidates = new List<(string text, string error, string source)>();

                if (pdfLayoutConverter != null)
                {
                    var layoutOptions = new PdfConverterOptions(
                        TabWidth: pdfOptions?.TabWidth ?? 12,
                        OcrDpi: pdfOcrOptions?.OcrDpi ?? pdfOptions?.OcrDpi ?? 300);
                    var (layoutText, layoutErr) = pdfLayoutConverter.Convert(pdfPath, layoutOptions);
                    candidates.Add((layoutText, layoutErr, "layout-ocr"));
                }

                if (pdfOcrOptions != null)
                {
                    var (plainOcrText, plainOcrErr) = pdfConverter.Convert(pdfPath, pdfOcrOptions);
                    candidates.Add((plainOcrText, plainOcrErr, "plain-ocr"));
                }

                var scoredCandidates = candidates
                    .Select(c =>
                    {
                        var cleaned = string.IsNullOrWhiteSpace(c.text) ? c.text : OcrOutputCleaner.Clean(c.text);
                        return (text: cleaned, c.error, c.source, score: ScoreChoQuality(cleaned, baseName, c.source));
                    })
                    .ToList();

                if (requestedOcrBackend == OcrBackend.Paddle)
                {
                    var layout = scoredCandidates.FirstOrDefault(c => string.Equals(c.source, "layout-ocr", StringComparison.OrdinalIgnoreCase));
                    var plain = scoredCandidates.FirstOrDefault(c => string.Equals(c.source, "plain-ocr", StringComparison.OrdinalIgnoreCase));

                    var hybridText = MergeChordRichPdfCandidate(layout.text, plain.text);
                    if (!string.IsNullOrWhiteSpace(hybridText) && !string.Equals(hybridText, layout.text, StringComparison.Ordinal))
                    {
                        scoredCandidates.Add((
                            text: hybridText,
                            error: string.Join(" | ", new[] { layout.error, plain.error }.Where(e => !string.IsNullOrWhiteSpace(e)).Distinct()),
                            source: "layout+plain-chords",
                            score: ScoreChoQuality(hybridText, baseName, "layout-ocr")));
                    }
                }

                scoredCandidates = scoredCandidates
                    .OrderByDescending(c => c.score)
                    .ToList();

                var best = SelectBestPdfCandidate(scoredCandidates, requestedOcrBackend);

                if (!string.IsNullOrWhiteSpace(best.text) && !IsEffectivelyEmptyCho(best.text))
                {
                    text = best.text;
                    errorMsg = best.error;
                }
            }
        }


static (string text, string error, string source, double score) SelectBestPdfCandidate(
    List<(string text, string error, string source, double score)> candidates,
    OcrBackend requestedOcrBackend)
{
    if (candidates == null || candidates.Count == 0)
        return default;

    if (requestedOcrBackend == OcrBackend.Paddle)
    {
        var layout = candidates.FirstOrDefault(c => string.Equals(c.source, "layout-ocr", StringComparison.OrdinalIgnoreCase));
        var plain = candidates.FirstOrDefault(c => string.Equals(c.source, "plain-ocr", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(layout.text) && !IsEffectivelyEmptyCho(layout.text))
        {
            if (string.IsNullOrWhiteSpace(plain.text) || IsEffectivelyEmptyCho(plain.text))
                return layout;

            // Paddle's strength is layout + region OCR; only abandon it when the layout result is clearly unusable.
            if (layout.score >= 40 || plain.score < layout.score + 55)
                return layout;
        }
    }

    return candidates.OrderByDescending(c => c.score).First();
}

static string MergeChordRichPdfCandidate(string preferredText, string donorText)
{
    if (string.IsNullOrWhiteSpace(preferredText) || string.IsNullOrWhiteSpace(donorText))
        return preferredText;

    var preferredChordCount = CountBracketChords(preferredText);
    var donorChordCount = CountBracketChords(donorText);
    if (preferredChordCount >= 6 || donorChordCount <= preferredChordCount + 2)
        return preferredText;

    var preferredLines = preferredText.Replace("\r\n", "\n").Split('\n').ToList();
    var donorSections = BuildChordedDonorSections(donorText);
    var usedDonorLines = new HashSet<string>(StringComparer.Ordinal);
    var mergedLines = new List<string>(preferredLines.Count);
    var currentSection = string.Empty;
    var mergedChordCount = 0;

    foreach (var line in preferredLines)
    {
        var trimmed = (line ?? string.Empty).Trim();
        if (trimmed.StartsWith("{comment:", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith("}", StringComparison.Ordinal))
        {
            currentSection = ExtractStructureCommentLabel(trimmed);
            mergedLines.Add(line);
            continue;
        }

        if (trimmed.Length == 0 || trimmed.StartsWith("{", StringComparison.Ordinal) || CountBracketChords(trimmed) > 0)
        {
            mergedLines.Add(line);
            mergedChordCount += CountBracketChords(trimmed);
            continue;
        }

        var donorLine = FindBestChordedDonorLine(trimmed, currentSection, donorSections, usedDonorLines);
        if (donorLine == null)
        {
            mergedLines.Add(line);
            continue;
        }

        var merged = TransferChordsToLyricLine(trimmed, donorLine);
        mergedLines.Add(merged);
        mergedChordCount += CountBracketChords(merged);
    }

    var mergedText = string.Join("\n", mergedLines);
    return mergedChordCount > preferredChordCount ? mergedText : preferredText;
}

static Dictionary<string, List<string>> BuildChordedDonorSections(string text)
{
    var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    var currentSection = string.Empty;

    foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
    {
        var line = raw?.Trim() ?? string.Empty;
        if (line.StartsWith("{comment:", StringComparison.OrdinalIgnoreCase) && line.EndsWith("}", StringComparison.Ordinal))
        {
            currentSection = ExtractStructureCommentLabel(line);
            continue;
        }

        if (CountBracketChords(line) == 0)
            continue;

        if (!sections.TryGetValue(currentSection, out var bucket))
        {
            bucket = new List<string>();
            sections[currentSection] = bucket;
        }

        bucket.Add(raw);
    }

    return sections;
}

static string FindBestChordedDonorLine(
    string preferredLyricLine,
    string section,
    Dictionary<string, List<string>> donorSections,
    HashSet<string> usedDonorLines)
{
    var candidates = new List<string>();
    if (donorSections.TryGetValue(section ?? string.Empty, out var sectionLines))
        candidates.AddRange(sectionLines);

    if (candidates.Count == 0)
        candidates.AddRange(donorSections.SelectMany(kvp => kvp.Value));

    var preferredNormalized = NormalizeLyricForMatching(preferredLyricLine);
    var bestScore = 0.0;
    string bestLine = null;

    foreach (var candidate in candidates)
    {
        if (usedDonorLines.Contains(candidate))
            continue;

        var donorLyric = StripChordsFromLine(candidate);
        var donorNormalized = NormalizeLyricForMatching(donorLyric);
        if (preferredNormalized.Length == 0 || donorNormalized.Length == 0)
            continue;

        var similarity = ComputeLyricSimilarity(preferredNormalized, donorNormalized);
        if (similarity > bestScore)
        {
            bestScore = similarity;
            bestLine = candidate;
        }
    }

    if (bestScore < 0.32)
        return null;

    usedDonorLines.Add(bestLine);
    return bestLine;
}

static string TransferChordsToLyricLine(string preferredLyricLine, string donorChordedLine)
{
    var anchors = ExtractChordAnchors(donorChordedLine, out var donorLyric);
    if (anchors.Count == 0 || string.IsNullOrWhiteSpace(preferredLyricLine))
        return preferredLyricLine;

    var targetPositions = new List<(int position, string chord)>();
    foreach (var (position, chord) in anchors)
    {
        var scaled = donorLyric.Length == 0
            ? 0
            : (int)Math.Round(position / (double)Math.Max(1, donorLyric.Length) * preferredLyricLine.Length);
        targetPositions.Add((SnapToWordBoundary(preferredLyricLine, scaled), chord));
    }

    return ChordProHelper.InsertChordsInline(preferredLyricLine, targetPositions);
}

static List<(int position, string chord)> ExtractChordAnchors(string chordedLine, out string lyricText)
{
    var anchors = new List<(int position, string chord)>();
    var lyricBuilder = new StringBuilder();

    for (int index = 0; index < (chordedLine?.Length ?? 0); index++)
    {
        if (chordedLine[index] == '[')
        {
            var end = chordedLine.IndexOf(']', index + 1);
            if (end > index)
            {
                var chord = chordedLine.Substring(index + 1, end - index - 1).Trim();
                if (!string.IsNullOrWhiteSpace(chord))
                    anchors.Add((lyricBuilder.Length, chord));
                index = end;
                continue;
            }
        }

        lyricBuilder.Append(chordedLine[index]);
    }

    lyricText = lyricBuilder.ToString();
    return anchors;
}

static int SnapToWordBoundary(string text, int position)
{
    if (string.IsNullOrEmpty(text)) return 0;
    position = Math.Max(0, Math.Min(position, text.Length));
    if (position == 0) return 0;

    var wordMatches = Regex.Matches(text, @"\S+").Cast<Match>().ToList();
    if (wordMatches.Count == 0) return 0;

    var best = wordMatches
        .Select(match => match.Index)
        .OrderBy(candidate => Math.Abs(candidate - position))
        .ThenBy(candidate => candidate)
        .First();

    return best;
}

static string StripChordsFromLine(string line)
{
    if (string.IsNullOrWhiteSpace(line)) return string.Empty;
    return Regex.Replace(line, @"\[[^\]]+\]", string.Empty).Trim();
}

static string NormalizeLyricForMatching(string line)
{
    if (string.IsNullOrWhiteSpace(line)) return string.Empty;
    var normalized = line.ToLowerInvariant();
    normalized = normalized.Replace("ö", "o").Replace("ä", "a").Replace("ü", "u").Replace("ß", "ss");
    normalized = Regex.Replace(normalized, @"\[[^\]]+\]", string.Empty);
    normalized = Regex.Replace(normalized, @"[^a-z0-9]+", string.Empty);
    return normalized;
}

static double ComputeLyricSimilarity(string left, string right)
{
    if (string.Equals(left, right, StringComparison.Ordinal)) return 1.0;
    if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return 0.0;
    if (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal)) return 0.9;

    var distance = ComputeLevenshteinDistance(left, right);
    var maxLength = Math.Max(left.Length, right.Length);
    return maxLength == 0 ? 1.0 : 1.0 - (distance / (double)maxLength);
}

static string ExtractStructureCommentLabel(string commentLine)
{
    var match = Regex.Match(commentLine ?? string.Empty, @"^\{comment:\s*(.+?)\}\s*$", RegexOptions.IgnoreCase);
    return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
}

static int CountBracketChords(string text)
{
    return string.IsNullOrWhiteSpace(text) ? 0 : Regex.Matches(text, @"\[[^\]]+\]").Count;
}

static string BuildChordDonorTextFromProcessedDump(string dumpText, int tabWidth)
{
    if (string.IsNullOrWhiteSpace(dumpText))
        return string.Empty;

    var output = new List<string>();
    string pendingChordLine = null;

    foreach (var raw in dumpText.Replace("\r\n", "\n").Split('\n'))
    {
        var trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            continue;

        if (TryExtractProcessedSection(trimmed, out var sectionLabel, out var sectionChordTail))
        {
            output.Add($"{{comment: {sectionLabel}}}");
            pendingChordLine = ExtractChordSequence(sectionChordTail);
            continue;
        }

        var chordSequence = ExtractChordSequence(trimmed);
        if (!string.IsNullOrWhiteSpace(chordSequence) && !LooksLikeProcessedLyricLine(trimmed))
        {
            pendingChordLine = chordSequence;
            continue;
        }

        if (!LooksLikeProcessedLyricLine(trimmed))
            continue;

        var lyric = CleanupProcessedLyricLine(trimmed);
        if (string.IsNullOrWhiteSpace(lyric))
            continue;

        if (!string.IsNullOrWhiteSpace(pendingChordLine))
        {
            output.Add(InsertChordSequenceIntoLyric(pendingChordLine, lyric));
            pendingChordLine = null;
        }
        else
        {
            output.Add(lyric);
        }
    }

    var donorText = string.Join("\n", output);
    return OcrOutputCleaner.Clean(donorText);
}

static bool TryExtractProcessedSection(string line, out string sectionLabel, out string chordTail)
{
    sectionLabel = null;
    chordTail = null;
    if (string.IsNullOrWhiteSpace(line))
        return false;

    var match = Regex.Match(line, @"^(Strophe|Vers|Verse|Refr(?:ain)?|Rcfrnn|Ref(?:rain)?)\b\s*(.*)$", RegexOptions.IgnoreCase);
    if (!match.Success)
        return false;

    var label = match.Groups[1].Value;
    sectionLabel = Regex.IsMatch(label, @"^Refr", RegexOptions.IgnoreCase) ? "Chorus" : "Vers 1";
    chordTail = match.Groups[2].Value?.Trim();
    return true;
}

static string ExtractChordSequence(string line)
{
    if (string.IsNullOrWhiteSpace(line))
        return null;

    var tokens = Regex.Split(line.Trim(), @"\s+")
        .Select(token => Regex.Replace(token.Trim(), @"[^A-Za-z0-9#/b]+", string.Empty))
        .Where(token => !string.IsNullOrWhiteSpace(token))
        .ToList();

    var chordTokens = tokens
        .Where(token => Regex.IsMatch(token, @"^(?:[A-Ha-h](?:is|es|[#b])?)(?:m|maj|min|sus|add|dim|aug)?(?:2|4|5|6|7|9|11|13)?(?:sus[24])?(?:/[A-Ha-h](?:is|es|[#b])?)?$", RegexOptions.IgnoreCase))
        .ToList();

    return chordTokens.Count >= 2 ? string.Join("\t", chordTokens) : null;
}

static bool LooksLikeProcessedLyricLine(string line)
{
    if (string.IsNullOrWhiteSpace(line)) return false;
    var letters = line.Count(char.IsLetter);
    var words = Regex.Matches(line, @"\p{L}+").Count;
    return letters >= 8 && words >= 3;
}

static string CleanupProcessedLyricLine(string line)
{
    if (string.IsNullOrWhiteSpace(line))
        return string.Empty;

    var cleaned = line.Trim();
    cleaned = Regex.Replace(cleaned, @"^[^\p{L}\d]+", string.Empty);
    cleaned = Regex.Replace(cleaned, @"[_`~|]+", string.Empty);
    cleaned = cleaned.Replace("—", "-").Replace("–", "-");
    cleaned = Regex.Replace(cleaned, @"\bWor\s*-\s*te\b", "Worte", RegexOptions.IgnoreCase);
    cleaned = Regex.Replace(cleaned, @"\bLie\s*-\s*be\b", "Liebe", RegexOptions.IgnoreCase);
    cleaned = Regex.Replace(cleaned, @"\bEhr\s*-\s*furcht\b", "Ehrfurcht", RegexOptions.IgnoreCase);
    cleaned = Regex.Replace(cleaned, @"\bschöns\s*tes\b", "schönstes", RegexOptions.IgnoreCase);
    cleaned = Regex.Replace(cleaned, @"\bSeit\s*-?\s*dem\b", "Seitdem", RegexOptions.IgnoreCase);
    cleaned = Regex.Replace(cleaned, @"\bge\s*-?\s*fun\s*-?\s*den\b", "gefunden", RegexOptions.IgnoreCase);
    cleaned = Regex.Replace(cleaned, @"\bal\s*-?\s*lein\b", "allein", RegexOptions.IgnoreCase);
    cleaned = Regex.Replace(cleaned, @"\bEn\s*-?\s*de\b", "Ende", RegexOptions.IgnoreCase);
    cleaned = Regex.Replace(cleaned, @"\bGe\s*-?\s*bet\b", "Gebet", RegexOptions.IgnoreCase);
    cleaned = Regex.Replace(cleaned, @"\s*-\s*", " ");
    cleaned = Regex.Replace(cleaned, @"\s+([,.;:!?])", "$1");
    cleaned = Regex.Replace(cleaned, @"\s{2,}", " ");
    return cleaned.Trim();
}

static string InsertChordSequenceIntoLyric(string chordLine, string lyricLine)
{
    if (string.IsNullOrWhiteSpace(chordLine) || string.IsNullOrWhiteSpace(lyricLine))
        return lyricLine ?? string.Empty;

    var chordTokens = Regex.Split(chordLine.Trim(), @"\s+")
        .Where(token => !string.IsNullOrWhiteSpace(token))
        .ToList();
    var wordMatches = Regex.Matches(lyricLine, @"\S+").Cast<Match>().ToList();
    if (chordTokens.Count == 0 || wordMatches.Count == 0)
        return lyricLine;

    var anchors = new List<(int position, string chord)>();
    for (int i = 0; i < chordTokens.Count; i++)
    {
        var wordIndex = chordTokens.Count == 1
            ? 0
            : (int)Math.Round(i * (wordMatches.Count - 1) / (double)(chordTokens.Count - 1));
        var insertPos = wordMatches[Math.Max(0, Math.Min(wordIndex, wordMatches.Count - 1))].Index;
        anchors.Add((insertPos, chordTokens[i]));
    }

    return ChordProHelper.InsertChordsInline(lyricLine, anchors);
}

        if (!string.IsNullOrWhiteSpace(errorMsg))
        {
            // Treat "notenversion" warnings as skip.
            if (IsNotenLikeMessage(errorMsg) || IsLikelyNotenFile(pdfPath))
                return ("SKIP", errorMsg);
        }

        if (string.IsNullOrWhiteSpace(text) || IsEffectivelyEmptyCho(text))
        {
            return IsLikelyNotenFile(pdfPath)
                ? ("SKIP", "Notenversion (kein konvertierbarer Text/Akkorde)")
                : ("FAIL", "CHO ist leer");
        }

        text = OcrOutputCleaner.Clean(text);
        text = NormalizeWeakTitle(text, baseName);

        var processedDumpPath = Path.Combine(outputDir, baseName + ".pdf.processed.txt");
        if (File.Exists(processedDumpPath))
        {
            var processedDump = File.ReadAllText(processedDumpPath, encoding);
            var donorText = BuildChordDonorTextFromProcessedDump(processedDump, pdfOptions?.TabWidth ?? 12);
            if (!string.IsNullOrWhiteSpace(donorText))
            {
                donorText = NormalizeWeakTitle(OcrOutputCleaner.Clean(donorText), baseName);
                var merged = MergeChordRichPdfCandidate(text, donorText);
                var currentScore = ScoreChoQuality(text, baseName, "layout-ocr");
                var donorScore = ScoreChoQuality(donorText, baseName, "layout-ocr");
                var mergedScore = ScoreChoQuality(merged, baseName, "layout-ocr");
                var currentFragmented = CountFragmentedOcrLines(text);
                var donorFragmented = CountFragmentedOcrLines(donorText);
                var currentChords = CountBracketChords(text);
                var donorChords = CountBracketChords(donorText);
                var currentHasChorus = HasStructureComment(text, "Chorus");
                var donorHasChorus = HasStructureComment(donorText, "Chorus");

                if (mergedScore > currentScore + 8)
                    text = NormalizeWeakTitle(OcrOutputCleaner.Clean(merged), baseName);
                else if (CountBracketChords(merged) > currentChords)
                    text = NormalizeWeakTitle(OcrOutputCleaner.Clean(merged), baseName);
                else if (donorScore > currentScore + 18
                         || (currentFragmented >= donorFragmented + 2
                             && donorHasChorus
                             && (!currentHasChorus || donorChords >= currentChords + 2)
                             && donorScore >= currentScore - 8))
                    text = donorText;
            }
        }

        var companionDocxText = TryBuildCompanionDocxFallback(pdfPath, baseName);
        if (!string.IsNullOrWhiteSpace(companionDocxText))
        {
            companionDocxText = NormalizeWeakTitle(OcrOutputCleaner.Clean(companionDocxText), baseName);

            var currentScore = ScoreChoQuality(text, baseName);
            var mergedWithCompanion = MergeChordRichPdfCandidate(text, companionDocxText);
            var mergedScore = ScoreChoQuality(mergedWithCompanion, baseName, "layout-ocr");
            var companionScore = ScoreChoQuality(companionDocxText, baseName, "layout-ocr");
            var currentChords = CountBracketChords(text);
            var companionChords = CountBracketChords(companionDocxText);
            var currentHasChorus = HasStructureComment(text, "Chorus");
            var companionHasChorus = HasStructureComment(companionDocxText, "Chorus");
            var currentGibberish = CountLikelyGibberishLines(text);
            var companionGibberish = CountLikelyGibberishLines(companionDocxText);
            var currentFragmented = CountFragmentedOcrLines(text);
            var companionFragmented = CountFragmentedOcrLines(companionDocxText);
            var currentNumberedVerses = CountNumberedVerseLines(text);
            var companionNumberedVerses = CountNumberedVerseLines(companionDocxText);

            if (mergedScore > currentScore + 8)
                text = mergedWithCompanion;
            else if (currentFragmented >= companionFragmented + 2
                     && companionNumberedVerses >= 2
                     && companionChords >= currentChords + 2
                     && companionScore >= currentScore - 12)
                text = companionDocxText;
            else if (currentGibberish >= 6
                     && companionNumberedVerses >= 2
                     && companionChords >= currentChords + 2
                     && companionGibberish <= currentGibberish)
                text = companionDocxText;
            else if (!currentHasChorus
                     && companionHasChorus
                     && companionChords >= currentChords + 4
                     && companionGibberish <= Math.Max(0, currentGibberish - 2)
                     && companionScore >= currentScore - 10)
                text = companionDocxText;
              else if (currentNumberedVerses == 0
                     && companionNumberedVerses >= 2
                     && companionChords >= currentChords + 4
                     && companionGibberish <= Math.Max(0, currentGibberish)
                     && companionScore >= currentScore - 5)
                 text = companionDocxText;
            else if (companionScore > currentScore + 20 || (currentScore < 90 && companionScore > currentScore + 8))
                text = companionDocxText;
        }

        // Safety net: some sheet-music PDFs slip past staff-line detection.
        // If the resulting CHO looks like OCR garbage / notation artifacts, treat as SKIP.
        if (LooksLikeNotenFromCho(text) && ScoreChoQuality(text, baseName) < 180)
        {
            return ("SKIP", "Noten (typische Notensatz-/OCR-Artefakte erkannt)");
        }

        var outPath = Path.Combine(outputDir, baseName + ".pdf.converted.cho");
        File.WriteAllText(outPath, text, encoding);
        return ("OK", Path.GetFileName(outPath));
    }
    catch (Exception ex)
    {
        if (IsNotenLikeException(ex) || IsLikelyNotenFile(pdfPath))
            return ("SKIP", ex.Message);
        return ("FAIL", ex.Message);
    }
}

static double ScoreChoQuality(string cho, string expectedTitle = null, string source = null)
{
    if (string.IsNullOrWhiteSpace(cho)) return double.NegativeInfinity;

    var normalized = cho.Replace("\r\n", "\n");
    var directives = normalized.Split('\n')
        .Select(line => line.Trim())
        .Where(line => line.StartsWith("{", StringComparison.Ordinal) && line.EndsWith("}", StringComparison.Ordinal))
        .ToList();
    var nonDirectiveLines = normalized.Split('\n')
        .Select(line => line.Trim())
        .Where(line => line.Length > 0)
        .Where(line => !(line.StartsWith("{", StringComparison.Ordinal) && line.EndsWith("}", StringComparison.Ordinal)))
        .ToList();

    if (nonDirectiveLines.Count == 0) return double.NegativeInfinity;

    var letters = normalized.Count(char.IsLetter);
    var bracketChords = Regex.Matches(normalized, @"\[[^\]]+\]").Count;
    var comments = Regex.Matches(normalized, @"\{comment:\s*[^}]+\}", RegexOptions.IgnoreCase).Count;
    var weird = normalized.Count(c => c is '_' or '|' or '~' or '^' or '�');
    var title = ExtractDirectiveValue(directives, "title") ?? string.Empty;
    var subtitle = ExtractDirectiveValue(directives, "subtitle") ?? string.Empty;
    var digitHeavyLines = nonDirectiveLines.Count(line => line.Count(char.IsDigit) > line.Count(char.IsLetter));
    var shortLines = nonDirectiveLines.Count(line => line.Length <= 4);
    var gibberishLines = nonDirectiveLines.Count(line =>
    {
        var lineLetters = line.Count(char.IsLetter);
        var lineDigits = line.Count(char.IsDigit);
        var lineSymbols = line.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && c is not '[' and not ']');
        var uppercase = line.Count(char.IsUpper);
        var letterRatio = lineLetters / (double)Math.Max(1, line.Length);
        return letterRatio < 0.35 ||
               lineSymbols >= 8 ||
               (lineDigits >= 3 && lineDigits > lineLetters) ||
               (uppercase >= 8 && uppercase > (lineLetters * 0.65));
    });
    var suspiciousTitle = nonDirectiveLines.Take(1).Count(line =>
        line.Any(char.IsDigit) ||
        line.Count(char.IsUpper) > Math.Max(4, line.Count(char.IsLetter) * 0.7) ||
        line.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c)) >= 4);
    var titlePenalty = ComputeTitlePenalty(title, expectedTitle);
    var subtitlePenalty = subtitle.Any(char.IsDigit) ? 8.0 : 0.0;
    var layoutBonus = string.Equals(source, "layout-ocr", StringComparison.OrdinalIgnoreCase) ? 10.0 : 0.0;

    return letters
           + (bracketChords * 18.0)
           + (comments * 4.0)
           + (nonDirectiveLines.Count * 3.0)
           + layoutBonus
           - (weird * 6.0)
           - (digitHeavyLines * 10.0)
           - (shortLines * 4.0)
           - (gibberishLines * 35.0)
           - (suspiciousTitle * 60.0)
           - titlePenalty
           - subtitlePenalty;
}

static string ExtractDirectiveValue(List<string> directives, string name)
{
    var prefix = $"{{{name}:";
    foreach (var directive in directives)
    {
        if (!directive.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
        return directive.Substring(prefix.Length, directive.Length - prefix.Length - 1).Trim();
    }
    return null;
}

static string NormalizeWeakTitle(string cho, string fallbackTitle)
{
    if (string.IsNullOrWhiteSpace(cho) || string.IsNullOrWhiteSpace(fallbackTitle))
        return cho;

    var lines = cho.Replace("\r\n", "\n").Split('\n').ToList();
    for (int i = 0; i < lines.Count; i++)
    {
        var line = lines[i].Trim();
        if (!line.StartsWith("{title:", StringComparison.OrdinalIgnoreCase) || !line.EndsWith("}", StringComparison.Ordinal))
            continue;

        var currentTitle = line.Substring(7, line.Length - 8).Trim();
        var normalizedCurrent = NormalizeTitleForComparison(currentTitle);
        var normalizedFallback = NormalizeTitleForComparison(fallbackTitle);
        var similarity = ComputeTitleSimilarity(normalizedCurrent, normalizedFallback);
        var suspicious = currentTitle.Any(char.IsDigit)
            || currentTitle.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && c is not '-' and not ':' and not ',') >= 2
            || currentTitle.Count(char.IsUpper) > Math.Max(4, currentTitle.Count(char.IsLetter) * 0.8);

        if (similarity < 0.45 || suspicious)
            lines[i] = $"{{title: {fallbackTitle}}}";

        break;
    }

    return string.Join("\n", lines);
}

static bool HasStructureComment(string cho, string label)
{
    if (string.IsNullOrWhiteSpace(cho) || string.IsNullOrWhiteSpace(label))
        return false;

    return Regex.IsMatch(cho, $@"\{{comment:\s*{Regex.Escape(label)}\s*\}}", RegexOptions.IgnoreCase);
}

static int CountLikelyGibberishLines(string cho)
{
    if (string.IsNullOrWhiteSpace(cho))
        return 0;

    return cho.Replace("\r\n", "\n")
        .Split('\n')
        .Select(line => line.Trim())
        .Where(line => line.Length > 0)
        .Where(line => !(line.StartsWith("{", StringComparison.Ordinal) && line.EndsWith("}", StringComparison.Ordinal)))
        .Count(line =>
        {
            var letters = line.Count(char.IsLetter);
            var digits = line.Count(char.IsDigit);
            var symbols = line.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && c is not '[' and not ']');
            var letterRatio = letters / (double)Math.Max(1, line.Length);
            return letterRatio < 0.45
                || (digits >= 2 && digits > letters / 3)
                || symbols >= 5
                || (letters < 8 && line.Length > 10);
        });
}

static int CountFragmentedOcrLines(string cho)
{
    if (string.IsNullOrWhiteSpace(cho))
        return 0;

    return cho.Replace("\r\n", "\n")
        .Split('\n')
        .Select(line => Regex.Replace(line ?? string.Empty, @"\[[^\]]+\]", string.Empty).Trim())
        .Where(line => line.Length > 0)
        .Where(line => !(line.StartsWith("{", StringComparison.Ordinal) && line.EndsWith("}", StringComparison.Ordinal)))
        .Count(line =>
        {
            var tokens = Regex.Matches(line, @"\p{L}+|\d+|\S")
                .Cast<Match>()
                .Select(match => match.Value)
                .ToList();
            if (tokens.Count < 5)
                return false;

            var wordTokens = tokens.Where(token => token.Any(char.IsLetter)).ToList();
            if (wordTokens.Count < 4)
                return false;

            var tinyWords = wordTokens.Count(token => token.Length <= 2);
            var singleUpperTokens = wordTokens.Count(token => token.Length == 1 && char.IsUpper(token[0]));
            var punctuationBursts = Regex.Matches(line, @"[^\p{L}\d\s]{2,}").Count;

            return tinyWords >= 4 && tinyWords >= (int)Math.Ceiling(wordTokens.Count * 0.35)
                || singleUpperTokens >= 2
                || punctuationBursts >= 2;
        });
}

static int CountNumberedVerseLines(string cho)
{
    if (string.IsNullOrWhiteSpace(cho))
        return 0;

    return cho.Replace("\r\n", "\n")
        .Split('\n')
        .Select(line => line.Trim())
        .Count(line => Regex.IsMatch(line, @"^(?:\[[^\]]+\])?\s*(?:\d+|[lI])\s*[.)]", RegexOptions.CultureInvariant));
}

static string TryBuildCompanionDocxFallback(string pdfPath, string expectedTitle)
{
    try
    {
        var companionDocx = FindCompanionDocxPath(pdfPath, expectedTitle);
        if (string.IsNullOrWhiteSpace(companionDocx) || !File.Exists(companionDocx))
            return null;

        var docxConverter = new DocxToChoConverterOpenXml();
        var text = docxConverter.Convert(companionDocx, new ConverterOptions());
        if (!string.IsNullOrWhiteSpace(text) && !IsEffectivelyEmptyCho(text) && ScoreChoQuality(text, expectedTitle) >= 90)
            return text;

        var ocrDataPath = FindOcrDataDir();
        if (string.IsNullOrWhiteSpace(ocrDataPath) || !HasOcrDataFiles(ocrDataPath))
            return null;

        var runtime = OcrRuntimeFactory.Create(new OcrBackendOptions(
            Backend: OcrBackend.Tesseract,
            Language: "deu+eng",
            DataPath: ocrDataPath,
            ScalePercent: 200,
            PreferDenseLayout: true,
            RemoveHorizontalLines: true,
            HorizontalLineMinPct: 35,
            RemoveStaffSystems: true));

        using (runtime.Cleanup)
        {
            var layoutConverter = new DocxToChoConverterOpenXmlImageOcrDualLayout(runtime.ChordLayoutEngine, runtime.LyricLayoutEngine);
            var candidates = new List<string>();

            text = layoutConverter.Convert(companionDocx, new ConverterOptions());
            if (!string.IsNullOrWhiteSpace(text) && !IsEffectivelyEmptyCho(text))
                candidates.Add(text);

            var plainConverter = new DocxToChoConverterOpenXmlImageOcr(runtime.TextEngine);
            text = plainConverter.Convert(companionDocx, new ConverterOptions());
            if (!string.IsNullOrWhiteSpace(text) && !IsEffectivelyEmptyCho(text))
                candidates.Add(text);

            return candidates
                .Select(candidate => NormalizeWeakTitle(OcrOutputCleaner.Clean(candidate), expectedTitle))
                .OrderByDescending(candidate => ScoreChoQuality(candidate, expectedTitle, "layout-ocr"))
                .FirstOrDefault();
        }
    }
    catch
    {
        return null;
    }
}

static string FindCompanionDocxPath(string pdfPath, string expectedTitle)
{
    var directory = Path.GetDirectoryName(pdfPath);
    if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        return null;

    var expected = NormalizeCompanionName(expectedTitle);
    var bestPath = default(string);
    var bestScore = 0.0;

    foreach (var candidate in Directory.EnumerateFiles(directory, "*.docx"))
    {
        var candidateName = NormalizeCompanionName(Path.GetFileNameWithoutExtension(candidate));
        if (string.IsNullOrWhiteSpace(candidateName))
            continue;

        var score = ComputeTitleSimilarity(expected, candidateName);
        if (expected.Length > 0 && candidateName.Contains(expected, StringComparison.Ordinal))
            score += 0.15;
        else if (candidateName.Length > 0 && expected.Contains(candidateName, StringComparison.Ordinal))
            score += 0.15;

        if (score > bestScore)
        {
            bestScore = score;
            bestPath = candidate;
        }
    }

    return bestScore >= 0.72 ? bestPath : null;
}

static string NormalizeCompanionName(string value)
{
    var normalized = NormalizeTitleForComparison(value);
    return Regex.Replace(normalized, @"\d+$", string.Empty);
}

static double ComputeTitlePenalty(string title, string expectedTitle)
{
    if (string.IsNullOrWhiteSpace(title)) return 40.0;

    double penalty = 0.0;
    if (title.Any(char.IsDigit)) penalty += 18.0;
    if (title.Count(c => c is ':' or ';' or '|' or '_' or '?' or '!') >= 1) penalty += 18.0;

    var letters = title.Count(char.IsLetter);
    var uppers = title.Count(char.IsUpper);
    if (letters > 0 && uppers > Math.Max(4, letters * 0.72)) penalty += 14.0;

    if (!string.IsNullOrWhiteSpace(expectedTitle))
    {
        var normalizedTitle = NormalizeTitleForComparison(title);
        var normalizedExpected = NormalizeTitleForComparison(expectedTitle);
        var similarity = ComputeTitleSimilarity(normalizedTitle, normalizedExpected);
        if (similarity < 0.45) penalty += 30.0;
        else if (similarity < 0.70) penalty += 12.0;
    }

    return penalty;
}

static string NormalizeTitleForComparison(string value)
{
    if (string.IsNullOrWhiteSpace(value)) return string.Empty;
    var normalized = value.ToLowerInvariant();
    normalized = normalized.Replace("ö", "o").Replace("ä", "a").Replace("ü", "u").Replace("ß", "ss");
    normalized = Regex.Replace(normalized, @"[^a-z0-9]+", string.Empty);
    return normalized;
}

static double ComputeTitleSimilarity(string left, string right)
{
    if (string.Equals(left, right, StringComparison.Ordinal)) return 1.0;
    if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return 0.0;

    var distance = ComputeLevenshteinDistance(left, right);
    var maxLength = Math.Max(left.Length, right.Length);
    return maxLength == 0 ? 1.0 : 1.0 - (distance / (double)maxLength);
}

static int ComputeLevenshteinDistance(string left, string right)
{
    var rows = left.Length + 1;
    var cols = right.Length + 1;
    var matrix = new int[rows, cols];

    for (int row = 0; row < rows; row++) matrix[row, 0] = row;
    for (int col = 0; col < cols; col++) matrix[0, col] = col;

    for (int row = 1; row < rows; row++)
    {
        for (int col = 1; col < cols; col++)
        {
            var cost = left[row - 1] == right[col - 1] ? 0 : 1;
            matrix[row, col] = Math.Min(
                Math.Min(matrix[row - 1, col] + 1, matrix[row, col - 1] + 1),
                matrix[row - 1, col - 1] + cost);
        }
    }

    return matrix[rows - 1, cols - 1];
}

/// <summary>
/// Heuristically determines whether a converted CHO text looks like OCR output from sheet music
/// rather than a real chord/lyric sheet, based on symbol ratios and token junk analysis.
/// </summary>
/// <param name="cho">The ChordPro text to analyze.</param>
/// <returns><c>true</c> if the text appears to originate from sheet-music OCR garbage.</returns>
static bool LooksLikeNotenFromCho(string cho)
{
    if (string.IsNullOrWhiteSpace(cho)) return false;

    // Build a short sample of non-directive content.
    var sampleLines = new List<string>();
    foreach (var raw in cho.Replace("\r\n", "\n").Split('\n'))
    {
        var line = raw?.Trim() ?? string.Empty;
        if (line.Length == 0) continue;
        if (line.StartsWith("{", StringComparison.Ordinal) && line.EndsWith("}", StringComparison.Ordinal))
            continue;

        sampleLines.Add(line);
        if (sampleLines.Count >= 80) break;
    }

    var sample = string.Join("\n", sampleLines);
    if (sample.Length == 0) return false;

    int total = 0;
    int letters = 0;
    int weird = 0;
    int scoreSymbols = 0;

    // Characters that are common in normal lyric/chord sheets.
    const string allowedPunct = "[](){}:;,.!?\"'`+-*/#&%|=";

    foreach (var ch in sample)
    {
        if (char.IsWhiteSpace(ch)) continue;
        total++;

        if (char.IsLetter(ch)) { letters++; continue; }
        if (char.IsDigit(ch)) continue;

        // Symbols that are very common in OCR of notation/staves.
        if (ch == '=' || ch == '_' || ch == '|' || ch == '—' || ch == '–') scoreSymbols++;
        if (allowedPunct.IndexOf(ch) >= 0) continue;

        // Common chord modifiers/symbols
        if (ch == '°' || ch == '♭' || ch == '♯') continue;

        weird++;
    }

    if (total == 0) return false;

    var letterRatio = letters / (double)total;
    var weirdRatio = weird / (double)total;

    // Strong notation/OCR garbage signals: underline/equal runs, dash runs, lots of pipes/slashes.
    bool hasGarbageRuns = Regex.IsMatch(sample, @"[=_]{3,}|[-–—]{6,}|\|{4,}|\\/{3,}|\*{4,}");

    // Token-level junk ratio: OCR on notation tends to produce many tiny fragments and punctuation-heavy tokens.
    var tokens = Regex.Split(sample, @"\s+").Where(t => !string.IsNullOrWhiteSpace(t)).Take(600).ToArray();
    int tokenCount = tokens.Length;
    int junkTokens = 0;
    if (tokenCount > 0)
    {
        static bool IsCommonShortWord(string t)
        {
            return t is "a" or "an" or "am" or "im" or "in" or "to" or "of" or "my" or "we" or "me" or "du" or "ich" or "der" or "die" or "das" or "und" or "ist" or "ein" or "eine";
        }

        foreach (var tok0 in tokens)
        {
            var tok = tok0.Trim().Trim('[', ']', '(', ')', '{', '}', ',', '.', ';', ':', '"', '\'', '`');
            if (tok.Length == 0) { junkTokens++; continue; }

            if (tok.Contains('=') || tok.Contains('_') || tok.Contains('¥')) { junkTokens++; continue; }

            // punctuation-only or mostly punctuation
            int letterInTok = tok.Count(char.IsLetter);
            int digitInTok = tok.Count(char.IsDigit);
            int nonWord = tok.Length - letterInTok - digitInTok;
            if (letterInTok == 0 && digitInTok == 0) { junkTokens++; continue; }
            if (nonWord >= 2 && nonWord >= tok.Length / 2) { junkTokens++; continue; }

            if (tok.Length <= 2 && !IsCommonShortWord(tok.ToLowerInvariant()))
            {
                // Single letters/numbers (e.g. "T", "P", "7") are common OCR fragments in notation.
                junkTokens++;
                continue;
            }
        }
    }

    var junkRatio = tokenCount == 0 ? 0.0 : junkTokens / (double)tokenCount;

    // If it looks like mostly symbols (not words), it is very likely notation OCR.
    if (weirdRatio > 0.45) return true;

    // If score-like symbols appear very frequently, it's also a strong signal for notation.
    var scoreRatio = scoreSymbols / (double)total;
    if (scoreRatio > 0.15 && (letterRatio < 0.70 || weirdRatio > 0.15)) return true;

    // Combined signal: lots of OCR junk fragments plus any other garbage hint.
    if (junkRatio > 0.35 && (hasGarbageRuns || scoreRatio > 0.10 || weirdRatio > 0.25)) return true;

    // If we have clear garbage-run patterns and not enough letters, treat as notation.
    if (hasGarbageRuns && (letterRatio < 0.55 || weirdRatio > 0.25)) return true;

    return false;
}

/// <summary>
/// Returns <c>true</c> if the filename contains patterns commonly used for sheet-music files
/// (e.g. contains <c>"noten"</c>).
/// </summary>
/// <param name="path">Full or relative file path to check.</param>
static bool IsLikelyNotenFile(string path)
{
    var name = (Path.GetFileNameWithoutExtension(path) ?? string.Empty);
    return name.Contains("noten", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("_noten", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("(noten", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Inspects all raster images embedded in a DOCX archive and returns <c>true</c>
/// if any of them pass the sheet-music detection heuristic.
/// </summary>
/// <param name="docxPath">Path to the DOCX file.</param>
static bool LooksLikeSheetMusicDocx(string docxPath)
{
    try
    {
        using var fs = File.OpenRead(docxPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName == null) continue;
            if (!entry.FullName.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase)) continue;

            // Only inspect common raster image types.
            var name = entry.FullName.ToLowerInvariant();
            if (!(name.EndsWith(".png") || name.EndsWith(".jpg") || name.EndsWith(".jpeg") || name.EndsWith(".bmp")))
                continue;

            using var es = entry.Open();
            using var ms = new MemoryStream();
            es.CopyTo(ms);
            var bytes = ms.ToArray();
            if (SheetMusicDetector.LooksLikeSheetMusic(bytes))
                return true;
        }
    }
    catch
    {
        // if we can't inspect it, do not block conversion
    }

    return false;
}

/// <summary>
/// Renders the first few pages of a PDF at 300 DPI and returns <c>true</c>
/// if any page passes the staff-line sheet-music detection heuristic.
/// </summary>
/// <param name="pdfPath">Path to the PDF file.</param>
static bool LooksLikeSheetMusicPdf(string pdfPath)
{
    try
    {
        // Render first few pages at higher DPI and run staff-line heuristic.
        // Some PDFs have very thin/light staff lines which are missed at low DPI.
        var renderer = new DocnetPdfRenderer();

        for (int page = 0; page < 6; page++)
        {
            try
            {
                using var s = renderer.RenderPageAsPng(pdfPath, pageIndex: page, dpi: 300);
                if (s == null) continue;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var bytes = ms.ToArray();
                if (SheetMusicDetector.LooksLikeSheetMusic(bytes, minLineLengthPct: 12))
                    return true;
            }
            catch
            {
                // Stop if page index is out of range or renderer fails.
                break;
            }
        }

        return false;
    }
    catch
    {
        return false;
    }
}

/// <summary>Returns <c>true</c> if the exception message indicates a sheet-music / no-chords skip condition.</summary>
/// <param name="ex">The exception to inspect.</param>
static bool IsNotenLikeException(Exception ex)
{
    if (ex == null) return false;
    return IsNotenLikeMessage(ex.Message);
}

/// <summary>Returns <c>true</c> if the message text indicates a sheet-music / no-chords skip condition.</summary>
/// <param name="message">The message string to check.</param>
static bool IsNotenLikeMessage(string message)
{
    if (string.IsNullOrWhiteSpace(message)) return false;
    var m = message;

    // Keep this broad; we just want to treat these as intentional skips in batch mode.
    return m.Contains("Notenversion", StringComparison.OrdinalIgnoreCase) ||
           m.Contains("keine erkennbaren Akkorde", StringComparison.OrdinalIgnoreCase) ||
           m.Contains("nur Bilder", StringComparison.OrdinalIgnoreCase) ||
           m.Contains("kein konvertierbarer Text", StringComparison.OrdinalIgnoreCase) ||
           m.Contains("AbortOnNoChords", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Returns <c>true</c> if the <c>--ocr</c> flag is present in the command-line arguments.</summary>
/// <param name="args">The command-line arguments to scan.</param>
static bool ParseEnableOcr(string[] args)
{
    foreach (var a in args ?? Array.Empty<string>())
    {
        if (string.Equals(a, "--ocr", StringComparison.OrdinalIgnoreCase))
            return true;
    }
    return false;
}

static bool ParseFastOcr(string[] args)
{
    foreach (var a in args ?? Array.Empty<string>())
    {
        if (string.Equals(a, "--ocr-fast", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "--fast", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }

    return false;
}

static OcrBackend ParseOcrBackend(string[] args)
{
    for (int i = 0; i < (args?.Length ?? 0); i++)
    {
        var a = args[i];
        if (!string.Equals(a, "--ocr-backend", StringComparison.OrdinalIgnoreCase)) continue;
        if (i + 1 < args.Length)
            return OcrRuntimeFactory.ParseBackend(args[i + 1]);
    }

    return OcrRuntimeFactory.ParseBackend(Environment.GetEnvironmentVariable("CHO_OCR_BACKEND"));
}


/// <summary>
/// Opens a folder browser dialog with the given title and returns the selected path,
/// or <c>null</c> if the user cancels.
/// </summary>
/// <param name="title">The dialog title shown to the user.</param>
static string PromptForFolder(string title)
{
    using var dialog = new FolderBrowserDialog
    {
        Description = title,
        UseDescriptionForTitle = true,
        ShowNewFolderButton = true
    };

    return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
}

static bool HasOcrDataFiles(string ocrDataDir)
{
    var deu = Path.Combine(ocrDataDir, "deu.traineddata");
    var eng = Path.Combine(ocrDataDir, "eng.traineddata");
    return File.Exists(deu) || File.Exists(eng);
}

static string FindOcrDataDir()
{
    var dir = AppContext.BaseDirectory;
    for (int i = 0; i < 6 && dir != null; i++)
    {
        var mainCandidate = Path.Combine(dir, "tessdata-main", "tessdata-main");
        if (Directory.Exists(mainCandidate)) return mainCandidate;

        var candidate = Path.Combine(dir, "tessdata");
        if (Directory.Exists(candidate)) return candidate;
        dir = System.IO.Directory.GetParent(dir)?.FullName;
    }

    var cwdMainCandidate = Path.Combine(Environment.CurrentDirectory, "tessdata-main", "tessdata-main");
    if (Directory.Exists(cwdMainCandidate)) return cwdMainCandidate;

    var cwdCandidate = Path.Combine(Environment.CurrentDirectory, "tessdata");
    return Directory.Exists(cwdCandidate) ? cwdCandidate : null;
}
