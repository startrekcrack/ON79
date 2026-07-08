using System;
using System.IO;
using Tesseract;

namespace SongConverters
{
    /// <summary>
    /// Tesseract OCR implementation of IOcrEngine.
    /// </summary>
    public sealed class TesseractOcrEngine : IOcrEngine, IDisposable
    {
        private readonly TesseractEngine _engine;

        /// <summary>
        /// Initializes a new Tesseract OCR engine.
        /// </summary>
        /// <param name="dataPath">Path to tessdata directory.</param>
        /// <param name="language">OCR language (default: deu).</param>
        public TesseractOcrEngine(string dataPath, string language = "deu")
        {
            if (string.IsNullOrWhiteSpace(dataPath))
                throw new ArgumentNullException(nameof(dataPath));

            if (!Directory.Exists(dataPath))
                throw new DirectoryNotFoundException($"tessdata not found: {dataPath}");

            _engine = new TesseractEngine(dataPath, language, EngineMode.Default);
            _engine.SetVariable("preserve_interword_spaces", "1");
        }

        /// <summary>
        /// Reads text from a PNG stream.
        /// </summary>
        /// <param name="pngStream">PNG image stream.</param>
        /// <returns>Recognized text.</returns>
        public string ReadText(Stream pngStream)
        {
            if (pngStream == null) throw new ArgumentNullException(nameof(pngStream));

            using var ms = new MemoryStream();
            pngStream.CopyTo(ms);
            var data = ms.ToArray();

            using var pix = Pix.LoadFromMemory(data);
            using var page = _engine.Process(pix, PageSegMode.SparseText);
            return page.GetText() ?? string.Empty;
        }

        /// <summary>
        /// Disposes the Tesseract engine and releases resources.
        /// </summary>
        public void Dispose()
        {
            _engine?.Dispose();
            
            // Force garbage collection to clean up unmanaged Tesseract resources
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
