using System;
using System.Windows.Forms;

namespace CHOConverterGUI
{
    internal static class Program
    {
        /// <summary>
        /// Application entry point. Loads user settings, sets the UI culture, shows a splash screen,
        /// and then starts the main form.
        /// </summary>
        [STAThread]
        static void Main()
        {
            var settings = CHOConverterGUI.Properties.Settings.Default;
            Localization.SetCulture(settings.UiCulture);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var splash = new SplashForm();
            splash.Show();
            Application.DoEvents();
            Thread.Sleep(3000);
            Application.DoEvents();

            var mainForm = new MainForm();
            mainForm.Load += (_, _) => splash.Close();

            Application.Run(mainForm);
        }
    }
}
