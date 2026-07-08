using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace CHOConverterGUI
{
    internal sealed class SplashForm : Form
    {
        /// <summary>
        /// Initializes the splash screen with a centered, borderless window showing the application
        /// logo and the current assembly version.
        /// </summary>
        public SplashForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition   = FormStartPosition.CenterScreen;
            Size            = new Size(320, 320);
            BackColor       = Color.White;
            ShowInTaskbar   = false;
            TopMost         = true;

            // Rounded-corner region
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            int r = 16;
            path.AddArc(0, 0, r * 2, r * 2, 180, 90);
            path.AddArc(Width - r * 2, 0, r * 2, r * 2, 270, 90);
            path.AddArc(Width - r * 2, Height - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(0, Height - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            Region = new Region(path);

            // Logo
            var pic = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.Zoom,
                Bounds   = new Rectangle(40, 40, 240, 220),
                BackColor = Color.Transparent
            };

            var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "ChoConverter.png");
            if (File.Exists(iconPath))
            {
                pic.Image = Image.FromFile(iconPath);
            }

            // Version label
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            var lblVersion = new Label
            {
                Text      = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "",
                ForeColor = Color.Gray,
                Font      = new Font("Segoe UI", 9f),
                AutoSize  = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Bounds    = new Rectangle(0, 276, 320, 28),
                BackColor = Color.Transparent
            };

            Controls.Add(pic);
            Controls.Add(lblVersion);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            // Subtle border
            using var pen = new Pen(Color.FromArgb(60, 0, 0, 0), 1f);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                // WS_EX_DROPSHADOW
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;
                return cp;
            }
        }
    }
}
