using System;

namespace SongConverters
{
    /// <summary>
    /// Fully initialized OCR runtime bundle for text OCR and dual-layout OCR.
    /// </summary>
    public sealed record OcrRuntimeBundle(
        IOcrEngine TextEngine,
        IOcrLayoutEngine ChordLayoutEngine,
        IOcrLayoutEngine LyricLayoutEngine,
        IDisposable Cleanup,
        string Description
    );

    /// <summary>
    /// Centralizes OCR backend construction so the apps can switch engines without duplicating setup logic.
    /// </summary>
    public static class OcrRuntimeFactory
    {
        /// <summary>
        /// Parses a user-supplied backend name.
        /// </summary>
        public static OcrBackend ParseBackend(string raw, OcrBackend fallback = OcrBackend.Paddle)
        {
            if (string.IsNullOrWhiteSpace(raw)) return fallback;

            return raw.Trim().ToLowerInvariant() switch
            {
                "tesseract" or "tess" => OcrBackend.Tesseract,
                "paddle" or "paddleocr" => OcrBackend.Paddle,
                _ => fallback,
            };
        }

        /// <summary>
        /// Creates the requested OCR runtime bundle.
        /// </summary>
        public static OcrRuntimeBundle Create(OcrBackendOptions options)
        {
            options ??= new OcrBackendOptions();

            return options.Backend switch
            {
                OcrBackend.Tesseract => CreateTesseract(options),
                OcrBackend.Paddle => CreatePaddle(options),
                _ => throw new NotSupportedException($"Unsupported OCR backend: {options.Backend}"),
            };
        }

        private static OcrRuntimeBundle CreateTesseract(OcrBackendOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.DataPath))
                throw new ArgumentException("Tesseract backend requires a data path (tessdata directory).", nameof(options));

            var textEngine = new TesseractOcrEngineAdvanced(
                options.DataPath,
                new TesseractOcrEngineAdvanced.Options(
                    Language: options.Language,
                    Preprocess: true,
                    PreprocessOptions: new ImagePreprocessor.Options(
                        ScalePercent: 180,
                        Grayscale: true,
                        AutoThreshold: true,
                        Invert: false),
                    PrimaryPageSegMode: Tesseract.PageSegMode.SingleBlock,
                    FallbackPageSegModes: new[]
                    {
                        Tesseract.PageSegMode.SingleColumn,
                        Tesseract.PageSegMode.SparseText,
                    }));

            var chordLayout = new TesseractLayoutOcrEngine(
                options.DataPath,
                new TesseractLayoutOcrEngine.Options(
                    Language: options.Language,
                    PageSegMode: Tesseract.PageSegMode.SparseText,
                    Preprocess: true,
                    PreprocessOptions: new ImagePreprocessor.Options(
                        ScalePercent: options.ScalePercent,
                        Grayscale: true,
                        AutoThreshold: true,
                        Invert: false,
                        RemoveHorizontalLines: options.RemoveHorizontalLines,
                        HorizontalLineMinPct: options.HorizontalLineMinPct,
                        RemoveStaffSystems: options.RemoveStaffSystems),
                    CharWhitelist: "ABCDEFGHabcdefghis#b/0123456789mMajinsudaugdimaddsus"));

            var lyricLayout = new TesseractLayoutOcrEngine(
                options.DataPath,
                new TesseractLayoutOcrEngine.Options(
                    Language: options.Language,
                    PageSegMode: options.PreferDenseLayout ? Tesseract.PageSegMode.SingleBlock : Tesseract.PageSegMode.SparseText,
                    Preprocess: true,
                    PreprocessOptions: new ImagePreprocessor.Options(
                        ScalePercent: options.ScalePercent,
                        Grayscale: true,
                        AutoThreshold: true,
                        Invert: false,
                        RemoveHorizontalLines: options.RemoveHorizontalLines,
                        HorizontalLineMinPct: options.HorizontalLineMinPct,
                        RemoveStaffSystems: options.RemoveStaffSystems)));

            return new OcrRuntimeBundle(
                textEngine,
                chordLayout,
                lyricLayout,
                new CompositeOcrDisposable(textEngine, chordLayout, lyricLayout),
                "Tesseract"
            );
        }

        private static OcrRuntimeBundle CreatePaddle(OcrBackendOptions options)
        {
            var preprocessOptions = new ImagePreprocessor.Options(
                ScalePercent: options.ScalePercent,
                Grayscale: true,
                AutoThreshold: true,
                Invert: false,
                RemoveHorizontalLines: options.RemoveHorizontalLines,
                HorizontalLineMinPct: options.HorizontalLineMinPct,
                RemoveStaffSystems: options.RemoveStaffSystems);

            var textEngine = new PaddleOcrEngine(new PaddleOcrEngine.Options(
                Preprocess: true,
                PreprocessOptions: preprocessOptions,
                AllowRotateDetection: true,
                Enable180Classification: false,
                RecognizeBatchSize: 0,
                MinRegionScore: 0.45f,
                Language: options.Language));

            var chordLayout = new PaddleLayoutOcrEngine(new PaddleLayoutOcrEngine.Options(
                Preprocess: true,
                PreprocessOptions: preprocessOptions,
                AllowRotateDetection: true,
                Enable180Classification: false,
                RecognizeBatchSize: 0,
                MinRegionScore: 0.35f,
                Language: options.Language));

            var lyricLayout = new PaddleLayoutOcrEngine(new PaddleLayoutOcrEngine.Options(
                Preprocess: true,
                PreprocessOptions: preprocessOptions,
                AllowRotateDetection: true,
                Enable180Classification: false,
                RecognizeBatchSize: 0,
                MinRegionScore: 0.35f,
                Language: options.Language));

            return new OcrRuntimeBundle(
                textEngine,
                chordLayout,
                lyricLayout,
                new CompositeOcrDisposable(textEngine, chordLayout, lyricLayout),
                "PaddleOCR"
            );
        }
    }

    /// <summary>
    /// Minimal disposable group for OCR engines created by the shared factory.
    /// </summary>
    public sealed class CompositeOcrDisposable : IDisposable
    {
        private readonly IDisposable[] _items;

        /// <summary>
        /// Creates a disposable group for OCR resources.
        /// </summary>
        public CompositeOcrDisposable(params IDisposable[] items)
        {
            _items = items ?? Array.Empty<IDisposable>();
        }

        /// <summary>
        /// Disposes all registered OCR resources, ignoring secondary failures.
        /// </summary>
        public void Dispose()
        {
            foreach (var item in _items)
            {
                try
                {
                    item?.Dispose();
                }
                catch
                {
                }
            }
        }
    }
}