using System.Collections.Generic;
using System.Drawing;

namespace SongConverters
{
    /// <summary>
    /// Represents a single word recognized by OCR with its bounding box.
    /// </summary>
    /// <param name="Text">The recognized text.</param>
    /// <param name="Box">The bounding box of the word.</param>
    public sealed record OcrWord(string Text, RectangleF Box);

    /// <summary>
    /// Represents a line of text recognized by OCR with word-level details.
    /// </summary>
    /// <param name="BaselineY">The Y-coordinate of the text baseline.</param>
    /// <param name="Box">The bounding box of the entire line.</param>
    /// <param name="Words">The words in this line.</param>
    public sealed record OcrLine(float BaselineY, RectangleF Box, IReadOnlyList<OcrWord> Words)
    {
        /// <summary>
        /// Returns a string representation of the line by joining all words.
        /// </summary>
        public override string ToString() => string.Join(" ", Words ?? new List<OcrWord>());
    }

    /// <summary>
    /// Represents a page of OCR results with line-level details.
    /// </summary>
    /// <param name="Width">Page width in pixels.</param>
    /// <param name="Height">Page height in pixels.</param>
    /// <param name="Lines">The lines of text on this page.</param>
    public sealed record OcrPage(int Width, int Height, IReadOnlyList<OcrLine> Lines);

    /// <summary>
    /// OCR engine that returns word-level layout (bounding boxes) for chord alignment.
    /// </summary>
    public interface IOcrLayoutEngine
    {
        /// <summary>
        /// Reads layout information from a PNG image stream.
        /// </summary>
        /// <param name="pngStream">PNG image stream to process.</param>
        /// <returns>OCR page with word-level bounding boxes.</returns>
        OcrPage ReadLayout(System.IO.Stream pngStream);
    }
}
