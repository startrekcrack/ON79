using System;

namespace CHOConverterGUI
{
    /// <summary>
    /// Aggregates multiple <see cref="IDisposable"/> instances and disposes them all in a single call.
    /// Disposal errors on individual items are silently swallowed so all items are always attempted.
    /// </summary>
    internal sealed class CompositeDisposable : IDisposable
    {
        private readonly IDisposable[] _items;

        /// <summary>
        /// Initializes a new instance with the given disposable items.
        /// </summary>
        /// <param name="items">The disposable objects to manage. A <c>null</c> argument is treated as an empty array.</param>
        public CompositeDisposable(params IDisposable[] items)
        {
            _items = items ?? Array.Empty<IDisposable>();
        }

        /// <summary>
        /// Disposes all contained items. Each item is disposed independently; exceptions are suppressed.
        /// </summary>
        public void Dispose()
        {
            foreach (var d in _items)
            {
                try { d?.Dispose(); } catch { }
            }
        }
    }
}
