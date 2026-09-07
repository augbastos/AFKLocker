using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Win32;

namespace AFKLocker.Core
{
    /// <summary>Registers a program to start when the user signs in.</summary>
    public interface IAutostartRegistry
    {
        bool IsRegistered { get; }

        /// <summary>The command currently registered, or null.</summary>
        string RegisteredCommand { get; }

        void Register(string command);

        void Unregister();
    }

    /// <summary>
    /// Autostart through HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
    ///
    /// Chosen over a scheduled task or a service because it is the smallest
    /// mechanism that does the job: it is per-user, needs no administrator
    /// rights, starts the watcher in the user's own interactive session (which
    /// is where it has to be to receive power notifications and lock that
    /// session), is a single value to remove on uninstall, and is visible to
    /// the user in Task Manager's Startup tab - so nothing about it is hidden.
    /// </summary>
    public sealed class RunKeyAutostartRegistry : IAutostartRegistry
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        private readonly string _valueName;

        public RunKeyAutostartRegistry()
            : this("AFKLocker Watcher")
        {
        }

        public RunKeyAutostartRegistry(string valueName)
        {
            if (string.IsNullOrEmpty(valueName)) throw new ArgumentException("valueName must not be empty", "valueName");
            _valueName = valueName;
        }

        public bool IsRegistered
        {
            get { return RegisteredCommand != null; }
        }

        public string RegisteredCommand
        {
            get
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return null;
                    return key.GetValue(_valueName) as string;
                }
            }
        }

        public void Register(string command)
        {
            if (string.IsNullOrEmpty(command)) throw new ArgumentException("command must not be empty", "command");
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (key == null)
                    throw new InvalidOperationException("Could not open the Windows startup registry key.");
                key.SetValue(_valueName, command, RegistryValueKind.String);
            }
        }

        public void Unregister()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key == null) return;
                if (key.GetValue(_valueName) != null)
                    key.DeleteValue(_valueName, false);
            }
        }
    }

    public enum AutoLockFailure
    {
        None,
        WatcherMissing,
        SettingsWriteFailed,
        AutostartFailed,
        WatcherStartFailed,
        WatcherNotReady,
        StopFailed
    }

    /// <summary>
    /// The outcome of an enable, disable or cleanup.
    ///
    /// A failure that was fully undone and a failure that left something behind
    /// are different things, and the caller is told which happened rather than
    /// getting a bare false.
    /// </summary>
    public sealed class AutoLockResult
    {
        public bool Success { get; private set; }
        public AutoLockFailure Failure { get; private set; }
        public string Message { get; private set; }

        /// <summary>True when a failure was undone, returning to the previous state.</summary>
        public bool RolledBack { get; private set; }

        /// <summary>Anything the operation could not put back or clean up.</summary>
        public ReadOnlyCollection<string> Residue { get; private set; }

        internal AutoLockResult(bool success, AutoLockFailure failure, string message,
            bool rolledBack, IList<string> residue)
        {
            Success = success;
            Failure = failure;
            Message = message;
            RolledBack = rolledBack;
            Residue = new ReadOnlyCollection<string>(residue ?? new List<string>());
        }

        /// <summary>True when nothing was left in an in-between state.</summary>
        public bool IsClean
        {
            get { return Residue.Count == 0; }
        }

        public static AutoLockResult Ok()
        {
            return new AutoLockResult(true, AutoLockFailure.None, null, false, null);
        }

        public static AutoLockResult Ok(IList<string> residue)
        {
            return new AutoLockResult(true, AutoLockFailure.None, null, false, residue);
        }

        public override string ToString()
        {
            if (Success) return IsClean ? "ok" : "ok with residue";
            return string.Format("{0}: {1}", Failure, Message);
        }
    }

    /// <summary>What the setup window shows about automatic locking.</summary>
    public sealed class AutoLockStatus
    {
        public LockMode Mode { get; set; }
        public bool AutostartRegistered { get; set; }
        public WatcherState WatcherState { get; set; }
        public bool WatcherInstalled { get; set; }

        public bool WatcherReady
        {
            get { return WatcherState == WatcherState.Ready; }
        }

        /// <summary>
        /// Describes any mismatch between what was configured and what is
        /// actually true, or null when they agree.
        /// </summary>
        public string Inconsistency
        {
            get
            {
                if (Mode == LockMode.Automatic)
                {
                    if (!WatcherInstalled)
                        return "Automatic mode is on, but the watcher program is missing.";
                    if (WatcherState == WatcherState.NotRunning)
                        return "Automatic mode is on, but no watcher is running.";
                    if (WatcherState == WatcherState.Unhealthy)
                        return "Automatic mode is on, but the watcher is in an unhealthy state.";
                    if (!AutostartRegistered)
                        return "Automatic mode is on, but the watcher is not set to start at sign-in.";
                    return null;
                }

                if (AutostartRegistered)
                    return "Manual mode, but a watcher is still set to start at sign-in.";
                if (WatcherState != WatcherState.NotRunning)
                    return "Manual mode, but a watcher is still running.";
                return null;
            }
        }

        public bool IsConsistent
        {
            get { return Inconsistency == null; }
        }
    }

    /// <summary>
    /// Turns automatic locking on and off.
    ///
    /// Enabling touches three things - the saved mode, the autostart entry and
    /// the running process - and any of them can fail. Rather than leaving the
    /// machine half-configured, enable is transactional: each step records how
    /// to undo itself, and a failure at any point unwinds the ones before it.
    ///
    /// The result is that Enable leaves exactly one of two states behind:
    /// automatic and genuinely working, or manual and clean. Anything it could
    /// not undo is reported as residue rather than passed over.
    /// </summary>
    public sealed class AutoLockManager
    {
        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

        private readonly ISettingsStore _settings;
        private readonly IAutostartRegistry _autostart;
        private readonly IWatcherProcess _watcher;
        private readonly string _watcherPath;

        public AutoLockManager(ISettingsStore settings, IAutostartRegistry autostart,
            IWatcherProcess watcher, string watcherPath)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            if (autostart == null) throw new ArgumentNullException("autostart");
            if (watcher == null) throw new ArgumentNullException("watcher");
            _settings = settings;
            _autostart = autostart;
            _watcher = watcher;
            _watcherPath = watcherPath;
        }

        private bool WatcherInstalled
        {
            get { return !string.IsNullOrEmpty(_watcherPath) && File.Exists(_watcherPath); }
        }

        private string AutostartCommand
        {
            get { return "\"" + _watcherPath + "\""; }
        }

        public AutoLockStatus GetStatus()
        {
            return new AutoLockStatus
            {
                Mode = _settings.Load().Mode,
                AutostartRegistered = _autostart.IsRegistered,
                WatcherState = _watcher.GetState(),
                WatcherInstalled = WatcherInstalled
            };
        }

        // ------------------------------------------------------------- enable ---

        /// <summary>
        /// Switches to automatic locking, or leaves the machine exactly as it
        /// was. There is no third outcome.
        /// </summary>
        public AutoLockResult Enable()
        {
            if (!WatcherInstalled)
                return new AutoLockResult(false, AutoLockFailure.WatcherMissing,
                    "AFKLockerWatcher.exe was not found next to this program.", false, null);

            // Undo steps, innermost last; run in reverse on failure.
            var undo = new List<UndoStep>();

            // 1. Remember the mode. Saved first so that a watcher which starts
            //    and immediately looks at the settings sees the intended state.
            AutoLockSettings previousSettings;
            try
            {
                previousSettings = _settings.Load();
            }
            catch (Exception ex)
            {
                return Failure(AutoLockFailure.SettingsWriteFailed,
                    "Could not read the current settings: " + ex.Message, undo);
            }

            try
            {
                _settings.Save(new AutoLockSettings { Mode = LockMode.Automatic });
                undo.Add(new UndoStep("saved mode", delegate { _settings.Save(previousSettings); }));
            }
            catch (Exception ex)
            {
                return Failure(AutoLockFailure.SettingsWriteFailed,
                    "Could not save the lock mode: " + ex.Message, undo);
            }

            // 2. Autostart, so it comes back at the next sign-in.
            string previousCommand;
            try
            {
                previousCommand = _autostart.RegisteredCommand;
                _autostart.Register(AutostartCommand);
                string restoreTo = previousCommand;
                undo.Add(new UndoStep("autostart entry", delegate
                {
                    if (restoreTo == null) _autostart.Unregister();
                    else _autostart.Register(restoreTo);
                }));
            }
            catch (Exception ex)
            {
                return Failure(AutoLockFailure.AutostartFailed,
                    "Could not register the watcher to start at sign-in: " + ex.Message, undo);
            }

            // 3. Start it now, and wait until it is genuinely able to lock.
            WatcherStartResult start;
            try
            {
                start = _watcher.Start(_watcherPath);
            }
            catch (Exception ex)
            {
                return Failure(AutoLockFailure.WatcherStartFailed,
                    "Could not start the watcher: " + ex.Message, undo);
            }

            if (!start.Success)
            {
                AutoLockFailure failure = start.Failure == WatcherStartFailure.ReadyTimeout
                    || start.Failure == WatcherStartFailure.LidNotificationFailed
                    ? AutoLockFailure.WatcherNotReady
                    : AutoLockFailure.WatcherStartFailed;

                // Stop reports refusal by returning false, not by throwing. The
                // undo has to turn that into a failure, or a watcher that would
                // not stop is reported as a clean rollback while still running.
                undo.Add(new UndoStep("watcher process", delegate
                {
                    if (!_watcher.Stop(StopTimeout))
                        throw new InvalidOperationException("it did not stop in time");
                }));
                return Failure(failure, start.Message, undo);
            }

            return AutoLockResult.Ok();
        }

        /// <summary>Unwinds the steps that succeeded, then reports what happened.</summary>
        private static AutoLockResult Failure(AutoLockFailure failure, string message, List<UndoStep> undo)
        {
            var residue = new List<string>();

            for (int i = undo.Count - 1; i >= 0; i--)
            {
                try
                {
                    undo[i].Undo();
                }
                catch (Exception ex)
                {
                    // Rollback itself can fail. Say so rather than claiming a
                    // clean revert - this is exactly the state a user needs to
                    // know about.
                    residue.Add(string.Format("could not undo the {0}: {1}", undo[i].Description, ex.Message));
                }
            }

            return new AutoLockResult(false, failure, message, undo.Count > 0, residue);
        }

        private sealed class UndoStep
        {
            private readonly Action _undo;

            public string Description { get; private set; }

            public UndoStep(string description, Action undo)
            {
                Description = description;
                _undo = undo;
            }

            public void Undo()
            {
                _undo();
            }
        }

        // ------------------------------------------------------------ disable ---

        /// <summary>
        /// Switches back to manual. Unlike enable there is nothing to roll back
        /// to - the safest reachable state is always "manual and clean" - so
        /// every step is attempted even if an earlier one fails, and whatever
        /// could not be undone is reported.
        /// </summary>
        public AutoLockResult Disable()
        {
            var residue = new List<string>();
            bool stopped = StopWatcher(residue);
            RemoveAutostart(residue);

            try
            {
                _settings.Save(new AutoLockSettings { Mode = LockMode.Manual });
            }
            catch (Exception ex)
            {
                residue.Add("the saved mode still says automatic: " + ex.Message);
                return new AutoLockResult(false, AutoLockFailure.SettingsWriteFailed,
                    "Could not save the lock mode: " + ex.Message, false, residue);
            }

            if (!stopped)
                return new AutoLockResult(false, AutoLockFailure.StopFailed,
                    "Automatic locking is off and will not start again, but the watcher "
                    + "did not stop in time.", false, residue);

            return AutoLockResult.Ok(residue);
        }

        /// <summary>
        /// Removes every trace of automatic locking without recording a mode.
        /// Used by the uninstaller, where the user's preference is about to stop
        /// existing anyway.
        /// </summary>
        public AutoLockResult Cleanup()
        {
            var residue = new List<string>();
            bool stopped = StopWatcher(residue);
            RemoveAutostart(residue);

            if (!stopped)
                return new AutoLockResult(false, AutoLockFailure.StopFailed,
                    "The watcher did not stop in time.", false, residue);

            return AutoLockResult.Ok(residue);
        }

        private bool StopWatcher(List<string> residue)
        {
            try
            {
                if (_watcher.Stop(StopTimeout)) return true;

                // Stop returns false for two different situations, and saying
                // the wrong one sends the user looking for the wrong thing.
                residue.Add(_watcher.GetState() == WatcherState.Unhealthy
                    ? "a stale watcher readiness signal is still present"
                    : "a watcher process is still running");
                return false;
            }
            catch (Exception ex)
            {
                residue.Add("could not stop the watcher: " + ex.Message);
                return false;
            }
        }

        private void RemoveAutostart(List<string> residue)
        {
            try
            {
                _autostart.Unregister();
            }
            catch (Exception ex)
            {
                residue.Add("the sign-in entry could not be removed: " + ex.Message);
            }
        }

        // -------------------------------------------------------- reconciling ---

        /// <summary>
        /// Brings reality back in line with the saved mode, and reports what it
        /// had to do.
        ///
        /// Called when the setup window opens. Things drift for ordinary
        /// reasons: the watcher was killed by Task Manager, a cleanup tool
        /// removed the startup entry, a crash left a stale readiness signal. The
        /// window would otherwise show a broken state and expect the user to
        /// work out the fix.
        ///
        /// If automatic mode cannot be restored, it converges to manual and
        /// clean rather than leaving a half-configured machine.
        /// </summary>
        public AutoLockResult Reconcile()
        {
            AutoLockStatus status = GetStatus();
            if (status.IsConsistent) return AutoLockResult.Ok();

            if (status.Mode == LockMode.Manual)
            {
                // Manual must mean nothing resident.
                var residue = new List<string>();
                StopWatcher(residue);
                RemoveAutostart(residue);
                return AutoLockResult.Ok(residue);
            }

            if (!status.WatcherInstalled)
            {
                // Automatic without a watcher binary cannot be repaired.
                AutoLockResult disabled = Disable();
                return new AutoLockResult(false, AutoLockFailure.WatcherMissing,
                    "Automatic locking was switched off: the watcher program is missing.",
                    true, disabled.Residue);
            }

            // Automatic, but something is missing. Re-running enable puts the
            // mode, the autostart entry and the process back together, and rolls
            // back to a clean manual state if it cannot.
            AutoLockResult repaired = Enable();
            if (repaired.Success) return repaired;

            AutoLockResult fallback = Disable();
            var combined = new List<string>(repaired.Residue);
            foreach (string item in fallback.Residue) combined.Add(item);

            return new AutoLockResult(false, repaired.Failure,
                "Automatic locking could not be restored and has been switched off: " + repaired.Message,
                true, combined);
        }
    }
}
