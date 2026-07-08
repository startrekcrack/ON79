using System.Reflection;
using System.Windows;

namespace CHOConverterWPF
{
    public partial class SplashWindow : Window
    {
        /// <summary>
        /// Initializes the splash window and displays the current assembly version in the version label.
        /// </summary>
        public SplashWindow()
        {
            InitializeComponent();

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            if (version != null)
            {
                txtVersion.Text = $"v{version.Major}.{version.Minor}.{version.Build}";
            }
        }
    }
}
