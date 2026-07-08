using SongConverters;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using WinForms = System.Windows.Forms;

using WpfMessageBox = System.Windows.MessageBox;

namespace CHOConverterWPF
{
    public partial class MainWindow : Window
    {
        /// <summary>
        /// Gets or sets whether the dark theme is currently active.
        /// </summary>
        internal bool IsDarkTheme { get; set; }

        /// <summary>
        /// Constructor for MainWindow. Initializes the WPF components, applies localization to UI elements, and sets default values for OCR language and DPI settings.
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
            ApplyLocalization();

            IsDarkTheme = Properties.Settings.Default.Theme.StartsWith("Dark", StringComparison.OrdinalIgnoreCase);
            ThemeManager.ApplyTheme(IsDarkTheme ? ThemeManager.DarkTheme : ThemeManager.LightTheme);

            txtOcrLanguage.Text = "deu+eng";
            txtOcrDpi.Text = "300";

            txtStatus.Text = Localization.T("StatusReady");
        }

        /// <summary>
        /// Applies localization to all UI elements by setting their text/content properties based on the current culture.
        /// </summary>
        private void ApplyLocalization()
        {
            Title = Localization.T("Title");
            rbFolder.Content = Localization.T("FolderMode");
            rbFiles.Content = Localization.T("FilesMode");
            lblInput.Text = Localization.T("InputFolder");
            lblInputFiles.Text = Localization.T("InputFiles");
            btnAddFiles.Content = Localization.T("AddFiles");
            btnClearFiles.Content = Localization.T("ClearFiles");
            lblOutput.Text = Localization.T("OutputFolder");
            btnBrowseInput.Content = Localization.T("Browse");
            btnBrowseOutput.Content = Localization.T("Browse");
            chkUseOcr.Content = Localization.T("UseOcr");
            lblOcrLang.Text = Localization.T("OcrLanguage");
            lblOcrDpi.Text = Localization.T("OcrDpi");
            btnConvert.Content = Localization.T("Convert");
            btnSettings.Content = Localization.T("Settings");
            btnExit.Content = Localization.T("Exit");
        }

        /// <summary>
        /// Applies the dark title bar via DWM as soon as the Win32 HWND is created –
        /// before the window chrome is painted for the first time.
        /// </summary>
        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            var handle = new WindowInteropHelper(this).Handle;
            TitleBarController.UseImmersiveDarkMode(handle, IsDarkTheme);
        }

        /// <summary>
        /// Shuts down the application.
        /// </summary>
        private void btnExit_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Application.Current.Shutdown();
        }

        /// <summary>
        /// Toggles the input panel between folder mode and file-list mode.
        /// </summary>
        private void rbMode_Checked(object sender, RoutedEventArgs e)
        {
            if (gridFolderInput == null) return;
            bool isFolder = rbFolder.IsChecked == true;
            gridFolderInput.Visibility = isFolder ? Visibility.Visible : Visibility.Collapsed;
            gridFilesInput.Visibility = isFolder ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Opens a file dialog and adds selected PDF/DOCX files to the file list.</summary>
        private void btnAddFiles_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new WinForms.OpenFileDialog
            {
                Title = Localization.T("FilesDialogTitle"),
                Filter = "PDF / DOCX|*.pdf;*.docx|PDF|*.pdf|DOCX|*.docx",
                Multiselect = true,
                CheckFileExists = true
            };

            if (dlg.ShowDialog() == WinForms.DialogResult.OK)
            {
                foreach (var f in dlg.FileNames)
                {
                    if (!lstFiles.Items.Contains(f))
                        lstFiles.Items.Add(f);
                }
            }
        }

        /// <summary>Removes all entries from the file list.</summary>
        private void btnClearFiles_Click(object sender, RoutedEventArgs e)
        {
            lstFiles.Items.Clear();
        }

        /// <summary>Opens a folder browser dialog and sets the input folder path.</summary>
        private void btnBrowseInput_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new WinForms.FolderBrowserDialog
            {
                Description = Localization.T("InputFolder"),
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };

            if (!string.IsNullOrWhiteSpace(txtInput.Text) && Directory.Exists(txtInput.Text))
                dialog.InitialDirectory = txtInput.Text;

            if (dialog.ShowDialog() == WinForms.DialogResult.OK)
                txtInput.Text = dialog.SelectedPath;
        }

        /// <summary>Opens a folder browser dialog and sets the output folder path.</summary>
        private void btnBrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new WinForms.FolderBrowserDialog
            {
                Description = Localization.T("OutputFolder"),
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };

            if (!string.IsNullOrWhiteSpace(txtOutput.Text) && Directory.Exists(txtOutput.Text))
                dialog.InitialDirectory = txtOutput.Text;

            if (dialog.ShowDialog() == WinForms.DialogResult.OK)
                txtOutput.Text = dialog.SelectedPath;
        }

        /// <summary>
        /// Validates inputs, disables the Convert button and runs the conversion asynchronously.
        /// Re-enables the button and shows a status message when finished.
        /// </summary>
        private async void btnConvert_Click(object sender, RoutedEventArgs e)
        {
            var outputDir = txtOutput.Text?.Trim();

            if (string.IsNullOrWhiteSpace(outputDir))
            {
                WpfMessageBox.Show(Localization.T("ErrOutputFolder"), Localization.T("Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            List<string> filesToConvert;

            if (rbFiles.IsChecked == true)
            {
                if (lstFiles.Items.Count == 0)
                {
                    WpfMessageBox.Show(Localization.T("ErrNoFiles"), Localization.T("Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                filesToConvert = lstFiles.Items.Cast<string>().ToList();
            }
            else
            {
                var inputDir = txtInput.Text?.Trim();
                if (string.IsNullOrWhiteSpace(inputDir) || !Directory.Exists(inputDir))
                {
                    WpfMessageBox.Show(Localization.T("ErrInputFolder"), Localization.T("Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                filesToConvert = Directory.EnumerateFiles(inputDir, "*.docx")
                    .Concat(Directory.EnumerateFiles(inputDir, "*.pdf"))
                    .ToList();
            }

            Directory.CreateDirectory(outputDir);

            btnConvert.IsEnabled = false;
            btnSettings.IsEnabled = false;
            txtLog.Clear();
            txtStatus.Text = Localization.T("StatusRunning");

            var useOcr = chkUseOcr.IsChecked == true;
            var ocrLanguage = string.IsNullOrWhiteSpace(txtOcrLanguage.Text) ? "deu+eng" : txtOcrLanguage.Text.Trim();
            var ocrDpi = TryParseInt(txtOcrDpi.Text, 300);

            try
            {
                await Task.Run(() => ConvertInternal(filesToConvert, outputDir, useOcr, ocrLanguage, ocrDpi));
                txtStatus.Text = Localization.T("StatusReady");
            }
            catch (Exception ex)
            {
                txtStatus.Text = Localization.T("Error");
                WpfMessageBox.Show(ex.Message, Localization.T("Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnConvert.IsEnabled = true;
                btnSettings.IsEnabled = true;
            }
        }

        /// <summary>
        /// Performs DOCX and PDF conversion on a background thread.
        /// Initializes OCR engines on demand and logs each file result via the UI dispatcher.
        /// </summary>
        private void ConvertInternal(IEnumerable<string> files, string outputDir, bool useOcr, string ocrLanguage, int ocrDpi)
        {
            void Log(string msg)
            {
                Dispatcher.Invoke(() =>
                {
                    txtLog.AppendText(msg + Environment.NewLine);
                    txtLog.ScrollToEnd();
                });
            }

            var orderedFiles = files.OrderBy(p => p).ToList();

            var utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

            var docxConverter = new DocxToChoConverterOpenXml();
            var pdfConverter = new PdfToChoConverterPdfSharpCore();

            IDisposable pdfCleanup = null;
            IDisposable pdfLayoutCleanup = null;
            PdfConverterOptions pdfOptions = null;
            DocxToChoConverterOpenXmlImageOcr docxOcrConverter = null;
            IPdfToChoConverter pdfLayoutConverter = null;

            try
            {
                if (useOcr)
                {
                    var ocrDataPath = FindOcrDataDir();
                    if (ocrDataPath != null && HasOcrDataFiles(ocrDataPath))
                    {
                        var renderer = new LoggingPdfRenderer(new DocnetPdfRenderer());
                        var advOptions = new TesseractOcrEngineAdvanced.Options(
                            Language: ocrLanguage,
                            Preprocess: true,
                            PreprocessOptions: new ImagePreprocessor.Options(
                                ScalePercent: 180,
                                Grayscale: true,
                                AutoThreshold: true,
                                Invert: false),
                            PrimaryPageSegMode: Tesseract.PageSegMode.SingleBlock,
                            FallbackPageSegModes: new[] { Tesseract.PageSegMode.SingleColumn, Tesseract.PageSegMode.SparseText });

                        var engine = new TesseractOcrEngineAdvanced(ocrDataPath, advOptions);
                        pdfCleanup = engine;
                        pdfOptions = new PdfConverterOptions(
                            AllowOcr: true,
                            OcrDpi: ocrDpi,
                            OcrDumpDirectory: outputDir,
                            AbortOnNoChords: false,
                            AbortOnImageOnly: false,
                            PageRenderer: renderer,
                            OcrEngine: engine);

                        docxOcrConverter = new DocxToChoConverterOpenXmlImageOcr(engine);

                        var chordOcr = new TesseractLayoutOcrEngine(ocrDataPath,
                            new TesseractLayoutOcrEngine.Options(
                                Language: ocrLanguage,
                                PageSegMode: Tesseract.PageSegMode.SparseText,
                                Preprocess: true,
                                PreprocessOptions: new ImagePreprocessor.Options(
                                    ScalePercent: 200,
                                    Grayscale: true,
                                    AutoThreshold: true,
                                    Invert: false),
                                CharWhitelist: "ABCDEFGHabcdefghis#b/0123456789mMajinsudaugdimaddsus"));

                        var lyricOcr = new TesseractLayoutOcrEngine(ocrDataPath,
                            new TesseractLayoutOcrEngine.Options(
                                Language: ocrLanguage,
                                PageSegMode: Tesseract.PageSegMode.SingleBlock,
                                Preprocess: true,
                                PreprocessOptions: new ImagePreprocessor.Options(
                                    ScalePercent: 180,
                                    Grayscale: true,
                                    AutoThreshold: true,
                                    Invert: false)));

                        pdfLayoutConverter = new PdfToChoConverterOcrDualLayout(renderer, chordOcr, lyricOcr);
                        pdfLayoutCleanup = new CompositeDisposable(chordOcr, lyricOcr);

                        Log(Localization.T("OcrInit") + ocrDataPath);
                    }
                    else
                    {
                        Log(Localization.T("OcrMissing"));
                        pdfOptions = new PdfConverterOptions(AbortOnNoChords: false);
                    }
                }
                else
                {
                    pdfOptions = new PdfConverterOptions(AbortOnNoChords: false);
                }

                int ok = 0, skip = 0, fail = 0;

                Log("=== DOCX ===");
                foreach (var docx in orderedFiles.Where(f => f.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        var text = docxConverter.Convert(docx, new ConverterOptions());
                        if ((string.IsNullOrWhiteSpace(text) || IsEffectivelyEmptyCho(text)) && docxOcrConverter != null)
                        {
                            Log("  -> OCR Fallback: " + Path.GetFileName(docx));
                            text = docxOcrConverter.Convert(docx, new ConverterOptions());
                            text = OcrOutputCleaner.Clean(text);
                        }

                        text = OcrOutputCleaner.RemoveSheetMusicNotationLines(text);

                        if (string.IsNullOrWhiteSpace(text) || IsEffectivelyEmptyCho(text))
                            throw new InvalidOperationException("CHO ist leer.");

                        var baseName = Path.GetFileNameWithoutExtension(docx);
                        var docxOut = Path.Combine(outputDir, baseName + ".docx.converted.cho");
                        File.WriteAllText(docxOut, text, utf8Bom);
                        Log("OK: " + Path.GetFileName(docxOut));
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        // Batch-friendly: likely "Noten" files should be skipped.
                        if (IsLikelyNotenFile(docx) || ex.Message.Contains("Notenversion", StringComparison.OrdinalIgnoreCase))
                        {
                            Log("SKIP: " + Path.GetFileName(docx) + " - " + ex.Message);
                            skip++;
                        }
                        else
                        {
                            Log("FAIL: " + Path.GetFileName(docx) + " - " + ex.Message);
                            fail++;
                        }
                    }
                }

                Log("=== PDF ===");
                foreach (var pdf in orderedFiles.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        string text;
                        string errorMsg;
                        if (pdfLayoutConverter != null)
                        {
                            (text, errorMsg) = pdfLayoutConverter.Convert(pdf, new PdfConverterOptions(
                                TabWidth: pdfOptions?.TabWidth ?? 12,
                                OcrDpi: pdfOptions?.OcrDpi ?? 300));
                        }
                        else
                        {
                            (text, errorMsg) = pdfConverter.Convert(pdf, pdfOptions);
                        }

                        if (!string.IsNullOrWhiteSpace(errorMsg))
                        {
                            if (IsLikelyNotenFile(pdf) || errorMsg.Contains("Notenversion", StringComparison.OrdinalIgnoreCase))
                            {
                                Log("SKIP: " + Path.GetFileName(pdf) + " - " + errorMsg);
                                skip++;
                                continue;
                            }

                            Log("Warning: " + errorMsg);
                        }

                        text = OcrOutputCleaner.Clean(text);
                        text = OcrOutputCleaner.RemoveSheetMusicNotationLines(text);

                        if (string.IsNullOrWhiteSpace(text) || IsEffectivelyEmptyCho(text))
                            throw new InvalidOperationException("CHO ist leer.");

                        var baseName = Path.GetFileNameWithoutExtension(pdf);
                        var outPath = Path.Combine(outputDir, baseName + ".pdf.converted.cho");
                        File.WriteAllText(outPath, text, utf8Bom);
                        Log("OK: " + Path.GetFileName(outPath));
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        if (IsLikelyNotenFile(pdf) || ex.Message.Contains("Notenversion", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("keine erkennbaren Akkorde", StringComparison.OrdinalIgnoreCase))
                        {
                            Log("SKIP: " + Path.GetFileName(pdf) + " - " + ex.Message);
                            skip++;
                        }
                        else
                        {
                            Log("FAIL: " + Path.GetFileName(pdf) + " - " + ex.Message);
                            fail++;
                        }
                    }
                }

                Log($"Summary: OK={ok}, SKIP={skip}, FAIL={fail}");
            }
            finally
            {
                pdfCleanup?.Dispose();
                pdfLayoutCleanup?.Dispose();
            }
        }

        /// <summary>Opens the settings dialog and restarts the application if the user changed the language.</summary>
        private void btnSettings_Click(object sender, RoutedEventArgs e)
        {
            var win = new SettingsWindow { Owner = this };

            if (win.ShowDialog() == true && win.RestartRequested)
            {
                App.Restart();
            }
        }

        /// <summary>
        /// Returns <c>true</c> if the given CHO text contains no meaningful content beyond directives.
        /// </summary>
        /// <param name="cho">The ChordPro text to inspect.</param>
        private static bool IsEffectivelyEmptyCho(string cho)
        {
            if (string.IsNullOrWhiteSpace(cho)) return true;

            int contentLines = 0;
            int totalContentChars = 0;
            foreach (var raw in cho.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw?.Trim() ?? string.Empty;
                if (line.Length == 0) continue;
                if (line.StartsWith("{", StringComparison.Ordinal) && line.EndsWith("}", StringComparison.Ordinal))
                    continue;
                contentLines++;
                totalContentChars += line.Length;
            }

            if (contentLines == 0) return true;
            if (totalContentChars < 20) return true;
            if ((double)totalContentChars / contentLines < 3.0) return true;

            return false;
        }

        /// <summary>
        /// Returns <c>true</c> if at least one trained data file (<c>deu.traineddata</c> or <c>eng.traineddata</c>)
        /// exists in the given directory.
        /// </summary>
        /// <param name="ocrDataDir">Path to the OCR data directory.</param>
        private static bool HasOcrDataFiles(string ocrDataDir)
        {
            var deu = Path.Combine(ocrDataDir, "deu.traineddata");
            var eng = Path.Combine(ocrDataDir, "eng.traineddata");
            return File.Exists(deu) || File.Exists(eng);
        }

        /// <summary>
        /// Searches for an OCR data directory by walking up the tree from the application base
        /// and also checking the current working directory.
        /// </summary>
        /// <returns>Full path to the OCR data directory, or <c>null</c> if not found.</returns>
        private static string FindOcrDataDir()
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 6 && dir != null; i++)
            {
                var mainCandidate = Path.Combine(dir, "tessdata-main", "tessdata-main");
                if (Directory.Exists(mainCandidate)) return mainCandidate;

                var candidate = Path.Combine(dir, "tessdata");
                if (Directory.Exists(candidate)) return candidate;
                dir = Directory.GetParent(dir)?.FullName;
            }

            var cwdMainCandidate = Path.Combine(Environment.CurrentDirectory, "tessdata-main", "tessdata-main");
            if (Directory.Exists(cwdMainCandidate)) return cwdMainCandidate;

            var cwdCandidate = Path.Combine(Environment.CurrentDirectory, "tessdata");
            return Directory.Exists(cwdCandidate) ? cwdCandidate : null;
        }

        /// <summary>
        /// Returns <c>true</c> if the filename contains patterns commonly used for sheet-music files
        /// (e.g. contains <c>"noten"</c>).
        /// </summary>
        /// <param name="path">Full or relative file path to check.</param>
        private static bool IsLikelyNotenFile(string path)
        {
            var name = (Path.GetFileNameWithoutExtension(path) ?? string.Empty);
            return name.Contains("noten", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("_noten", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("(noten", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Parses an integer from a string, returning <paramref name="fallback"/> if parsing fails or the value is not positive.</summary>
        /// <param name="text">The string to parse.</param>
        /// <param name="fallback">The value returned when parsing fails.</param>
        private static int TryParseInt(string text, int fallback)
        {
            if (int.TryParse((text ?? string.Empty).Trim(), out var v) && v > 0) return v;
            return fallback;
        }

    }
}
