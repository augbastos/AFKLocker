using System;
using Microsoft.Win32;
using AFKLocker.Core;

namespace AFKLocker.Watcher
{
    /// <summary>
    /// Tracks whether this session is locked, by listening to the session
    /// switch notifications Windows already sends.
    ///
    /// The watcher starts when the user signs in, which means the session is
    /// unlocked at that point - so starting from "unlocked" is correct rather
    /// than an assumption. Locking an already-locked session is harmless
    /// anyway; this only exists so the watcher can avoid pointless work and
    /// report honestly about what it did.
    /// </summary>
    internal sealed class SessionStateTracker : ISessionState, IDisposable
    {
        private bool _isLocked;
        private bool _subscribed;

        public bool IsLocked
        {
            get { return _isLocked; }
        }

        public void Start()
        {
            if (_subscribed) return;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            _subscribed = true;
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock:
                case SessionSwitchReason.ConsoleDisconnect:
                case SessionSwitchReason.RemoteDisconnect:
                    _isLocked = true;
                    break;

                case SessionSwitchReason.SessionUnlock:
                case SessionSwitchReason.SessionLogon:
                case SessionSwitchReason.ConsoleConnect:
                case SessionSwitchReason.RemoteConnect:
                    _isLocked = false;
                    break;
            }
        }

        public void Dispose()
        {
            if (!_subscribed) return;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _subscribed = false;
        }
    }
}
