using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Windows.Forms;
using AFKLocker.Core;

namespace AFKLocker.Setup
{
    /// <summary>Administrator detection and elevated re-launch.</summary>
    internal static class ElevationHelper
    {
        public const string ElevatedFlag = "--elevated";
        public const string BatteryFlag = "--battery";

        public static bool IsElevated
        {
            get
            {
                try
                {
                    using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch
                {
                    return false;
                }
            }
        }

        public static bool RelaunchElevated(bool includeBattery)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    Arguments = ElevatedFlag + (includeBattery ? " " + BatteryFlag : string.Empty),
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(startInfo);
                return true;
            }
            catch (Exception)
            {
                // The user dismissing the UAC prompt lands here. Nothing to do.
                return false;
            }
        }
    }

    internal static class Program
    {
        private const string RestoreSilentFlag = "--restore-silent";
        private const string DisableAutoLockSilentFlag = "--disable-autolock-silent";

        [STAThread]
        private static int Main(string[] args)
        {
            args = args ?? new string[0];

            if (HasFlag(args, DisableAutoLockSilentFlag))
                return DisableAutoLockSilently();

            if (HasFlag(args, RestoreSilentFlag))
                return RestoreSilently();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                using (var form = new SetupForm(
                    new WindowsPowerConfiguration(),
                    new WindowsPowerInformation(),
                    new FileBackupStore(),
                    CreateAutoLockManager(),
                    HasFlag(args, ElevationHelper.BatteryFlag)))
                {
                    Application.Run(form);
                }
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("AFKLocker Setup could not start.\r\n\r\n" + ex.Message,
                    "AFKLocker Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        /// <summary>
        /// Used by the uninstaller: put the power settings back without showing
        /// any UI. Missing backups are not an error - it just means AFKLocker
        /// never changed anything.
        /// </summary>
        private static int RestoreSilently()
        {
            try
            {
                var configurator = new PowerConfigurator(new WindowsPowerConfiguration(), new FileBackupStore());
                configurator.RestoreAll();
                return 0;
            }
            catch (Exception)
            {
                // Never block an uninstall on this. The backup files stay on disk
                // so the values can still be restored by hand.
                return 1;
            }
        }

        /// <summary>
        /// Used by the uninstaller: stop the watcher and remove its autostart
        /// entry, with no UI. Unconditional - removing the program must not
        /// leave something of it starting at sign-in.
        /// </summary>
        private static int DisableAutoLockSilently()
        {
            try
            {
                CreateAutoLockManager().Cleanup();
                return 0;
            }
            catch (Exception)
            {
                // Never block an uninstall on this.
                return 1;
            }
        }

        private static AutoLockManager CreateAutoLockManager()
        {
            return new AutoLockManager(
                new FileSettingsStore(),
                new RunKeyAutostartRegistry(),
                new WatcherController(),
                WatcherController.DefaultWatcherPath);
        }

        private static bool HasFlag(string[] args, string flag)
        {
            return args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        }
    }
}
