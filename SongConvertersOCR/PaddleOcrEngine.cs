using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Local;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SongConverters
{
    /// <summary>
    /// PaddleOCR-based text OCR engine using local models and MKLDNN execution.
    /// </summary>
    public sealed class PaddleOcrEngine : IOcrEngine, IDisposable
    {
        /// <summary>
        /// Options for PaddleOCR text extraction.
        /// </summary>
        public sealed record Options(
            bool Preprocess = true,
            ImagePreprocessor.Options PreprocessOptions = null,
            bool AllowRotateDetection = true,
            bool Enable180Classification = false,
            int RecognizeBatchSize = 0,
            float MinRegionScore = 0.45f,
            string Language = "deu+eng"
        );

        private static readonly Regex MultiWhitespace = new(@"[ \t]+", RegexOptions.Compiled);

        private readonly PaddleOcrAll _ocr;
        private readonly Options _options;

        /// <summary>
        /// Creates a new PaddleOCR text engine.
        /// </summary>
        public PaddleOcrEngine(Options options = null)
        {
            _options = options ?? new Options();
            _ocr = new PaddleOcrAll(ResolveModel(_options.Language), PaddleDevice.Mkldnn())
            {
                AllowRotateDetection = _options.AllowRotateDetection,
                Enable180Classification = _options.Enable180Classification,
            };
            _ocr.Detector.MaxSize = 1536;
            _ocr.Detector.UnclipRatio = 1.3f;
        }

        /// <summary>
        /// Runs OCR and returns flattened text.
        /// </summary>
        public string ReadText(Stream pngStream)
        {
            if (pngStream == null) throw new ArgumentNullException(nameof(pngStream));

            using var src = DecodeMat(pngStream, _options.Preprocess, _options.PreprocessOptions);
            var result = _ocr.Run(src, _options.RecognizeBatchSize);

            if (result?.Regions == null || result.Regions.Length == 0) return string.Empty;

            var lines = PaddleLayoutProjector.ProjectLines(result.Regions, src.Width, src.Height, _options.MinRegionScore);
            return string.Join("\n", lines.Select(l => NormalizeText(string.Join(" ", l.Words.Select(w => w.Text)))));
        }

        /// <summary>
        /// Disposes native PaddleOCR resources.
        /// </summary>
        public void Dispose()
        {
            _ocr.Dispose();
        }

        internal static Mat DecodeMat(Stream pngStream, bool preprocess, ImagePreprocessor.Options preprocessOptions)
        {
            using var ms = new MemoryStream();
            pngStream.CopyTo(ms);
            var bytes = ms.ToArray();

            if (preprocess)
            {
                try
                {
                    bytes = ImagePreprocessor.PreprocessToPng(bytes, preprocessOptions ?? new ImagePreprocessor.Options());
                }
                catch
                {
                }
            }

            var src = Cv2.ImDecode(bytes, ImreadModes.Color);
            if (src.Empty())
            {
                src.Dispose();
                throw new InvalidOperationException("PaddleOCR could not decode the image stream.");
            }

            return src;
        }

        internal static string NormalizeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            return MultiWhitespace.Replace(text.Trim(), " ");
        }

        internal static FullOcrModel ResolveModel(string language)
        {
            var normalized = (language ?? string.Empty).ToLowerInvariant();
            if (normalized.Contains("deu") || normalized.Contains("ger") || normalized.Contains("latin"))
                return LocalFullModels.LatinV3;
            return LocalFullModels.EnglishV4;
        }
    }

    /// <summary>
    /// PaddleOCR-based layout OCR engine that maps detected regions into line and word boxes.
    /// </summary>
    public sealed class PaddleLayoutOcrEngine : IOcrLayoutEngine, IDisposable
    {
        /// <summary>
        /// Options for PaddleOCR layout extraction.
        /// </summary>
        public sealed record Options(
            bool Preprocess = true,
            ImagePreprocessor.Options PreprocessOptions = null,
            bool AllowRotateDetection = true,
            bool Enable180Classification = false,
            int RecognizeBatchSize = 0,
            float MinRegionScore = 0.35f,
            string Language = "deu+eng"
        );

        private readonly PaddleOcrAll _ocr;
        private readonly Options _options;

        /// <summary>
        /// Creates a new PaddleOCR layout engine.
        /// </summary>
        public PaddleLayoutOcrEngine(Options options = null)
        {
            _options = options ?? new Options();
            _ocr = new PaddleOcrAll(PaddleOcrEngine.ResolveModel(_options.Language), PaddleDevice.Mkldnn())
            {
                AllowRotateDetection = _options.AllowRotateDetection,
                Enable180Classification = _options.Enable180Classification,
            };
            _ocr.Detector.MaxSize = null;
            _ocr.Detector.UnclipRatio = 1.2f;
        }

        /// <summary>
        /// Reads OCR layout from an image stream.
        /// </summary>
        public OcrPage ReadLayout(Stream pngStream)
        {
            if (pngStream == null) throw new ArgumentNullException(nameof(pngStream));

            using var src = PaddleOcrEngine.DecodeMat(pngStream, _options.Preprocess, _options.PreprocessOptions);
            var result = _ocr.Run(src, _options.RecognizeBatchSize);
            var lines = PaddleLayoutProjector.ProjectLines(result?.Regions, src.Width, src.Height, _options.MinRegionScore);
            return new OcrPage(src.Width, src.Height, lines);
        }

        /// <summary>
        /// Disposes native PaddleOCR resources.
        /// </summary>
        public void Dispose()
        {
            _ocr.Dispose();
        }
    }

    internal static class PaddleLayoutProjector
    {
        public static IReadOnlyList<OcrLine> ProjectLines(IReadOnlyList<PaddleOcrResultRegion> regions, int width, int height, float minRegionScore)
        {
            if (regions == null || regions.Count == 0)
                return Array.Empty<OcrLine>();

            var projected = regions
                .Where(r => !string.IsNullOrWhiteSpace(r.Text) && r.Score >= minRegionScore)
                .Select(r => new RegionProjection(r, ToRectangle(r.Rect), PaddleOcrEngine.NormalizeText(r.Text), r.Score))
                .Where(r => r.Box.Width > 1f && r.Box.Height > 1f && r.Text.Length > 0)
                .Where(r => !ShouldDiscardRegion(r, minRegionScore))
                .OrderBy(r => r.Box.Top)
                .ThenBy(r => r.Box.Left)
                .ToList();

            if (projected.Count == 0)
                return Array.Empty<OcrLine>();

            projected = DeduplicateRegions(projected);

            var buckets = new List<LineBucket>();
            foreach (var region in projected)
            {
                var bucketIndex = FindBestBucketIndex(buckets, region);
                if (bucketIndex < 0)
                {
                    buckets.Add(new LineBucket(region));
                }
                else
                {
                    buckets[bucketIndex].Add(region);
                }
            }

            return buckets
                .Select(BuildLine)
                .OrderBy(l => l.Box.Top)
                .ToList();
        }

        private static OcrLine BuildLine(LineBucket bucket)
        {
            var regions = DeduplicateRegions(bucket.Regions);
            var ordered = MergeAdjacentRegions(regions.OrderBy(r => r.Box.Left).ToList(), bucket.AverageHeight);
            var fragments = ordered
                .SelectMany(ExtractWordFragments)
                .OrderBy(w => w.Box.Left)
                .ToList();

            var words = MergeWordFragments(fragments, bucket.AverageHeight);

            var lineBox = ordered.Select(r => r.Box).Aggregate(Union);
            var baselineY = lineBox.Top + (lineBox.Height * 0.8f);
            return new OcrLine(baselineY, lineBox, words.OrderBy(w => w.Box.Left).ToList());
        }

        private static List<RegionProjection> MergeAdjacentRegions(IReadOnlyList<RegionProjection> regions, float averageLineHeight)
        {
            var merged = new List<RegionProjection>();
            foreach (var region in regions)
            {
                if (merged.Count == 0)
                {
                    merged.Add(region);
                    continue;
                }

                var previous = merged[^1];
                if (!ShouldMergeRegions(previous, region, averageLineHeight))
                {
                    merged.Add(region);
                    continue;
                }

                merged[^1] = new RegionProjection(
                    previous.Region,
                    Union(previous.Box, region.Box),
                    MergeRegionTexts(previous.Text, region.Text),
                    Math.Max(previous.Score, region.Score));
            }

            return merged;
        }

        private static bool ShouldMergeRegions(RegionProjection previous, RegionProjection current, float averageLineHeight)
        {
            if (string.IsNullOrWhiteSpace(previous.Text) || string.IsNullOrWhiteSpace(current.Text))
                return false;

            if (LooksLikeChord(previous.Text) || LooksLikeChord(current.Text))
                return false;

            var gap = current.Box.Left - previous.Box.Right;
            if (gap < -2f)
                return false;

            var height = Math.Max(1f, Math.Min(Math.Max(previous.Box.Height, current.Box.Height), averageLineHeight));
            var mergeGap = Math.Max(4.5f, height * 0.42f);
            if (gap > mergeGap)
                return false;

            var prevText = previous.Text.Trim();
            var currText = current.Text.Trim();
            if (prevText.Length == 0 || currText.Length == 0)
                return false;

            var prevEndsWithLetter = char.IsLetter(prevText[^1]);
            var currStartsWithLetter = char.IsLetter(currText[0]);
            if (!prevEndsWithLetter || !currStartsWithLetter)
                return false;

            if (char.IsUpper(prevText[^1]) && char.IsUpper(currText[0]))
                return false;

            var sameBaseline = Math.Abs(previous.Box.Top - current.Box.Top) <= Math.Max(4f, height * 0.25f);
            if (!sameBaseline)
                return false;

            return prevText.Length <= 5
                   || currText.Length <= 5
                   || gap <= 1.5f
                   || (char.IsLower(prevText[^1]) && char.IsLower(currText[0]));
        }

        private static string MergeRegionTexts(string previous, string current)
        {
            var left = previous?.Trim() ?? string.Empty;
            var right = current?.Trim() ?? string.Empty;
            if (left.Length == 0) return right;
            if (right.Length == 0) return left;

            var joinWithoutSpace = char.IsLetter(left[^1]) && char.IsLetter(right[0]);
            if (joinWithoutSpace)
                return PaddleOcrEngine.NormalizeText(left + right);

            return PaddleOcrEngine.NormalizeText(left + " " + right);
        }

        private static IEnumerable<OcrWord> ExtractWordFragments(RegionProjection region)
        {
            var matches = Regex.Matches(region.Text, @"\S+");
            if (matches.Count == 0)
                yield break;

            if (matches.Count == 1)
            {
                yield return new OcrWord(region.Text, region.Box);
                yield break;
            }

            var weightedLength = 0f;
            foreach (Match match in matches)
            {
                weightedLength += ComputeWeightedTextLength(match.Value);
            }

            var spaces = Math.Max(0, matches.Count - 1);
            weightedLength += spaces * 0.45f;

            var cursor = region.Box.Left;
            var availableWidth = Math.Max(2f, region.Box.Width);
            for (int i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                var token = match.Value;
                var units = ComputeWeightedTextLength(token);
                var fraction = weightedLength <= 0f ? 1f / matches.Count : units / weightedLength;
                var tokenWidth = i == matches.Count - 1
                    ? (region.Box.Right - cursor)
                    : Math.Max(2f, availableWidth * fraction);

                var box = RectangleF.FromLTRB(cursor, region.Box.Top, Math.Min(region.Box.Right, cursor + tokenWidth), region.Box.Bottom);
                yield return new OcrWord(token, box);

                cursor = box.Right;
                if (i < matches.Count - 1)
                {
                    var gapFraction = weightedLength <= 0f ? 0.08f : 0.45f / weightedLength;
                    var gapWidth = Math.Max(1.5f, availableWidth * gapFraction);
                    cursor = Math.Min(region.Box.Right, cursor + gapWidth);
                }
            }
        }

        private static List<OcrWord> MergeWordFragments(IReadOnlyList<OcrWord> fragments, float averageLineHeight)
        {
            var merged = new List<OcrWord>();
            foreach (var fragment in fragments)
            {
                if (merged.Count == 0)
                {
                    merged.Add(fragment);
                    continue;
                }

                var previous = merged[^1];
                if (!ShouldMergeWordFragments(previous, fragment, averageLineHeight))
                {
                    merged.Add(fragment);
                    continue;
                }

                merged[^1] = new OcrWord(previous.Text + fragment.Text, Union(previous.Box, fragment.Box));
            }

            return merged;
        }

        private static bool ShouldMergeWordFragments(OcrWord previous, OcrWord current, float averageLineHeight)
        {
            if (string.IsNullOrWhiteSpace(previous.Text) || string.IsNullOrWhiteSpace(current.Text))
                return false;

            if (LooksLikeChord(previous.Text) || LooksLikeChord(current.Text))
                return false;

            if (previous.Text.EndsWith("-", StringComparison.Ordinal) || current.Text.StartsWith("-", StringComparison.Ordinal))
                return false;

            var gap = current.Box.Left - previous.Box.Right;
            var height = Math.Max(1f, Math.Min(Math.Max(previous.Box.Height, current.Box.Height), averageLineHeight));
            var mergeGap = Math.Max(3.5f, height * 0.30f);
            if (gap > mergeGap)
                return false;

            var prevEndsWithLetter = char.IsLetter(previous.Text[^1]);
            var currStartsWithLetter = char.IsLetter(current.Text[0]);
            if (!prevEndsWithLetter || !currStartsWithLetter)
                return false;

            if (char.IsUpper(previous.Text[^1]) && char.IsUpper(current.Text[0]))
                return false;

            return previous.Text.Length <= 3
                   || current.Text.Length <= 4
                   || gap <= 2.0f
                   || (char.IsLower(previous.Text[^1]) && char.IsLower(current.Text[0]));
        }

        private static int FindBestBucketIndex(List<LineBucket> buckets, RegionProjection candidate)
        {
            var bestIndex = -1;
            var bestDistance = float.MaxValue;

            for (int i = 0; i < buckets.Count; i++)
            {
                if (!BelongsToLine(buckets[i], candidate, out var distance)) continue;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        private static bool BelongsToLine(LineBucket bucket, RegionProjection candidate, out float distance)
        {
            var anchor = bucket.Box;
            var candidateCenter = candidate.Box.Top + (candidate.Box.Height / 2f);
            var anchorCenter = anchor.Top + (anchor.Height / 2f);
            var tolerance = Math.Max(8f, Math.Max(bucket.AverageHeight, candidate.Box.Height) * 0.62f);
            var centerDistance = Math.Abs(candidateCenter - anchorCenter);
            var verticalOverlap = Math.Min(anchor.Bottom, candidate.Box.Bottom) - Math.Max(anchor.Top, candidate.Box.Top);
            var minHeight = Math.Max(1f, Math.Min(anchor.Height, candidate.Box.Height));

            distance = centerDistance;
            if (centerDistance > tolerance && verticalOverlap < minHeight * 0.3f)
                return false;

            // Prevent regions from a different text row from joining just because their centers are close.
            if (candidate.Box.Top > anchor.Bottom + tolerance || candidate.Box.Bottom < anchor.Top - tolerance)
                return false;

            return true;
        }

        private static bool ShouldDiscardRegion(RegionProjection region, float minRegionScore)
        {
            if (region.Text.Length == 0) return true;
            if (region.Box.Width < 4f || region.Box.Height < 4f) return true;

            var tokenCount = region.Tokens.Count;
            var letters = region.Text.Count(char.IsLetter);
            var digits = region.Text.Count(char.IsDigit);
            var weird = region.Text.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && c is not '-' and not '/' and not '(' and not ')');
            var shortTokens = region.Tokens.Count(t => t.Length <= 2);

            if (region.Score < Math.Max(minRegionScore + 0.08f, 0.55f) && letters <= 2 && digits > 0)
                return true;

            if (region.Score < 0.55f && tokenCount >= 3 && shortTokens >= tokenCount * 0.75)
                return true;

            if (letters == 0 && digits > 0)
                return true;

            return weird >= 4 && letters <= 3;
        }

        private static List<RegionProjection> DeduplicateRegions(IEnumerable<RegionProjection> regions)
        {
            var result = new List<RegionProjection>();
            foreach (var region in regions.OrderBy(r => r.Box.Top).ThenBy(r => r.Box.Left))
            {
                var existingIndex = result.FindIndex(r => IsDuplicateRegion(r, region));
                if (existingIndex < 0)
                {
                    result.Add(region);
                    continue;
                }

                result[existingIndex] = PickBetterRegion(result[existingIndex], region);
            }

            return result;
        }

        private static bool IsDuplicateRegion(RegionProjection left, RegionProjection right)
        {
            var leftNorm = NormalizeForComparison(left.Text);
            var rightNorm = NormalizeForComparison(right.Text);
            if (leftNorm.Length == 0 || rightNorm.Length == 0) return false;

            var centerDistanceY = Math.Abs((left.Box.Top + left.Box.Height / 2f) - (right.Box.Top + right.Box.Height / 2f));
            var centerDistanceX = Math.Abs((left.Box.Left + left.Box.Width / 2f) - (right.Box.Left + right.Box.Width / 2f));
            var overlap = IntersectionOverUnion(left.Box, right.Box);

            if (overlap >= 0.55f && (leftNorm == rightNorm || leftNorm.Contains(rightNorm, StringComparison.Ordinal) || rightNorm.Contains(leftNorm, StringComparison.Ordinal)))
                return true;

            return centerDistanceY <= Math.Max(left.Box.Height, right.Box.Height) * 0.35f
                   && centerDistanceX <= Math.Max(left.Box.Width, right.Box.Width) * 0.35f
                   && leftNorm == rightNorm;
        }

        private static RegionProjection PickBetterRegion(RegionProjection left, RegionProjection right)
        {
            var leftScore = left.Score + (left.Text.Length * 0.01f);
            var rightScore = right.Score + (right.Text.Length * 0.01f);
            return rightScore > leftScore ? right : left;
        }

        private static float IntersectionOverUnion(RectangleF left, RectangleF right)
        {
            var overlapLeft = Math.Max(left.Left, right.Left);
            var overlapTop = Math.Max(left.Top, right.Top);
            var overlapRight = Math.Min(left.Right, right.Right);
            var overlapBottom = Math.Min(left.Bottom, right.Bottom);
            if (overlapRight <= overlapLeft || overlapBottom <= overlapTop) return 0f;

            var intersection = (overlapRight - overlapLeft) * (overlapBottom - overlapTop);
            var union = (left.Width * left.Height) + (right.Width * right.Height) - intersection;
            return union <= 0f ? 0f : intersection / union;
        }

        private static string NormalizeForComparison(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            return Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9]+", string.Empty);
        }

        private static float ComputeWeightedTextLength(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0f;

            var total = 0f;
            foreach (var c in text)
            {
                total += c switch
                {
                    'i' or 'l' or 'I' or '|' => 0.55f,
                    'm' or 'w' or 'M' or 'W' => 1.35f,
                    '-' or '/' or '\\' => 0.65f,
                    '.' or ',' or ':' or ';' or '!' => 0.45f,
                    '(' or ')' or '[' or ']' => 0.6f,
                    _ when char.IsUpper(c) => 1.1f,
                    _ => 1f,
                };
            }

            return total;
        }

        private static bool LooksLikeChord(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return Regex.IsMatch(text.Trim(), @"^[A-H](#|b)?(m|maj|min|sus|add|dim|aug)?[0-9/+#b]*(?:/[A-H](#|b)?)?$", RegexOptions.IgnoreCase);
        }

        private static RectangleF ToRectangle(RotatedRect rect)
        {
            var points = rect.Points();
            var left = points.Min(p => p.X);
            var top = points.Min(p => p.Y);
            var right = points.Max(p => p.X);
            var bottom = points.Max(p => p.Y);
            return RectangleF.FromLTRB(left, top, right, bottom);
        }

        private static RectangleF Union(RectangleF a, RectangleF b)
        {
            var left = Math.Min(a.Left, b.Left);
            var top = Math.Min(a.Top, b.Top);
            var right = Math.Max(a.Right, b.Right);
            var bottom = Math.Max(a.Bottom, b.Bottom);
            return RectangleF.FromLTRB(left, top, right, bottom);
        }

        private sealed record RegionProjection(PaddleOcrResultRegion Region, RectangleF Box, string Text, float Score)
        {
            public IReadOnlyList<string> Tokens { get; } = Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        private sealed class LineBucket
        {
            public List<RegionProjection> Regions { get; } = new();
            public RectangleF Box { get; private set; }
            public float AverageHeight { get; private set; }

            public LineBucket(RegionProjection region)
            {
                Regions.Add(region);
                Box = region.Box;
                AverageHeight = region.Box.Height;
            }

            public void Add(RegionProjection region)
            {
                Regions.Add(region);
                Box = Union(Box, region.Box);
                AverageHeight = Regions.Average(r => r.Box.Height);
            }
        }
    }
}