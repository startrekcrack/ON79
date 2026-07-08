using System;
using System.Runtime.InteropServices;

namespace CHOConverterWPF
{
    /// <summary>
    /// Controls the Windows title bar appearance via the Desktop Window Manager (DWM) API.
    /// </summary>
    public static class TitleBarController
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        /// <summary>
        /// Enables or disables the immersive dark title bar for the given window handle.
        /// Requires Windows 10 build 17763 or later; silently returns <c>false</c> on older systems.
        /// </summary>
        /// <param name="handle">The Win32 window handle (HWND). Must not be <see cref="IntPtr.Zero"/>.</param>
        /// <param name="enabled"><c>true</c> to apply a dark title bar; <c>false</c> to restore the light title bar.</param>
        /// <returns><c>true</c> if the DWM call succeeded; otherwise <c>false</c>.</returns>
        public static bool UseImmersiveDarkMode(IntPtr handle, bool enabled)
        {
            if (handle == IntPtr.Zero) return false;

            // OperatingSystem.IsWindowsVersionAtLeast uses RtlGetVersion internally,
            // which returns the real build number regardless of app-manifest settings –
            // unlike Environment.OSVersion which can report 6.2 on .NET 5+ without a manifest.
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return false;

            int attribute = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18985)
                ? DWMWA_USE_IMMERSIVE_DARK_MODE
                : DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1;

            int value = enabled ? 1 : 0;
            return DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int)) == 0;
        }
    }
}
