using System;
using System.Drawing;
using System.Windows.Forms;
using AFKLocker.Core.Diagnostics;

namespace AFKLocker.Setup
{
    /// <summary>
    /// Asks the user to move the lid, and shows what arrives.
    ///
    /// The window has to stay in a message loop for the lid events to be
    /// delivered at all, so this doubles as the thing that keeps the test alive.
    /// It closes itself once both events have been seen, or when the time runs
    /// out.
    /// </summary>
    internal sealed class LidTestProgressForm : Form
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

        private readonly LidDetectionTest _test;
        private readonly Label _instruction = new Label();
        private readonly Label _events = new Label();
        private readonly Label _countdown = new Label();
        private readonly Button _finish = new Button();
        private readonly Timer _timer = new Timer();

        private DateTime _deadline;

        public LidTestProgressForm(LidDetectionTest test)
        {
            _test = test;
            BuildLayout();
        }

        private void BuildLayout()
        {
            Text = "Test lid detection";
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.White;
            ClientSize = new Size(420, 190);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            _instruction.Text = "Close the lid, wait a second, then open it again.";
            _instruction.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            _instruction.Location = new Point(16, 18);
            _instruction.AutoSize = true;
            _instruction.MaximumSize = new Size(ClientSize.Width - 32, 0);

            var reassurance = new Label
            {
                Text = "Your session will not be locked. This only listens for the events Windows sends.",
                ForeColor = Color.FromArgb(94, 94, 94),
                Location = new Point(16, 52),
                AutoSize = true,
                MaximumSize = new Size(ClientSize.Width - 32, 0)
            };

            _events.Location = new Point(16, 92);
            _events.AutoSize = true;
            _events.Font = new Font("Consolas", 9.5F);

            _countdown.Location = new Point(16, 130);
            _countdown.AutoSize = true;
            _countdown.ForeColor = Color.FromArgb(94, 94, 94);

            _finish.Text = "Finish";
            _finish.Size = new Size(90, 28);
            _finish.Location = new Point(ClientSize.Width - 106, ClientSize.Height - 40);
            _finish.Click += delegate { Close(); };

            AcceptButton = _finish;
            CancelButton = _finish;

            Controls.AddRange(new Control[] { _instruction, reassurance, _events, _countdown, _finish });

            Load += OnLoad;
            FormClosed += OnClosed;
        }

        private void OnLoad(object sender, EventArgs e)
        {
            if (!_test.Start())
            {
                _instruction.Text = "This machine does not report lid state.";
                _events.Text = "Windows refused the registration.";
                _countdown.Text = string.Empty;
                return;
            }

            _test.Progress += OnProgress;
            _deadline = DateTime.UtcNow + Timeout;

            _timer.Interval = 500;
            _timer.Tick += OnTick;
            _timer.Start();

            UpdateLabels();
        }

        private void OnProgress(object sender, EventArgs e)
        {
            UpdateLabels();

            // Both halves seen: there is nothing more to learn from waiting.
            if (_test.SawClose && _test.SawOpen)
            {
                _instruction.Text = "Lid detection works on this machine.";
                _timer.Stop();
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            TimeSpan left = _deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                _timer.Stop();
                Close();
                return;
            }
            _countdown.Text = string.Format("Waiting - {0:N0} seconds left.", left.TotalSeconds);
        }

        private void UpdateLabels()
        {
            _events.Text = string.Format("lid closed : {0}\r\nlid opened : {1}",
                _test.SawClose ? "detected" : "waiting...",
                _test.SawOpen ? "detected" : "waiting...");
        }

        private void OnClosed(object sender, FormClosedEventArgs e)
        {
            _timer.Stop();
            _test.Progress -= OnProgress;
            _test.Stop();
        }
    }
}
