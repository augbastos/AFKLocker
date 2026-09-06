using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using AFKLocker.Core;

namespace AFKLocker.Watcher
{
    /// <summary>
    /// The optional background half of AFKLocker: waits for the laptop lid to
    /// close and locks the session when it does.
    ///
    /// It runs only when Automatic mode is switched on. It has no window, no
    /// tray icon and no console. It does not poll - the process is blocked in
    /// the message loop until Windows posts a lid event - and it makes no
    /// network requests of any kind.
    ///
    /// What it deliberately does NOT do: change power settings, hold execution
    /// state, or otherwise keep the machine awake. Staying awake with the lid
    /// closed is the job of the power configuration applied by AFKLocker Setup.
    /// This process only locks.
    /// </summary>
    internal static class Program
    {
        private const int ATTACH_PARENT_PROCESS = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessWorkingSetSize(IntPtr process,
            IntPtr minimumWorkingSetSize, IntPtr maximumWorkingSetSize);

        /// <summary>
        /// Hands the startup working set back to Windows once everything is
        /// registered. This process then sits idle in a message loop for hours,
        /// so holding on to the pages the CLR touched while starting is pure
        /// waste; Windows pages them back in if they are ever needed again.
        ///
        /// Passing -1 for both sizes is the documented way to ask for a trim.
        /// </summary>
        private static void TrimWorkingSet()
        {
            SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }

        [STAThread]
        private static int Main(string[] args)
        {
            args = args ?? new string[0];

            if (HasFlag(args, "status"))
                return ReportStatus();

            if (HasFlag(args, "stop"))
                return StopRunningWatcher();

            if (HasFlag(args, "help", "h", "?"))
                return ShowHelp();

            return RunWatcher();
        }

        private static int RunWatcher()
        {
            bool createdNew;
            // One watcher per session. A second launch - from autostart plus a
            // manual start, say - exits quietly rather than double-locking.
            using (var instanceLock = new Mutex(true, WatcherController.RunningMutexName, out createdNew))
            {
                if (!createdNew)
                    return 0;

                using (var stopSignal = new EventWaitHandle(false, EventResetMode.ManualReset,
                    WatcherController.StopEventName))
                using (var sessionState = new SessionStateTracker())
                using (var lidWindow = new LidNotificationWindow())
                {
                    var policy = new AutoLockPolicy(new WindowsSessionLocker(), sessionState);

                    sessionState.Start();

                    if (!lidWindow.Start())
                        return 2;   // Windows refused the power notification registration

                    lidWindow.LidStateChanged += (s, e) => policy.Handle(e.State);

                    // After resume the lid may have moved while the machine was
                    // asleep, so the next event is treated as a fresh starting
                    // position instead of a transition.
                    lidWindow.Resumed += (s, e) => policy.Reset();

                    lidWindow.CloseRequested += (s, e) => Application.ExitThread();

                    // Another process asking us to stop arrives on a pool
                    // thread; bounce it onto the message loop's thread.
                    RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(
                        stopSignal, (state, timedOut) => lidWindow.RequestClose(), null,
                        Timeout.Infinite, true);

                    SessionEndingEventHandler endingHandler = (s, e) => lidWindow.RequestClose();
                    SystemEvents.SessionEnding += endingHandler;

                    try
                    {
                        TrimWorkingSet();
                        Application.Run();
                    }
                    finally
                    {
                        SystemEvents.SessionEnding -= endingHandler;
                        registration.Unregister(null);
                        instanceLock.ReleaseMutex();
                    }
                }
            }

            return 0;
        }

        /// <summary>
        /// Diagnostics. Writes to the console of whoever launched it, if there
        /// is one - the watcher is a windowless program, so it has none of its
        /// own and must never create one.
        /// </summary>
        private static int ReportStatus()
        {
            bool running = WatcherController.IsAnyRunning;
            var autostart = new RunKeyAutostartRegistry();
            LockMode mode = new FileSettingsStore().Load().Mode;

            WriteLine("AFKLocker watcher");
            WriteLine("  lock mode : " + (mode == LockMode.Automatic ? "automatic" : "manual"));
            WriteLine("  running   : " + (running ? "yes" : "no"));
            WriteLine("  autostart : " + (autostart.IsRegistered ? autostart.RegisteredCommand : "not registered"));

            return running ? 0 : 1;
        }

        private static int StopRunningWatcher()
        {
            bool stopped = new WatcherController().Stop(TimeSpan.FromSeconds(5));
            WriteLine(stopped ? "AFKLocker watcher stopped." : "AFKLocker watcher did not stop.");
            return stopped ? 0 : 1;
        }

        private static int ShowHelp()
        {
            WriteLine("AFKLockerWatcher - locks the session when the laptop lid closes.");
            WriteLine("");
            WriteLine("  (no arguments)  Run the watcher.");
            WriteLine("  --status        Report lock mode, whether a watcher is running, and autostart.");
            WriteLine("  --stop          Ask a running watcher to exit.");
            WriteLine("");
            WriteLine("Turn automatic locking on or off in AFKLocker Setup, which also manages autostart.");
            return 0;
        }

        private static void WriteLine(string text)
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            Console.WriteLine(text);
        }

        private static bool HasFlag(string[] args, params string[] names)
        {
            return args.Any(arg =>
            {
                string flag = (arg ?? string.Empty).TrimStart('-', '/').ToLowerInvariant();
                return names.Any(name => string.Equals(flag, name, StringComparison.OrdinalIgnoreCase));
            });
        }
    }
}
