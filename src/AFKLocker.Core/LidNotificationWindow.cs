using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AFKLocker.Core
{
    /// <summary>
    /// The helper's hidden window: lid events, and the global hotkey.
    ///
    /// Windows delivers GUID_LIDSWITCH_STATE_CHANGE and WM_HOTKEY to a window,
    /// so this owns a hidden one. There is no polling anywhere: the process
    /// sleeps until Windows posts a message.
    ///
    /// Creating the window and subscribing to something are separate steps,
    /// because the helper may want only one of the two: a hotkey-only helper has
    /// no business registering for lid notifications, and failing to register
    /// for lid events must not be fatal to a helper that was never asked for
    /// them.
    ///
    /// The name says lid because that is what it started as and renaming it
    /// would churn every caller for nothing; it is the helper's window now.
    ///
    /// The window is an ordinary top-level window that is simply never shown,
    /// not a message-only (HWND_MESSAGE) window. Message-only windows are
    /// documented as not receiving broadcast messages, and WM_POWERBROADCAST is
    /// one - relying on the direct delivery that registration provides would be
    /// betting on an implementation detail. WS_EX_TOOLWINDOW keeps it out of
    /// the taskbar and Alt+Tab.
    /// </summary>
    public sealed class LidNotificationWindow : NativeWindow, ILidEventProvider
    {
        // Documented in "Power Setting GUIDs": the Data member is a DWORD,
        // 0x0 = lid closed, 0x1 = lid opened.
        private static readonly Guid GuidLidSwitchStateChange =
            new Guid("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");

        private const int WM_POWERBROADCAST = 0x0218;
        private const int WM_CLOSE = 0x0010;
        private const int WM_HOTKEY = 0x0312;
        private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
        private const int PBT_APMRESUMESUSPEND = 0x0007;
        private const int PBT_POWERSETTINGCHANGE = 0x8013;
        private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        // POWERBROADCAST_SETTING: GUID PowerSetting; DWORD DataLength; UCHAR Data[1];
        private const int DataLengthOffset = 16;
        private const int DataOffset = 20;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient,
            ref Guid powerSettingGuid, int flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private IntPtr _notificationHandle = IntPtr.Zero;
        private bool _disposed;

        public event EventHandler<LidStateEventArgs> LidStateChanged;

        /// <summary>Raised when the machine resumes, so state can be re-established.</summary>
        public event EventHandler Resumed;

        /// <summary>Raised when Windows asks the window to close.</summary>
        public event EventHandler CloseRequested;

        /// <summary>
        /// Raised when the registered hotkey is pressed. This is the only thing
        /// this process ever learns about the keyboard: Windows posts one
        /// message for one reserved combination, and no other keystroke is
        /// visible here at all.
        /// </summary>
        public event EventHandler HotkeyPressed;

        /// <summary>
        /// True once Windows has actually reported a lid position. Registration
        /// succeeds even on machines with no lid - the documentation is explicit
        /// that the callback is not made "until a lid device is found and its
        /// current state is known" - so this is the only honest way to tell
        /// whether lid events work here.
        /// </summary>
        public bool HasSeenLidEvent { get; private set; }

        /// <summary>
        /// Creates the window without subscribing to anything. Needed on its own
        /// by a helper that only wants the hotkey, which still needs a window
        /// for Windows to post WM_HOTKEY to.
        /// </summary>
        public bool Create()
        {
            if (Handle != IntPtr.Zero) return true;

            try
            {
                var parameters = new CreateParams
                {
                    Caption = "AFKLocker Helper",
                    X = 0,
                    Y = 0,
                    Height = 0,
                    Width = 0,
                    Style = 0,                       // no WS_VISIBLE: never shown
                    ExStyle = WS_EX_TOOLWINDOW       // no taskbar, no Alt+Tab
                };
                CreateHandle(parameters);
            }
            catch (Exception)
            {
                return false;
            }

            return Handle != IntPtr.Zero;
        }

        /// <summary>
        /// Subscribes to lid open/close. Returns false when this machine cannot
        /// deliver them, which is the difference between a helper that will lock
        /// on lid close and one that would sit there looking healthy.
        /// </summary>
        public bool StartLidNotifications()
        {
            if (!Create()) return false;
            if (_notificationHandle != IntPtr.Zero) return true;

            Guid setting = GuidLidSwitchStateChange;
            _notificationHandle = RegisterPowerSettingNotification(Handle, ref setting,
                DEVICE_NOTIFY_WINDOW_HANDLE);

            return _notificationHandle != IntPtr.Zero;
        }

        /// <summary>Creates the window and subscribes to lid events, as one step.</summary>
        public bool Start()
        {
            return StartLidNotifications();
        }

        public void Stop()
        {
            if (_notificationHandle != IntPtr.Zero)
            {
                UnregisterPowerSettingNotification(_notificationHandle);
                _notificationHandle = IntPtr.Zero;
            }

            if (Handle != IntPtr.Zero)
                DestroyHandle();
        }

        /// <summary>
        /// Asks the message loop to finish. Safe to call from another thread -
        /// which is the point, since the stop signal arrives on a pool thread.
        /// </summary>
        public void RequestClose()
        {
            IntPtr handle = Handle;
            if (handle != IntPtr.Zero)
                PostMessage(handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_POWERBROADCAST)
            {
                int eventType = m.WParam.ToInt32();

                if (eventType == PBT_POWERSETTINGCHANGE)
                    HandlePowerSettingChange(m.LParam);
                else if (eventType == PBT_APMRESUMESUSPEND || eventType == PBT_APMRESUMEAUTOMATIC)
                    Raise(Resumed);

                m.Result = (IntPtr)1;   // TRUE
                return;
            }

            if (m.Msg == WM_HOTKEY)
            {
                // The message carries which hotkey fired and which modifiers were
                // down. Neither is inspected: this process registered exactly one
                // combination, so anything arriving here is that one.
                Raise(HotkeyPressed);
                return;
            }

            if (m.Msg == WM_CLOSE)
            {
                Raise(CloseRequested);
                return;
            }

            base.WndProc(ref m);
        }

        private void HandlePowerSettingChange(IntPtr lParam)
        {
            if (lParam == IntPtr.Zero) return;

            var setting = (Guid)Marshal.PtrToStructure(lParam, typeof(Guid));
            if (setting != GuidLidSwitchStateChange) return;

            int dataLength = Marshal.ReadInt32(lParam, DataLengthOffset);
            if (dataLength < sizeof(int)) return;

            int value = Marshal.ReadInt32(lParam, DataOffset);
            if (value != (int)LidState.Closed && value != (int)LidState.Opened) return;

            HasSeenLidEvent = true;

            EventHandler<LidStateEventArgs> handler = LidStateChanged;
            if (handler != null)
                handler(this, new LidStateEventArgs((LidState)value));
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null) handler(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
