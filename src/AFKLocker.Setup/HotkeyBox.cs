using System;
using System.Drawing;
using System.Windows.Forms;
using AFKLocker.Core;

namespace AFKLocker.Setup
{
    /// <summary>
    /// A box that shows the chosen key combination, and captures a new one while
    /// it has focus.
    ///
    /// It reads keys only while this control is focused, in this window, in this
    /// process. There is no hook and nothing global: the moment focus leaves, it
    /// sees nothing at all. That distinction matters more than usual here,
    /// because the feature being configured is a hotkey - and a hotkey feature
    /// implemented with a keyboard hook is a keylogger with a nice name.
    ///
    /// The captured key is never stored as text or logged anywhere; it becomes a
    /// virtual-key code and modifier flags, which is exactly what Windows needs
    /// to reserve it.
    /// </summary>
    internal sealed class HotkeyBox : TextBox
    {
        private HotkeyBinding _binding = HotkeyBinding.Empty;

        public HotkeyBox()
        {
            ReadOnly = true;
            Cursor = Cursors.Hand;
            TextAlign = HorizontalAlignment.Center;
            BackColor = SystemColors.Window;
            Text = "Click here, then press a key";
        }

        /// <summary>Raised when the user picks something different.</summary>
        public event EventHandler BindingChanged;

        public HotkeyBinding Binding
        {
            get { return _binding; }
            set
            {
                _binding = value ?? HotkeyBinding.Empty;
                Redraw();
            }
        }

        /// <summary>Sets the binding without raising the change event.</summary>
        public void SetBindingQuietly(HotkeyBinding binding)
        {
            _binding = binding ?? HotkeyBinding.Empty;
            Redraw();
        }

        private void Redraw()
        {
            if (_binding.IsEmpty)
            {
                Text = Focused ? "Press a key or combination..." : "Click here, then press a key";
                ForeColor = Color.FromArgb(120, 120, 120);
                return;
            }

            Text = _binding.Describe();
            ForeColor = _binding.IsUsable ? SystemColors.WindowText : Color.FromArgb(160, 60, 30);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Redraw();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Redraw();
        }

        /// <summary>
        /// Claims the keys the dialog would otherwise eat.
        ///
        /// Without this, Tab moves focus, Escape closes the window and the arrow
        /// keys walk the controls - so those keys could never be bound, which is
        /// a silent and confusing limitation rather than an honest one.
        /// </summary>
        protected override bool IsInputKey(Keys keyData)
        {
            return true;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!Focused) return base.ProcessCmdKey(ref msg, keyData);

            // Alt and F10 arrive here rather than through KeyDown, because
            // Windows treats them as menu keys. Binding Alt+something is
            // perfectly reasonable, so they are handled rather than passed on.
            const int WM_SYSKEYDOWN = 0x0104;
            if (msg.Msg == WM_SYSKEYDOWN)
            {
                CaptureCombination(keyData);
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            CaptureCombination(e.KeyData);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        /// <summary>
        /// Named for the combination, not just "Capture": Control.Capture is a
        /// mouse-capture property, and hiding it would have compiled with a
        /// warning this project turns into an error for exactly this reason.
        /// </summary>
        private void CaptureCombination(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;

            HotkeyModifiers modifiers = HotkeyModifiers.None;
            if ((keyData & Keys.Control) != 0) modifiers |= HotkeyModifiers.Control;
            if ((keyData & Keys.Alt) != 0) modifiers |= HotkeyModifiers.Alt;
            if ((keyData & Keys.Shift) != 0) modifiers |= HotkeyModifiers.Shift;

            // A modifier pressed on its own is shown rather than swallowed, so
            // holding Ctrl on the way to Ctrl+L does not look like nothing is
            // happening. HotkeyBinding decides it is not usable, and the form
            // says why.
            var candidate = new HotkeyBinding(modifiers, (uint)key);
            if (candidate.Equals(_binding)) return;

            _binding = candidate;
            Redraw();

            EventHandler handler = BindingChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }
}
