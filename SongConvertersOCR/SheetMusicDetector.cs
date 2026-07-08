using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace SongConverters
{
    /// <summary>
    /// Class for detecting sheet music in images using heuristic methods.
    /// </summary>
    public static class SheetMusicDetector
    {
        /// <summary>
        /// Heuristic: detect staff systems (5 roughly evenly spaced long horizontal dark lines)
        /// to classify an image as sheet music.
        /// </summary>
        public static bool LooksLikeSheetMusic(byte[] imageBytes, int minLineLengthPct = 40)
        {
            if (imageBytes == null || imageBytes.Length == 0) return false;

            try
            {
                using var ms = new MemoryStream(imageBytes);
                using var src = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);

                // Skip tiny images.
                if (src.Width < 200 || src.Height < 150) return false;

                // Work on a downscaled bitmap for speed.
                const int targetWidth = 900;
                var scale = Math.Min(1.0, targetWidth / (double)src.Width);
                var w = Math.Max(1, (int)Math.Round(src.Width * scale));
                var h = Math.Max(1, (int)Math.Round(src.Height * scale));

                using var bmp = new Bitmap(w, h);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.White);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(src, 0, 0, w, h);
                }

                int minRunPx = Math.Max(1, w * Math.Clamp(minLineLengthPct, 1, 99) / 100);
                var candidateRows = FindCandidateStaffRows(bmp, minRunPx);
                if (candidateRows.Count < 5) return false;

                var lineBands = ClusterRowBands(candidateRows);
                if (lineBands.Count < 5) return false;

                // Real staff lines are thin; filter out thick "bands" (often text baselines / dense text).
                const int maxBandThicknessPx = 10;
                lineBands.RemoveAll(b => b.thickness > maxBandThicknessPx);
                if (lineBands.Count < 5) return false;

                return HasAnyStaffSystem(lineBands, h);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// FindCandidateStaffRows scans each row of the bitmap and identifies rows that have a long horizontal run of dark pixels,
        /// </summary>
        /// <param name="bmp"></param>
        /// <param name="minRunPx"></param>
        /// <returns></returns>
        private static List<int> FindCandidateStaffRows(Bitmap bmp, int minRunPx)
        {
            var rows = new List<int>();

            // Dark threshold in RGB average.
            // PDFs often render staff lines as very light anti-aliased gray; keep this fairly high.
            const int darkThreshold = 230;

            // Require a long, mostly continuous horizontal run. This reduces false positives on text-heavy pages.
            int relaxedRunPx = Math.Max(1, (int)Math.Round(minRunPx * 0.25));

            // Staff lines are close to uniform across the row; text rows have many dark/light transitions.
            int maxTransitions = Math.Max(12, bmp.Width / 15);

            // Sample every 2nd row for speed.
            for (int y = 0; y < bmp.Height; y += 2)
            {
                int darkCount = 0;
                int currentRun = 0;
                int maxRun = 0;
                int transitions = 0;
                bool prevDark = false;
                bool hasPrev = false;

                // Sample every pixel; for downscaled image this is fine.
                for (int x = 0; x < bmp.Width; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    var gray = (c.R + c.G + c.B) / 3;
                    var isDark = gray < darkThreshold;

                    if (hasPrev && isDark != prevDark) transitions++;
                    prevDark = isDark;
                    hasPrev = true;

                    if (isDark)
                    {
                        darkCount++;
                        currentRun++;
                        if (currentRun > maxRun) maxRun = currentRun;
                    }
                    else
                    {
                        currentRun = 0;
                    }
                }

                // Staff lines are long and thin. Allow slightly broken lines (note heads) by accepting
                // either a full run or a moderately long run + high coverage.
                bool coverageUniform = darkCount >= minRunPx && transitions <= maxTransitions;
                if (coverageUniform || maxRun >= minRunPx || (maxRun >= relaxedRunPx && darkCount >= minRunPx))
                    rows.Add(y);
            }

            return rows;
        }

        /// <summary>
        /// Clusters nearby candidate rows into bands, which helps identify groups of 5 lines that form staff systems.
        /// </summary>
        /// <param name="candidateRows"></param>
        /// <returns></returns>
        private static List<(int top, int bot, int center, int thickness)> ClusterRowBands(List<int> candidateRows)
        {
            var bands = new List<(int top, int bot, int center, int thickness)>();
            if (candidateRows == null || candidateRows.Count == 0) return bands;

            candidateRows.Sort();

            int start = candidateRows[0];
            int end = candidateRows[0];

            for (int i = 1; i < candidateRows.Count; i++)
            {
                if (candidateRows[i] - candidateRows[i - 1] <= 6)
                {
                    end = candidateRows[i];
                }
                else
                {
                    int thickness = (end - start) + 1;
                    bands.Add((start, end, (start + end) / 2, thickness));
                    start = end = candidateRows[i];
                }
            }

            bands.Add((start, end, (start + end) / 2, (end - start) + 1));
            return bands;
        }

        /// <summary>
        /// HasAnyStaffSystem checks if there are any groups of 5 line bands that are roughly evenly spaced, which is characteristic of staff systems in sheet music.
        /// </summary>
        /// <param name="lineBands"></param>
        /// <param name="height"></param>
        /// <returns></returns>
        private static bool HasAnyStaffSystem(List<(int top, int bot, int center, int thickness)> lineBands, int height)
        {
            if (lineBands == null || lineBands.Count < 5) return false;

            // Slide a window of 5 line bands and check for roughly even spacing.
            for (int i = 0; i <= lineBands.Count - 5; i++)
            {
                float s0 = lineBands[i + 1].center - lineBands[i].center;
                float s1 = lineBands[i + 2].center - lineBands[i + 1].center;
                float s2 = lineBands[i + 3].center - lineBands[i + 2].center;
                float s3 = lineBands[i + 4].center - lineBands[i + 3].center;

                if (s0 <= 4 || s1 <= 4 || s2 <= 4 || s3 <= 4) continue;

                float avg = (s0 + s1 + s2 + s3) / 4f;

                bool even = Math.Abs(s0 - avg) < avg * 0.45f &&
                            Math.Abs(s1 - avg) < avg * 0.45f &&
                            Math.Abs(s2 - avg) < avg * 0.45f &&
                            Math.Abs(s3 - avg) < avg * 0.45f;

                if (!even) continue;

                // Reject extremely small systems (noise) and extremely large spacing (likely text/table lines).
                float maxSpacing = Math.Min(24f, height * 0.06f);
                if (avg < 6 || avg > maxSpacing) continue;

                return true;
            }

            return false;
        }
    }
}
