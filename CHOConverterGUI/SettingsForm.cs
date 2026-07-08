using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace CHOConverterGUI
{
    internal sealed class SettingsForm : Form
    {
        private readonly ComboBox _cmbLanguage;

        /// <summary>Gets a value indicating whether the user confirmed a language change that requires a restart.</summary>
        public bool RestartRequested { get; private set; }

        /// <summary>
        /// Initializes the settings dialog, loading current settings and populating the language selector.
        /// </summary>
        public SettingsForm()
        {
            Text = Localization.T("Settings");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(320, 100);
            Font = new Font("Segoe UI", 9F);

            var lblLanguage = new Label
            {
                Text = Localization.T("Language"),
                Left = 12,
                Top = 20,
                AutoSize = true
            };

            _cmbLanguage = new ComboBox
            {
                Left = 110,
                Top = 17,
                Width = 182,
                DropDownStyle = ComboBoxStyle.DropDownList
            };

            var items = new List<(string display, string culture)>
            {
                (Localization.T("German"), "de-DE"),
                (Localization.T("English"), "en-US"),
            };

            foreach (var item in items)
                _cmbLanguage.Items.Add(item.display);

            var current = string.IsNullOrWhiteSpace(Properties.Settings.Default.UiCulture) ? Localization.CultureName : Properties.Settings.Default.UiCulture;
            _cmbLanguage.SelectedIndex = current.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

            var btnOk = new Button
            {
                Text = Localization.T("Ok"),
                Left = 120,
                Top = 60,
                Width = 85,
                DialogResult = DialogResult.OK
            };

            var btnCancel = new Button
            {
                Text = Localization.T("Cancel"),
                Left = 218,
                Top = 60,
                Width = 85,
                DialogResult = DialogResult.Cancel
            };

            btnOk.Click += BtnOk_Click;
            AcceptButton = btnOk;
            CancelButton = btnCancel;

            Controls.Add(lblLanguage);
            Controls.Add(_cmbLanguage);
            Controls.Add(btnOk);
            Controls.Add(btnCancel);
        }

        /// <summary>
        /// Saves the selected language, prompts the user to restart if the culture changed,
        /// and sets <see cref="RestartRequested"/> accordingly.
        /// </summary>
        private void BtnOk_Click(object sender, EventArgs e)
        {
            var selectedCulture = _cmbLanguage.SelectedIndex == 0 ? "de-DE" : "en-US";
            var changed = !string.Equals(selectedCulture, Properties.Settings.Default.UiCulture, StringComparison.OrdinalIgnoreCase);
            Properties.Settings.Default.UiCulture = selectedCulture;
            Properties.Settings.Default.Save();

            if (changed)
            {
                var result = MessageBox.Show(
                    Localization.T("RestartRequired"),
                    Localization.T("Info"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);
                RestartRequested = result == DialogResult.Yes;
            }
        }
    }
}
