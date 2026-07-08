using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace CHOConverterWPF
{
    internal static class Localization
    {
        private static string _cultureName = "de-DE";

        /// <summary>Gets the currently active culture name (e.g. <c>"de-DE"</c> or <c>"en-US"</c>).</summary>
        public static string CultureName => _cultureName;

        /// <summary>
        /// Sets the active UI culture for the application and updates both the default thread culture
        /// and the current thread's culture so that all subsequent string operations use the new locale.
        /// Falls back to <c>"de-DE"</c> if the given culture name is invalid.
        /// </summary>
        /// <param name="cultureName">BCP 47 culture tag, e.g. <c>"de-DE"</c> or <c>"en-US"</c>.</param>
        public static void SetCulture(string cultureName)
        {
            _cultureName = string.IsNullOrWhiteSpace(cultureName) ? "de-DE" : cultureName;

            CultureInfo ci;
            try { ci = CultureInfo.GetCultureInfo(_cultureName); }
            catch { ci = CultureInfo.GetCultureInfo("de-DE"); _cultureName = ci.Name; }

            CultureInfo.DefaultThreadCurrentCulture = ci;
            CultureInfo.DefaultThreadCurrentUICulture = ci;
            Thread.CurrentThread.CurrentCulture = ci;
            Thread.CurrentThread.CurrentUICulture = ci;
        }

        /// <summary>
        /// Returns the localized string for the given resource key in the active culture.
        /// Falls back to English if the key is missing from the active culture dictionary.
        /// Returns the key itself if it is not found in any dictionary.
        /// </summary>
        /// <param name="key">The resource key to look up (case-insensitive).</param>
        /// <returns>The localized string, or <paramref name="key"/> if no match is found.</returns>
        public static string T(string key)
        {
            if (key == null) return string.Empty;

            var dict = _cultureName.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? De : En;
            if (dict.TryGetValue(key, out var value)) return value;

            // fallback to english
            if (En.TryGetValue(key, out value)) return value;
            return key;
        }

        private static readonly Dictionary<string, string> De = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Title"] = "CHOConverter",
            ["InputFolder"] = "Quell-Ordner:",
            ["OutputFolder"] = "Ziel-Ordner:",
            ["Browse"] = "Durchsuchen...",
            ["Convert"] = "Konvertieren",
            ["UseOcr"] = "OCR verwenden",
            ["OcrLanguage"] = "OCR Sprache:",
            ["OcrDpi"] = "OCR DPI:",
            ["StatusReady"] = "Bereit.",
            ["StatusRunning"] = "Konvertierung läuft...",
            ["ErrInputFolder"] = "Bitte einen gültigen Quell-Ordner auswählen.",
            ["ErrOutputFolder"] = "Bitte einen Ziel-Ordner auswählen.",
            ["Settings"] = "Einstellungen",
            ["Language"] = "Sprache:",
            ["German"] = "Deutsch",
            ["English"] = "Englisch",
            ["Ok"] = "OK",
            ["Cancel"] = "Abbrechen",
            ["Theme"] = "Design:",
            ["ThemeLight"] = "Hell",
            ["ThemeDark"] = "Dunkel",
            ["RestartRequired"] = "Änderung der Sprache erfordert einen Neustart. Jetzt neu starten?",
            ["Info"] = "Info",
            ["Error"] = "Fehler",
            ["Warning"] = "Warnung",
            ["OcrInit"] = "OCR initialisiert: ",
            ["OcrMissing"] = "Warnung: tessdata nicht gefunden, OCR deaktiviert.",
            ["FolderMode"] = "Ordner",
            ["FilesMode"] = "Dateien",
            ["InputFiles"] = "Quell-Dateien:",
            ["AddFiles"] = "Dateien hinzufügen...",
            ["ClearFiles"] = "Liste leeren",
            ["ErrNoFiles"] = "Bitte mindestens eine Datei auswählen.",
            ["FilesDialogTitle"] = "PDF / DOCX Dateien auswählen",
            ["Exit"] = "Beenden",
        };

        private static readonly Dictionary<string, string> En = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Title"] = "CHOConverter",
            ["InputFolder"] = "Input folder:",
            ["OutputFolder"] = "Output folder:",
            ["Browse"] = "Browse...",
            ["Convert"] = "Convert",
            ["UseOcr"] = "Use OCR",
            ["OcrLanguage"] = "OCR language:",
            ["OcrDpi"] = "OCR DPI:",
            ["StatusReady"] = "Ready.",
            ["StatusRunning"] = "Conversion running...",
            ["ErrInputFolder"] = "Please select a valid input folder.",
            ["ErrOutputFolder"] = "Please select an output folder.",
            ["Settings"] = "Settings",
            ["Language"] = "Language:",
            ["German"] = "German",
            ["English"] = "English",
            ["Ok"] = "OK",
            ["Cancel"] = "Cancel",
            ["Theme"] = "Theme:",
            ["ThemeLight"] = "Light",
            ["ThemeDark"] = "Dark",
            ["RestartRequired"] = "Changing the language requires a restart. Restart now?",
            ["Info"] = "Info",
            ["Error"] = "Error",
            ["Warning"] = "Warning",
            ["OcrInit"] = "OCR initialized: ",
            ["OcrMissing"] = "Warning: tessdata not found, OCR disabled.",
            ["FolderMode"] = "Folder",
            ["FilesMode"] = "Files",
            ["InputFiles"] = "Input files:",
            ["AddFiles"] = "Add files...",
            ["ClearFiles"] = "Clear list",
            ["ErrNoFiles"] = "Please select at least one file.",
            ["FilesDialogTitle"] = "Select PDF / DOCX files",
            ["Exit"] = "Exit",
        };
    }
}
