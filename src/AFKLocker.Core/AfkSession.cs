using System;

namespace AFKLocker.Core
{
    /// <summary>
    /// Owns every temporary part of one AFK activation. Disposal is the single
    /// path that restores keyboard lighting and the user's power behaviour.
    /// </summary>
    public sealed class AfkSession : IDisposable
    {
        private readonly DisplayBlanker _screens;
        private readonly TemporaryPowerMode _power;
        private readonly IKeyboardLightingSession _lighting;
        private bool _finished;
        private bool _disposed;

        private AfkSession(DisplayBlanker screens, TemporaryPowerMode power,
            IKeyboardLightingSession lighting)
        {
            _screens = screens;
            _power = power;
            _lighting = lighting;
        }

        public event EventHandler Finished;

        public bool HasFinished
        {
            get { return _finished; }
        }

        /// <summary>Non-fatal reason the Acer keyboard could not be controlled.</summary>
        public string LightingError { get; private set; }

        /// <summary>Reason temporary Windows power state could not be fully restored.</summary>
        public string RestoreError { get; private set; }

        public static AfkSession Start(ISessionLocker locker, IDisplayController displays,
            IUserInputMonitor input, TemporaryPowerMode power,
            IKeyboardLightingSession lighting, bool lockSession)
        {
            if (locker == null) throw new ArgumentNullException("locker");
            if (displays == null) throw new ArgumentNullException("displays");
            if (input == null) throw new ArgumentNullException("input");
            if (power == null) throw new ArgumentNullException("power");

            lighting = lighting ?? new NoKeyboardLightingSession();
            var screens = new DisplayBlanker(displays, input);
            var session = new AfkSession(screens, power, lighting);

            try
            {
                // Power first: from this point forward closing the lid cannot
                // win a race with setup. Lock and issue the first display-off
                // request before talking to optional OEM firmware, which can
                // be slow or unavailable on a particular Acer model.
                power.Enter();
                if (lockSession) locker.Lock();

                screens.Finished += session.OnScreensFinished;
                if (!screens.Start())
                    throw new InvalidOperationException(
                        "Windows would not provide the display and lid notifications AFK mode needs.");

                // Lighting is deliberately non-fatal; an Acer firmware
                // mismatch must not stop the lock/display guarantees.
                try
                {
                    lighting.Enter();
                }
                catch (Exception ex)
                {
                    session.LightingError = ex.Message;
                }

                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        private void OnScreensFinished(object sender, EventArgs e)
        {
            Finish();
        }

        private void Finish()
        {
            if (_finished) return;
            _finished = true;

            // Restore Windows power behaviour first. Keyboard firmware work
            // can take a moment and must not extend the temporary lid policy
            // into the user's normal session.
            try
            {
                _power.Dispose();
            }
            catch (Exception ex)
            {
                RestoreError = ex.Message;
            }

            try
            {
                _lighting.Dispose();
            }
            catch (Exception ex)
            {
                if (LightingError == null) LightingError = ex.Message;
            }

            EventHandler handler = Finished;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _screens.Finished -= OnScreensFinished;
            _screens.Dispose();
            Finish();
        }
    }
}
