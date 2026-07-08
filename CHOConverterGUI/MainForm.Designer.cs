namespace CHOConverterGUI
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;

        // Controls
        private System.Windows.Forms.Label lblTitle;
        private System.Windows.Forms.GroupBox grpInput;
        private System.Windows.Forms.TextBox txtInput;
        private System.Windows.Forms.Button btnBrowseInput;
        private System.Windows.Forms.GroupBox grpOutput;
        private System.Windows.Forms.TextBox txtOutput;
        private System.Windows.Forms.Button btnBrowseOutput;
        private System.Windows.Forms.GroupBox grpOptions;
        private System.Windows.Forms.CheckBox chkUseOcr;
        private System.Windows.Forms.Label lblOcrLanguage;
        private System.Windows.Forms.TextBox txtOcrLanguage;
        private System.Windows.Forms.Label lblOcrDpi;
        private System.Windows.Forms.NumericUpDown numOcrDpi;
        private System.Windows.Forms.Button btnConvert;
        private System.Windows.Forms.Button btnSettings;
        private System.Windows.Forms.Button btnExit;
        private System.Windows.Forms.RichTextBox txtLog;
        private System.Windows.Forms.StatusStrip statusStrip;
        private System.Windows.Forms.ToolStripStatusLabel toolStripStatus;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainForm));
            lblTitle = new Label();
            grpInput = new GroupBox();
            txtInput = new TextBox();
            btnBrowseInput = new Button();
            grpOutput = new GroupBox();
            txtOutput = new TextBox();
            btnBrowseOutput = new Button();
            grpOptions = new GroupBox();
            chkUseOcr = new CheckBox();
            lblOcrLanguage = new Label();
            txtOcrLanguage = new TextBox();
            lblOcrDpi = new Label();
            numOcrDpi = new NumericUpDown();
            btnConvert = new Button();
            btnSettings = new Button();
            btnExit = new Button();
            txtLog = new RichTextBox();
            statusStrip = new StatusStrip();
            toolStripStatus = new ToolStripStatusLabel();
            grpInput.SuspendLayout();
            grpOutput.SuspendLayout();
            grpOptions.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)numOcrDpi).BeginInit();
            statusStrip.SuspendLayout();
            SuspendLayout();
            // 
            // lblTitle
            // 
            lblTitle.Location = new Point(0, 0);
            lblTitle.Name = "lblTitle";
            lblTitle.Size = new Size(100, 23);
            lblTitle.TabIndex = 0;
            // 
            // grpInput
            // 
            grpInput.Controls.Add(txtInput);
            grpInput.Controls.Add(btnBrowseInput);
            grpInput.Location = new Point(0, 0);
            grpInput.Name = "grpInput";
            grpInput.Size = new Size(200, 100);
            grpInput.TabIndex = 1;
            grpInput.TabStop = false;
            // 
            // txtInput
            // 
            txtInput.Location = new Point(0, 0);
            txtInput.Name = "txtInput";
            txtInput.Size = new Size(100, 31);
            txtInput.TabIndex = 0;
            // 
            // btnBrowseInput
            // 
            btnBrowseInput.Location = new Point(0, 0);
            btnBrowseInput.Name = "btnBrowseInput";
            btnBrowseInput.Size = new Size(75, 23);
            btnBrowseInput.TabIndex = 1;
            btnBrowseInput.Click += btnBrowseInput_Click;
            // 
            // grpOutput
            // 
            grpOutput.Controls.Add(txtOutput);
            grpOutput.Controls.Add(btnBrowseOutput);
            grpOutput.Location = new Point(0, 0);
            grpOutput.Name = "grpOutput";
            grpOutput.Size = new Size(200, 100);
            grpOutput.TabIndex = 2;
            grpOutput.TabStop = false;
            // 
            // txtOutput
            // 
            txtOutput.Location = new Point(0, 0);
            txtOutput.Name = "txtOutput";
            txtOutput.Size = new Size(100, 31);
            txtOutput.TabIndex = 0;
            // 
            // btnBrowseOutput
            // 
            btnBrowseOutput.Location = new Point(0, 0);
            btnBrowseOutput.Name = "btnBrowseOutput";
            btnBrowseOutput.Size = new Size(75, 23);
            btnBrowseOutput.TabIndex = 1;
            btnBrowseOutput.Click += btnBrowseOutput_Click;
            // 
            // grpOptions
            // 
            grpOptions.Controls.Add(chkUseOcr);
            grpOptions.Controls.Add(lblOcrLanguage);
            grpOptions.Controls.Add(txtOcrLanguage);
            grpOptions.Controls.Add(lblOcrDpi);
            grpOptions.Controls.Add(numOcrDpi);
            grpOptions.Location = new Point(0, 0);
            grpOptions.Name = "grpOptions";
            grpOptions.Size = new Size(200, 100);
            grpOptions.TabIndex = 3;
            grpOptions.TabStop = false;
            // 
            // chkUseOcr
            // 
            chkUseOcr.Location = new Point(0, 0);
            chkUseOcr.Name = "chkUseOcr";
            chkUseOcr.Size = new Size(104, 24);
            chkUseOcr.TabIndex = 0;
            // 
            // lblOcrLanguage
            // 
            lblOcrLanguage.Location = new Point(0, 0);
            lblOcrLanguage.Name = "lblOcrLanguage";
            lblOcrLanguage.Size = new Size(100, 23);
            lblOcrLanguage.TabIndex = 1;
            // 
            // txtOcrLanguage
            // 
            txtOcrLanguage.Location = new Point(0, 0);
            txtOcrLanguage.Name = "txtOcrLanguage";
            txtOcrLanguage.Size = new Size(100, 31);
            txtOcrLanguage.TabIndex = 2;
            // 
            // lblOcrDpi
            // 
            lblOcrDpi.Location = new Point(0, 0);
            lblOcrDpi.Name = "lblOcrDpi";
            lblOcrDpi.Size = new Size(100, 23);
            lblOcrDpi.TabIndex = 3;
            // 
            // numOcrDpi
            // 
            numOcrDpi.Location = new Point(0, 0);
            numOcrDpi.Name = "numOcrDpi";
            numOcrDpi.Size = new Size(120, 31);
            numOcrDpi.TabIndex = 4;
            // 
            // btnConvert
            // 
            btnConvert.Location = new Point(0, 0);
            btnConvert.Name = "btnConvert";
            btnConvert.Size = new Size(75, 23);
            btnConvert.TabIndex = 4;
            btnConvert.Click += btnConvert_Click;
            // 
            // btnSettings
            // 
            btnSettings.Location = new Point(0, 0);
            btnSettings.Name = "btnSettings";
            btnSettings.Size = new Size(90, 23);
            btnSettings.TabIndex = 7;
            btnSettings.Click += btnSettings_Click;
            // 
            // btnExit
            // 
            btnExit.Location = new Point(0, 0);
            btnExit.Name = "btnExit";
            btnExit.Size = new Size(75, 23);
            btnExit.TabIndex = 8;
            btnExit.Click += btnExit_Click;
            // 
            // txtLog
            // 
            txtLog.Location = new Point(0, 0);
            txtLog.Name = "txtLog";
            txtLog.Size = new Size(100, 96);
            txtLog.TabIndex = 5;
            txtLog.Text = "";
            // 
            // statusStrip
            // 
            statusStrip.ImageScalingSize = new Size(24, 24);
            statusStrip.Items.AddRange(new ToolStripItem[] { toolStripStatus });
            statusStrip.Location = new Point(0, 622);
            statusStrip.Name = "statusStrip";
            statusStrip.Size = new Size(678, 22);
            statusStrip.TabIndex = 6;
            // 
            // toolStripStatus
            // 
            toolStripStatus.Name = "toolStripStatus";
            toolStripStatus.Size = new Size(0, 15);
            // 
            // MainForm
            // 
            ClientSize = new Size(678, 644);
            Controls.Add(lblTitle);
            Controls.Add(grpInput);
            Controls.Add(grpOutput);
            Controls.Add(grpOptions);
            Controls.Add(btnConvert);
            Controls.Add(btnSettings);
            Controls.Add(btnExit);
            Controls.Add(txtLog);
            Controls.Add(statusStrip);
            Font = new Font("Segoe UI", 9F);
            Icon = (Icon)resources.GetObject("$this.Icon");
            MinimumSize = new Size(600, 600);
            Name = "MainForm";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "CHO Converter";
            grpInput.ResumeLayout(false);
            grpInput.PerformLayout();
            grpOutput.ResumeLayout(false);
            grpOutput.PerformLayout();
            grpOptions.ResumeLayout(false);
            grpOptions.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)numOcrDpi).EndInit();
            statusStrip.ResumeLayout(false);
            statusStrip.PerformLayout();
            ResumeLayout(false);
            PerformLayout();
        }
    }
}
