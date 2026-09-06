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
        /// <summary>The user double-clicks AFKLocker. Nothing stays resident. The default.</summary>
        Manual,

        /// <summary>A small watcher locks the session when the lid closes.</summary>
        Automatic
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
