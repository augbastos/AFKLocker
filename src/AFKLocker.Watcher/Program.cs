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
    /// The optional background half of AFKLocker.
    ///
    /// It started life as a lid watcher and is now a helper with two jobs, each
    /// switched on separately: lock when the lid closes (Automatic mode), and
    /// lock when a registered key combination is pressed (the global hotkey).
    /// The file name has not changed, because renaming it would rewrite every
    /// user's sign-in entry to buy nothing.
    ///
    /// What decides whether it runs at all is the set of features switched on,
    /// not the lock mode. With neither on it exits immediately rather than
    /// sitting there: manual locking with no hotkey stays exactly as
    /// non-resident as it has always been.
    ///
    /// It has no window, no tray icon and no console. It does not poll - the
    /// process is blocked in the message loop until Windows posts an event - and
    /// it makes no network requests of any kind.
    ///
    /// What it deliberately does NOT do: change power settings, hold execution
    /// state, or otherwise keep the machine awake. Staying awake with the lid
    /// closed is the job of the power configuration applied by AFKLocker Setup.
    /// This process only locks.
    ///
    /// It also does not watch the keyboard. The hotkey is a reservation made
    /// with RegisterHotKey: Windows is told one combination and posts one
    /// message when exactly that is pressed. No other keystroke reaches here.
    ///
    /// Startup is a handshake, not a launch. The readiness event is set only
    /// after every feature that was asked for is genuinely working, so whoever
    /// started this process learns the truth instead of assuming it.
    /// </summary>
    internal static class Program
    {
        private const int ATTACH_PARENT_PROCESS = -1;

        private const int ExitOk = 0;
        private const int ExitLidNotificationFailed = WatcherController.ExitCodeLidNotificationFailed;
        private const int ExitAlreadyRunning = WatcherController.ExitCodeAlreadyRunning;
        private const int ExitHotkeyFailed = WatcherController.ExitCodeHotkeyRegistrationFailed;
        private const int ExitNothingToDo = WatcherController.ExitCodeNothingToDo;
        private const int ExitSettingsUnreadable = WatcherController.ExitCodeSettingsUnreadable;

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

            return RunHelper();
        }

        private static int RunHelper()
        {
            AutoLockSettings settings;
            try
            {
                settings = new FileSettingsStore().Load();
            }
            catch (Exception)
            {
                // Settings that exist but cannot be read no longer pretend to be
                // the defaults, because that let callers overwrite them. Here it
                // means this process cannot know what to register, and a helper
                // that registers nothing while claiming to be ready is the
                // failure this handshake exists to prevent. Exiting says so.
                return ExitSettingsUnreadable;
            }

            HelperFeatures features = settings.RequiredFeatures;

            // Nothing is switched on. Exiting with a distinct code is the honest
            // answer: a process that stayed alive doing nothing would make
            // "helper running" stop meaning anything.
            if (features == HelperFeatures.None)
                return ExitNothingToDo;

            bool wantsLid = (features & HelperFeatures.LidLock) != 0;
            bool wantsHotkey = (features & HelperFeatures.GlobalHotkey) != 0;

            bool createdNew;
            // One helper per session. A second launch - autostart plus a manual
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
                using (var helperWindow = new LidNotificationWindow())
                {
                    // Both events can outlive a process that crashed while a
                    // handle was open elsewhere. Start from a known state rather
                    // than inheriting whatever the last run left behind.
                    readySignal.Reset();
                    stopSignal.Reset();

                    var locker = new WindowsSessionLocker();
                    var displays = new WindowsDisplayController();
                    var inputMonitor = new WindowsUserInputMonitor();
                    var policy = new AutoLockPolicy(locker, sessionState);

                    sessionState.Start();

                    var screens = new ScreenHolder(displays, inputMonitor);

                    // The window must exist before anything is registered on it.
                    if (!helperWindow.Create())
                        return ExitLidNotificationFailed;

                    // --- lid, when Automatic is on -------------------------------
                    if (wantsLid)
                    {
                        // The one lid step that can genuinely fail. Without it the
                        // process would sit there looking healthy and never lock.
                        if (!helperWindow.StartLidNotifications())
                            return ExitLidNotificationFailed;

                        helperWindow.LidStateChanged += delegate(object s, LidStateEventArgs e)
                        {
                            if (policy.Handle(e.State) != AutoLockDecision.Locked) return;

                            // Turning the panel dark is the lid's job on a single
                            // screen. With an external monitor attached it is not:
                            // closing the lid makes Windows reconfigure the
                            // displays, and that reconfiguration wakes the external
                            // one back up - after the lock - leaving the lock screen
                            // lit on a desk the user has walked away from.
                            //
                            // The policy already locked, so only the screens are
                            // left to deal with here.
                            screens.Darken();
                        };

                        // After resume the lid may have moved while the machine was
                        // asleep, so the next event is treated as a fresh starting
                        // position instead of a transition.
                        helperWindow.Resumed += delegate { policy.Reset(); };
                    }

                    // --- hotkey, when it is switched on --------------------------
                    var hotkeys = new WindowsHotkeyRegistrar(helperWindow.Handle);
                    try
                    {
                        if (wantsHotkey)
                        {
                            HotkeyRegistrationResult registration = hotkeys.Register(settings.Hotkey);
                            if (!registration.Success)
                                return ExitHotkeyFailed;

                            // Exactly what double-clicking the icon does, through
                            // exactly the same code. A second implementation of
                            // "lock and darken" is how the two drift apart.
                            helperWindow.HotkeyPressed += delegate
                            {
                                screens.Adopt(LockAction.LockAndDarken(locker, displays, inputMonitor));
                            };
                        }

                        helperWindow.CloseRequested += delegate { Application.ExitThread(); };

                        // Another process asking us to stop arrives on a pool
                        // thread; bounce it onto the message loop's thread.
                        RegisteredWaitHandle registration2 = ThreadPool.RegisterWaitForSingleObject(
                            stopSignal, delegate { helperWindow.RequestClose(); }, null,
                            Timeout.Infinite, true);

                        SessionEndingEventHandler endingHandler =
                            delegate { helperWindow.RequestClose(); };
                        SystemEvents.SessionEnding += endingHandler;

                        try
                        {
                            TrimWorkingSet();

                            // Everything above succeeded: the slot is held, session
                            // tracking is live, the window exists, and every feature
                            // that was asked for is registered and working. Only now
                            // is this helper able to do its job.
                            readySignal.Set();

                            Application.Run();
                        }
                        finally
                        {
                            // Stop claiming readiness the moment we start going away,
                            // so nothing sees a ready signal from a dying process.
                            readySignal.Reset();

                            SystemEvents.SessionEnding -= endingHandler;
                            registration2.Unregister(null);
                            screens.Dispose();

                            // Before the mutex, deliberately. Releasing the slot
                            // first would let a replacement helper start and try
                            // to register the same combination while this one
                            // still holds it - Windows would refuse with
                            // "already registered", and a reconfiguration that
                            // is only ever this process handing over to itself
                            // would fail for a conflict with nobody.
                            //
                            // The outer finally repeats this for the paths that
                            // never reach here; Unregister is idempotent.
                            hotkeys.Dispose();

                            instanceLock.ReleaseMutex();
                        }
                    }
                    finally
                    {
                        // Releasing the reservation is not optional. A hotkey left
                        // registered by a process that is gone is a combination
                        // nobody can use and nobody can release.
                        hotkeys.Dispose();
                    }
                }
            }

            return ExitOk;
        }

        /// <summary>
        /// Owns whichever display blanker is currently holding the screens dark.
        ///
        /// There is at most one, and a new lock replaces the old one rather than
        /// leaving two of them counting requests against the same monitor. It is
        /// a class rather than a captured local so that both callers - the lid
        /// and the hotkey - go through the same replacement.
        /// </summary>
        private sealed class ScreenHolder : IDisposable
        {
            private readonly IDisplayController _displays;
            private readonly IUserInputMonitor _input;
            private DisplayBlanker _current;

            public ScreenHolder(IDisplayController displays, IUserInputMonitor input)
            {
                _displays = displays;
                _input = input;
            }

            /// <summary>Starts holding the screens dark. Does not lock; the caller already did.</summary>
            public void Darken()
            {
                Adopt(StartBlanker());
            }

            /// <summary>Takes ownership of a blanker somebody else started.</summary>
            public void Adopt(DisplayBlanker blanker)
            {
                DisplayBlanker previous = _current;
                _current = blanker;

                if (previous != null && !ReferenceEquals(previous, blanker))
                    previous.Dispose();

                if (blanker != null)
                {
                    DisplayBlanker adopted = blanker;
                    blanker.Finished += delegate
                    {
                        if (ReferenceEquals(_current, adopted)) _current = null;
                    };
                }
            }

            private DisplayBlanker StartBlanker()
            {
                try
                {
                    var blanker = new DisplayBlanker(_displays, _input);
                    blanker.Start();
                    return blanker;
                }
                catch (Exception)
                {
                    // Cosmetic. A machine that locked but kept its screens on is
                    // still locked, and that is the guarantee.
                    return null;
                }
            }

            public void Dispose()
            {
                if (_current == null) return;
                _current.Dispose();
                _current = null;
            }
        }

        /// <summary>
        /// Diagnostics. Writes to the console of whoever launched it, if there
        /// is one - the helper is a windowless program, so it has none of its
        /// own and must never create one.
        /// </summary>
        private static int ReportStatus()
        {
            WatcherState state = new WatcherController().GetState();
            var autostart = new RunKeyAutostartRegistry();
            AutoLockSettings settings = new FileSettingsStore().Load();
            AutostartInspection entry = AutostartInspector.Inspect(autostart,
                WatcherController.DefaultWatcherPath);

            WriteLine("AFKLocker helper");
            WriteLine("  lock mode : " + (settings.Mode == LockMode.Automatic ? "automatic" : "manual"));
            WriteLine("  hotkey    : " + (settings.HotkeyEnabled
                ? settings.Hotkey.Describe()
                : "off"));
            WriteLine("  needs it  : " + DescribeFeatures(settings.RequiredFeatures));
            WriteLine("  state     : " + DescribeState(state));
            WriteLine("  autostart : " + AutostartInspector.Describe(entry.State));

            // The autostart entry is part of the answer, not decoration. A helper
            // that is Ready but whose sign-in entry points somewhere else will
            // simply not come back tomorrow, and exiting 0 on that would make
            // --status agree with a machine the rest of the code calls broken.
            bool wanted = settings.RequiredFeatures != HelperFeatures.None;

            if (!wanted)
                return state == WatcherState.NotRunning
                    && entry.State == AutostartState.Absent ? 0 : 1;

            return state == WatcherState.Ready
                && entry.State == AutostartState.Correct ? 0 : 1;
        }

        private static string DescribeFeatures(HelperFeatures features)
        {
            if (features == HelperFeatures.None) return "nothing - the helper should not be running";
            if (features == (HelperFeatures.LidLock | HelperFeatures.GlobalHotkey))
                return "lid close and the global hotkey";
            if ((features & HelperFeatures.LidLock) != 0) return "lid close";
            return "the global hotkey";
        }

        private static string DescribeState(WatcherState state)
        {
            switch (state)
            {
                case WatcherState.Ready:
                    return "ready (every enabled feature is registered)";
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
            WriteLine(stopped ? "AFKLocker helper stopped." : "AFKLocker helper did not stop.");
            return stopped ? 0 : 1;
        }

        private static int ShowHelp()
        {
            WriteLine("AFKLockerWatcher - the optional AFKLocker background helper.");
            WriteLine("");
            WriteLine("It runs only while automatic locking or the global hotkey is switched on,");
            WriteLine("and registers only what those need. It does not watch the keyboard: the");
            WriteLine("hotkey is a RegisterHotKey reservation for one combination.");
            WriteLine("");
            WriteLine("  (no arguments)  Run the helper.");
            WriteLine("  --status        Report settings, helper state, and autostart.");
            WriteLine("  --stop          Ask a running helper to exit.");
            WriteLine("");
            WriteLine("Exit codes when running: 0 exited normally, 2 could not register for lid");
            WriteLine("events, 3 another helper already holds this session, 4 Windows refused the");
            WriteLine("hotkey, 5 nothing is switched on so there was nothing to do.");
            WriteLine("");
            WriteLine("Turn these on or off in AFKLocker Setup, which also manages autostart.");
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
