using System;
using System.Threading;
using System.Windows.Forms;
using AFKLocker.Core;

namespace AFKLocker.App
{
    /// <summary>
    /// The everyday entry point: lock the session, put the screens out, exit.
    ///
    /// Built as a Windows application rather than a console application so that
    /// double-clicking it never flashes a console window. What keeps the machine
    /// running with the lid closed is the power configuration, not a process.
    ///
    /// It does linger, briefly, and only for the screens. Locking alone does not
    /// darken them: on a machine with an external monitor, closing the lid makes
    /// Windows reconfigure the displays and light the external panel back up,
    /// leaving the lock screen glowing at an empty desk. So after locking, this
    /// spends up to <see cref="DisplayBlanker.DefaultTimeLimit"/> making sure the
    /// screens go dark and stay dark, then exits by itself. It stops the instant
    /// anyone touches the keyboard, opens the lid, or unlocks.
    /// </summary>
    internal static class Program
    {
        /// <summary>
        /// How long after the blanking window closes before the process is
        /// killed outright. This is a net under a net: nothing should ever reach
        /// it, and if something does, a stuck AFKLocker is worse than an abrupt
        /// one. An earlier version had no net, hung, and every later lock
        /// silently stopped darkening the screen.
        /// </summary>
        private static readonly TimeSpan WatchdogGrace = TimeSpan.FromSeconds(20);

        [STAThread]
        private static int Main(string[] args)
        {
            var mode = ParseArguments(args);

            switch (mode)
            {
                case StartupMode.Help:
                    ShowUsage();
                    return 0;

                case StartupMode.DisplayOff:
                    return TurnDisplayOff();

                default:
                    return LockSession();
            }
        }

        private enum StartupMode
        {
            Lock,
            DisplayOff,
            Help
        }

        private static StartupMode ParseArguments(string[] args)
        {
            if (args == null) return StartupMode.Lock;

            foreach (string arg in args)
            {
                string flag = (arg ?? string.Empty).TrimStart('-', '/').ToLowerInvariant();
                switch (flag)
                {
                    case "display-off":
                    case "displayoff":
                    case "d":
                        return StartupMode.DisplayOff;
                    case "help":
                    case "h":
                    case "?":
                        return StartupMode.Help;
                }
            }

            return StartupMode.Lock;
        }

        private static int LockSession()
        {
            try
            {
                new WindowsSessionLocker().Lock();
            }
            catch (Exception ex)
            {
                // A failure here is rare, but silently doing nothing would leave
                // the user believing the machine is locked when it is not.
                ShowError("AFKLocker could not lock this session.\r\n\r\n" + ex.Message);
                return 1;
            }

            // The lock is the guarantee and it is already done. Everything below
            // is about the screens, and nothing below can undo it.
            KeepScreensDark();
            return 0;
        }

        /// <summary>
        /// Asks for display-off and holds that for a short while, so the lid
        /// closing afterwards does not leave a lit lock screen behind.
        ///
        /// Deliberately after the lock, never before: a dark screen on a session
        /// that failed to lock would look locked without being locked.
        /// </summary>
        private static void KeepScreensDark()
        {
            // Deliberately no "only one instance" lock here. The obvious design
            // is a mutex so two locks in a row cannot both blank, and it is a
            // trap: when the holder gets stuck, every later lock skips blanking
            // and says nothing. Two blankers briefly asking for the same thing
            // is harmless; a silent opt-out is not.
            StartWatchdog();

            try
            {
                using (var blanker = new DisplayBlanker(
                    new WindowsDisplayController(), new WindowsUserInputMonitor()))
                {
                    bool finished = false;
                    blanker.Finished += delegate
                    {
                        finished = true;
                        Application.ExitThread();
                    };

                    blanker.Start();

                    // Start can finish synchronously - Windows refusing the
                    // registration, say - and pumping after that would wait for
                    // a message loop nothing is going to end.
                    if (!finished) Application.Run();
                }
            }
            catch (Exception)
            {
                // Cosmetic by definition: the session is locked either way, and
                // an error box about a monitor after the screen went dark would
                // be worse than the problem.
            }
        }

        /// <summary>
        /// Guarantees this process dies. It is a background thread, so a normal
        /// exit kills it first and it costs nothing; it only ever gets to act if
        /// the message loop is stuck, which is exactly the case that must never
        /// leave an AFKLocker running for hours.
        /// </summary>
        private static void StartWatchdog()
        {
            var watchdog = new Thread(delegate()
            {
                Thread.Sleep(DisplayBlanker.DefaultTimeLimit + WatchdogGrace);
                Environment.Exit(0);
            });

            watchdog.IsBackground = true;
            watchdog.Start();
        }

        private static int TurnDisplayOff()
        {
            try
            {
                new WindowsDisplayController().TurnOff();
                return 0;
            }
            catch (Exception ex)
            {
                ShowError("AFKLocker could not turn the display off.\r\n\r\n" + ex.Message);
                return 1;
            }
        }

        private static void ShowUsage()
        {
            MessageBox.Show(
                "AFKLocker\r\n\r\n" +
                "Run with no arguments to lock the session.\r\n\r\n" +
                "  --display-off   Turn the display off without locking.\r\n" +
                "  --help          Show this message.\r\n\r\n" +
                "Use \"AFKLocker Setup\" to check and configure Windows power settings.",
                "AFKLocker",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private static void ShowError(string message)
        {
            MessageBox.Show(message, "AFKLocker", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
