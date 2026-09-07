using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

namespace AFKLocker.Core
{
    /// <summary>
    /// What a watcher is actually doing, as opposed to whether a process exists.
    ///
    /// The distinction matters: a process that has started is not the same as a
    /// watcher that can receive lid events. Between the two there is a window in
    /// which the runtime is still starting, the notification window does not
    /// exist yet, and closing the lid would do nothing.
    /// </summary>
    public enum WatcherState
    {
        /// <summary>No watcher process holds this session's slot.</summary>
        NotRunning,

        /// <summary>A process is alive but has not reported itself ready yet.</summary>
        Starting,

        /// <summary>Running, registered for lid events, and able to lock.</summary>
        Ready,

        /// <summary>
        /// The signals contradict each other - typically a readiness signal left
        /// behind by a process that is gone. Reported rather than smoothed over.
        /// </summary>
        Unhealthy
    }

    public enum WatcherStartFailure
    {
        None,
        ExecutableMissing,
        ProcessDidNotStart,

        /// <summary>The process exited before signalling readiness.</summary>
        ExitedBeforeReady,

        /// <summary>The process is alive but never became ready in time.</summary>
        ReadyTimeout,

        /// <summary>The watcher could not register for lid notifications.</summary>
        LidNotificationFailed,

        /// <summary>Windows would not reserve the chosen key combination.</summary>
        HotkeyRegistrationFailed,

        /// <summary>Nothing is switched on, so there was no reason to run.</summary>
        NothingToDo,

        /// <summary>The settings exist but could not be read, so it knew nothing to register.</summary>
        SettingsUnreadable,

        /// <summary>
        /// The caller holds an administrator token. A child process inherits it,
        /// and the helper must never run with more rights than the session it
        /// looks after.
        /// </summary>
        RequiresStandardUser
    }

    public sealed class WatcherStartResult
    {
        public bool Success { get; private set; }
        public WatcherStartFailure Failure { get; private set; }
        public string Message { get; private set; }

        private WatcherStartResult(bool success, WatcherStartFailure failure, string message)
        {
            Success = success;
            Failure = failure;
            Message = message;
        }

        public static WatcherStartResult Ok()
        {
            return new WatcherStartResult(true, WatcherStartFailure.None, null);
        }

        public static WatcherStartResult Failed(WatcherStartFailure failure, string message)
        {
            return new WatcherStartResult(false, failure, message);
        }
    }

    /// <summary>
    /// Starting, stopping and inspecting the watcher process. An interface so
    /// the manager's logic - including every failure path - can be tested
    /// without launching anything.
    /// </summary>
    public interface IWatcherProcess
    {
        WatcherState GetState();

        /// <summary>
        /// Launches the watcher and waits until it reports itself ready. Returns
        /// only once the outcome is known, so a caller never has to guess.
        /// </summary>
        WatcherStartResult Start(string watcherPath);

        /// <summary>Asks a running watcher to exit; true when none is left.</summary>
        bool Stop(TimeSpan timeout);
    }

    /// <summary>
    /// Coordinates with the watcher process through three named kernel objects
    /// in the session's own namespace (Local\), so each signed-in user gets
    /// their own watcher and switching users does not cross the wires.
    ///
    ///   Running  a mutex held for the lifetime of the process
    ///   Ready    an event set once the watcher can actually receive lid events
    ///   Stop     an event set to ask the watcher to exit
    ///
    /// Running and Ready are deliberately separate. Waiting on Running alone
    /// says nothing about whether the watcher works: the process claims the
    /// mutex early, long before it has registered for lid notifications, and a
    /// watcher that fails to register would look perfectly healthy.
    /// </summary>
    public sealed class WatcherController : IWatcherProcess
    {
        /// <summary>Held for the lifetime of a running watcher.</summary>
        public const string RunningMutexName = @"Local\AFKLocker.Watcher.Running";

        /// <summary>Set once the watcher is registered and able to lock.</summary>
        public const string ReadyEventName = @"Local\AFKLocker.Watcher.Ready";

        /// <summary>Set to ask a running watcher to exit.</summary>
        public const string StopEventName = @"Local\AFKLocker.Watcher.Stop";

        public const string WatcherFileName = "AFKLockerWatcher.exe";

        /// <summary>Exit code the watcher uses when it cannot register for lid events.</summary>
        public const int ExitCodeLidNotificationFailed = 2;

        /// <summary>Exit code the watcher uses when another instance already holds the slot.</summary>
        public const int ExitCodeAlreadyRunning = 3;

        /// <summary>Exit code the watcher uses when Windows refused the hotkey.</summary>
        public const int ExitCodeHotkeyRegistrationFailed = 4;

        /// <summary>Exit code the watcher uses when no feature needs it running.</summary>
        public const int ExitCodeNothingToDo = 5;

        /// <summary>Exit code the watcher uses when it cannot read its settings.</summary>
        public const int ExitCodeSettingsUnreadable = 6;

        private static readonly TimeSpan DefaultReadyTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

        private readonly TimeSpan _readyTimeout;
        private readonly Func<bool> _isElevated;

        public WatcherController()
            : this(DefaultReadyTimeout)
        {
        }

        public WatcherController(TimeSpan readyTimeout)
            : this(readyTimeout, null)
        {
        }

        /// <summary>
        /// The elevation check is a parameter only so the refusal in
        /// <see cref="Start"/> can be tested without running the test suite as
        /// administrator. Passing null uses the real token.
        /// </summary>
        public WatcherController(TimeSpan readyTimeout, Func<bool> isElevated)
        {
            _readyTimeout = readyTimeout;
            _isElevated = isElevated ?? new Func<bool>(CurrentProcessIsElevated);
        }

        /// <summary>TOKEN_INFORMATION_CLASS.TokenElevationType.</summary>
        private const int TokenElevationType = 18;

        /// <summary>The user's ordinary token: UAC off, or the built-in Administrator.</summary>
        private const int TokenElevationTypeDefault = 1;

        /// <summary>Raised above the ordinary token - what a UAC prompt produces.</summary>
        private const int TokenElevationTypeFull = 2;

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
            out int tokenInformation, int tokenInformationLength, out int returnLength);

        /// <summary>
        /// True when this process was raised above the token it would ordinarily
        /// have - what Setup's UAC relaunch produces, and what a child process
        /// would inherit.
        ///
        /// Deliberately not "is this an administrator". On a machine with UAC
        /// switched off, and under the built-in Administrator account, every
        /// process carries the administrator token, including the helper the
        /// user starts themselves - so answering "administrator" there would
        /// refuse a launch that is not an escalation at all, and the background
        /// features would simply stop working with no way to switch them back
        /// on. TokenElevationType tells the two apart: Default is the user's
        /// ordinary token however powerful it is, Full is one that was raised.
        /// </summary>
        public static bool CurrentProcessIsElevated()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    int elevationType;
                    int returned;
                    if (!GetTokenInformation(identity.Token, TokenElevationType,
                            out elevationType, sizeof(int), out returned))
                        return false;

                    return elevationType == TokenElevationTypeFull;
                }
            }
            catch (Exception)
            {
                // Unknown is treated as not elevated: refusing to start the
                // helper because the token could not be read would break the
                // ordinary case to guard against a rare one.
                return false;
            }
        }

        /// <summary>Full path of the watcher that sits next to the running program.</summary>
        public static string DefaultWatcherPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WatcherFileName); }
        }

        /// <summary>True when a watcher process holds this session's slot.</summary>
        public static bool IsAnyRunning
        {
            get
            {
                try
                {
                    bool createdNew;
                    // Opening the mutex is enough: if we created it, nobody held it.
                    using (new Mutex(false, RunningMutexName, out createdNew))
                    {
                        return !createdNew;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Its twin below already guards this. Without the same guard
                    // here, a throw escapes GetState, then GetStatus - which
                    // calls it outside its own try - and Setup fails to open at
                    // all. Something exists that we may not touch, which is
                    // closer to "running" than to "not".
                    return true;
                }
                catch (IOException)
                {
                    return true;
                }
            }
        }

        /// <summary>True when a watcher has signalled that it can receive lid events.</summary>
        public static bool IsAnyReady
        {
            get
            {
                try
                {
                    using (EventWaitHandle ready = EventWaitHandle.OpenExisting(ReadyEventName))
                        return ready.WaitOne(0);
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }

        public WatcherState GetState()
        {
            bool running = IsAnyRunning;
            bool ready = IsAnyReady;

            if (running && ready) return WatcherState.Ready;
            if (running) return WatcherState.Starting;
            if (ready) return WatcherState.Unhealthy;   // readiness signal outlived its process
            return WatcherState.NotRunning;
        }

        public WatcherStartResult Start(string watcherPath)
        {
            if (string.IsNullOrEmpty(watcherPath))
                return WatcherStartResult.Failed(WatcherStartFailure.ExecutableMissing,
                    "No watcher path was given.");

            if (!File.Exists(watcherPath))
                return WatcherStartResult.Failed(WatcherStartFailure.ExecutableMissing,
                    "AFKLockerWatcher.exe was not found at " + watcherPath);

            // Checked before anything else, and deliberately unconditional. A
            // child process inherits its parent's token, so a helper launched
            // from an elevated AFKLocker Setup would sit in the session as
            // administrator - holding a global hotkey registration and locking
            // the session with rights it has no use for. The only place
            // elevation is ever needed is writing power settings, and that
            // happens in Setup itself. This is the one line the helper can be
            // started from, so refusing here is what makes "never elevated" a
            // property of the program rather than of its callers.
            if (_isElevated())
                return WatcherStartResult.Failed(WatcherStartFailure.RequiresStandardUser,
                    "The helper was not started: this program is running as administrator, and "
                    + "the helper must never run elevated. Close this window and open AFKLocker "
                    + "Setup normally to change the background features.");

            if (GetState() == WatcherState.Ready)
                return WatcherStartResult.Ok();

            // Create the readiness event before launching, so the watcher opens
            // this one rather than racing to create it, and reset it so a stale
            // signal from a previous run cannot be mistaken for this one.
            bool createdNew;
            using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset,
                ReadyEventName, out createdNew))
            {
                ready.Reset();

                Process process;
                try
                {
                    process = Process.Start(new ProcessStartInfo
                    {
                        FileName = watcherPath,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetDirectoryName(watcherPath) ?? string.Empty
                    });
                }
                catch (Exception ex)
                {
                    return WatcherStartResult.Failed(WatcherStartFailure.ProcessDidNotStart,
                        "The watcher process could not be started: " + ex.Message);
                }

                if (process == null)
                    return WatcherStartResult.Failed(WatcherStartFailure.ProcessDidNotStart,
                        "The watcher process could not be started.");

                return WaitForReady(process, ready);
            }
        }

        /// <summary>
        /// Waits for readiness, for the process to die, or for the timeout -
        /// whichever comes first. A dead process is detected immediately rather
        /// than waited out, so a watcher that cannot register for lid events
        /// fails fast instead of stalling the caller for the whole timeout.
        /// </summary>
        private WatcherStartResult WaitForReady(Process process, EventWaitHandle ready)
        {
            using (process)
            {
                DateTime deadline = DateTime.UtcNow + _readyTimeout;

                while (DateTime.UtcNow < deadline)
                {
                    if (ready.WaitOne(PollInterval))
                        return WatcherStartResult.Ok();

                    if (!process.HasExited)
                        continue;

                    // The process is gone. One more check closes the race where
                    // it signalled readiness and exited between our two looks.
                    if (ready.WaitOne(0))
                        return WatcherStartResult.Ok();

                    int exitCode = process.ExitCode;

                    if (exitCode == ExitCodeLidNotificationFailed)
                        return WatcherStartResult.Failed(WatcherStartFailure.LidNotificationFailed,
                            "Windows refused to register the helper for lid notifications, so "
                            + "closing the lid could never lock this machine.");

                    if (exitCode == ExitCodeHotkeyRegistrationFailed)
                        return WatcherStartResult.Failed(WatcherStartFailure.HotkeyRegistrationFailed,
                            "Windows would not reserve that key combination, so the global hotkey "
                            + "could never fire. It is most likely already registered by another "
                            + "application.");

                    if (exitCode == ExitCodeNothingToDo)
                        return WatcherStartResult.Failed(WatcherStartFailure.NothingToDo,
                            "The helper had nothing to do: neither automatic locking nor the "
                            + "global hotkey is switched on.");

                    if (exitCode == ExitCodeSettingsUnreadable)
                        return WatcherStartResult.Failed(WatcherStartFailure.SettingsUnreadable,
                            "The helper could not read the AFKLocker settings, so it did not "
                            + "know what to listen for.");

                    if (exitCode == ExitCodeAlreadyRunning)
                    {
                        // Another instance owns the slot. That is only good news
                        // if that instance is actually ready.
                        if (WaitForExistingReady(deadline))
                            return WatcherStartResult.Ok();

                        return WatcherStartResult.Failed(WatcherStartFailure.ReadyTimeout,
                            "Another watcher already holds this session, but it never became ready.");
                    }

                    return WatcherStartResult.Failed(WatcherStartFailure.ExitedBeforeReady,
                        string.Format("The watcher exited with code {0} before it was ready.", exitCode));
                }

                if (ready.WaitOne(0))
                    return WatcherStartResult.Ok();

                return WatcherStartResult.Failed(WatcherStartFailure.ReadyTimeout,
                    string.Format("The watcher did not become ready within {0:N0} seconds.",
                        _readyTimeout.TotalSeconds));
            }
        }

        private static bool WaitForExistingReady(DateTime deadline)
        {
            while (DateTime.UtcNow < deadline)
            {
                if (IsAnyReady) return true;
                if (!IsAnyRunning) return false;
                Thread.Sleep(PollInterval);
            }
            return IsAnyReady;
        }

        /// <summary>
        /// Asks a running watcher to exit and waits for it to release the slot.
        /// </summary>
        /// <returns>True when no watcher is running afterwards.</returns>
        public bool Stop(TimeSpan timeout)
        {
            if (!IsAnyRunning)
                return !IsAnyReady;   // a leftover readiness signal is not "stopped"

            try
            {
                using (EventWaitHandle stop = EventWaitHandle.OpenExisting(StopEventName))
                    stop.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // Nothing is listening. Either it is already going away, or it
                // never got far enough to create the event.
                return WaitUntilGone(timeout);
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            return WaitUntilGone(timeout);
        }

        private static bool WaitUntilGone(TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (!IsAnyRunning) return true;
                Thread.Sleep(PollInterval);
            }
            return !IsAnyRunning;
        }
    }
}
