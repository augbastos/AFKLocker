using System;

namespace AFKLocker.Core
{
    /// <summary>
    /// The one thing AFKLocker does: lock the session, then put the screens out
    /// and hold them out.
    ///
    /// It exists so there is exactly one answer to "what happens when AFKLocker
    /// locks". Double-clicking the icon, pressing the global hotkey and closing
    /// the lid all arrive here, so none of them can drift into behaving
    /// differently from the others - which is the failure this project already
    /// shipped once, when manual locking quietly did not darken anything while
    /// automatic locking did.
    ///
    /// The lock happens first and unconditionally. Everything after it is about
    /// screens, and nothing after it can undo it.
    /// </summary>
    public static class LockAction
    {
        /// <summary>
        /// Locks, then starts holding the displays dark.
        ///
        /// Returns the blanker so the caller can decide how to keep it alive: a
        /// program that exists only to lock has to pump messages for it, while
        /// the helper already has a message loop running. Returns null when
        /// there is nothing to keep alive.
        /// </summary>
        /// <exception cref="Exception">
        /// Only from the lock itself. A screen that stays lit is not worth
        /// failing over, so display errors are swallowed here rather than
        /// dressed up as a failed lock.
        /// </exception>
        public static DisplayBlanker LockAndDarken(ISessionLocker locker,
            IDisplayController displays, IUserInputMonitor input)
        {
            if (locker == null) throw new ArgumentNullException("locker");

            locker.Lock();

            if (displays == null || input == null) return null;

            try
            {
                var blanker = new DisplayBlanker(displays, input);
                blanker.Start();
                return blanker;
            }
            catch (Exception)
            {
                // The session is locked, which is the guarantee. A machine that
                // locked but kept its screens on is still locked.
                return null;
            }
        }
    }
}
