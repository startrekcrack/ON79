using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using SongConverters;

namespace CHOConverterGUI
{
    /// <summary>
    /// Main form for the CHOConverter GUI application.
    /// Provides a user interface for converting DOCX and PDF files to ChordPro format.
    /// </summary>
    public partial class MainForm : Form
    {
        private BackgroundWorker _worker;

        /// <summary>
        /// Initializes a new instance of the MainForm class.
        /// </summary>
        public MainForm()
        {
            InitializeComponent();
            LoadAppIcon();
            ApplyLocalization();
            InitializeWorker();
        }

        /// <summary>
        /// Applies localized text to all UI labels, buttons, and status strip items.
        /// </summary>
        private void ApplyLocalization()
        {
            Text = Localization.T("Title");
            grpInput.Text = Localization.T("GrpInput");
            grpOutput.Text = Localization.T("GrpOutput");
            grpOptions.Text = Localization.T("GrpOptions");
            btnBrowseInput.Text = Localization.T("Browse");
            btnBrowseOutput.Text = Localization.T("Browse");
            btnConvert.Text = Localization.T("Convert");
            btnSettings.Text = Localization.T("Settings");
            btnExit.Text = Localization.T("Exit");
            chkUseOcr.Text = Localization.T("UseOcr");
            lblOcrLanguage.Text = Localization.T("OcrLanguage");
            lblOcrDpi.Text = Localization.T("OcrDpi");
            toolStripStatus.Text = Localization.T("StatusReady");
        }

        /// <summary>
        /// Loads the application icon from the Assets folder and applies it to the form.
        /// Silently skips if the icon file is not found.
        /// </summary>
        private void LoadAppIcon()
        {
            var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "ChoConverter.png");
            if (File.Exists(iconPath))
            {
                using var bmp = new Bitmap(iconPath);
                this.Icon = Icon.FromHandle(bmp.GetHicon());
            }
        }

        /// <summary>
        /// Creates and configures the <see cref="BackgroundWorker"/> used for the conversion task,
        /// wiring up progress, work, and completion event handlers.
        /// </summary>
        private void InitializeWorker()
        {
            _worker = new BackgroundWorker
            {
                WorkerReportsProgress = true,
                WorkerSupportsCancellation = false
            };
            _worker.DoWork += Worker_DoWork;
            _worker.ProgressChanged += Worker_ProgressChanged;
            _worker.RunWorkerCompleted += Worker_RunWorkerCompleted;
        }

        /// <summary>Opens a folder browser dialog and sets the input folder path.</summary>
        private void btnBrowseInput_Click(object sender, EventArgs e)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = Localization.T("BrowseInputTitle"),
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };
            if (!string.IsNullOrWhiteSpace(txtInput.Text) && Directory.Exists(txtInput.Text))
                dialog.InitialDirectory = txtInput.Text;

            if (dialog.ShowDialog() == DialogResult.OK)
                txtInput.Text = dialog.SelectedPath;
        }

        /// <summary>Opens a folder browser dialog and sets the output folder path.</summary>
        private void btnBrowseOutput_Click(object sender, EventArgs e)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = Localization.T("BrowseOutputTitle"),
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };
            if (!string.IsNullOrWhiteSpace(txtOutput.Text) && Directory.Exists(txtOutput.Text))
                dialog.InitialDirectory = txtOutput.Text;

            if (dialog.ShowDialog() == DialogResult.OK)
                txtOutput.Text = dialog.SelectedPath;
        }

        /// <summary>
        /// Validates inputs and starts the background conversion worker.
        /// Disables the Convert button while the worker is running.
        /// </summary>
        private void btnConvert_Click(object sender, EventArgs e)
        {
            var inputDir = txtInput.Text?.Trim();
            var outputDir = txtOutput.Text?.Trim();

            if (string.IsNullOrWhiteSpace(inputDir) || !Directory.Exists(inputDir))
            {
                MessageBox.Show(Localization.T("ErrInputFolder"), Localization.T("Error"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(outputDir))
            {
                MessageBox.Show(Localization.T("ErrOutputFolder"), Localization.T("Error"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Directory.CreateDirectory(outputDir);

            btnConvert.Enabled = false;
            txtLog.Clear();
            toolStripStatus.Text = Localization.T("StatusRunning");

            var args = new ConversionArgs
            {
                InputDir = inputDir,
                OutputDir = outputDir,
                UseOcr = chkUseOcr.Checked,
                OcrLanguage = txtOcrLanguage.Text?.Trim() ?? "deu+eng",
                OcrDpi = (int)numOcrDpi.Value
            };

            _worker.RunWorkerAsync(args);
        }

        /// <summary>
        /// Performs the DOCX and PDF conversion on the background thread.
        /// Initializes OCR engines if requested and logs progress via
        /// <see cref="BackgroundWorker.ReportProgress(int, object)"/>.
        /// </summary>
        private void Worker_DoWork(object sender, DoWorkEventArgs e)
        {
            var worker = (BackgroundWorker)sender;
            var args = (ConversionArgs)e.Argument;

            void Log(string msg) => worker.ReportProgress(0, msg);

            var docxConverter = new DocxToChoConverterOpenXml();
            var pdfConverter = new PdfToChoConverterPdfSharpCore();

            IDisposable pdfCleanup = null;
            IDisposable pdfLayoutCleanup = null;
            PdfConverterOptions pdfOptions = null;
            DocxToChoConverterOpenXmlImageOcr docxOcrConverter = null;
            IPdfToChoConverter pdfLayoutConverter = null;

            try
            {
                if (args.UseOcr)
                {
                    var ocrDataPath = FindOcrDataDir();
                    if (ocrDataPath != null && HasOcrDataFiles(ocrDataPath))
                    {
                        var renderer = new LoggingPdfRenderer(new DocnetPdfRenderer());
                        var advOptions = new TesseractOcrEngineAdvanced.Options(
                            Language: args.OcrLanguage,
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
                            OcrDpi: args.OcrDpi,
                            OcrDumpDirectory: args.OutputDir,
                            AbortOnNoChords: false,
                            AbortOnImageOnly: false,
                            PageRenderer: renderer,
                            OcrEngine: engine);

                        docxOcrConverter = new DocxToChoConverterOpenXmlImageOcr(engine);

                        var chordOcr = new TesseractLayoutOcrEngine(ocrDataPath,
                            new TesseractLayoutOcrEngine.Options(
                                Language: args.OcrLanguage,
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
                                Language: args.OcrLanguage,
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

                int ok = 0, fail = 0;
                var utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

                // DOCX
                Log("=== DOCX ===");
                foreach (var docx in Directory.EnumerateFiles(args.InputDir, "*.docx").OrderBy(p => p))
                {
                    try
                    {
                        Log($"Verarbeite: {Path.GetFileName(docx)}");
                        var baseName = Path.GetFileNameWithoutExtension(docx);
                        var text = docxConverter.Convert(docx, new ConverterOptions());
                        if ((string.IsNullOrWhiteSpace(text) || IsEffectivelyEmptyCho(text)) && docxOcrConverter != null)
                        {
                            Log("  -> Fallback auf OCR...");
                            text = docxOcrConverter.Convert(docx, new ConverterOptions());
                            text = OcrOutputCleaner.Clean(text);
                        }
                        text = OcrOutputCleaner.RemoveSheetMusicNotationLines(text);
                        if (string.IsNullOrWhiteSpace(text))
                            throw new InvalidOperationException("CHO ist leer.");
                        var docxOut = Path.Combine(args.OutputDir, baseName + ".docx.converted.cho");
                        var baseOut = Path.Combine(args.OutputDir, baseName + ".converted.cho");
                        File.WriteAllText(docxOut, text, utf8Bom);
                        File.WriteAllText(baseOut, text, utf8Bom);
                        Log($"  OK -> {Path.GetFileName(docxOut)}");
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        Log($"  FEHLER: {ex.Message}");
                        fail++;
                    }
                }

                // PDF
                Log("=== PDF ===");
                foreach (var pdf in Directory.EnumerateFiles(args.InputDir, "*.pdf").OrderBy(p => p))
                {
                    try
                    {
                        Log($"Verarbeite: {Path.GetFileName(pdf)}");
                        var baseName = Path.GetFileNameWithoutExtension(pdf);

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
                            Log($"  Warnung: {errorMsg}");
                        }

                        text = OcrOutputCleaner.Clean(text);
                        text = OcrOutputCleaner.RemoveSheetMusicNotationLines(text);

                        if (string.IsNullOrWhiteSpace(text))
                            throw new InvalidOperationException("CHO ist leer.");
                        var outPath = Path.Combine(args.OutputDir, baseName + ".pdf.converted.cho");
                        File.WriteAllText(outPath, text, utf8Bom);
                        Log($"  OK -> {Path.GetFileName(outPath)}");
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        Log($"  FEHLER: {ex.Message}");
                        fail++;
                    }
                }

                e.Result = (ok, fail);
            }
            finally
            {
                pdfCleanup?.Dispose();
                pdfLayoutCleanup?.Dispose();
            }
        }

        /// <summary>Appends a log message reported by the background worker to the log text box.</summary>
        private void Worker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            if (e.UserState is string msg)
            {
                txtLog.AppendText(msg + Environment.NewLine);
            }
        }

        /// <summary>
        /// Re-enables the Convert button and updates the status strip with the conversion result or error message.
        /// </summary>
        private void Worker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            btnConvert.Enabled = true;

            if (e.Error != null)
            {
                toolStripStatus.Text = Localization.T("StatusError") + e.Error.Message;
                MessageBox.Show(Localization.T("StatusError") + e.Error.Message, Localization.T("Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else if (e.Result is (int ok, int fail))
            {
                toolStripStatus.Text = string.Format(Localization.T("StatusDone"), ok, fail);
            }
            else
            {
                toolStripStatus.Text = Localization.T("StatusFinished");
            }
        }

        /// <summary>Opens the settings dialog and restarts the application if the user changed the language.</summary>
        private void btnSettings_Click(object sender, EventArgs e)
        {
            using var form = new SettingsForm();
            if (form.ShowDialog(this) == DialogResult.OK && form.RestartRequested)
                Application.Restart();
        }

        /// <summary>Exits the application.</summary>
        private void btnExit_Click(object sender, EventArgs e)
        {
            Application.Exit();
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
        /// Returns <c>true</c> if at least one trained data file (<c>deu.traineddata</c> or <c>eng.traineddata</c>) exists in the given directory.
        /// </summary>
        /// <param name="ocrDataDir">Path to the OCR data directory.</param>
        private static bool HasOcrDataFiles(string ocrDataDir)
        {
            var deu = Path.Combine(ocrDataDir, "deu.traineddata");
            var eng = Path.Combine(ocrDataDir, "eng.traineddata");
            return File.Exists(deu) || File.Exists(eng);
        }

        /// <summary>
        /// Searches for an OCR data directory by walking up the directory tree from the application base
        /// and also checking the current working directory.
        /// </summary>
        /// <returns>The full path to the OCR data directory, or <c>null</c> if not found.</returns>
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

        private sealed class ConversionArgs
        {
            public string InputDir { get; set; }
            public string OutputDir { get; set; }
            public bool UseOcr { get; set; }
            public string OcrLanguage { get; set; }
            public int OcrDpi { get; set; }
        }
    }
}
