using System;

namespace AFKLocker.Core
{
    /// <summary>
    /// The one thing AFKLocker does: enter a temporary AFK session.
    ///
    /// It exists so there is exactly one answer to "what happens when AFKLocker
    /// locks". Double-clicking the icon, pressing the global hotkey and closing
    /// the lid all arrive here, so none of them can drift into behaving
    /// differently from the others - which is the failure this project already
    /// shipped once, when manual locking quietly did not darken anything while
    /// automatic locking did.
    ///
    /// The temporary lid policy is armed first so closing cannot win a race
    /// with startup; locking and the first display-off request follow
    /// immediately. Everything is restored when the AFK session finishes.
    /// </summary>
    public static class LockAction
    {
        /// <summary>
        /// Enters temporary power mode, locks, then holds the displays dark.
        ///
        /// Returns the blanker so the caller can decide how to keep it alive: a
        /// program that exists only to lock has to pump messages for it, while
        /// the helper already has a message loop running. Returns null when
        /// there is nothing to keep alive.
        /// </summary>
        /// <exception cref="Exception">The AFK session could not be armed safely.</exception>
        public static AfkSession EnterAfkMode(ISessionLocker locker,
            IDisplayController displays, IUserInputMonitor input,
            TemporaryPowerMode power, IKeyboardLightingSession lighting)
        {
            return AfkSession.Start(locker, displays, input, power, lighting, true);
        }
    }
}
