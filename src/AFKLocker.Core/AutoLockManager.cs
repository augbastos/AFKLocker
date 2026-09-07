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
        StopFailed,

        /// <summary>The helper could not reserve the chosen key combination.</summary>
        HotkeyUnavailable
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

    /// <summary>What the setup window shows about the background helper.</summary>
    public sealed class AutoLockStatus
    {
        public LockMode Mode { get; set; }
        public bool HotkeyEnabled { get; set; }

        /// <summary>The chosen combination. Never null in practice; empty means none.</summary>
        public HotkeyBinding Hotkey { get; set; }

        /// <summary>What the sign-in entry is, not merely whether one exists.</summary>
        public AutostartState Autostart { get; set; }

        /// <summary>The sentence explaining a bad autostart entry, or null.</summary>
        public string AutostartProblem { get; set; }

        public WatcherState WatcherState { get; set; }
        public bool WatcherInstalled { get; set; }

        public AutoLockStatus()
        {
            Hotkey = HotkeyBinding.Empty;
            Autostart = AutostartState.Absent;
        }

        public bool WatcherReady
        {
            get { return WatcherState == WatcherState.Ready; }
        }

        /// <summary>Kept for callers that only want the yes/no.</summary>
        public bool AutostartRegistered
        {
            get { return Autostart != AutostartState.Absent && Autostart != AutostartState.ReadFailed; }
        }

        /// <summary>
        /// What the helper has to be doing, derived from the features switched
        /// on rather than from the lock mode.
        /// </summary>
        public HelperFeatures RequiredFeatures
        {
            get
            {
                HelperFeatures features = HelperFeatures.None;
                if (Mode == LockMode.Automatic) features |= HelperFeatures.LidLock;
                if (HotkeyEnabled && Hotkey != null && Hotkey.IsUsable)
                    features |= HelperFeatures.GlobalHotkey;
                return features;
            }
        }

        /// <summary>True when something switched on needs a process to be resident.</summary>
        public bool HelperRequired
        {
            get { return RequiredFeatures != HelperFeatures.None; }
        }

        /// <summary>Why the helper exists, in words, or null when it should not.</summary>
        public string HelperPurpose
        {
            get
            {
                HelperFeatures features = RequiredFeatures;
                bool lid = (features & HelperFeatures.LidLock) != 0;
                bool hotkey = (features & HelperFeatures.GlobalHotkey) != 0;

                if (lid && hotkey) return "lid close and the global hotkey";
                if (lid) return "lid close";
                if (hotkey) return "the global hotkey";
                return null;
            }
        }

        /// <summary>
        /// Describes any mismatch between what was configured and what is
        /// actually true, or null when they agree.
        /// </summary>
        public string Inconsistency
        {
            get
            {
                // A hotkey switched on with nothing usable bound is a
                // contradiction in the settings themselves, before any process
                // or registry entry is considered.
                if (HotkeyEnabled && (Hotkey == null || !Hotkey.IsUsable))
                    return "The global hotkey is switched on, but no usable key is bound to it.";

                if (HelperRequired)
                {
                    string purpose = HelperPurpose;

                    if (!WatcherInstalled)
                        return "The helper is needed for " + purpose + ", but its program is missing.";
                    if (WatcherState == WatcherState.NotRunning)
                        return "The helper is needed for " + purpose + ", but it is not running.";
                    if (WatcherState == WatcherState.Starting)
                        return "The helper is running but has not reported itself ready.";
                    if (WatcherState == WatcherState.Unhealthy)
                        return "The helper is needed for " + purpose + ", but it is in an unhealthy state.";

                    if (Autostart != AutostartState.Correct)
                        return "The helper is needed for " + purpose + ", but the sign-in entry "
                            + AutostartInspector.Describe(Autostart) + ".";

                    return null;
                }

                if (Autostart != AutostartState.Absent)
                    return "Nothing needs a background helper, but a sign-in entry is still present ("
                        + AutostartInspector.Describe(Autostart) + ").";
                if (WatcherState != WatcherState.NotRunning)
                    return "Nothing needs a background helper, but one is still running.";
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
            get { return Core.AutostartCommand.For(_watcherPath); }
        }

        public AutoLockStatus GetStatus()
        {
            AutoLockSettings settings;
            try
            {
                settings = _settings.Load();
            }
            catch (Exception)
            {
                // Manual with no hotkey is the safe reading of unreadable
                // settings: it asks for nothing resident.
                settings = new AutoLockSettings();
            }

            AutostartInspection autostart = AutostartInspector.Inspect(_autostart, _watcherPath);

            return new AutoLockStatus
            {
                Mode = settings.Mode,
                HotkeyEnabled = settings.HotkeyEnabled,
                Hotkey = settings.Hotkey ?? HotkeyBinding.Empty,
                Autostart = autostart.State,
                AutostartProblem = autostart.Problem,
                WatcherState = _watcher.GetState(),
                WatcherInstalled = WatcherInstalled
            };
        }

        /// <summary>The settings as stored, or the safe default if unreadable.</summary>
        public AutoLockSettings GetSettings()
        {
            try
            {
                return _settings.Load().Clone();
            }
            catch (Exception)
            {
                return new AutoLockSettings();
            }
        }

        // -------------------------------------------------------------- apply ---

        /// <summary>
        /// Moves the machine to the given settings, or leaves it exactly as it
        /// was. There is no third outcome.
        ///
        /// Every switch goes through here - automatic on or off, hotkey on or
        /// off, a rebind - because they all change the same three things and the
        /// interesting part is the same in every case: whether a helper has to
        /// exist afterwards, and whether it came up genuinely working.
        ///
        /// Turning the last feature off takes the other path deliberately.
        /// There is nothing to roll back to when the destination is "nothing
        /// resident": that state is always reachable and always safe, so every
        /// step is attempted even if an earlier one fails and whatever could not
        /// be undone is reported as residue.
        /// </summary>
        public AutoLockResult Apply(AutoLockSettings desired)
        {
            if (desired == null) throw new ArgumentNullException("desired");

            // Checked before anything else, and deliberately before the
            // stand-down branch below. An unusable key contributes no feature,
            // so asking for one in manual mode used to look identical to asking
            // for nothing at all: it stood the machine down, reported success,
            // and saved a hotkey switched on with a key that could never fire.
            // Refusing is the only answer that does not lie.
            if (desired.HotkeyEnabled && (desired.Hotkey == null || !desired.Hotkey.IsUsable))
                return new AutoLockResult(false, AutoLockFailure.HotkeyUnavailable,
                    desired.Hotkey == null || desired.Hotkey.IsEmpty
                        ? "No hotkey is set."
                        : desired.Hotkey.Problem, false, null);

            if (desired.RequiredFeatures == HelperFeatures.None)
                return StandDown(desired);

            if (!WatcherInstalled)
                return new AutoLockResult(false, AutoLockFailure.WatcherMissing,
                    "AFKLockerWatcher.exe was not found next to this program.", false, null);

            // Undo steps, innermost last; run in reverse on failure.
            var undo = new List<UndoStep>();

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

            // 1. Stop whatever is running first. The helper reads its settings
            //    once at startup, so a running one is configured for the old
            //    state and has to be replaced rather than left alone.
            //
            //    Its undo runs LAST, by which time step 2's undo has put the old
            //    settings back - so the helper that comes back is the old one.
            //    Only an actual process is worth stopping. Unhealthy means a
            //    readiness signal outlived the process that set it - there is
            //    nothing to stop, and Stop reports that by returning false.
            //    Treating it as a failed stop would make a stale signal block
            //    every configuration change, including the reconciliation that
            //    exists to clear it.
            WatcherState before = _watcher.GetState();
            bool wasRunning = before == WatcherState.Ready || before == WatcherState.Starting;
            if (wasRunning)
            {
                var stopResidue = new List<string>();
                if (!StopWatcher(stopResidue))
                    return new AutoLockResult(false, AutoLockFailure.StopFailed,
                        "The previous helper did not stop, so it could not be reconfigured.",
                        false, stopResidue);

                bool restorePrevious = previousSettings.RequiredFeatures != HelperFeatures.None;
                undo.Add(new UndoStep("previous helper", delegate
                {
                    if (!restorePrevious) return;
                    WatcherStartResult back = _watcher.Start(_watcherPath);
                    if (!back.Success)
                        throw new InvalidOperationException(back.Message);
                }));
            }

            // 2. Save the settings, so the helper started below reads the state
            //    that is being asked for rather than the one being left.
            try
            {
                _settings.Save(desired);
                undo.Add(new UndoStep("saved settings", delegate { _settings.Save(previousSettings); }));
            }
            catch (Exception ex)
            {
                return Failure(AutoLockFailure.SettingsWriteFailed,
                    "Could not save the settings: " + ex.Message, undo);
            }

            // 3. Autostart, so it comes back at the next sign-in.
            try
            {
                string previousCommand = _autostart.RegisteredCommand;
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
                    "Could not register the helper to start at sign-in: " + ex.Message, undo);
            }

            // 4. Start it now, and wait until every feature it was asked for is
            //    genuinely working.
            WatcherStartResult start;
            try
            {
                start = _watcher.Start(_watcherPath);
            }
            catch (Exception ex)
            {
                return Failure(AutoLockFailure.WatcherStartFailed,
                    "Could not start the helper: " + ex.Message, undo);
            }

            if (!start.Success)
            {
                // Stop reports refusal by returning false, not by throwing. The
                // undo has to turn that into a failure, or a helper that would
                // not stop is reported as a clean rollback while still running.
                undo.Add(new UndoStep("helper process", delegate
                {
                    if (!_watcher.Stop(StopTimeout))
                        throw new InvalidOperationException("it did not stop in time");
                }));
                return Failure(FailureFor(start.Failure), start.Message, undo);
            }

            return AutoLockResult.Ok();
        }

        private static AutoLockFailure FailureFor(WatcherStartFailure failure)
        {
            switch (failure)
            {
                case WatcherStartFailure.HotkeyRegistrationFailed:
                    return AutoLockFailure.HotkeyUnavailable;
                case WatcherStartFailure.ReadyTimeout:
                case WatcherStartFailure.LidNotificationFailed:
                    return AutoLockFailure.WatcherNotReady;
                default:
                    return AutoLockFailure.WatcherStartFailed;
            }
        }

        // ------------------------------------------------------------- enable ---

        /// <summary>Switches automatic locking on, leaving the hotkey as it is.</summary>
        public AutoLockResult Enable()
        {
            AutoLockSettings desired = GetSettings();
            desired.Mode = LockMode.Automatic;
            return Apply(desired);
        }

        /// <summary>
        /// Switches the global hotkey on or off, leaving the lock mode as it is.
        /// Passing an empty binding with <paramref name="enabled"/> false also
        /// clears the stored key.
        /// </summary>
        public AutoLockResult SetHotkey(bool enabled, HotkeyBinding binding)
        {
            AutoLockSettings desired = GetSettings();
            desired.HotkeyEnabled = enabled;
            desired.Hotkey = binding ?? HotkeyBinding.Empty;
            return Apply(desired);
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
        /// Switches automatic locking off, leaving the hotkey as it is.
        ///
        /// If the hotkey is on, the helper stays: it is still needed, and killing
        /// it here would silently break a feature the user never touched.
        /// </summary>
        public AutoLockResult Disable()
        {
            AutoLockSettings desired = GetSettings();
            desired.Mode = LockMode.Manual;
            return Apply(desired);
        }

        /// <summary>
        /// Reaches the state where nothing is resident, and records it.
        ///
        /// Unlike <see cref="Apply"/> there is nothing to roll back to: this
        /// destination is always reachable and always safe, so every step is
        /// attempted even if an earlier one fails, and whatever could not be
        /// undone is reported rather than passed over.
        /// </summary>
        private AutoLockResult StandDown(AutoLockSettings desired)
        {
            var residue = new List<string>();
            bool stopped = StopWatcher(residue);
            bool autostartRemoved = RemoveAutostart(residue);

            try
            {
                _settings.Save(desired);
            }
            catch (Exception ex)
            {
                residue.Add("the saved settings were not updated: " + ex.Message);
                return new AutoLockResult(false, AutoLockFailure.SettingsWriteFailed,
                    "Could not save the settings: " + ex.Message, false, residue);
            }

            // A sign-in entry that survived is not a detail. It points at a
            // helper that will start again tomorrow for features that are now
            // off - and after an uninstall, at a program that no longer exists,
            // with nothing left to repair it. Reporting success here put that
            // outcome behind a residue string every caller ignored.
            if (!autostartRemoved)
                return new AutoLockResult(false, AutoLockFailure.AutostartFailed,
                    "The features are off, but the sign-in entry could not be removed, "
                    + "so something will still try to start at the next sign-in.",
                    false, residue);

            if (!stopped)
                return new AutoLockResult(false, AutoLockFailure.StopFailed,
                    "Nothing will start again at sign-in, but the helper "
                    + "did not stop in time.", false, residue);

            return AutoLockResult.Ok(residue);
        }

        /// <summary>
        /// Stops the helper and removes the sign-in entry, whichever features
        /// were keeping them alive, without recording a preference.
        ///
        /// Used by the uninstaller. It deliberately does not rewrite the
        /// settings: the preferences are about to stop mattering, and a
        /// reinstall finding the user's old choices intact is friendlier than
        /// one that silently reset them. Stopping the helper is what releases
        /// the hotkey registration, so nothing survives the uninstall holding a
        /// key combination hostage.
        /// </summary>
        public AutoLockResult Cleanup()
        {
            var residue = new List<string>();
            bool stopped = StopWatcher(residue);

            // The uninstaller is the caller that most needs this to be a real
            // failure rather than residue: {app} is about to be deleted, and an
            // entry left pointing into it would try to start a program that no
            // longer exists, forever, with no AFKLocker left to remove it.
            if (!RemoveAutostart(residue))
                return new AutoLockResult(false, AutoLockFailure.AutostartFailed,
                    "The sign-in entry could not be removed.", false, residue);

            if (!stopped)
                return new AutoLockResult(false, AutoLockFailure.StopFailed,
                    "The helper did not stop in time.", false, residue);

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
                    ? "a stale helper readiness signal is still present"
                    : "a helper process is still running");
                return false;
            }
            catch (Exception ex)
            {
                residue.Add("could not stop the helper: " + ex.Message);
                return false;
            }
        }

        /// <summary>Removes the sign-in entry. False when it is still there.</summary>
        private bool RemoveAutostart(List<string> residue)
        {
            try
            {
                _autostart.Unregister();
                return true;
            }
            catch (Exception ex)
            {
                residue.Add("the sign-in entry could not be removed: " + ex.Message);
                return false;
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
            // Read strictly, not through GetSettings. Reconciliation is the one
            // caller that both reads the settings and writes them back, so a
            // read that quietly became "the defaults" would let it erase the
            // configuration it was asked to restore. Changing nothing is always
            // recoverable; a silent teardown is not.
            AutoLockSettings stored;
            try
            {
                stored = _settings.Load().Clone();
            }
            catch (Exception ex)
            {
                return new AutoLockResult(false, AutoLockFailure.SettingsWriteFailed,
                    "The settings could not be read, so nothing was changed: " + ex.Message,
                    false, null);
            }

            AutoLockStatus status = GetStatus();
            if (status.IsConsistent) return AutoLockResult.Ok();

            // A hotkey switched on with nothing usable bound asks for a helper
            // that would have nothing to register. Switch the flag off rather
            // than start a process that cannot do the job it exists for.
            if (stored.HotkeyEnabled && (stored.Hotkey == null || !stored.Hotkey.IsUsable))
                stored.HotkeyEnabled = false;

            if (stored.RequiredFeatures == HelperFeatures.None)
                return StandDown(stored);

            if (!status.WatcherInstalled)
            {
                // Nothing can be repaired without the helper binary. Switch the
                // features off but KEEP the chosen key: "bound but not active"
                // is a state the product deliberately supports and shows, and
                // erasing somebody's binding because a file went missing would
                // be a second, unrelated loss.
                AutoLockResult disabled = StandDown(SwitchedOff(stored));
                return new AutoLockResult(false, AutoLockFailure.WatcherMissing,
                    "The background features were switched off: the helper program is missing.",
                    true, disabled.Residue);
            }

            // Something is missing. Re-applying the stored settings puts the
            // settings, the sign-in entry and the process back together - and
            // rewrites a sign-in entry that pointed somewhere else, which is the
            // whole reason a wrong entry is now a repairable state instead of an
            // accepted one.
            AutoLockResult repaired = Apply(stored);
            if (repaired.Success) return repaired;

            AutoLockResult fallback = StandDown(SwitchedOff(stored));
            var combined = new List<string>(repaired.Residue);
            foreach (string item in fallback.Residue) combined.Add(item);

            return new AutoLockResult(false, repaired.Failure,
                "The background features could not be restored and have been switched off: "
                + repaired.Message, true, combined);
        }

        /// <summary>
        /// The same settings with everything that needs a helper switched off,
        /// but the chosen hotkey remembered. Switching a feature off and
        /// forgetting the key the user picked are two different things, and only
        /// the first one was ever asked for.
        /// </summary>
        private static AutoLockSettings SwitchedOff(AutoLockSettings settings)
        {
            AutoLockSettings off = settings.Clone();
            off.Mode = LockMode.Manual;
            off.HotkeyEnabled = false;
            return off;
        }
    }
}
