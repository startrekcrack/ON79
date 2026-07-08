using System;
using System.IO;

namespace SongConverters
{
    /// <summary>
    /// Placeholder OCR engine (WinRT removed).
    /// </summary>
    public sealed class WindowsMediaOcrEngine : IOcrEngine
    {
        /// <summary>
        /// Initializes a new instance (throws NotSupportedException).
        /// </summary>
        /// <param name="languageTag">Language tag (not used).</param>
        public WindowsMediaOcrEngine(string languageTag = "de-DE")
        {
            throw new NotSupportedException("WinRT OCR support has been removed.");
        }

        /// <summary>
        /// Reads text from stream (throws NotSupportedException).
        /// </summary>
        /// <param name="pngStream">PNG stream (not used).</param>
        /// <returns>Never returns.</returns>
        public string ReadText(Stream pngStream)
        {
            throw new NotSupportedException("WinRT OCR support has been removed.");
        }
    }
}
