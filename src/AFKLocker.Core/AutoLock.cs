using System;

namespace AFKLocker.Core
{
    /// <summary>
    /// Physical lid position, as reported by Windows through
    /// GUID_LIDSWITCH_STATE_CHANGE. The values match the DWORD Windows sends.
    /// </summary>
    public enum LidState
    {
        Closed = 0,
        Opened = 1
    }

    /// <summary>How AFKLocker locks the session.</summary>
    public enum LockMode
    {
        /// <summary>The user double-clicks AFKLocker. The default.</summary>
        Manual,

        /// <summary>The background helper locks the session when the lid closes.</summary>
        Automatic
    }

    /// <summary>
    /// The jobs the background helper can be asked to do.
    ///
    /// This exists because the lock mode stopped being the thing that decides
    /// whether a process is resident. Manual used to mean "no helper, ever";
    /// with a global hotkey it can mean "a helper, but only to wait for one
    /// key". Deriving residency from the features actually switched on keeps
    /// that from becoming a special case scattered through the lifecycle.
    /// </summary>
    [Flags]
    public enum HelperFeatures
    {
        /// <summary>Nothing is switched on, so no helper should exist at all.</summary>
        None = 0,

        /// <summary>Lock when the lid closes. Automatic mode.</summary>
        LidLock = 1,

        /// <summary>Lock when the registered key combination is pressed.</summary>
        GlobalHotkey = 2
    }

    public sealed class LidStateEventArgs : EventArgs
    {
        public LidState State { get; private set; }

        public LidStateEventArgs(LidState state)
        {
            State = state;
        }
    }

    /// <summary>
    /// Source of lid open/close events.
    ///
    /// An interface so the policy can be tested by feeding it lid events, with
    /// no laptop and no lid involved.
    /// </summary>
    public interface ILidEventProvider : IDisposable
    {
        event EventHandler<LidStateEventArgs> LidStateChanged;

        /// <summary>
        /// Begins listening. Returns false when this machine cannot deliver lid
        /// events at all - a desktop, or a device Windows has no lid for.
        /// </summary>
        bool Start();

        void Stop();
    }

    /// <summary>Whether the interactive session is currently locked.</summary>
    public interface ISessionState
    {
        bool IsLocked { get; }
    }

    /// <summary>What the policy did with one lid event, and why.</summary>
    public enum AutoLockDecision
    {
        /// <summary>The session was locked.</summary>
        Locked,

        /// <summary>
        /// The very first event only reports the current lid position, not a
        /// change, so it is never acted on.
        /// </summary>
        IgnoredInitialState,

        /// <summary>Opening the lid never unlocks anything.</summary>
        IgnoredLidOpened,

        /// <summary>The lid was already closed; Windows can repeat the event.</summary>
        IgnoredAlreadyClosed,

        /// <summary>The session was already locked, so there was nothing to do.</summary>
        IgnoredSessionAlreadyLocked
    }

    /// <summary>Something the user should be told about automatic locking.</summary>
    public enum AutoLockWarning
    {
        None,

        /// <summary>
        /// Windows reports no lid device, so lid events will never arrive and
        /// automatic locking cannot fire. Usually the ACPI Lid device has been
        /// disabled - a common trick for stopping a laptop sleeping on lid
        /// close, which this makes redundant and actively harmful.
        /// </summary>
        NoLidReported,

        /// <summary>
        /// Locking will work, but Windows may sleep afterwards on battery
        /// because the battery settings were left alone.
        /// </summary>
        BatteryMaySleep
    }

    /// <summary>
    /// Works out what is worth warning about when automatic locking is on.
    ///
    /// This exists because "the watcher is running" is not the same as "closing
    /// the lid will lock". A machine whose lid device is disabled reports a
    /// perfectly healthy watcher and then does nothing at all, which is the
    /// worst kind of failure: silent, and indistinguishable from working.
    /// </summary>
    public static class AutoLockAdvisor
    {
        public static AutoLockWarning Evaluate(LockMode mode, PowerSnapshot snapshot)
        {
            if (mode != LockMode.Automatic || snapshot == null)
                return AutoLockWarning.None;

            SystemCapabilities caps = snapshot.Capabilities;
            if (caps != null && !caps.LidPresent)
                return AutoLockWarning.NoLidReported;

            SettingValue lid = snapshot[PowerSettings.LidCloseDc];
            SettingValue sleep = snapshot[PowerSettings.SleepDc];
            bool batteryKeepsRunning =
                (!lid.IsPresent || lid.Is((uint)LidAction.DoNothing)) &&
                (!sleep.IsPresent || sleep.Is(0));

            return batteryKeepsRunning ? AutoLockWarning.None : AutoLockWarning.BatteryMaySleep;
        }

        /// <summary>The sentence to show, or null when there is nothing to say.</summary>
        public static string Describe(AutoLockWarning warning)
        {
            switch (warning)
            {
                case AutoLockWarning.NoLidReported:
                    return "This machine does not report lid state to Windows, so closing the lid "
                           + "will not lock it. Check whether the \"ACPI Lid\" device is disabled "
                           + "in Device Manager (System devices) and enable it.";

                case AutoLockWarning.BatteryMaySleep:
                    return "Automatic lock will still work on battery, but Windows may sleep after "
                           + "the lid closes unless you also configure battery above.";

                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Decides whether a lid event should lock the session.
    ///
    /// Pure logic with no Windows calls, which is the point: every awkward case
    /// - the first event, repeated events, opening the lid, a session that is
    /// already locked - is decided here and can be tested directly.
    /// </summary>
    public sealed class AutoLockPolicy
    {
        private readonly ISessionLocker _locker;
        private readonly ISessionState _session;

        private bool _sawFirstEvent;
        private LidState _lastState;

        public AutoLockPolicy(ISessionLocker locker, ISessionState session)
        {
            if (locker == null) throw new ArgumentNullException("locker");
            if (session == null) throw new ArgumentNullException("session");
            _locker = locker;
            _session = session;
        }

        /// <summary>Number of times this policy has locked the session.</summary>
        public int LockCount { get; private set; }

        public AutoLockDecision Handle(LidState state)
        {
            // Windows delivers the current lid position as soon as the watcher
            // registers. That is a state, not a transition. Acting on it would
            // lock the session of someone working on an external monitor with
            // the laptop docked and shut - which is exactly the setup where
            // that would be most unwelcome.
            if (!_sawFirstEvent)
            {
                _sawFirstEvent = true;
                _lastState = state;
                return AutoLockDecision.IgnoredInitialState;
            }

            if (state == LidState.Opened)
            {
                // Opening the lid must never unlock. Windows stays on the lock
                // screen and the user signs in normally.
                _lastState = state;
                return AutoLockDecision.IgnoredLidOpened;
            }

            if (_lastState == LidState.Closed)
                return AutoLockDecision.IgnoredAlreadyClosed;

            _lastState = LidState.Closed;

            if (_session.IsLocked)
                return AutoLockDecision.IgnoredSessionAlreadyLocked;

            _locker.Lock();
            LockCount++;
            return AutoLockDecision.Locked;
        }

        /// <summary>
        /// Forgets what it has seen, so the next event is treated as an initial
        /// state again. Used after resume, when the lid position observed
        /// before suspending may no longer be true.
        /// </summary>
        public void Reset()
        {
            _sawFirstEvent = false;
        }
    }
}
