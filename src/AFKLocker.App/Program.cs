using System;
using System.Collections.Generic;
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
            args = args ?? new string[0];

            switch (ParseArguments(args))
            {
                case StartupMode.Help:
                    ShowUsage();
                    return 0;

                case StartupMode.DisplayOff:
                    return TurnDisplayOff();

                case StartupMode.KeyboardLightingOff:
                    return KeyboardLightingHelper.RunWorker(false);

                case StartupMode.KeyboardLightingRestore:
                    return KeyboardLightingHelper.RunWorker(true);

                case StartupMode.EnableKeyboardLighting:
                    return EnableKeyboardLighting(args.Length > 1 ? args[1] : null);

                case StartupMode.DisableKeyboardLighting:
                    return DisableKeyboardLighting();

                case StartupMode.Recover:
                    return Recover();

                default:
                    return LockSession();
            }
        }

        private enum StartupMode
        {
            Lock,
            DisplayOff,
            KeyboardLightingOff,
            KeyboardLightingRestore,
            EnableKeyboardLighting,
            DisableKeyboardLighting,
            Recover,
            Help
        }

        private static StartupMode ParseArguments(string[] args)
        {
            foreach (string arg in args)
            {
                string flag = (arg ?? string.Empty).TrimStart('-', '/').ToLowerInvariant();
                switch (flag)
                {
                    case "display-off":
                    case "displayoff":
                    case "d":
                        return StartupMode.DisplayOff;
                    case "keyboard-lighting-off":
                        return StartupMode.KeyboardLightingOff;
                    case "keyboard-lighting-restore":
                        return StartupMode.KeyboardLightingRestore;
                    case "enable-keyboard-lighting":
                        return StartupMode.EnableKeyboardLighting;
                    case "disable-keyboard-lighting":
                        return StartupMode.DisableKeyboardLighting;
                    case "recover":
                        return StartupMode.Recover;
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
                    KeyboardLightingSessionFactory.Create(),
                    AfkRecovery.ForExecutable(Application.ExecutablePath));
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
                ShowError("AFK mode worked, but keyboard lighting could not be controlled.\r\n\r\n"
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

        /// <summary>
        /// Run by Windows at sign-in when an AFK session never reached its own
        /// cleanup. See <see cref="AfkRecovery"/>.
        /// </summary>
        private static int Recover()
        {
            var failures = new List<string>();

            try
            {
                new TemporaryPowerMode(new WindowsPowerConfiguration(), new WindowsExecutionStateController())
                    .Recover();
            }
            catch (Exception ex)
            {
                failures.Add(ex.Message);
            }

            try
            {
                if (KeyboardLightingHelper.IsInstalled)
                    new ElevatedKeyboardLightingSession().RestorePending();
            }
            catch (Exception ex)
            {
                failures.Add("Keyboard lighting: " + ex.Message);
            }

            if (failures.Count == 0) return 0;

            // Try again at the next sign-in rather than giving up.
            try
            {
                AfkRecovery recovery = AfkRecovery.ForExecutable(Application.ExecutablePath);
                if (recovery != null) recovery.Arm();
            }
            catch (Exception)
            {
            }

            ShowError("An AFK session was interrupted, and AFKLocker could not put everything back.\r\n\r\n"
                      + string.Join("\r\n", failures.ToArray()));
            return 1;
        }

        /// <summary>
        /// Run elevated by Setup when the user switches keyboard lighting on.
        /// Detects and installs; any failure removes everything it installed.
        /// The self-test is Setup's job, not this process's: it has to start the
        /// tasks as the standard user a real AFK session runs as.
        /// </summary>
        private static int EnableKeyboardLighting(string userSid)
        {
            var tasks = new SchtasksScheduledTasks();
            try
            {
                if (!KeyboardLightingHelper.IsAdministrator)
                    throw new UnauthorizedAccessException("Windows did not grant administrator approval.");
                if (string.IsNullOrEmpty(userSid))
                    throw new ArgumentException("Open AFKLocker Setup to switch keyboard lighting on.");

                KeyboardLightingDetection detection = new KeyboardLightingController(
                    KeyboardLightingHelper.SnapshotPath(KeyboardLightingHelper.Directory)).Detect();
                if (detection.Support == KeyboardLightingSupport.Unsupported)
                    throw new InvalidOperationException("This PC's keyboard lighting is not supported.");
                if (detection.Support == KeyboardLightingSupport.DetectionFailed)
                    throw new InvalidOperationException("Keyboard lighting could not be detected: " + detection.Detail);

                KeyboardLightingHelper.Install(AppDomain.CurrentDomain.BaseDirectory, userSid, tasks);
                return 0;
            }
            catch (Exception ex)
            {
                string cleanup = string.Empty;
                try
                {
                    if (KeyboardLightingHelper.IsAdministrator) KeyboardLightingHelper.Uninstall(tasks);
                }
                catch (Exception undo)
                {
                    cleanup = "\r\n\r\nIt could not be fully removed again: " + undo.Message;
                }

                ShowError("Keyboard lighting could not be turned on.\r\n\r\n" + ex.Message + cleanup);
                return 1;
            }
        }

        private static int DisableKeyboardLighting()
        {
            try
            {
                KeyboardLightingHelper.Uninstall(new SchtasksScheduledTasks());
                return 0;
            }
            catch (Exception ex)
            {
                ShowError("Keyboard lighting could not be turned off cleanly.\r\n\r\n" + ex.Message);
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
                "Use \"AFKLocker Setup\" to check power settings and keyboard lighting.",
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
