namespace SongConverters
{
    /// <summary>
    /// Selects which OCR implementation should be used behind the converter pipeline.
    /// </summary>
    public enum OcrBackend
    {
        /// <summary>
        /// Uses the existing Tesseract-based OCR pipeline.
        /// </summary>
        Tesseract,

        /// <summary>
        /// Reserves the future PaddleOCR-based OCR pipeline.
        /// </summary>
        Paddle,
    }

    /// <summary>
    /// Shared OCR runtime settings used by backend factories.
    /// </summary>
    public sealed record OcrBackendOptions(
        OcrBackend Backend = OcrBackend.Paddle,
        string Language = "deu+eng",
        string DataPath = null,
        int ScalePercent = 200,
        bool PreferDenseLayout = true,
        bool RemoveHorizontalLines = true,
        int HorizontalLineMinPct = 35,
        bool RemoveStaffSystems = true,
        string ModelRootPath = null
    );
}