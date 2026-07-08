using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SongConverters
{
    /// <summary>
    /// Wraps an OCR engine and enforces a per-call timeout.
    /// If the timeout elapses, returns empty text instead of blocking indefinitely.
    /// </summary>
    public sealed class TimeoutOcrEngine : IOcrEngine
    {
        private readonly Func<IOcrEngine> _factory;
        private readonly TimeSpan _timeout;
        private readonly int _maxTimeouts;
        private int _timeoutCount;

        /// <summary>
        /// Initializes a timeout OCR engine with factory.
        /// </summary>
        /// <param name="factory">Factory function to create OCR engine instances.</param>
        /// <param name="timeout">Timeout duration.</param>
        /// <param name="maxTimeouts">Maximum number of timeouts before giving up.</param>
        public TimeoutOcrEngine(Func<IOcrEngine> factory, TimeSpan timeout, int maxTimeouts = 1)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _timeout = timeout;
            _maxTimeouts = Math.Max(1, maxTimeouts);
        }

        /// <summary>
        /// Initializes a timeout OCR engine with instance.
        /// </summary>
        /// <param name="inner">OCR engine instance.</param>
        /// <param name="timeout">Timeout duration.</param>
        /// <param name="maxTimeouts">Maximum number of timeouts before giving up.</param>
        public TimeoutOcrEngine(IOcrEngine inner, TimeSpan timeout, int maxTimeouts = 1)
            : this(() => inner, timeout, maxTimeouts)
        {
        }

        /// <summary>
        /// Reads text from stream with timeout enforcement.
        /// </summary>
        /// <param name="pngStream">PNG image stream.</param>
        /// <returns>Recognized text or empty string if timeout.</returns>
        public string ReadText(Stream pngStream)
        {
            if (pngStream == null) throw new ArgumentNullException(nameof(pngStream));

            // If OCR already timed out before, don't start more background OCR jobs.
            if (Volatile.Read(ref _timeoutCount) >= _maxTimeouts)
            {
                return string.Empty;
            }

            // Copy to memory so the background task can read safely.
            using var ms = new MemoryStream();
            pngStream.CopyTo(ms);
            var data = ms.ToArray();

            using var cts = new CancellationTokenSource(_timeout);
            try
            {
                var task = Task.Run(() =>
                {
                    using var local = new MemoryStream(data);
                    var engine = _factory();
                    try
                    {
                        return engine.ReadText(local) ?? string.Empty;
                    }
                    finally
                    {
                        if (engine is IDisposable d) d.Dispose();
                    }
                }, cts.Token);

                // Wait synchronously (API is sync), but bounded.
                if (!task.Wait(_timeout))
                {
                    Interlocked.Increment(ref _timeoutCount);
                    return string.Empty;
                }

                return task.Result ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
