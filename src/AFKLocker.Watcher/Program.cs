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
    ///
    /// Startup is a handshake, not a launch. The readiness event is set only
    /// after every step needed to actually lock has succeeded, so whoever
    /// started this process learns the truth instead of assuming it.
    /// </summary>
    internal static class Program
    {
        private const int ATTACH_PARENT_PROCESS = -1;

        private const int ExitOk = 0;
        private const int ExitLidNotificationFailed = WatcherController.ExitCodeLidNotificationFailed;
        private const int ExitAlreadyRunning = WatcherController.ExitCodeAlreadyRunning;

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
            // One watcher per session. A second launch - autostart plus a manual
            // start, say - exits with a distinct code so the caller can tell
            // "already covered" from "failed".
            using (var instanceLock = new Mutex(true, WatcherController.RunningMutexName, out createdNew))
            {
                if (!createdNew)
                    return ExitAlreadyRunning;

                bool readyCreated;
                using (var readySignal = new EventWaitHandle(false, EventResetMode.ManualReset,
                    WatcherController.ReadyEventName, out readyCreated))
                using (var stopSignal = new EventWaitHandle(false, EventResetMode.ManualReset,
                    WatcherController.StopEventName))
                using (var sessionState = new SessionStateTracker())
                using (var lidWindow = new LidNotificationWindow())
                {
                    // Both events can outlive a process that crashed while a
                    // handle was open elsewhere. Start from a known state rather
                    // than inheriting whatever the last run left behind.
                    readySignal.Reset();
                    stopSignal.Reset();

                    var policy = new AutoLockPolicy(new WindowsSessionLocker(), sessionState);

                    sessionState.Start();

                    // The one step that can genuinely fail. Without it the
                    // process would sit there looking healthy and never lock.
                    if (!lidWindow.Start())
                        return ExitLidNotificationFailed;

                    var displays = new WindowsDisplayController();
                    var inputMonitor = new WindowsUserInputMonitor();
                    DisplayBlanker blanker = null;

                    lidWindow.LidStateChanged += delegate(object s, LidStateEventArgs e)
                    {
                        if (policy.Handle(e.State) != AutoLockDecision.Locked) return;

                        // Turning the panel dark is the lid's job on a single
                        // screen. With an external monitor attached it is not:
                        // closing the lid makes Windows reconfigure the
                        // displays, and that reconfiguration wakes the external
                        // one back up - after the lock - leaving the lock screen
                        // lit on a desk the user has walked away from.
                        //
                        // One request loses that race, so this holds the screens
                        // dark for a short while instead. It stops on its own,
                        // and stops immediately if anyone comes back.
                        if (blanker != null) blanker.Dispose();

                        blanker = new DisplayBlanker(displays, inputMonitor);
                        blanker.Finished += delegate { blanker = null; };
                        blanker.Start();
                    };

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

                        // Everything above succeeded: the slot is held, session
                        // tracking is live, the window exists, Windows accepted
                        // the lid registration and the handlers are attached.
                        // Only now is this watcher able to do its job.
                        readySignal.Set();

                        Application.Run();
                    }
                    finally
                    {
                        // Stop claiming readiness the moment we start going away,
                        // so nothing sees a ready signal from a dying process.
                        readySignal.Reset();

                        SystemEvents.SessionEnding -= endingHandler;
                        registration.Unregister(null);
                        instanceLock.ReleaseMutex();
                    }
                }
            }

            return ExitOk;
        }

        /// <summary>
        /// Diagnostics. Writes to the console of whoever launched it, if there
        /// is one - the watcher is a windowless program, so it has none of its
        /// own and must never create one.
        /// </summary>
        private static int ReportStatus()
        {
            WatcherState state = new WatcherController().GetState();
            var autostart = new RunKeyAutostartRegistry();
            LockMode mode = new FileSettingsStore().Load().Mode;

            WriteLine("AFKLocker watcher");
            WriteLine("  lock mode : " + (mode == LockMode.Automatic ? "automatic" : "manual"));
            WriteLine("  state     : " + DescribeState(state));
            WriteLine("  autostart : " + (autostart.IsRegistered ? autostart.RegisteredCommand : "not registered"));

            return state == WatcherState.Ready ? 0 : 1;
        }

        private static string DescribeState(WatcherState state)
        {
            switch (state)
            {
                case WatcherState.Ready:
                    return "ready (registered for lid events)";
                case WatcherState.Starting:
                    return "starting (running, not ready yet)";
                case WatcherState.Unhealthy:
                    return "unhealthy (a readiness signal outlived its process)";
                default:
                    return "not running";
            }
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
            WriteLine("  --status        Report lock mode, watcher state, and autostart.");
            WriteLine("  --stop          Ask a running watcher to exit.");
            WriteLine("");
            WriteLine("Exit codes when running: 0 exited normally, 2 could not register for lid");
            WriteLine("events, 3 another watcher already holds this session.");
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
