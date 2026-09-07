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
        private const string DiagnosticsFlag = "--diagnostics";

        /// <summary>
        /// Exit codes the uninstaller reads. Three values rather than two,
        /// because "nothing was cleaned up" and "most of it was" call for
        /// different words to the person removing the program.
        /// </summary>
        public const int ExitOk = 0;

        /// <summary>Something could not be done, and it matters.</summary>
        public const int ExitFailed = 1;

        /// <summary>It mostly worked; something minor was left behind.</summary>
        public const int ExitPartial = 2;

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

            // Opens diagnostics straight away, without the main window. Useful
            // when someone is being talked through a problem, and it makes the
            // window reachable from a script.
            if (HasFlag(args, DiagnosticsFlag))
                return RunDiagnosticsOnly();

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
                RestoreResult result = configurator.RestoreAll();

                // A setting that could not be put back is the case the caller
                // most needs to hear about, and it does not throw - it comes
                // back as a skipped entry. Returning 0 for it told the
                // uninstaller everything was fine while the machine was still
                // configured for closed-lid operation.
                return result.Skipped.Count > 0 ? ExitPartial : ExitOk;
            }
            catch (Exception)
            {
                // Never block an uninstall on this. The backup files stay on disk
                // so the values can still be restored by hand.
                return ExitFailed;
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
                AutoLockResult result = CreateAutoLockManager().Cleanup();
                if (result.Success) return ExitOk;

                // The distinction matters to the uninstaller. A sign-in entry
                // that survived will keep trying to start a program that is
                // about to be deleted; a helper that would not stop is untidy
                // but goes away at the next sign-out.
                return result.Failure == AutoLockFailure.AutostartFailed
                    ? ExitFailed
                    : ExitPartial;
            }
            catch (Exception)
            {
                // Never block an uninstall on this.
                return ExitFailed;
            }
        }

        private static int RunDiagnosticsOnly()
        {
            try
            {
                using (var form = new DiagnosticsForm(SetupForm.RunSelfTestOn, SetupForm.CreateLidTestOn))
                {
                    Application.Run(form);
                }
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Diagnostics could not start.\r\n\r\n" + ex.Message,
                    "AFKLocker Diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
