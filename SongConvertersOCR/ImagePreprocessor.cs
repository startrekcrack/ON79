using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace SongConverters
{
    /// <summary>
    /// Image preprocessing utilities for OCR optimization.
    /// </summary>
    public static class ImagePreprocessor
    {
        /// <summary>
        /// Image preprocessing options.
        /// </summary>
        /// <param name="ScalePercent">Scale percentage (100 = original size).</param>
        /// <param name="Grayscale">Convert to grayscale.</param>
        /// <param name="AutoThreshold">Apply automatic thresholding.</param>
        /// <param name="FixedThreshold">Fixed threshold value (0 = auto).</param>
        /// <param name="Invert">Invert colors.</param>
        /// <param name="RemoveHorizontalLines">Remove long horizontal dark pixel runs (staff/Notenlinien) before OCR.</param>
        /// <param name="HorizontalLineMinPct">Minimum run length as % of image width to be treated as a staff line (default 40).</param>
        /// <param name="RemoveStaffSystems">Remove entire staff systems (5 lines + note heads) — best for sheet music PDFs.</param>
        /// <param name="StaffSystemNotePadPx">Padding above/below outermost staff line to cover note heads (default 18 = ~1.5x line spacing).</param>
        public sealed record Options(
            int ScalePercent = 200,
            bool Grayscale = true,
            bool AutoThreshold = true,
            byte FixedThreshold = 0,
            bool Invert = false,
            bool RemoveHorizontalLines = true,
            int HorizontalLineMinPct = 40,
            bool RemoveStaffSystems = false,
            int StaffSystemNotePadPx = 18
        );

        /// <summary>
        /// Preprocesses an image for OCR and returns PNG bytes.
        /// </summary>
        /// <param name="inputBytes">Input image bytes.</param>
        /// <param name="options">Preprocessing options.</param>
        /// <returns>Preprocessed PNG image bytes.</returns>
        public static byte[] PreprocessToPng(byte[] inputBytes, Options options)
        {
            if (inputBytes == null) throw new ArgumentNullException(nameof(inputBytes));
            if (options == null) options = new Options();

            using var inputStream = new MemoryStream(inputBytes);
            using var srcImage = Image.FromStream(inputStream, useEmbeddedColorManagement: false, validateImageData: false);

            var scale = Math.Max(100, options.ScalePercent) / 100.0;
            var targetWidth = Math.Max(1, (int)Math.Round(srcImage.Width * scale));
            var targetHeight = Math.Max(1, (int)Math.Round(srcImage.Height * scale));

            using var resized = new Bitmap(targetWidth, targetHeight, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(resized))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.Clear(Color.White);
                g.DrawImage(srcImage, 0, 0, targetWidth, targetHeight);
            }

            if (options.Grayscale || options.AutoThreshold || options.FixedThreshold > 0 || options.Invert)
            {
                ApplyGrayscaleAndThresholdInPlace(resized, options);
            }

            if (options.RemoveHorizontalLines)
            {
                RemoveHorizontalLinesInPlace(resized, options.HorizontalLineMinPct);
            }

            if (options.RemoveStaffSystems)
            {
                int minRun = Math.Max(1, targetWidth * Math.Clamp(options.HorizontalLineMinPct, 1, 99) / 100);
                RemoveStaffSystemsInPlace(resized, minRun, options.StaffSystemNotePadPx);
            }

            using var outStream = new MemoryStream();
            resized.Save(outStream, ImageFormat.Png);
            return outStream.ToArray();
        }

        /// <summary>
        /// Converts the bitmap to grayscale in-place and then applies binary thresholding.
        /// The threshold is either a fixed value (<see cref="Options.FixedThreshold"/>),
        /// automatically determined via Otsu's method (<see cref="Options.AutoThreshold"/>),
        /// or zero (no thresholding). The result can optionally be inverted.
        /// </summary>
        /// <param name="bitmap">The 24-bpp bitmap to modify in-place.</param>
        /// <param name="options">Preprocessing options controlling grayscale, thresholding, and inversion.</param>
        private static void ApplyGrayscaleAndThresholdInPlace(Bitmap bitmap, Options options)
        {
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            try
            {
                var stride = data.Stride;
                var absStride = Math.Abs(stride);
                var byteCount = absStride * bitmap.Height;
                var buffer = new byte[byteCount];
                Marshal.Copy(data.Scan0, buffer, 0, byteCount);

                // First pass: optional grayscale + histogram for Otsu.
                var histogram = (options.AutoThreshold && options.FixedThreshold <= 0) ? new int[256] : null;

                for (int y = 0; y < bitmap.Height; y++)
                {
                    var rowOffset = y * absStride;
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        var i = rowOffset + (x * 3);
                        var b = buffer[i + 0];
                        var g = buffer[i + 1];
                        var r = buffer[i + 2];

                        var gray = (byte)Math.Clamp((int)(0.114 * b + 0.587 * g + 0.299 * r), 0, 255);

                        if (options.Grayscale)
                        {
                            buffer[i + 0] = gray;
                            buffer[i + 1] = gray;
                            buffer[i + 2] = gray;
                        }

                        if (histogram != null) histogram[gray]++;
                    }
                }

                byte threshold = 0;
                if (options.FixedThreshold > 0)
                {
                    threshold = options.FixedThreshold;
                }
                else if (histogram != null)
                {
                    threshold = ComputeOtsuThreshold(histogram, bitmap.Width * bitmap.Height);
                }

                var doThreshold = options.AutoThreshold || options.FixedThreshold > 0;
                if (doThreshold)
                {
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        var rowOffset = y * absStride;
                        for (int x = 0; x < bitmap.Width; x++)
                        {
                            var i = rowOffset + (x * 3);
                            var gray = buffer[i + 0];
                            byte bw = gray >= threshold ? (byte)255 : (byte)0;
                            if (options.Invert) bw = (byte)(255 - bw);
                            buffer[i + 0] = bw;
                            buffer[i + 1] = bw;
                            buffer[i + 2] = bw;
                        }
                    }
                }
                else if (options.Invert)
                {
                    for (int i = 0; i < buffer.Length; i++)
                    {
                        buffer[i] = (byte)(255 - buffer[i]);
                    }
                }

                Marshal.Copy(buffer, 0, data.Scan0, byteCount);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        /// <summary>
        /// Computes an optimal binarization threshold using Otsu's method.
        /// The threshold maximizes the inter-class variance between foreground and background pixels.
        /// </summary>
        /// <param name="histogram">A 256-element grayscale histogram of the image.</param>
        /// <param name="totalPixels">Total number of pixels in the image.</param>
        /// <returns>The optimal threshold byte value (0–255).</returns>
        private static byte ComputeOtsuThreshold(int[] histogram, int totalPixels)
        {
            // Standard Otsu thresholding.
            long sum = 0;
            for (int i = 0; i < 256; i++) sum += (long)i * histogram[i];

            long sumB = 0;
            int wB = 0;
            int wF = 0;

            double maxVariance = -1;
            int threshold = 128;

            for (int t = 0; t < 256; t++)
            {
                wB += histogram[t];
                if (wB == 0) continue;

                wF = totalPixels - wB;
                if (wF == 0) break;

                sumB += (long)t * histogram[t];

                var mB = (double)sumB / wB;
                var mF = (double)(sum - sumB) / wF;

                var between = (double)wB * wF * (mB - mF) * (mB - mF);
                if (between > maxVariance)
                {
                    maxVariance = between;
                    threshold = t;
                }
            }

            return (byte)Math.Clamp(threshold, 0, 255);
        }

        /// <summary>
        /// Removes long horizontal dark pixel runs (staff lines / Notenlinien) from the image.
        /// A run is erased if its length >= minLengthPct % of the image width.
        /// </summary>
        private static void RemoveHorizontalLinesInPlace(Bitmap bitmap, int minLengthPct)
        {
            int minRun = Math.Max(1, bitmap.Width * Math.Clamp(minLengthPct, 1, 99) / 100);
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            try
            {
                int stride = Math.Abs(data.Stride);
                var buffer = new byte[stride * bitmap.Height];
                Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

                for (int y = 0; y < bitmap.Height; y++)
                {
                    int rowOffset = y * stride;
                    int runStart = -1;

                    for (int x = 0; x <= bitmap.Width; x++)
                    {
                        bool dark = x < bitmap.Width && buffer[rowOffset + x * 3] < 128;
                        if (dark)
                        {
                            if (runStart < 0) runStart = x;
                        }
                        else
                        {
                            if (runStart >= 0)
                            {
                                int len = x - runStart;
                                if (len >= minRun)
                                {
                                    // Erase this staff line run to white.
                                    for (int rx = runStart; rx < x; rx++)
                                    {
                                        int i = rowOffset + rx * 3;
                                        buffer[i] = buffer[i + 1] = buffer[i + 2] = 255;
                                    }
                                }
                                runStart = -1;
                            }
                        }
                    }
                }

                Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        /// <summary>
        /// Detects groups of 5 evenly-spaced horizontal staff lines and blanks out
        /// the entire band (staff + note-head padding) so OCR only sees chords and lyrics.
        /// </summary>
        private static void RemoveStaffSystemsInPlace(Bitmap bitmap, int minRunPx, int notePadPx)
        {
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            try
            {
                int stride = Math.Abs(data.Stride);
                var buf = new byte[stride * bitmap.Height];
                Marshal.Copy(data.Scan0, buf, 0, buf.Length);

                // Step 1: find rows containing a horizontal run >= minRunPx dark pixels.
                var candidateRows = new List<int>();
                for (int y = 0; y < bitmap.Height; y++)
                {
                    int off = y * stride, run = 0, maxRun = 0;
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        if (buf[off + x * 3] < 128) { run++; maxRun = Math.Max(maxRun, run); }
                        else run = 0;
                    }
                    if (maxRun >= minRunPx) candidateRows.Add(y);
                }

                if (candidateRows.Count < 5) { Marshal.Copy(buf, 0, data.Scan0, buf.Length); return; }

                // Step 2: cluster consecutive candidate rows into individual line bands.
                var lineBands = new List<(int top, int bot, int center)>();
                int gs = candidateRows[0], ge = candidateRows[0];
                for (int i = 1; i < candidateRows.Count; i++)
                {
                    if (candidateRows[i] - candidateRows[i - 1] <= 5) { ge = candidateRows[i]; }
                    else { lineBands.Add((gs, ge, (gs + ge) / 2)); gs = ge = candidateRows[i]; }
                }
                lineBands.Add((gs, ge, (gs + ge) / 2));

                // Step 3: find runs of 5 bands with roughly equal spacing → staff systems.
                var masks = new List<(int top, int bot)>();
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

                    int pad = Math.Max(notePadPx, (int)(avg * 1.2f));
                    int maskTop = Math.Max(0, lineBands[i].top - pad);
                    int maskBot = Math.Min(bitmap.Height - 1, lineBands[i + 4].bot + pad);
                    masks.Add((maskTop, maskBot));
                    i += 4; // skip past this system
                }

                // Step 4: blank every masked row.
                foreach (var (mt, mb) in masks)
                    for (int y = mt; y <= mb; y++)
                    {
                        int off = y * stride;
                        for (int x = 0; x < bitmap.Width; x++)
                        { int k = off + x * 3; buf[k] = buf[k + 1] = buf[k + 2] = 255; }
                    }

                Marshal.Copy(buf, 0, data.Scan0, buf.Length);
            }
            finally { bitmap.UnlockBits(data); }
        }
    }
}
