namespace SongConverters
{
    /// <summary>
    /// Interface for DOCX to CHO converters.
    /// Allows switching between different implementations (Xceed, OpenXML, etc.)
    /// </summary>
    public interface IDocxToChoConverter
    {
        /// <summary>
        /// Converts a DOCX file to CHO (ChordPro) format.
        /// </summary>
        /// <param name="docxPath">Path to the DOCX file</param>
        /// <param name="options">Conversion options (tab width, reference CHO path)</param>
        /// <returns>CHO formatted string</returns>
        string Convert(string docxPath, ConverterOptions options = null);
    }

    /// <summary>
    /// Conversion options for DOCX to CHO converters.
    /// </summary>
    public sealed record ConverterOptions(int TabWidth = 12, string ReferenceChoPath = null);
}
