using System;

namespace SongConverters
{
    /// <summary>
    /// Base exception for all converter-related errors.
    /// </summary>
    public class ConverterException : Exception
    {
        /// <summary>
        /// Path to the file that caused the exception.
        /// </summary>
        public string FilePath { get; }

        /// <summary>
        /// Initializes a new instance of the ConverterException class.
        /// </summary>
        /// <param name="message">Error message.</param>
        /// <param name="filePath">Path to the file that caused the error.</param>
        /// <param name="innerException">Inner exception.</param>
        public ConverterException(string message, string filePath, Exception innerException = null)
            : base(message, innerException)
        {
            FilePath = filePath;
        }
    }

    /// <summary>
    /// Exception thrown when OCR processing fails.
    /// </summary>
    public class OcrException : ConverterException
    {
        /// <summary>
        /// Initializes a new instance of the OcrException class.
        /// </summary>
        /// <param name="message">Error message.</param>
        /// <param name="filePath">Path to the file that caused the error.</param>
        /// <param name="innerException">Inner exception.</param>
        public OcrException(string message, string filePath, Exception innerException = null)
            : base(message, filePath, innerException)
        {
        }
    }

    /// <summary>
    /// Exception thrown when a document is invalid or corrupt.
    /// </summary>
    public class InvalidDocumentException : ConverterException
    {
        /// <summary>
        /// Initializes a new instance of the InvalidDocumentException class.
        /// </summary>
        /// <param name="message">Error message.</param>
        /// <param name="filePath">Path to the invalid document.</param>
        public InvalidDocumentException(string message, string filePath)
            : base(message, filePath)
        {
        }
    }

    /// <summary>
    /// Exception thrown when no chords are found in a document.
    /// </summary>
    public class NoChordsFoundException : ConverterException
    {
        /// <summary>
        /// Initializes a new instance of the NoChordsFoundException class.
        /// </summary>
        /// <param name="message">Error message.</param>
        /// <param name="filePath">Path to the document.</param>
        public NoChordsFoundException(string message, string filePath)
            : base(message, filePath)
        {
        }
    }

    /// <summary>
    /// Exception thrown when a document contains only images and OCR is disabled.
    /// </summary>
    public class ImageOnlyDocumentException : ConverterException
    {
        /// <summary>
        /// Initializes a new instance of the ImageOnlyDocumentException class.
        /// </summary>
        /// <param name="message">Error message.</param>
        /// <param name="filePath">Path to the document.</param>
        public ImageOnlyDocumentException(string message, string filePath)
            : base(message, filePath)
        {
        }
    }
}
