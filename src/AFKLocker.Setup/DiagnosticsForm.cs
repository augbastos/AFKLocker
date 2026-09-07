using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using AFKLocker.Core;
using AFKLocker.Core.Diagnostics;

namespace AFKLocker.Setup
{
    /// <summary>
    /// Runs the self-test and lets the user save or copy the result.
    ///
    /// A separate window on purpose: the main setup window is for the two things
    /// people do every day, and burying diagnostics in it would make both worse.
    ///
    /// Nothing here sends anything anywhere. The user reads the report, decides,
    /// and saves a file if they want to.
    /// </summary>
    internal sealed class DiagnosticsForm : Form
    {
        private const int EdgeMargin = 16;
        private const string IssuesUrl = "https://github.com/augbastos/AFKLocker/issues/new";

        private readonly Func<SelfTestOptions, DiagnosticReport> _runSelfTest;
        private readonly Func<LidDetectionTest> _createLidTest;

        private readonly TextBox _output = new TextBox();
        private readonly CheckBox _testWatcher = new CheckBox();
        private readonly CheckBox _includeDevice = new CheckBox();
        private readonly Button _runButton = new Button();
        private readonly Button _lidButton = new Button();
        private readonly Button _exportButton = new Button();
        private readonly Button _copyButton = new Button();
        private readonly Button _reportButton = new Button();
        private readonly Button _closeButton = new Button();

        private DiagnosticReport _report;

        public DiagnosticsForm(Func<SelfTestOptions, DiagnosticReport> runSelfTest,
            Func<LidDetectionTest> createLidTest)
        {
            _runSelfTest = runSelfTest;
            _createLidTest = createLidTest;
            BuildLayout();
        }

        private void BuildLayout()
        {
            Text = "AFKLocker Diagnostics";
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.White;
            ClientSize = new Size(720, 580);
            MinimumSize = new Size(620, 440);
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;

            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (Exception)
            {
                // Cosmetic only.
            }

            var intro = new Label
            {
                Text = "Runs a set of checks on this machine and shows what AFKLocker can and cannot "
                       + "do here. Nothing is sent anywhere - you choose whether to save or share the result.",
                ForeColor = Color.FromArgb(94, 94, 94),
                Location = new Point(EdgeMargin, 12),
                AutoSize = true,
                MaximumSize = new Size(ClientSize.Width - (EdgeMargin * 2), 0)
            };

            _testWatcher.Text = "Also test starting and stopping the watcher";
            _testWatcher.AutoSize = true;
            _testWatcher.Location = new Point(EdgeMargin, 58);

            _includeDevice.Text = "Include this device's manufacturer and model";
            _includeDevice.AutoSize = true;
            _includeDevice.Location = new Point(EdgeMargin, 80);

            _output.Multiline = true;
            _output.ReadOnly = true;
            _output.ScrollBars = ScrollBars.Vertical;
            _output.Font = new Font("Consolas", 9F);
            _output.BackColor = Color.FromArgb(250, 250, 250);
            _output.Location = new Point(EdgeMargin, 110);
            _output.Size = new Size(ClientSize.Width - (EdgeMargin * 2), ClientSize.Height - 110 - 56);
            _output.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _output.Text = "Press \"Run self-test\" to begin.";

            // Laid out left to right from one cursor, with each button sized to
            // its own text: fixed widths clipped the longer labels.
            int row = ClientSize.Height - 42;
            int x = EdgeMargin;
            x = LayoutButton(_runButton, "Run self-test", x, row, OnRun);
            x = LayoutButton(_lidButton, "Test lid detection", x, row, OnLidTest);
            x = LayoutButton(_exportButton, "Export diagnostics", x, row, OnExport);
            x = LayoutButton(_copyButton, "Copy", x, row, OnCopy);
            LayoutButton(_reportButton, "Report a problem", x, row, OnReportProblem);

            LayoutButton(_closeButton, "Close", 0, row, delegate { Close(); });
            _closeButton.Location = new Point(ClientSize.Width - EdgeMargin - _closeButton.Width, row);
            _closeButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

            _exportButton.Enabled = false;
            _copyButton.Enabled = false;

            CancelButton = _closeButton;

            Controls.AddRange(new Control[]
            {
                intro, _testWatcher, _includeDevice, _output,
                _runButton, _lidButton, _exportButton, _copyButton, _reportButton, _closeButton
            });
        }

        /// <summary>
        /// Places a button sized to its own label, and returns where the next
        /// one starts.
        /// </summary>
        private int LayoutButton(Button button, string text, int x, int y, EventHandler onClick)
        {
            const int Padding = 24;
            const int Gap = 8;

            button.Text = text;
            button.AutoSize = false;

            using (Graphics graphics = CreateGraphics())
            {
                int width = (int)Math.Ceiling(graphics.MeasureString(text, Font).Width) + Padding;
                button.Size = new Size(Math.Max(width, 70), 28);
            }

            button.Location = new Point(x, y);
            button.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            button.Click += onClick;

            return button.Right + Gap;
        }

        private void OnRun(object sender, EventArgs e)
        {
            Cursor = Cursors.WaitCursor;
            _runButton.Enabled = false;
            try
            {
                _report = _runSelfTest(new SelfTestOptions
                {
                    TestWatcherLifecycle = _testWatcher.Checked,
                    IncludeDeviceModel = _includeDevice.Checked
                });
                _output.Text = _report.ToText().Replace("\n", "\r\n");
                _exportButton.Enabled = true;
                _copyButton.Enabled = true;
            }
            catch (Exception ex)
            {
                _output.Text = "The self-test could not finish.\r\n\r\n" + ex.Message;
            }
            finally
            {
                _runButton.Enabled = true;
                Cursor = Cursors.Default;
            }
        }

        /// <summary>
        /// The interactive part: ask the user to move the lid and report what
        /// Windows delivered. Never locks - the watcher is not involved.
        /// </summary>
        private void OnLidTest(object sender, EventArgs e)
        {
            if (MessageBox.Show(this,
                    "This checks whether Windows tells AFKLocker about the lid on this machine.\r\n\r\n"
                    + "Close the lid and open it again when the next window appears. Your session "
                    + "will NOT be locked - this test only listens.\r\n\r\n"
                    + "If this machine has no lid, or its lid device is disabled, nothing will be "
                    + "detected and the test will say so.",
                    "Test lid detection", MessageBoxButtons.OKCancel, MessageBoxIcon.Information)
                != DialogResult.OK)
                return;

            LidDetectionTest test = _createLidTest();
            using (var progress = new LidTestProgressForm(test))
            {
                progress.ShowDialog(this);
            }

            AppendLidResult(test);
        }

        private void AppendLidResult(LidDetectionTest test)
        {
            var text = new StringBuilder();
            text.AppendLine();
            text.AppendLine("Lid detection");
            text.AppendLine(string.Format("  [{0,-14}] Physical lid test",
                test.Outcome.ToString().ToUpperInvariant()));
            text.AppendLine("                   " + test.Detail);
            _output.AppendText(text.ToString());

            if (_report != null)
            {
                _report.Add("Lid detection", "lid.detection", "Physical lid test",
                    test.Outcome, test.Detail);
                _report.Fact("lidTest.result", test.Outcome.ToString());
                _report.Fact("lidTest.sawClose", test.SawClose);
                _report.Fact("lidTest.sawOpen", test.SawOpen);
            }
        }

        private void OnExport(object sender, EventArgs e)
        {
            if (_report == null) return;

            if (MessageBox.Show(this, DiagnosticsBundle.PrivacyNotice + "\r\n\r\nSave this report?",
                    "Export diagnostics", MessageBoxButtons.OKCancel, MessageBoxIcon.Information)
                != DialogResult.OK)
                return;

            using (var dialog = new SaveFileDialog())
            {
                dialog.Title = "Save AFKLocker diagnostics";
                dialog.Filter = "Zip archive (*.zip)|*.zip";
                dialog.FileName = DiagnosticsBundle.SuggestedFileName();
                dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    string path = new DiagnosticsBundle().Write(_report, dialog.FileName);
                    if (MessageBox.Show(this,
                            "Saved.\r\n\r\nOpen the folder so you can attach it?",
                            "Export diagnostics", MessageBoxButtons.YesNo, MessageBoxIcon.Information)
                        == DialogResult.Yes)
                    {
                        Process.Start("explorer.exe", "/select,\"" + path + "\"");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not save the report.\r\n\r\n" + ex.Message,
                        "Export diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void OnCopy(object sender, EventArgs e)
        {
            if (_report == null) return;
            try
            {
                Clipboard.SetText(DiagnosticsBundle.BuildSummary(_report));
                MessageBox.Show(this, "The report is on your clipboard.", "AFKLocker Diagnostics",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not copy the report.\r\n\r\n" + ex.Message,
                    "AFKLocker Diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// Opens the issue page. Deliberately does not put the report in the URL:
        /// that would publish it before the user had a chance to read it.
        /// </summary>
        private void OnReportProblem(object sender, EventArgs e)
        {
            if (MessageBox.Show(this,
                    "This opens the AFKLocker issue page in your browser.\r\n\r\n"
                    + "Nothing is sent automatically. Describe what happened, and attach the "
                    + "diagnostics file if you exported one - you can read it first.",
                    "Report a problem", MessageBoxButtons.OKCancel, MessageBoxIcon.Information)
                != DialogResult.OK)
                return;

            try
            {
                Process.Start(IssuesUrl);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not open the browser.\r\n\r\n" + IssuesUrl
                                      + "\r\n\r\n" + ex.Message,
                    "Report a problem", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
