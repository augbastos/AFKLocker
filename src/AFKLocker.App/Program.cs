using System;
using System.Windows.Forms;
using AFKLocker.Core;

namespace AFKLocker.App
{
    /// <summary>
    /// The everyday entry point: lock the session and exit.
    ///
    /// Built as a Windows application rather than a console application so that
    /// double-clicking it never flashes a console window. It does its one job
    /// and terminates - nothing stays resident, because nothing needs to. What
    /// keeps the machine running with the lid closed is the power configuration,
    /// not a process.
    /// </summary>
    internal static class Program
    {
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
                return 0;
            }
            catch (Exception ex)
            {
                // A failure here is rare, but silently doing nothing would leave
                // the user believing the machine is locked when it is not.
                ShowError("AFKLocker could not lock this session.\r\n\r\n" + ex.Message);
                return 1;
            }
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
