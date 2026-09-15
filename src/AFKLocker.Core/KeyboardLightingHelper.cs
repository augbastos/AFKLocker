using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace AFKLocker.Core
{
    /// <summary>The Task Scheduler operations the keyboard lighting helper uses.</summary>
    public interface IScheduledTasks
    {
        bool Exists(string name);
        void Run(string name);
        void Create(string name, string xmlPath);
        void Delete(string name);
    }

    public sealed class SchtasksScheduledTasks : IScheduledTasks
    {
        private static readonly string Schtasks = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");

        public bool Exists(string name)
        {
            string error;
            return Execute("/Query /TN " + Quote(name), out error) == 0;
        }

        public void Run(string name)
        {
            Require("/Run /TN " + Quote(name), "Windows could not start keyboard lighting.");
        }

        public void Create(string name, string xmlPath)
        {
            Require("/Create /TN " + Quote(name) + " /XML " + Quote(xmlPath) + " /F",
                "Windows could not register the keyboard lighting task.");
        }

        public void Delete(string name)
        {
            Require("/Delete /TN " + Quote(name) + " /F", "Windows could not remove the keyboard lighting task.");
        }

        private static void Require(string arguments, string failure)
        {
            string error;
            if (Execute(arguments, out error) != 0)
                throw new InvalidOperationException(string.IsNullOrEmpty(error) ? failure : failure + " " + error);
        }

        private static int Execute(string arguments, out string error)
        {
            using (Process process = Process.Start(new ProcessStartInfo
            {
                FileName = Schtasks,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            }))
            {
                error = process.StandardError.ReadToEnd().Trim();
                if (!process.WaitForExit(10000))
                {
                    error = "Task Scheduler did not answer.";
                    return -1;
                }
                return process.ExitCode;
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value + "\"";
        }
    }

    /// <summary>
    /// Runs keyboard lighting changes in a small elevated helper.
    ///
    /// Why elevation at all: the only keyboard lighting interface AFKLocker can
    /// drive today is vendor firmware behind WMI, and Windows lets only
    /// administrators call it. A UAC prompt at every AFK session would arrive
    /// exactly as the user walks away, so switching the feature on asks once and
    /// installs two things:
    ///
    ///  - a copy of AFKLocker.exe and AFKLocker.Core.dll in Program Files, and
    ///  - two on-demand scheduled tasks that run that copy elevated with one fixed
    ///    argument each: turn lighting off, or restore it. They have no triggers.
    ///
    /// The tasks never point at the per-user install folder. That folder is
    /// writable without elevation, so anything running as the user could replace
    /// the executable and have Task Scheduler run it elevated. The helper keeps
    /// its snapshot and results next to itself for the same reason: an elevated
    /// process never writes to a path a standard user controls.
    /// </summary>
    public static class KeyboardLightingHelper
    {
        public const string OffTaskName = "AFKLocker Keyboard Lighting Off";
        public const string RestoreTaskName = "AFKLocker Keyboard Lighting Restore";
        public const string OffArgument = "--keyboard-lighting-off";
        public const string RestoreArgument = "--keyboard-lighting-restore";

        private const string WorkerExe = "AFKLocker.exe";
        private const string WorkerLibrary = "AFKLocker.Core.dll";
        private const string SnapshotFile = "keyboard-lighting.txt";

        public static readonly string Directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AFKLocker Keyboard Lighting");

        /// <summary>
        /// Whether the feature is switched on. A file check only, because this
        /// runs before every AFK session, ahead of display-off.
        /// </summary>
        public static bool IsInstalled
        {
            get { return File.Exists(Path.Combine(Directory, WorkerExe)); }
        }

        public static bool TasksRegistered(IScheduledTasks tasks)
        {
            return tasks.Exists(OffTaskName) && tasks.Exists(RestoreTaskName);
        }

        public static string SnapshotPath(string directory)
        {
            return Path.Combine(directory, SnapshotFile);
        }

        internal static string ResultPath(string directory, bool restore)
        {
            return Path.Combine(directory, restore ? "restore.result" : "off.result");
        }

        public static bool IsAdministrator
        {
            get
            {
                try
                {
                    using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Installs the helper for <paramref name="userSid"/>: the account that asked,
        /// which is not necessarily the one that approved the UAC prompt.
        /// </summary>
        public static void Install(string sourceDirectory, string userSid, IScheduledTasks tasks)
        {
            if (!IsAdministrator)
                throw new UnauthorizedAccessException("Administrator approval is needed to set up keyboard lighting.");
            string sid = new SecurityIdentifier(userSid).Value;

            System.IO.Directory.CreateDirectory(Directory);
            foreach (string file in new[] { WorkerExe, WorkerLibrary })
                File.Copy(Path.Combine(sourceDirectory, file), Path.Combine(Directory, file), true);

            string worker = Path.Combine(Directory, WorkerExe);
            CreateTask(tasks, OffTaskName, worker, OffArgument, sid,
                "Turns keyboard lighting off when an AFKLocker session starts. Runs only when AFKLocker starts it.");
            CreateTask(tasks, RestoreTaskName, worker, RestoreArgument, sid,
                "Restores keyboard lighting when an AFKLocker session ends. Runs only when AFKLocker starts it.");
        }

        /// <summary>
        /// Restores any lighting still dark, then removes the tasks and the helper.
        /// If the lighting cannot be restored its snapshot is kept, so switching
        /// the feature on again restores it first.
        /// </summary>
        public static void Uninstall(IScheduledTasks tasks)
        {
            if (!IsAdministrator)
                throw new UnauthorizedAccessException("Administrator approval is needed to remove keyboard lighting.");

            var failures = new List<string>();
            bool restored = true;
            try
            {
                new KeyboardLightingController(SnapshotPath(Directory)).Restore();
            }
            catch (Exception ex)
            {
                restored = false;
                failures.Add("restore the lighting: " + ex.Message);
            }

            foreach (string name in new[] { OffTaskName, RestoreTaskName })
            {
                try
                {
                    if (tasks.Exists(name)) tasks.Delete(name);
                }
                catch (Exception ex)
                {
                    failures.Add(ex.Message);
                }
            }

            try
            {
                if (restored)
                {
                    if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
                }
                else
                {
                    foreach (string file in new[] { WorkerExe, WorkerLibrary })
                        File.Delete(Path.Combine(Directory, file));
                }
            }
            catch (Exception ex)
            {
                failures.Add("remove " + Directory + ": " + ex.Message);
            }

            if (failures.Count > 0)
                throw new InvalidOperationException("Keyboard lighting was not removed cleanly: "
                                                    + string.Join("; ", failures.ToArray()));
        }

        private static void CreateTask(IScheduledTasks tasks, string name, string command, string argument,
            string sid, string description)
        {
            string xmlPath = Path.Combine(Directory, "task.xml");
            File.WriteAllText(xmlPath, TaskXml(command, argument, sid, description), Encoding.Unicode);
            try
            {
                tasks.Create(name, xmlPath);
            }
            finally
            {
                File.Delete(xmlPath);
            }
        }

        /// <summary>
        /// No triggers, only the current user's interactive session, and no
        /// battery restriction - the Task Scheduler default refuses to start on
        /// battery, which is exactly when a laptop is left AFK. Queue rather than
        /// ignore a second start, so a quick AFK-return-AFK never drops a restore.
        /// </summary>
        internal static string TaskXml(string command, string argument, string sid, string description)
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n"
                + "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
                + "<RegistrationInfo><Description>" + SecurityElement.Escape(description) + "</Description></RegistrationInfo>"
                + "<Principals><Principal id=\"Author\"><UserId>" + SecurityElement.Escape(sid) + "</UserId>"
                + "<LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>"
                + "<Settings><MultipleInstancesPolicy>Queue</MultipleInstancesPolicy>"
                + "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>"
                + "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>"
                + "<AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>false</StartWhenAvailable>"
                + "<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden>"
                + "<RunOnlyIfIdle>false</RunOnlyIfIdle><WakeToRun>false</WakeToRun>"
                + "<ExecutionTimeLimit>PT1M</ExecutionTimeLimit><Priority>4</Priority></Settings>"
                + "<Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(command) + "</Command>"
                + "<Arguments>" + SecurityElement.Escape(argument) + "</Arguments></Exec></Actions></Task>";
        }

        /// <summary>The elevated side: what the scheduled tasks run.</summary>
        public static int RunWorker(bool restore)
        {
            return RunWorker(restore, Directory, new KeyboardLightingController(SnapshotPath(Directory)));
        }

        public static int RunWorker(bool restore, string directory, KeyboardLightingController controller)
        {
            // Serialises off and restore, which are separate tasks. Best effort:
            // a gate that cannot be taken only costs ordering, never the result.
            Mutex gate = null;
            bool owned = false;
            string message = null;
            try
            {
                try
                {
                    gate = new Mutex(false, @"Local\AFKLocker.KeyboardLighting");
                    owned = gate.WaitOne(TimeSpan.FromSeconds(30));
                }
                catch (AbandonedMutexException)
                {
                    owned = true;
                }
                catch (Exception)
                {
                }

                try
                {
                    if (restore) controller.Restore();
                    else controller.TurnOff();
                }
                catch (Exception ex)
                {
                    message = ex.Message.Replace("\r", " ").Replace("\n", " ");
                }

                string path = ResultPath(directory, restore);
                WorkerResult previous = WorkerResult.Read(path);
                long sequence = previous == null ? 1 : previous.Sequence + 1;
                AtomicFile.WriteAllText(path, new WorkerResult(sequence, message).Serialize());
            }
            catch (Exception)
            {
                return 2;
            }
            finally
            {
                if (gate != null)
                {
                    if (owned) gate.ReleaseMutex();
                    gate.Dispose();
                }
            }

            return message == null ? 0 : 1;
        }
    }

    /// <summary>
    /// What the helper reports back. The unelevated side cannot delete these
    /// files, so each result carries a sequence number instead: a caller notes
    /// the number before starting a task and waits for a higher one.
    /// </summary>
    internal sealed class WorkerResult
    {
        public WorkerResult(long sequence, string failure)
        {
            Sequence = sequence;
            Failure = failure;
        }

        public long Sequence { get; private set; }

        /// <summary>Null on success.</summary>
        public string Failure { get; private set; }

        public string Serialize()
        {
            return "sequence=" + Sequence.ToString(CultureInfo.InvariantCulture) + "\n"
                   + (Failure == null ? "status=ok\n" : "status=failed\nmessage=" + Failure + "\n");
        }

        /// <summary>Null when there is no complete result to read yet.</summary>
        public static WorkerResult Read(string path)
        {
            string text;
            try
            {
                if (!File.Exists(path)) return null;
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (IOException)
            {
                return null;
            }

            long sequence = -1;
            string status = null;
            string message = null;
            foreach (string line in text.Split('\n'))
            {
                if (line.StartsWith("sequence=", StringComparison.Ordinal))
                    long.TryParse(line.Substring(9), NumberStyles.None, CultureInfo.InvariantCulture, out sequence);
                else if (line.StartsWith("status=", StringComparison.Ordinal))
                    status = line.Substring(7);
                else if (line.StartsWith("message=", StringComparison.Ordinal))
                    message = line.Substring(8);
            }

            if (sequence < 0 || (status != "ok" && status != "failed")) return null;
            return new WorkerResult(sequence, status == "ok" ? null : (message ?? "Keyboard lighting failed."));
        }
    }

    /// <summary>
    /// The unelevated side of one AFK session: starts the helper's tasks and
    /// waits for their results.
    /// </summary>
    public sealed class ElevatedKeyboardLightingSession : IKeyboardLightingSession
    {
        private readonly IScheduledTasks _tasks;
        private readonly string _directory;
        private readonly TimeSpan _timeout;
        private long _offBaseline;
        private bool _entered;
        private bool _disposed;

        public ElevatedKeyboardLightingSession()
            : this(new SchtasksScheduledTasks(), KeyboardLightingHelper.Directory, TimeSpan.FromSeconds(15))
        {
        }

        public ElevatedKeyboardLightingSession(IScheduledTasks tasks, string directory, TimeSpan timeout)
        {
            if (tasks == null) throw new ArgumentNullException("tasks");
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("directory must not be empty", "directory");
            _tasks = tasks;
            _directory = directory;
            _timeout = timeout;
        }

        /// <summary>Starts "off" and returns at once: display-off never waits on keyboard firmware.</summary>
        public void Enter()
        {
            if (_entered || _disposed) return;
            _offBaseline = Sequence(false);
            _tasks.Run(KeyboardLightingHelper.OffTaskName);
            _entered = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_entered) return;

            // Off must have finished first. A restore that ran before it would
            // find nothing to restore, and off would then leave the keyboard dark.
            Exception offError = null;
            try
            {
                WaitForResult(false, _offBaseline);
            }
            catch (Exception ex)
            {
                offError = ex;
            }

            // Restore even when off failed: it may have changed something before failing.
            RunAndWait(true);
            if (offError != null) throw offError;
        }

        public bool HasPendingRestore
        {
            get { return File.Exists(KeyboardLightingHelper.SnapshotPath(_directory)); }
        }

        /// <summary>Restores lighting left dark by a session that never finished.</summary>
        public void RestorePending()
        {
            if (HasPendingRestore) RunAndWait(true);
        }

        private void RunAndWait(bool restore)
        {
            long baseline = Sequence(restore);
            _tasks.Run(restore ? KeyboardLightingHelper.RestoreTaskName : KeyboardLightingHelper.OffTaskName);
            WaitForResult(restore, baseline);
        }

        private long Sequence(bool restore)
        {
            WorkerResult result = WorkerResult.Read(KeyboardLightingHelper.ResultPath(_directory, restore));
            return result == null ? 0 : result.Sequence;
        }

        private void WaitForResult(bool restore, long baseline)
        {
            string path = KeyboardLightingHelper.ResultPath(_directory, restore);
            DateTime deadline = DateTime.UtcNow + _timeout;
            while (true)
            {
                WorkerResult result = WorkerResult.Read(path);
                if (result != null && result.Sequence > baseline)
                {
                    if (result.Failure == null) return;
                    throw new InvalidOperationException(result.Failure);
                }

                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("Keyboard lighting did not respond in time.");
                Thread.Sleep(100);
            }
        }
    }

    public static class KeyboardLightingSessionFactory
    {
        public static IKeyboardLightingSession Create()
        {
            return KeyboardLightingHelper.IsInstalled
                ? (IKeyboardLightingSession)new ElevatedKeyboardLightingSession()
                : new NoKeyboardLightingSession();
        }
    }
}
