using System;
using System.Diagnostics;
using System.Windows.Forms;
using AFKLocker.Core;

namespace AFKLocker.App
{
    /// <summary>
    /// The everyday entry point: enter one temporary AFK session.
    ///
    /// Built as a Windows application rather than a console application so that
    /// double-clicking it never flashes a console window. It stays alive only
    /// while AFK: a process-scoped execution request prevents idle sleep, the
    /// current lid-close values are temporarily set to Do nothing, and display
    /// notifications keep every monitor dark while the lid is closed.
    ///
    /// Opening the lid wakes the displays without ending AFK. Keyboard/mouse
    /// input or unlocking ends the session, restores power and keyboard state,
    /// and exits the process. Nothing remains resident during normal use.
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

                case StartupMode.KeyboardOff:
                    return KeyboardLightingTaskWorker.Run(false);

                case StartupMode.KeyboardRestore:
                    return KeyboardLightingTaskWorker.Run(true);

                case StartupMode.InstallKeyboardIntegration:
                    return ConfigureKeyboardIntegration(false);

                case StartupMode.UninstallKeyboardIntegration:
                    return ConfigureKeyboardIntegration(true);

                default:
                    return LockSession();
            }
        }

        private enum StartupMode
        {
            Lock,
            DisplayOff,
            KeyboardOff,
            KeyboardRestore,
            InstallKeyboardIntegration,
            UninstallKeyboardIntegration,
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
                    case "keyboard-off":
                        return StartupMode.KeyboardOff;
                    case "keyboard-restore":
                        return StartupMode.KeyboardRestore;
                    case "install-keyboard-integration":
                        return StartupMode.InstallKeyboardIntegration;
                    case "uninstall-keyboard-integration":
                        return StartupMode.UninstallKeyboardIntegration;
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
            AfkSession session;
            try
            {
                RestoreLegacyPersistentConfiguration();

                var power = new WindowsPowerConfiguration();
                session = LockAction.EnterAfkMode(
                    new WindowsSessionLocker(),
                    new WindowsDisplayController(),
                    new WindowsUserInputMonitor(),
                    new TemporaryPowerMode(power, new WindowsExecutionStateController()),
                    KeyboardLightingSessionFactory.Create());
            }
            catch (Exception ex)
            {
                ShowError("AFKLocker could not enter AFK mode.\r\n\r\n" + ex.Message);
                return 1;
            }

            RunAfkSession(session);

            if (!string.IsNullOrEmpty(session.RestoreError))
                ShowError("AFK mode ended, but the temporary lid settings could not be fully restored. "
                          + "Run AFKLocker again to retry recovery.\r\n\r\n" + session.RestoreError);

            if (!string.IsNullOrEmpty(session.LightingError))
                ShowError("AFK mode worked, but the Acer keyboard lighting could not be controlled.\r\n\r\n"
                          + session.LightingError);
            return 0;
        }

        /// <summary>
        /// Versions through 0.5.x changed the active plan permanently. Manual
        /// mode no longer needs that configuration, so put the saved originals
        /// back once before capturing the temporary values for this session.
        /// Automatic lid locking still relies on the old configuration and is
        /// therefore left alone until the user switches it off.
        /// </summary>
        private static void RestoreLegacyPersistentConfiguration()
        {
            AutoLockSettings settings = new FileSettingsStore().Load();
            if (settings.Mode == LockMode.Automatic) return;

            var configurator = new PowerConfigurator(
                new WindowsPowerConfiguration(), new FileBackupStore());
            if (!configurator.HasBackup) return;

            RestoreResult result = configurator.RestoreAll();
            if (result.Skipped.Count > 0)
                throw new PowerConfigurationException(
                    "The old permanent power configuration could not be fully restored. "
                    + "Open AFKLocker Setup and choose Restore previous before trying again.");
        }

        /// <summary>
        /// Pumps the notification window until input/unlock finishes AFK mode.
        /// </summary>
        private static void RunAfkSession(AfkSession session)
        {
            // Deliberately no "only one instance" lock here. The obvious design
            // is a mutex so two locks in a row cannot both blank, and it is a
            // trap: when the holder gets stuck, every later lock skips blanking
            // and says nothing. Two blankers briefly asking for the same thing
            // is harmless; a silent opt-out is not.
            if (session == null) return;

            try
            {
                using (session)
                {
                    bool finished = false;
                    session.Finished += delegate
                    {
                        finished = true;
                        Application.ExitThread();
                    };

                    if (!session.HasFinished && !finished) Application.Run();
                }
            }
            catch (Exception)
            {
                // Cosmetic by definition: the session is locked either way, and
                // an error box about a monitor after the screen went dark would
                // be worse than the problem.
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

        private static int ConfigureKeyboardIntegration(bool uninstall)
        {
            if (!KeyboardLightingTaskInstaller.IsAdministrator)
            {
                try
                {
                    using (Process elevated = Process.Start(new ProcessStartInfo
                    {
                        FileName = Application.ExecutablePath,
                        Arguments = uninstall
                            ? "--uninstall-keyboard-integration"
                            : "--install-keyboard-integration",
                        UseShellExecute = true,
                        Verb = "runas"
                    }))
                    {
                        elevated.WaitForExit();
                        return elevated.ExitCode;
                    }
                }
                catch (Exception ex)
                {
                    ShowError("Acer keyboard integration was not configured.\r\n\r\n" + ex.Message);
                    return 1;
                }
            }

            try
            {
                if (uninstall) KeyboardLightingTaskInstaller.Uninstall();
                else
                {
                    KeyboardLightingTaskInstaller.Install(Application.ExecutablePath);

                    // Prove this exact firmware accepts both operations now,
                    // while Setup is open, instead of calling untested task
                    // registration "ready" and failing at bedtime.
                    using (var test = new ScheduledAcerKeyboardLightingSession())
                    {
                        test.Enter();
                    }
                }

                MessageBox.Show(uninstall
                        ? "Acer keyboard integration was removed."
                        : "Acer keyboard integration is ready. AFKLocker will now turn the keyboard "
                          + "lighting off during AFK mode and restore it afterwards.",
                    "AFKLocker", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            catch (Exception ex)
            {
                if (!uninstall && KeyboardLightingTaskInstaller.IsAdministrator)
                {
                    try
                    {
                        KeyboardLightingTaskInstaller.Uninstall();
                    }
                    catch (Exception)
                    {
                    }
                }
                ShowError("Acer keyboard integration could not be configured.\r\n\r\n" + ex.Message);
                return 1;
            }
        }

        private static void ShowUsage()
        {
            MessageBox.Show(
                "AFKLocker\r\n\r\n" +
                "Run with no arguments to lock the session.\r\n\r\n" +
                "  --display-off   Turn the display off without locking.\r\n" +
                "  --install-keyboard-integration\r\n" +
                "                  Configure one-time elevated Acer RGB control.\r\n" +
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
