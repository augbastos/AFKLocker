using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using AFKLocker.Core;

namespace AFKLocker.Setup
{
    /// <summary>
    /// Shows whether Windows is configured for closed-lid operation, and offers
    /// to configure it. Read-only until the user presses a button.
    /// </summary>
    internal sealed class SetupForm : Form
    {
        private readonly IPowerConfiguration _power;
        private readonly IPowerInformation _info;
        private readonly IBackupStore _backups;

        private readonly Label _planLabel = new Label();
        private readonly Panel _checksPanel = new Panel();
        private readonly Label _separator = new Label();
        private readonly Label _summaryLabel = new Label();
        private readonly CheckBox _batteryCheck = new CheckBox();
        private readonly Label _batteryNote = new Label();
        private readonly Button _applyButton = new Button();
        private readonly Button _restoreButton = new Button();
        private readonly Button _closeButton = new Button();

        private ReadinessReport _report;

        public SetupForm(IPowerConfiguration power, IPowerInformation info, IBackupStore backups)
            : this(power, info, backups, false)
        {
        }

        public SetupForm(IPowerConfiguration power, IPowerInformation info, IBackupStore backups,
            bool preselectBattery)
        {
            _power = power;
            _info = info;
            _backups = backups;

            BuildLayout();
            _batteryCheck.Checked = preselectBattery;
            Refresh(showErrors: true);
        }

        private void BuildLayout()
        {
            Text = "AFKLocker Setup";
            Font = new Font("Segoe UI", 9F);

            // WinForms defaults to its own icon, not the one compiled into the
            // executable, which leaves a generic icon in the title bar and Alt+Tab.
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (Exception)
            {
                // Cosmetic only - never stop the window opening over an icon.
            }

            BackColor = Color.White;
            ClientSize = new Size(560, 560);
            MinimumSize = new Size(520, 480);
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;

            var title = new Label
            {
                Text = "AFKLocker",
                Font = new Font("Segoe UI", 16F, FontStyle.Regular),
                ForeColor = Color.FromArgb(23, 23, 23),
                AutoSize = true,
                Location = new Point(24, 20)
            };

            var subtitle = new Label
            {
                Text = "Lock your PC. Close the lid. Keep it running.",
                ForeColor = Color.FromArgb(94, 94, 94),
                AutoSize = true,
                Location = new Point(26, 52)
            };

            _planLabel.ForeColor = Color.FromArgb(94, 94, 94);
            _planLabel.AutoSize = true;
            _planLabel.Location = new Point(26, 84);

            _checksPanel.Location = new Point(24, 112);
            _checksPanel.Size = new Size(ClientSize.Width - 48, 230);
            _checksPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _checksPanel.AutoScroll = true;

            _separator.BorderStyle = BorderStyle.Fixed3D;
            _separator.Height = 2;
            _separator.Location = new Point(24, 350);
            _separator.Width = ClientSize.Width - 48;
            _separator.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _summaryLabel.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            _summaryLabel.AutoSize = true;
            _summaryLabel.Location = new Point(24, 364);

            _batteryCheck.Text = "Also keep running on battery";
            _batteryCheck.AutoSize = true;
            _batteryCheck.Location = new Point(26, 400);
            _batteryCheck.CheckedChanged += (s, e) => UpdateButtons();

            _batteryNote.Text =
                "Off by default. On battery this keeps the machine awake with the lid closed, " +
                "which drains the battery and can overheat in a bag or drawer.";
            _batteryNote.ForeColor = Color.FromArgb(94, 94, 94);
            _batteryNote.Location = new Point(44, 422);
            // AutoSize with a width cap so the note grows to however many lines
            // it needs and Bottom stays accurate for the layout pass.
            _batteryNote.AutoSize = true;
            _batteryNote.MaximumSize = new Size(ClientSize.Width - 72, 0);

            _applyButton.Text = "Apply configuration";
            _applyButton.Size = new Size(160, 32);
            _applyButton.Location = new Point(24, ClientSize.Height - 52);
            _applyButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _applyButton.Click += OnApplyClicked;

            _restoreButton.Text = "Restore previous";
            _restoreButton.Size = new Size(140, 32);
            _restoreButton.Location = new Point(192, ClientSize.Height - 52);
            _restoreButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _restoreButton.Click += OnRestoreClicked;

            _closeButton.Text = "Close";
            _closeButton.Size = new Size(100, 32);
            _closeButton.Location = new Point(ClientSize.Width - 124, ClientSize.Height - 52);
            _closeButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _closeButton.Click += (s, e) => Close();

            AcceptButton = _closeButton;
            CancelButton = _closeButton;

            Controls.AddRange(new Control[]
            {
                title, subtitle, _planLabel, _checksPanel, _separator, _summaryLabel,
                _batteryCheck, _batteryNote, _applyButton, _restoreButton, _closeButton
            });
        }

        private void Refresh(bool showErrors)
        {
            try
            {
                PowerSnapshot snapshot = PowerSnapshot.Read(_power, _info);
                _report = ReadinessEvaluator.Evaluate(snapshot);

                _planLabel.Text = "Power plan: " + (snapshot.SchemeName ?? snapshot.Scheme.ToString("D"));
                RenderChecks(_report);

                _summaryLabel.Text = _report.Summary;
                _summaryLabel.ForeColor = _report.IsReady
                    ? Color.FromArgb(16, 124, 16)
                    : Color.FromArgb(196, 43, 28);

                UpdateButtons();
            }
            catch (Exception ex)
            {
                _report = null;
                _planLabel.Text = "Power plan: unavailable";
                _summaryLabel.Text = "Could not read power settings";
                _summaryLabel.ForeColor = Color.FromArgb(196, 43, 28);
                _applyButton.Enabled = false;
                if (showErrors)
                    ShowMessage("AFKLocker could not read this machine's power configuration.\r\n\r\n"
                                + ex.Message, MessageBoxIcon.Warning);
            }
        }

        private void RenderChecks(ReadinessReport report)
        {
            _checksPanel.SuspendLayout();
            foreach (Control existing in _checksPanel.Controls.Cast<Control>().ToList())
                existing.Dispose();
            _checksPanel.Controls.Clear();

            int y = 0;
            int width = _checksPanel.ClientSize.Width - 4;
            foreach (ReadinessCheck check in report.Checks)
            {
                var row = new StatusRow(check)
                {
                    Width = width,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };

                // Add first: a control only inherits the form's font once it has
                // a parent, and measuring with the default font underestimates
                // the height, which clipped the last line of longer details.
                _checksPanel.Controls.Add(row);
                row.Location = new Point(0, y);
                row.Height = row.MeasureHeight(width);
                y += row.Height;
            }
            _checksPanel.ResumeLayout();
            LayoutBelowChecks(y);
        }

        /// <summary>
        /// Positions everything under the checks list and sizes the window to
        /// its content, so a machine with an extra warning does not get a
        /// scrollbar and a machine with fewer checks does not get a gap.
        /// </summary>
        private void LayoutBelowChecks(int contentHeight)
        {
            const int MaxChecksHeight = 320;
            _checksPanel.Height = Math.Min(Math.Max(contentHeight, 60), MaxChecksHeight);

            _separator.Top = _checksPanel.Bottom + 12;
            _summaryLabel.Top = _separator.Top + 14;
            _batteryCheck.Top = _summaryLabel.Bottom + 16;
            _batteryNote.Top = _batteryCheck.Bottom + 4;

            int desiredHeight = _batteryNote.Bottom + 24 + _applyButton.Height + 20;
            ClientSize = new Size(ClientSize.Width, desiredHeight);
        }

        private void UpdateButtons()
        {
            bool hasBackup = _backups.ListSchemes().Any();
            _restoreButton.Enabled = hasBackup;

            if (_report == null)
            {
                _applyButton.Enabled = false;
                return;
            }

            ConfigurationPlan plan = ConfigurationPlanner.Create(_report.Snapshot, _batteryCheck.Checked);
            _applyButton.Enabled = plan.HasChanges;
            _applyButton.Text = plan.HasChanges ? "Apply configuration" : "Nothing to change";
        }

        private void OnApplyClicked(object sender, EventArgs e)
        {
            if (_report == null) return;

            ConfigurationPlan plan = ConfigurationPlanner.Create(_report.Snapshot, _batteryCheck.Checked);
            if (!plan.HasChanges)
            {
                ShowMessage("This machine is already configured for closed-lid operation.",
                    MessageBoxIcon.Information);
                return;
            }

            if (!ConfirmPlan(plan)) return;

            try
            {
                var configurator = new PowerConfigurator(_power, _backups);
                ApplyResult result = configurator.Apply(plan);
                ShowMessage(DescribeApply(result), MessageBoxIcon.Information);
            }
            catch (PowerConfigurationException ex)
            {
                if (!ex.IsAccessDenied)
                {
                    ShowMessage("Could not apply the configuration.\r\n\r\n" + ex.Message, MessageBoxIcon.Warning);
                }
                else if (!TryElevate())
                {
                    ShowMessage("Windows refused to change these settings.\r\n\r\n"
                                + "This usually means the machine is managed by Group Policy, or that "
                                + "AFKLocker Setup needs to run as administrator.", MessageBoxIcon.Warning);
                }
                else
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                ShowMessage("Could not apply the configuration.\r\n\r\n" + ex.Message, MessageBoxIcon.Warning);
            }

            Refresh(showErrors: false);
        }

        private bool ConfirmPlan(ConfigurationPlan plan)
        {
            var text = new StringBuilder();
            text.AppendLine("AFKLocker will change these Windows power settings:");
            text.AppendLine();
            foreach (PlannedChange change in plan.Changes)
                text.AppendLine("  - " + change.Setting.DisplayName + "  ->  " + Describe(change));
            text.AppendLine();
            text.AppendLine("The current values are saved first and can be restored from this window.");

            if (plan.IncludesBattery)
            {
                text.AppendLine();
                text.AppendLine("On battery, the machine will keep running with the lid closed. "
                                + "Never leave it running in a bag or other enclosed space.");
            }

            return MessageBox.Show(this, text.ToString(), "AFKLocker Setup",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;
        }

        private static string Describe(PlannedChange change)
        {
            return change.Setting.Setting == PowerSettingIds.LidCloseAction
                ? PowerValueFormatter.Lid(SettingValue.Of(change.DesiredValue))
                : PowerValueFormatter.Timeout(SettingValue.Of(change.DesiredValue));
        }

        private static string DescribeApply(ApplyResult result)
        {
            var text = new StringBuilder();
            if (result.ChangedAnything)
            {
                text.AppendLine("Configuration applied:");
                foreach (PlannedChange change in result.Applied)
                    text.AppendLine("  - " + change.Setting.DisplayName + " -> " + Describe(change));
            }
            else
            {
                text.AppendLine("No settings were changed.");
            }

            if (result.Skipped.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("Skipped:");
                foreach (string skipped in result.Skipped)
                    text.AppendLine("  - " + skipped);
            }

            return text.ToString();
        }

        private void OnRestoreClicked(object sender, EventArgs e)
        {
            var configurator = new PowerConfigurator(_power, _backups);
            if (!configurator.HasBackup)
            {
                ShowMessage("There is nothing to restore - AFKLocker has not changed any power settings.",
                    MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show(this,
                    "Restore the power settings AFKLocker changed, back to the values they had before?\r\n\r\n"
                    + "After this, closing the lid will do whatever Windows was set to do originally.",
                    "AFKLocker Setup", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            try
            {
                RestoreResult result = configurator.RestoreAll();
                ShowMessage(result.RestoredAnything
                        ? string.Format("Restored {0} setting(s) to their previous values.", result.SettingsRestored)
                        : "Nothing needed restoring.",
                    MessageBoxIcon.Information);
            }
            catch (PowerConfigurationException ex)
            {
                ShowMessage(ex.IsAccessDenied
                        ? "Windows refused to change these settings. Try running AFKLocker Setup "
                          + "as administrator."
                        : "Could not restore the settings.\r\n\r\n" + ex.Message,
                    MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                ShowMessage("Could not restore the settings.\r\n\r\n" + ex.Message, MessageBoxIcon.Warning);
            }

            Refresh(showErrors: false);
        }

        /// <summary>
        /// Re-launches this window elevated when Windows refuses the write.
        /// Returns true when a new elevated instance was started.
        /// </summary>
        private bool TryElevate()
        {
            if (ElevationHelper.IsElevated) return false;

            if (MessageBox.Show(this,
                    "Changing these settings needs administrator rights on this machine.\r\n\r\n"
                    + "Restart AFKLocker Setup as administrator?",
                    "AFKLocker Setup", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return false;

            if (!ElevationHelper.RelaunchElevated(_batteryCheck.Checked))
                return false;

            Close();
            return true;
        }

        private void ShowMessage(string text, MessageBoxIcon icon)
        {
            MessageBox.Show(this, text, "AFKLocker Setup", MessageBoxButtons.OK, icon);
        }
    }
}
