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
    /// Shows whether Windows is configured for closed-lid operation, and how
    /// AFKLocker locks the session. Read-only until the user presses something.
    ///
    /// The two halves are deliberately separate: keeping the machine running
    /// with the lid shut and locking when the lid shuts are different
    /// responsibilities, and conflating them would hide the fact that either
    /// can be set up without the other.
    /// </summary>
    internal sealed class SetupForm : Form
    {
        private const int EdgeMargin = 24;

        private readonly IPowerConfiguration _power;
        private readonly IPowerInformation _info;
        private readonly IBackupStore _backups;
        private readonly AutoLockManager _autoLock;

        private readonly Label _readinessHeader = new Label();
        private readonly Label _planLabel = new Label();
        private readonly Panel _checksPanel = new Panel();
        private readonly Label _separator = new Label();
        private readonly Label _summaryLabel = new Label();
        private readonly CheckBox _batteryCheck = new CheckBox();
        private readonly Label _batteryNote = new Label();

        private readonly Label _lockHeader = new Label();
        private readonly RadioButton _manualRadio = new RadioButton();
        private readonly Label _manualNote = new Label();
        private readonly RadioButton _automaticRadio = new RadioButton();
        private readonly Label _automaticNote = new Label();
        private readonly Label _watcherStatus = new Label();

        private readonly Button _applyButton = new Button();
        private readonly Button _restoreButton = new Button();
        private readonly Button _closeButton = new Button();

        private ReadinessReport _report;
        private bool _updatingMode;

        public SetupForm(IPowerConfiguration power, IPowerInformation info, IBackupStore backups,
            AutoLockManager autoLock)
            : this(power, info, backups, autoLock, false)
        {
        }

        public SetupForm(IPowerConfiguration power, IPowerInformation info, IBackupStore backups,
            AutoLockManager autoLock, bool preselectBattery)
        {
            _power = power;
            _info = info;
            _backups = backups;
            _autoLock = autoLock;

            BuildLayout();
            _batteryCheck.Checked = preselectBattery;
            Refresh(showErrors: true);
        }

        private void BuildLayout()
        {
            Text = "AFKLocker Setup";
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.White;
            ClientSize = new Size(560, 740);
            MinimumSize = new Size(540, 560);
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScroll = true;

            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (Exception)
            {
                // Cosmetic only - never stop the window opening over an icon.
            }

            int width = ClientSize.Width - (EdgeMargin * 2);

            var title = new Label
            {
                Text = "AFKLocker",
                Font = new Font("Segoe UI", 16F, FontStyle.Regular),
                ForeColor = Color.FromArgb(23, 23, 23),
                AutoSize = true,
                Location = new Point(EdgeMargin, 20)
            };

            var subtitle = new Label
            {
                Text = "Lock your PC. Close the lid. Keep it running.",
                ForeColor = Color.FromArgb(94, 94, 94),
                AutoSize = true,
                Location = new Point(EdgeMargin + 2, 52)
            };

            StyleSectionHeader(_readinessHeader, "CLOSED-LID READINESS");
            _planLabel.ForeColor = Color.FromArgb(94, 94, 94);
            _planLabel.AutoSize = true;

            _checksPanel.Width = width;
            _checksPanel.Height = 200;
            _checksPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _separator.BorderStyle = BorderStyle.Fixed3D;
            _separator.Height = 2;
            _separator.Width = width;
            _separator.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _summaryLabel.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            _summaryLabel.AutoSize = true;

            _batteryCheck.Text = "Also keep running on battery";
            _batteryCheck.AutoSize = true;
            _batteryCheck.CheckedChanged += (s, e) => UpdateButtons();

            StyleNote(_batteryNote, width - 20,
                "Off by default. On battery this keeps the machine awake with the lid closed, "
                + "which drains the battery and can overheat in a bag or drawer.");

            StyleSectionHeader(_lockHeader, "LOCK BEHAVIOUR");

            _manualRadio.Text = "Manual";
            _manualRadio.AutoSize = true;
            _manualRadio.Checked = true;
            _manualRadio.CheckedChanged += OnModeChanged;
            StyleNote(_manualNote, width - 20, "Double-click AFKLocker before closing the lid. "
                                               + "Nothing of AFKLocker stays running.");

            _automaticRadio.Text = "Automatic";
            _automaticRadio.AutoSize = true;
            _automaticRadio.CheckedChanged += OnModeChanged;
            StyleNote(_automaticNote, width - 20,
                "Lock Windows automatically whenever the laptop lid closes. A small background "
                + "watcher runs while you are signed in. Opening the lid never unlocks anything.");

            // AutoSize with a width cap, like the other notes. A fixed height
            // silently clipped the longer status messages mid-sentence.
            _watcherStatus.AutoSize = true;
            _watcherStatus.MaximumSize = new Size(width - 20, 0);
            _watcherStatus.ForeColor = Color.FromArgb(94, 94, 94);

            _applyButton.Text = "Apply configuration";
            _applyButton.Size = new Size(160, 32);
            _applyButton.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _applyButton.Click += OnApplyClicked;

            _restoreButton.Text = "Restore previous";
            _restoreButton.Size = new Size(140, 32);
            _restoreButton.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _restoreButton.Click += OnRestoreClicked;

            _closeButton.Text = "Close";
            _closeButton.Size = new Size(100, 32);
            _closeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _closeButton.Click += (s, e) => Close();

            AcceptButton = _closeButton;
            CancelButton = _closeButton;

            Controls.AddRange(new Control[]
            {
                title, subtitle,
                _readinessHeader, _planLabel, _checksPanel, _separator, _summaryLabel,
                _batteryCheck, _batteryNote,
                _lockHeader, _manualRadio, _manualNote, _automaticRadio, _automaticNote, _watcherStatus,
                _applyButton, _restoreButton, _closeButton
            });
        }

        private void StyleSectionHeader(Label label, string text)
        {
            label.Text = text;
            label.Font = new Font("Segoe UI", 8F, FontStyle.Bold);
            label.ForeColor = Color.FromArgb(120, 120, 120);
            label.AutoSize = true;
        }

        private static void StyleNote(Label label, int width, string text)
        {
            label.Text = text;
            label.ForeColor = Color.FromArgb(94, 94, 94);
            label.AutoSize = true;
            label.MaximumSize = new Size(width, 0);
        }

        /// <summary>
        /// Lays everything out top to bottom. Doing this in code rather than
        /// with fixed coordinates keeps the window correct when a machine
        /// produces an extra check, or a longer message.
        /// </summary>
        private void PerformVerticalLayout(int checksContentHeight)
        {
            const int MaxChecksHeight = 300;
            int y = 88;

            _readinessHeader.Location = new Point(EdgeMargin, y);
            y = _readinessHeader.Bottom + 8;

            _planLabel.Location = new Point(EdgeMargin, y);
            y = _planLabel.Bottom + 8;

            _checksPanel.Location = new Point(EdgeMargin, y);
            _checksPanel.Height = Math.Min(Math.Max(checksContentHeight, 60), MaxChecksHeight);
            _checksPanel.AutoScroll = checksContentHeight > MaxChecksHeight;
            y = _checksPanel.Bottom + 10;

            _separator.Location = new Point(EdgeMargin, y);
            y = _separator.Bottom + 12;

            _summaryLabel.Location = new Point(EdgeMargin, y);
            y = _summaryLabel.Bottom + 14;

            _batteryCheck.Location = new Point(EdgeMargin + 2, y);
            y = _batteryCheck.Bottom + 4;

            _batteryNote.Location = new Point(EdgeMargin + 20, y);
            y = _batteryNote.Bottom + 24;

            _lockHeader.Location = new Point(EdgeMargin, y);
            y = _lockHeader.Bottom + 10;

            _manualRadio.Location = new Point(EdgeMargin + 2, y);
            y = _manualRadio.Bottom + 2;
            _manualNote.Location = new Point(EdgeMargin + 20, y);
            y = _manualNote.Bottom + 12;

            _automaticRadio.Location = new Point(EdgeMargin + 2, y);
            y = _automaticRadio.Bottom + 2;
            _automaticNote.Location = new Point(EdgeMargin + 20, y);
            y = _automaticNote.Bottom + 8;

            _watcherStatus.Location = new Point(EdgeMargin + 20, y);
            y = _watcherStatus.Bottom + 20;

            int buttonRow = y;
            _applyButton.Location = new Point(EdgeMargin, buttonRow);
            _restoreButton.Location = new Point(_applyButton.Right + 8, buttonRow);
            _closeButton.Location = new Point(ClientSize.Width - EdgeMargin - _closeButton.Width, buttonRow);

            int desired = buttonRow + _applyButton.Height + 20;
            int available = Screen.FromControl(this).WorkingArea.Height - 80;
            ClientSize = new Size(ClientSize.Width, Math.Min(desired, Math.Max(available, 400)));
        }

        private void Refresh(bool showErrors)
        {
            try
            {
                PowerSnapshot snapshot = PowerSnapshot.Read(_power, _info);
                _report = ReadinessEvaluator.Evaluate(snapshot);

                _planLabel.Text = "Power plan: " + (snapshot.SchemeName ?? snapshot.Scheme.ToString("D"));
                int contentHeight = RenderChecks(_report);

                _summaryLabel.Text = _report.Summary;
                _summaryLabel.ForeColor = _report.IsReady
                    ? Color.FromArgb(16, 124, 16)
                    : Color.FromArgb(196, 43, 28);

                RefreshLockBehaviour();
                PerformVerticalLayout(contentHeight);
                UpdateButtons();
            }
            catch (Exception ex)
            {
                _report = null;
                _planLabel.Text = "Power plan: unavailable";
                _summaryLabel.Text = "Could not read power settings";
                _summaryLabel.ForeColor = Color.FromArgb(196, 43, 28);
                _applyButton.Enabled = false;
                RefreshLockBehaviour();
                PerformVerticalLayout(60);
                if (showErrors)
                    ShowMessage("AFKLocker could not read this machine's power configuration.\r\n\r\n"
                                + ex.Message, MessageBoxIcon.Warning);
            }
        }

        private int RenderChecks(ReadinessReport report)
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
            return y;
        }

        // ------------------------------------------------------------ lock mode ---

        private void RefreshLockBehaviour()
        {
            AutoLockStatus status = _autoLock.GetStatus();

            _updatingMode = true;
            _manualRadio.Checked = status.Mode == LockMode.Manual;
            _automaticRadio.Checked = status.Mode == LockMode.Automatic;
            _updatingMode = false;

            _automaticRadio.Enabled = status.WatcherInstalled;
            _watcherStatus.Text = DescribeWatcher(status);

            // Red for the two cases where automatic mode is on but will not
            // actually lock: no watcher, or a machine that reports no lid.
            bool wontWork = status.Mode == LockMode.Automatic
                && (!status.WatcherRunning
                    || (_report != null
                        && AutoLockAdvisor.Evaluate(status.Mode, _report.Snapshot)
                           == AutoLockWarning.NoLidReported));

            _watcherStatus.ForeColor = wontWork
                ? Color.FromArgb(196, 43, 28)
                : Color.FromArgb(94, 94, 94);
        }

        private string DescribeWatcher(AutoLockStatus status)
        {
            if (!status.WatcherInstalled)
                return "Automatic lock is unavailable: AFKLockerWatcher.exe was not found next to this program.";

            if (status.Mode == LockMode.Manual)
                return "Watcher: not running. Nothing of AFKLocker is resident in manual mode.";

            var text = new StringBuilder();
            text.Append(status.WatcherRunning
                ? "Watcher: running. It starts again each time you sign in."
                : "Watcher: not running, although automatic mode is on. Select Manual and then "
                  + "Automatic again to restart it.");

            // "The watcher is running" is not the same as "closing the lid will
            // lock". Anything that stands between the two gets said here.
            if (_report != null)
            {
                string advice = AutoLockAdvisor.Describe(
                    AutoLockAdvisor.Evaluate(status.Mode, _report.Snapshot));
                if (advice != null)
                    text.Append("\r\n" + advice);
            }

            return text.ToString();
        }

        private void OnModeChanged(object sender, EventArgs e)
        {
            if (_updatingMode) return;

            // Only react to the radio that just became checked.
            var radio = sender as RadioButton;
            if (radio == null || !radio.Checked) return;

            if (radio == _automaticRadio)
                EnableAutomatic();
            else
                DisableAutomatic();
        }

        private void EnableAutomatic()
        {
            if (MessageBox.Show(this,
                    "Turn on automatic locking?\r\n\r\n"
                    + "AFKLocker will start a small background watcher when you sign in. When the "
                    + "laptop lid closes, it locks Windows. Opening the lid never unlocks anything.\r\n\r\n"
                    + "The watcher does not change power settings and does not keep the machine "
                    + "awake by itself - that is what the settings above do.\r\n\r\n"
                    + "Never leave a running, lid-closed laptop in a bag, sleeve or drawer.",
                    "AFKLocker Setup", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            {
                RefreshLockBehaviour();
                return;
            }

            try
            {
                if (!_autoLock.Enable())
                    ShowMessage("Could not start the watcher: AFKLockerWatcher.exe was not found "
                                + "next to this program.", MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                ShowMessage("Could not turn on automatic locking.\r\n\r\n" + ex.Message, MessageBoxIcon.Warning);
            }

            RefreshLockBehaviour();
        }

        private void DisableAutomatic()
        {
            try
            {
                if (!_autoLock.Disable())
                    ShowMessage("Automatic locking is off, but the watcher did not stop in time. "
                                + "It will not start again when you sign in.", MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                ShowMessage("Could not turn off automatic locking.\r\n\r\n" + ex.Message, MessageBoxIcon.Warning);
            }

            RefreshLockBehaviour();
        }

        // -------------------------------------------------------- power settings ---

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
