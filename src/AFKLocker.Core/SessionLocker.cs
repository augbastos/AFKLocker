using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AFKLocker.Core
{
    /// <summary>
    /// Locks the interactive session.
    ///
    /// Behind an interface for one practical reason: a test that called the real
    /// thing would lock the machine running the test suite.
    /// </summary>
    public interface ISessionLocker
    {
        void Lock();
    }

    /// <summary>
    /// Locks the session with user32!LockWorkStation.
    ///
    /// This locks the session and nothing else. It does not suspend the machine,
    /// stop processes, or sign the user out - which is exactly the point:
    /// everything running keeps running behind the lock screen.
    /// </summary>
    public sealed class WindowsSessionLocker : ISessionLocker
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LockWorkStation();

        public void Lock()
        {
            if (!LockWorkStation())
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error,
                    "Windows refused to lock the session: " + new Win32Exception(error).Message);
            }
        }
    }

    /// <summary>
    /// Turns the display off without suspending the system.
    ///
    /// This is an extra, not part of the main flow. Windows re-lights the
    /// monitor on its own when the lock screen appears, so software display-off
    /// is not a reliable way to get a dark screen at lock time - closing the lid
    /// is. Offered separately for desktop users and for turning the screen off
    /// while staying at the machine.
    /// </summary>
    public interface IDisplayController
    {
        void TurnOff();

        /// <summary>Wakes every attached display without synthesizing user input.</summary>
        void TurnOn();
    }

    /// <inheritdoc cref="IDisplayController"/>
    public sealed class WindowsDisplayController : IDisplayController
    {
        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MONITORPOWER = 0xF170;
        private const int MONITOR_ON = -1;
        private const int MONITOR_OFF = 2;

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Asks every top-level window's default handler to put the monitors
        /// into standby, which is what actually powers a panel down rather than
        /// painting it black.
        ///
        /// Deliberately PostMessage rather than SendMessage. A synchronous
        /// broadcast waits on every top-level window and was measured blocking
        /// for almost ten seconds on the target machine. AFK mode has to answer
        /// an external-monitor relight immediately, so the request is queued to
        /// every window and this thread stays free for the next lid/display event.
        /// </summary>
        public void TurnOff()
        {
            SendPowerCommand(MONITOR_OFF);
        }

        public void TurnOn()
        {
            SendPowerCommand(MONITOR_ON);
        }

        private static void SendPowerCommand(int state)
        {
            if (!PostMessage(HWND_BROADCAST, WM_SYSCOMMAND, new IntPtr(SC_MONITORPOWER),
                    new IntPtr(state)))
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error,
                    "Windows refused the display power request: " + new Win32Exception(error).Message);
            }
        }
    }
}
