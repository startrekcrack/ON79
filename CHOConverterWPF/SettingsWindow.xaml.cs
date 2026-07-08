using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using WpfMessageBox = System.Windows.MessageBox;

namespace CHOConverterWPF
{
    public partial class SettingsWindow : Window
    {
        /// <summary>Gets or sets whether the dark theme is currently active.</summary>
        internal bool IsDarkTheme { get; set; }

        /// <summary>Gets a value indicating whether the user confirmed a language change that requires a restart.</summary>
        public bool RestartRequested { get; private set; }

        /// <summary>
        /// Initializes the settings dialog, reads the current culture and theme from
        /// <see cref="Properties.Settings.Default"/>, and populates the language and theme selectors.
        /// </summary>
        public SettingsWindow()
        {
            InitializeComponent();

            IsDarkTheme = Properties.Settings.Default.Theme.StartsWith("Dark", StringComparison.OrdinalIgnoreCase);

            Title = Localization.T("Settings");
            lblLanguage.Text = Localization.T("Language");
            lblTheme.Text = Localization.T("Theme");
            btnOk.Content = Localization.T("Ok");
            btnCancel.Content = Localization.T("Cancel");

            var items = new List<Tuple<string, string>>
            {
                Tuple.Create(Localization.T("German"), "de-DE"),
                Tuple.Create(Localization.T("English"), "en-US"),
            };

            cmbLanguage.ItemsSource = items;
            cmbLanguage.DisplayMemberPath = "Item1";
            cmbLanguage.SelectedValuePath = "Item2";

            var current = string.IsNullOrWhiteSpace(Properties.Settings.Default.UiCulture) ? Localization.CultureName : Properties.Settings.Default.UiCulture;
            cmbLanguage.SelectedValue = items.Any(i => i.Item2.Equals(current, StringComparison.OrdinalIgnoreCase)) ? current : "de-DE";

            var themeItems = new List<Tuple<string, string>>
            {
                Tuple.Create(Localization.T("ThemeLight"), ThemeManager.LightTheme),
                Tuple.Create(Localization.T("ThemeDark"), ThemeManager.DarkTheme),
            };

            cmbTheme.ItemsSource = themeItems;
            cmbTheme.DisplayMemberPath = "Item1";
            cmbTheme.SelectedValuePath = "Item2";

            var currentTheme = string.IsNullOrWhiteSpace(Properties.Settings.Default.Theme) ? ThemeManager.LightTheme : Properties.Settings.Default.Theme;
            cmbTheme.SelectedValue = themeItems.Any(i => i.Item2.Equals(currentTheme, StringComparison.OrdinalIgnoreCase))
                ? currentTheme
                : ThemeManager.LightTheme;
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

        /// <summary>Closes the dialog without saving.</summary>
        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// Saves the selected language and theme to <see cref="Properties.Settings.Default"/>,
        /// applies a theme change immediately if needed, and prompts the user to restart
        /// when the language has changed.
        /// </summary>
        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            var selected = cmbLanguage.SelectedValue as string;
            if (string.IsNullOrWhiteSpace(selected)) selected = "de-DE";

            var selectedTheme = cmbTheme.SelectedValue as string;
            if (string.IsNullOrWhiteSpace(selectedTheme)) selectedTheme = ThemeManager.LightTheme;

            var languageChanged = !string.Equals(selected, Properties.Settings.Default.UiCulture, StringComparison.OrdinalIgnoreCase);
            var themeChanged = !string.Equals(selectedTheme, Properties.Settings.Default.Theme, StringComparison.OrdinalIgnoreCase);

            Properties.Settings.Default.UiCulture = selected;
            Properties.Settings.Default.Theme = selectedTheme;
            Properties.Settings.Default.Save();

            if (themeChanged)
            {
                ThemeManager.ApplyTheme(selectedTheme);
            }

            if (languageChanged)
            {
                var result = WpfMessageBox.Show(Localization.T("RestartRequired"), Localization.T("Info"), MessageBoxButton.YesNo, MessageBoxImage.Information);
                RestartRequested = result == MessageBoxResult.Yes;
            }

            DialogResult = true;
            Close();
        }
    }
}
