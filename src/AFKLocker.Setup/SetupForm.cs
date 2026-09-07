using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using AFKLocker.Core;
using AFKLocker.Core.Diagnostics;

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

        /// <summary>
        /// The window is two columns, not one tall strip.
        ///
        /// Everything here is a short, self-contained block, and stacking them
        /// all vertically produced a window taller than the screen: it scrolled,
        /// the buttons at the bottom needed scrolling to reach, and the
        /// right-aligned Close ended up colliding with Diagnostics once the
        /// scrollbar took its width. Reading across two columns costs nothing -
        /// the left side is what this machine is, the right side is what you
        /// want it to do - and the whole thing fits on screen with no scrolling.
        /// </summary>
        private const int ColumnWidth = 430;

        private const int ColumnGutter = 40;

        private readonly IPowerConfiguration _power;
        private readonly IPowerInformation _info;
        private readonly IBackupStore _backups;
        private readonly AutoLockManager _autoLock;

        /// <summary>
        /// True in the window that was relaunched as administrator, which exists
        /// only to write power settings. Read once: it cannot change while the
        /// window is open, and the background features are off-limits here
        /// because a helper started from this process would inherit the token.
        /// </summary>
        private readonly bool _elevated;

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

        private readonly Label _hotkeyHeader = new Label();
        private readonly CheckBox _hotkeyCheck = new CheckBox();
        private readonly HotkeyBox _hotkeyBox = new HotkeyBox();
        private readonly Button _hotkeyClear = new Button();
        private readonly Label _hotkeyNote = new Label();
        private readonly Label _hotkeyStatus = new Label();

        /// <summary>What the form last read from disk, so a change can be told from a redraw.</summary>
        private AutoLockSettings _savedSettings = new AutoLockSettings();

        /// <summary>Guards the handlers while the form is writing its own controls.</summary>
        private bool _loading;

        private readonly Button _applyButton = new Button();
        private readonly Button _restoreButton = new Button();
        private readonly Button _diagnosticsButton = new Button();
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
            _elevated = autoLock.RequiresStandardUser;

            BuildLayout();
            _batteryCheck.Checked = preselectBattery;
            ReconcileOnOpen();
            Refresh(showErrors: true);
        }

        /// <summary>
        /// Puts reality back in line with the saved mode before showing
        /// anything.
        ///
        /// State drifts for ordinary reasons - the watcher was killed in Task
        /// Manager, a cleanup tool removed the startup entry, a crash left a
        /// stale signal. Repairing it here means the window shows a working
        /// machine instead of a puzzle for the user to solve.
        /// </summary>
        private void ReconcileOnOpen()
        {
            try
            {
                AutoLockResult result = _autoLock.Reconcile();
                if (!result.Success)
                    ShowMessage(DescribeFailure(
                        "AFKLocker found automatic locking in a broken state and could not repair it.",
                        result), MessageBoxIcon.Warning);
            }
            catch (Exception)
            {
                // Never stop the window opening over this: the status it shows
                // will report the inconsistency anyway.
            }
        }

        private void BuildLayout()
        {
            Text = "AFKLocker Setup";
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.White;
            ClientSize = new Size((EdgeMargin * 2) + (ColumnWidth * 2) + ColumnGutter, 660);
            MinimumSize = new Size(560, 480);
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

            // Notes and panels are sized to a COLUMN, not to the window. Sizing
            // them to the window is what made every block full-width and forced
            // the vertical stack in the first place.
            int width = ColumnWidth;

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

            // Left-anchored, not stretched to the window: these live in the left
            // column and must not grow across the gutter into the right one.
            _checksPanel.Width = width;
            _checksPanel.Height = 200;
            _checksPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left;

            _separator.BorderStyle = BorderStyle.Fixed3D;
            _separator.Height = 2;
            _separator.Width = width;
            _separator.Anchor = AnchorStyles.Top | AnchorStyles.Left;

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

            StyleSectionHeader(_hotkeyHeader, "GLOBAL HOTKEY");

            _hotkeyCheck.Text = "Lock with a keyboard shortcut";
            _hotkeyCheck.AutoSize = true;
            _hotkeyCheck.CheckedChanged += OnHotkeyEnabledChanged;

            _hotkeyBox.Width = 200;
            _hotkeyBox.BindingChanged += OnHotkeyBindingChanged;

            _hotkeyClear.Text = "Clear";
            _hotkeyClear.Size = new Size(70, 24);
            _hotkeyClear.Click += OnHotkeyClearClicked;

            StyleNote(_hotkeyNote, width - 20,
                "Off by default. Click the box and press the keys you want. Windows tells AFKLocker "
                + "only when that exact combination is pressed - it never sees anything else you type. "
                + "A small background helper runs while this is on, because something has to be "
                + "waiting for the key.");

            _hotkeyStatus.AutoSize = true;
            _hotkeyStatus.MaximumSize = new Size(width - 20, 0);
            _hotkeyStatus.ForeColor = Color.FromArgb(94, 94, 94);

            _applyButton.Text = "Apply configuration";
            _applyButton.Size = new Size(160, 32);
            _applyButton.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _applyButton.Click += OnApplyClicked;

            _restoreButton.Text = "Restore previous";
            _restoreButton.Size = new Size(140, 32);
            _restoreButton.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _restoreButton.Click += OnRestoreClicked;

            _diagnosticsButton.Text = "Diagnostics";
            _diagnosticsButton.Size = new Size(100, 32);
            _diagnosticsButton.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _diagnosticsButton.Click += OnDiagnosticsClicked;

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
                _hotkeyHeader, _hotkeyCheck, _hotkeyBox, _hotkeyClear, _hotkeyNote, _hotkeyStatus,
                _applyButton, _restoreButton, _diagnosticsButton, _closeButton
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

            int left = EdgeMargin;
            int right = EdgeMargin + ColumnWidth + ColumnGutter;
            int top = 88;

            // --- left column: what this machine is ---------------------------
            int y = top;

            _readinessHeader.Location = new Point(left, y);
            y = _readinessHeader.Bottom + 8;

            _planLabel.Location = new Point(left, y);
            y = _planLabel.Bottom + 8;

            _checksPanel.Location = new Point(left, y);
            _checksPanel.Height = Math.Min(Math.Max(checksContentHeight, 60), MaxChecksHeight);
            _checksPanel.AutoScroll = checksContentHeight > MaxChecksHeight;
            y = _checksPanel.Bottom + 10;

            _separator.Location = new Point(left, y);
            y = _separator.Bottom + 12;

            _summaryLabel.Location = new Point(left, y);
            y = _summaryLabel.Bottom + 14;

            _batteryCheck.Location = new Point(left + 2, y);
            y = _batteryCheck.Bottom + 4;

            _batteryNote.Location = new Point(left + 20, y);
            int leftBottom = _batteryNote.Bottom;

            // --- right column: what you want it to do -------------------------
            y = top;

            _lockHeader.Location = new Point(right, y);
            y = _lockHeader.Bottom + 10;

            _manualRadio.Location = new Point(right + 2, y);
            y = _manualRadio.Bottom + 2;
            _manualNote.Location = new Point(right + 20, y);
            y = _manualNote.Bottom + 12;

            _automaticRadio.Location = new Point(right + 2, y);
            y = _automaticRadio.Bottom + 2;
            _automaticNote.Location = new Point(right + 20, y);
            y = _automaticNote.Bottom + 8;

            _watcherStatus.Location = new Point(right + 20, y);
            y = _watcherStatus.Bottom + 24;

            _hotkeyHeader.Location = new Point(right, y);
            y = _hotkeyHeader.Bottom + 10;

            _hotkeyCheck.Location = new Point(right + 2, y);
            y = _hotkeyCheck.Bottom + 6;

            _hotkeyBox.Location = new Point(right + 20, y);
            _hotkeyClear.Location = new Point(_hotkeyBox.Right + 8, y - 1);
            y = _hotkeyBox.Bottom + 6;

            _hotkeyNote.Location = new Point(right + 20, y);
            y = _hotkeyNote.Bottom + 6;

            _hotkeyStatus.Location = new Point(right + 20, y);
            int rightBottom = _hotkeyStatus.Bottom;

            // --- buttons, under whichever column ran longer -------------------
            int buttonRow = Math.Max(leftBottom, rightBottom) + 24;

            _applyButton.Location = new Point(EdgeMargin, buttonRow);
            _restoreButton.Location = new Point(_applyButton.Right + 8, buttonRow);
            _diagnosticsButton.Location = new Point(_restoreButton.Right + 8, buttonRow);

            // Right-aligned, but never on top of Diagnostics. The old code
            // trusted ClientSize.Width, which shrinks when a scrollbar appears -
            // and that is exactly when the two collided.
            int closeX = ClientSize.Width - EdgeMargin - _closeButton.Width;
            int earliestCloseX = _diagnosticsButton.Right + 16;
            _closeButton.Location = new Point(Math.Max(closeX, earliestCloseX), buttonRow);

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

        /// <summary>
        /// Shown in place of the mode and hotkey status in the elevated window.
        /// The controls are switched off rather than left clickable: they cannot
        /// do anything here, and a control that fails when pressed is worse than
        /// one that says why it is unavailable.
        /// </summary>
        private const string ElevatedNote =
            "Unavailable while running as administrator - the background helper must never run "
            + "elevated. Close this window and open AFKLocker Setup normally to change this. "
            + "Whatever is set now keeps working.";

        private void RefreshLockBehaviour()
        {
            AutoLockStatus status = _autoLock.GetStatus();

            _updatingMode = true;
            _manualRadio.Checked = status.Mode == LockMode.Manual;
            _automaticRadio.Checked = status.Mode == LockMode.Automatic;
            _updatingMode = false;

            _manualRadio.Enabled = !_elevated;
            _automaticRadio.Enabled = status.WatcherInstalled && !_elevated;
            _watcherStatus.Text = _elevated ? ElevatedNote : DescribeWatcher(status);

            // Red whenever automatic mode is on but will not actually lock:
            // the watcher is not ready, the configuration disagrees with
            // reality, or the machine reports no lid at all.
            // Not gated on HelperRequired: "nothing needs a helper, but a
            // sign-in entry is still there" is exactly a state worth colouring.
            // Never in the elevated window: it does not reconcile, so what it
            // would be colouring is its own refusal to touch anything.
            bool wontWork = !_elevated
                && (!status.IsConsistent
                    || (status.HelperRequired
                        && (!status.WatcherReady
                            || (status.Mode == LockMode.Automatic
                                && _report != null
                                && AutoLockAdvisor.Evaluate(status.Mode, _report.Snapshot)
                                   == AutoLockWarning.NoLidReported))));

            _watcherStatus.ForeColor = wontWork
                ? Color.FromArgb(196, 43, 28)
                : Color.FromArgb(94, 94, 94);

            RefreshHotkey(status);
        }

        private void RefreshHotkey(AutoLockStatus status)
        {
            _savedSettings = _autoLock.GetSettings();

            _loading = true;
            _hotkeyCheck.Checked = status.HotkeyEnabled;
            _hotkeyBox.SetBindingQuietly(status.Hotkey);
            _loading = false;

            _hotkeyCheck.Enabled = status.WatcherInstalled && !_elevated;
            _hotkeyBox.Enabled = status.WatcherInstalled && !_elevated;
            _hotkeyClear.Enabled = status.WatcherInstalled && !_elevated && !status.Hotkey.IsEmpty;

            if (_elevated)
            {
                _hotkeyStatus.Text = ElevatedNote;
                _hotkeyStatus.ForeColor = Color.FromArgb(94, 94, 94);
                return;
            }

            if (!status.WatcherInstalled)
            {
                _hotkeyStatus.Text = "Unavailable: the AFKLocker helper was not found next to this program.";
                _hotkeyStatus.ForeColor = Color.FromArgb(196, 43, 28);
                return;
            }

            bool broken = false;

            if (!status.HotkeyEnabled)
            {
                _hotkeyStatus.Text = status.Hotkey.IsEmpty
                    ? "Off. Nothing is bound."
                    : "Off. " + status.Hotkey.Describe() + " is remembered but not active.";
            }
            else if (!status.Hotkey.IsUsable)
            {
                _hotkeyStatus.Text = "On, but not usable: " + status.Hotkey.Problem;
                broken = true;
            }
            else if (status.WatcherState == WatcherState.Ready)
            {
                _hotkeyStatus.Text = "On. Press " + status.Hotkey.Describe() + " anywhere to lock.";
            }
            else
            {
                // Saying "on" while the helper that answers the key is not
                // running would be the same lie the readiness handshake exists
                // to prevent.
                _hotkeyStatus.Text = "On, but the helper is not ready, so the key will not work yet.";
                broken = true;
            }

            _hotkeyStatus.ForeColor = broken
                ? Color.FromArgb(196, 43, 28)
                : Color.FromArgb(94, 94, 94);
        }

        private string DescribeWatcher(AutoLockStatus status)
        {
            if (!status.WatcherInstalled)
                return "Automatic lock is unavailable: AFKLockerWatcher.exe was not found next to this program.";

            if (!status.HelperRequired)
            {
                if (status.IsConsistent)
                    return "Helper: not running. Nothing of AFKLocker is resident with manual "
                           + "locking and the hotkey off.";

                // Returning the happy sentence unconditionally hid the one thing
                // worth saying here: that something is still set to start at
                // sign-in for features that are now off.
                return "Helper: not running. " + status.Inconsistency;
            }

            var text = new StringBuilder();
            text.Append(DescribeWatcherState(status.WatcherState));
            text.Append(" It is needed for " + status.HelperPurpose + ".");

            // A mismatch between what was configured and what is true gets said
            // plainly, rather than showing a healthy-looking line over a broken
            // setup.
            if (!status.IsConsistent)
                text.Append("\r\n" + status.Inconsistency);

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

        /// <summary>
        /// "Running" and "able to lock" are different claims, so the wording
        /// tracks the actual state rather than flattening them into one.
        /// </summary>
        private static string DescribeWatcherState(WatcherState state)
        {
            switch (state)
            {
                case WatcherState.Ready:
                    return "Helper: ready. It starts again each time you sign in.";
                case WatcherState.Starting:
                    return "Helper: starting - running, but not ready yet.";
                case WatcherState.Unhealthy:
                    return "Helper: unhealthy. Reopening this window repairs it.";
                default:
                    return "Helper: not running, although something needs it. "
                           + "Reopening this window repairs it.";
            }
        }

        // ------------------------------------------------------------ hotkey ---

        private void OnHotkeyEnabledChanged(object sender, EventArgs e)
        {
            if (_loading) return;

            if (!_hotkeyCheck.Checked)
            {
                ApplyHotkey(false, _hotkeyBox.Binding);
                return;
            }

            // Ticking the box with nothing bound is not an error, it is the
            // normal order of doing this. Wait for a key rather than refusing.
            if (_hotkeyBox.Binding.IsEmpty)
            {
                _hotkeyStatus.Text = "Click the box and press the keys you want to use.";
                _hotkeyBox.Focus();
                return;
            }

            ApplyHotkey(true, _hotkeyBox.Binding);
        }

        private void OnHotkeyBindingChanged(object sender, EventArgs e)
        {
            if (_loading) return;

            HotkeyBinding binding = _hotkeyBox.Binding;

            if (!binding.IsUsable)
            {
                _hotkeyStatus.Text = binding.Problem;
                return;
            }

            // Ask Windows before saving anything. A conflict found here is one
            // sentence; the same conflict found later is a helper that will not
            // start and a user with no idea why.
            HotkeyRegistrationResult probe = HotkeyProbe.TestAvailability(binding);
            if (!probe.Success)
            {
                _hotkeyStatus.Text = probe.Message + " Pick a different combination.";
                return;
            }

            if (!_hotkeyCheck.Checked)
            {
                _hotkeyStatus.Text = binding.Describe()
                    + " is available. Tick the box above to switch it on.";
                return;
            }

            ApplyHotkey(true, binding);
        }

        private void OnHotkeyClearClicked(object sender, EventArgs e)
        {
            _hotkeyBox.SetBindingQuietly(HotkeyBinding.Empty);
            ApplyHotkey(false, HotkeyBinding.Empty);
        }

        private void ApplyHotkey(bool enabled, HotkeyBinding binding)
        {
            try
            {
                AutoLockResult result = _autoLock.SetHotkey(enabled, binding);
                if (!result.Success)
                    ShowMessage(DescribeFailure(enabled
                        ? "The global hotkey could not be turned on."
                        : "The global hotkey could not be turned off.", result), MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                ShowMessage("Could not change the global hotkey.\r\n\r\n" + ex.Message,
                    MessageBoxIcon.Warning);
            }

            RefreshLockBehaviour();
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
                AutoLockResult result = _autoLock.Enable();
                if (!result.Success)
                    ShowMessage(DescribeFailure("Automatic locking could not be turned on.", result),
                        MessageBoxIcon.Warning);
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
                AutoLockResult result = _autoLock.Disable();
                if (!result.Success)
                    ShowMessage(DescribeFailure("Automatic locking was switched off, but not cleanly.",
                        result), MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                ShowMessage("Could not turn off automatic locking.\r\n\r\n" + ex.Message, MessageBoxIcon.Warning);
            }

            RefreshLockBehaviour();
        }

        /// <summary>
        /// Explains a failure, and - just as importantly - whether it left
        /// anything behind. "It failed and everything was put back" and "it
        /// failed and something is still half-configured" call for different
        /// reactions from the user.
        /// </summary>
        private static string DescribeFailure(string headline, AutoLockResult result)
        {
            var text = new StringBuilder();
            text.AppendLine(headline);
            text.AppendLine();
            text.AppendLine(result.Message);

            if (result.RolledBack && result.IsClean)
            {
                // Deliberately does not say what "the way it was" is. A rollback
                // used to be able to end only in "manual and nothing running",
                // so the message said so; now that turning one feature on can
                // fail while another stays on, a rollback often ends with a
                // helper legitimately still running. Naming a state here would
                // be telling the user the opposite of their machine.
                text.AppendLine();
                text.AppendLine("Everything was put back the way it was - nothing on this machine "
                                + "was left half-changed.");
            }

            if (!result.IsClean)
            {
                text.AppendLine();
                text.AppendLine("These could not be cleaned up:");
                foreach (string item in result.Residue)
                    text.AppendLine("  - " + item);
            }

            return text.ToString();
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
                ShowMessage(DescribeApply(result),
                    result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
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

            if (!result.Success)
            {
                text.AppendLine("The power settings could not be changed.");
                text.AppendLine();
                text.AppendLine(result.Message);

                if (result.RolledBack && result.IsClean)
                {
                    text.AppendLine();
                    text.AppendLine("Everything that had already been changed was put back, so this "
                                    + "machine is exactly as it was before.");
                }

                if (!result.IsClean)
                {
                    text.AppendLine();
                    text.AppendLine("These could not be put back:");
                    foreach (string item in result.Residue)
                        text.AppendLine("  - " + item);
                    text.AppendLine();
                    text.AppendLine("Your original values are still saved, so \"Restore previous\" "
                                    + "can put them back.");
                }

                return text.ToString();
            }

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
        /// Opens the diagnostics window, wiring it to the same real components
        /// this window uses so the report describes this machine and not a
        /// simulation of it.
        /// </summary>
        private void OnDiagnosticsClicked(object sender, EventArgs e)
        {
            try
            {
                using (var form = new DiagnosticsForm(RunSelfTest, CreateLidTest))
                {
                    form.ShowDialog(this);
                }
            }
            catch (Exception ex)
            {
                ShowMessage("Diagnostics could not be opened.\r\n\r\n" + ex.Message,
                    MessageBoxIcon.Warning);
            }

            // A self-test can start and stop the watcher, so re-read the state.
            RefreshLockBehaviour();
        }

        private DiagnosticReport RunSelfTest(SelfTestOptions options)
        {
            return RunSelfTestOn(options);
        }

        /// <summary>
        /// Builds and runs a self-test against the real machine. Static so the
        /// diagnostics window can be opened on its own, without a setup window
        /// behind it.
        /// </summary>
        internal static DiagnosticReport RunSelfTestOn(SelfTestOptions options)
        {
            var selfTest = new SelfTest(
                new WindowsPowerConfiguration(), new WindowsPowerInformation(), new FileBackupStore(),
                new FileSettingsStore(),
                new RunKeyAutostartRegistry(),
                new WatcherController(),
                new WindowsEnvironmentProbe(),
                new PathRedactor(),
                WatcherController.DefaultWatcherPath);

            return selfTest.Run(options);
        }

        private static LidDetectionTest CreateLidTest()
        {
            return CreateLidTestOn();
        }

        internal static LidDetectionTest CreateLidTestOn()
        {
            return new LidDetectionTest(new LidNotificationWindow());
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
