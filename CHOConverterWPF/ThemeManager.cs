using System;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using System.Windows;
using System.Windows.Media;

namespace CHOConverterWPF
{
    internal static class ThemeManager
    {
        public const string LightTheme = "Light";
        public const string DarkTheme = "Dark";

        /// <summary>
        /// Applies the specified theme to the current application, updating visual resources to match the selected
        /// appearance.
        /// </summary>
        /// <remarks>If the application is not running, this method has no effect. The method updates
        /// brushes in the application resource dictionary to reflect the chosen theme, affecting window backgrounds,
        /// control colors, status bar appearance, and accent colors.</remarks>
        /// <param name="theme">The name of the theme to apply. Supported values are "Dark" and "Light". The comparison is case-insensitive.</param>
        public static void ApplyTheme(string theme)
        {
            var app = Application.Current;
            if (app == null) return;

            var isDark = string.Equals(theme, DarkTheme, StringComparison.OrdinalIgnoreCase);

            var windowBackground = isDark ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Colors.White;
            var controlBackground = isDark ? Color.FromRgb(0x2D, 0x2D, 0x30) : Color.FromRgb(0xF3, 0xF3, 0xF3);
            var controlForeground = isDark ? Colors.White : Colors.Black;
            var controlBorder     = isDark ? Color.FromRgb(0x3F, 0x3F, 0x46) : Color.FromRgb(0xC8, 0xC8, 0xC8);
            var controlHover      = isDark ? Color.FromRgb(0x3F, 0x3F, 0x4E) : Color.FromRgb(0xDB, 0xE5, 0xF1);
            var statusBackground  = isDark ? Color.FromRgb(0x24, 0x24, 0x24) : Color.FromRgb(0xE8, 0xE8, 0xE8);
            var statusForeground  = isDark ? Colors.White : Colors.Black;
            var accent            = isDark ? Color.FromRgb(0x3A, 0x7A, 0xCC) : Color.FromRgb(0x2F, 0x6F, 0xC6);

            SetBrush(app.Resources, "WindowBackgroundBrush",  windowBackground);
            SetBrush(app.Resources, "ControlBackgroundBrush", controlBackground);
            SetBrush(app.Resources, "ControlForegroundBrush", controlForeground);
            SetBrush(app.Resources, "ControlBorderBrush",     controlBorder);
            SetBrush(app.Resources, "ControlHoverBrush",      controlHover);
            SetBrush(app.Resources, "StatusBackgroundBrush",  statusBackground);
            SetBrush(app.Resources, "StatusForegroundBrush",  statusForeground);
            SetBrush(app.Resources, "AccentBrush",            accent);
        }

        /// <summary>
        /// Sets a SolidColorBrush resource to the specified color. If the resource already exists, 
        /// it updates the existing brush's color; otherwise, it creates a new SolidColorBrush and adds it to the resources.
        /// </summary>
        /// <param name="resources"></param>
        /// <param name="key"></param>
        /// <param name="color"></param>
        private static void SetBrush(ResourceDictionary resources, object key, Color color)
        {
            if (resources[key] is SolidColorBrush brush)
            {
                if (brush.IsFrozen)
                {
                    resources[key] = new SolidColorBrush(color);
                }
                else
                {
                    brush.Color = color;
                }
            }
            else
            {
                resources[key] = new SolidColorBrush(color);
            }
        }
    }
}
