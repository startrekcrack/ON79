using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows;
using AppSettings = CHOConverterWPF.Properties.Settings;

namespace CHOConverterWPF
{
    public partial class App : System.Windows.Application
    {
        /// <summary>
        /// Loads saved settings, applies the active culture and theme, then shows the splash screen
        /// followed by the main window.
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            Localization.SetCulture(AppSettings.Default.UiCulture);
            ThemeManager.ApplyTheme(AppSettings.Default.Theme);

            base.OnStartup(e);

            var splash = new SplashWindow();
            splash.Show();

            var main = new MainWindow();
            main.ContentRendered += (_, _) => splash.Close();
            main.Show();
        }

        /// <summary>
        /// Starts a new instance of the application and shuts down the current one.
        /// The restart is best-effort; if launching the new process fails the shutdown still proceeds.
        /// </summary>
        public static void Restart()
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                }
            }
            catch
            {
                // best-effort
            }

            Current.Shutdown();
        }
    }
}
