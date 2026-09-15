using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace AFKLocker.Core
{
    /// <summary>
    /// Invokes two on-demand, elevated Task Scheduler entries. Acer's firmware
    /// WMI ACL normally requires administrator rights; configuring the tasks
    /// once avoids a UAC prompt every time AFK mode starts and ends.
    /// </summary>
    public sealed class ScheduledAcerKeyboardLightingSession : IKeyboardLightingSession
    {
        public const string OffTaskName = "AFKLocker Acer Keyboard Off";
        public const string RestoreTaskName = "AFKLocker Acer Keyboard Restore";

        private bool _entered;
        private bool _disposed;
        private bool _offPending;

        public void Enter()
        {
            if (_entered) return;
            DeleteResult(KeyboardLightingTaskWorker.OffResultPath);
            StartTask(OffTaskName);
            _offPending = true;
            _entered = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_entered) return;

            // Enter deliberately does not wait for Acer WMI: display-off must
            // never be delayed by optional keyboard firmware. Before restore,
            // wait for that first task so the two cannot race over the snapshot.
            Exception offError = null;
            if (_offPending)
            {
                try
                {
                    WaitForResult(OffTaskName, KeyboardLightingTaskWorker.OffResultPath);
                }
                catch (Exception ex)
                {
                    offError = ex;
                }
                _offPending = false;
            }

            // Restore even when "off" reported an error. The worker writes the
            // snapshot before changing firmware, so a partial failure can still
            // have left the keyboard dark.
            DeleteResult(KeyboardLightingTaskWorker.RestoreResultPath);
            StartTask(RestoreTaskName);
            WaitForResult(RestoreTaskName, KeyboardLightingTaskWorker.RestoreResultPath);
            _entered = false;

            if (offError != null) throw offError;
        }

        public static bool IsInstalled
        {
            get { return TaskExists(OffTaskName) && TaskExists(RestoreTaskName); }
        }

        private static bool TaskExists(string name)
        {
            try
            {
                using (Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = "/Query /TN \"" + name + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }))
                {
                    process.WaitForExit(5000);
                    return process.HasExited && process.ExitCode == 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void DeleteResult(string resultPath)
        {
            if (File.Exists(resultPath)) File.Delete(resultPath);
        }

        private static void StartTask(string name)
        {
            using (Process process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/Run /TN \"" + name + "\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                process.WaitForExit(5000);
                if (!process.HasExited || process.ExitCode != 0)
                    throw new InvalidOperationException("Windows could not start the scheduled task " + name + ".");
            }
        }

        private static void WaitForResult(string name, string resultPath)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(resultPath))
                {
                    string result = File.ReadAllText(resultPath, Encoding.UTF8).Trim();
                    File.Delete(resultPath);
                    if (string.Equals(result, "ok", StringComparison.Ordinal)) return;
                    throw new InvalidOperationException(result);
                }
                Thread.Sleep(100);
            }

            throw new TimeoutException("The scheduled task " + name + " did not report completion.");
        }
    }

    public static class KeyboardLightingTaskWorker
    {
        public static readonly string OffResultPath = Path.Combine(
            FileBackupStore.DefaultDirectory, "keyboard-off.result");
        public static readonly string RestoreResultPath = Path.Combine(
            FileBackupStore.DefaultDirectory, "keyboard-restore.result");

        public static int Run(bool restore)
        {
            string resultPath = restore ? RestoreResultPath : OffResultPath;
            try
            {
                var lighting = new AcerKeyboardLightingSession();
                if (restore) lighting.Dispose();
                else lighting.Enter();
                AtomicFile.WriteAllText(resultPath, "ok");
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    AtomicFile.WriteAllText(resultPath, "Acer keyboard control failed: " + ex.Message);
                }
                catch (Exception)
                {
                }
                return 1;
            }
        }
    }

    public static class KeyboardLightingTaskInstaller
    {
        public static bool IsAdministrator
        {
            get
            {
                try
                {
                    using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                        return new WindowsPrincipal(identity)
                            .IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public static void Install(string executablePath)
        {
            if (!IsAdministrator)
                throw new UnauthorizedAccessException("Administrator rights are required once to configure Acer keyboard control.");
            if (string.IsNullOrEmpty(executablePath) || !File.Exists(executablePath))
                throw new FileNotFoundException("AFKLocker.exe was not found.", executablePath);

            string exe = PsQuote(Path.GetFullPath(executablePath));
            string script =
                "$ErrorActionPreference='Stop';" +
                "$user=[Security.Principal.WindowsIdentity]::GetCurrent().Name;" +
                "$principal=New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest;" +
                "$settings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries " +
                    "-ExecutionTimeLimit (New-TimeSpan -Minutes 1);" +
                "$off=New-ScheduledTaskAction -Execute '" + exe + "' -Argument '--keyboard-off';" +
                "$restore=New-ScheduledTaskAction -Execute '" + exe + "' -Argument '--keyboard-restore';" +
                "Register-ScheduledTask -TaskName '" +
                    PsQuote(ScheduledAcerKeyboardLightingSession.OffTaskName) +
                    "' -Action $off -Principal $principal -Settings $settings -Force | Out-Null;" +
                "Register-ScheduledTask -TaskName '" +
                    PsQuote(ScheduledAcerKeyboardLightingSession.RestoreTaskName) +
                    "' -Action $restore -Principal $principal -Settings $settings -Force | Out-Null;";

            RunPowerShell(script);
        }

        public static void Uninstall()
        {
            if (!IsAdministrator)
                throw new UnauthorizedAccessException("Administrator rights are required to remove the tasks.");

            string script =
                "$ErrorActionPreference='Stop';" +
                "Unregister-ScheduledTask -TaskName '" +
                    PsQuote(ScheduledAcerKeyboardLightingSession.OffTaskName) +
                    "' -Confirm:$false -ErrorAction SilentlyContinue;" +
                "Unregister-ScheduledTask -TaskName '" +
                    PsQuote(ScheduledAcerKeyboardLightingSession.RestoreTaskName) +
                    "' -Confirm:$false -ErrorAction SilentlyContinue;";
            RunPowerShell(script);
        }

        private static string PsQuote(string value)
        {
            return value.Replace("'", "''");
        }

        private static void RunPowerShell(string script)
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                @"System32\WindowsPowerShell\v1.0\powershell.exe");

            using (Process process = Process.Start(new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("Windows could not configure the Acer keyboard tasks.");
            }
        }
    }

    public static class KeyboardLightingSessionFactory
    {
        public static IKeyboardLightingSession Create()
        {
            return ScheduledAcerKeyboardLightingSession.IsInstalled
                ? (IKeyboardLightingSession)new ScheduledAcerKeyboardLightingSession()
                : new NoKeyboardLightingSession();
        }
    }
}
