using System;
using System.IO;

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
        private readonly AfkRecovery _recovery;
        private bool _finished;
        private bool _disposed;

        private AfkSession(DisplayBlanker screens, TemporaryPowerMode power,
            IKeyboardLightingSession lighting, AfkRecovery recovery)
        {
            _screens = screens;
            _power = power;
            _lighting = lighting;
            _recovery = recovery;
        }

        public event EventHandler Finished;

        public bool HasFinished
        {
            get { return _finished; }
        }

        /// <summary>Non-fatal reason keyboard lighting could not be controlled.</summary>
        public string LightingError { get; private set; }

        /// <summary>Reason temporary Windows power state could not be fully restored.</summary>
        public string RestoreError { get; private set; }

        /// <param name="recovery">Null when there is no AFKLocker.exe to run at sign-in.</param>
        public static AfkSession Start(ISessionLocker locker, IDisplayController displays,
            IUserInputMonitor input, TemporaryPowerMode power,
            IKeyboardLightingSession lighting, AfkRecovery recovery, bool lockSession)
        {
            if (locker == null) throw new ArgumentNullException("locker");
            if (displays == null) throw new ArgumentNullException("displays");
            if (input == null) throw new ArgumentNullException("input");
            if (power == null) throw new ArgumentNullException("power");

            lighting = lighting ?? new NoKeyboardLightingSession();
            var screens = new DisplayBlanker(displays, input);
            var session = new AfkSession(screens, power, lighting, recovery);

            try
            {
                // Before anything changes, so there is no moment where a setting
                // is changed and nothing would put it back after a crash. Failing
                // to arm is not a reason to refuse AFK: the next activation still
                // restores a stale snapshot first.
                if (recovery != null)
                {
                    try
                    {
                        recovery.Arm();
                    }
                    catch (Exception)
                    {
                    }
                }

                // Power first: from this point forward closing the lid cannot
                // win a race with setup. Lock and issue the first display-off
                // request before talking to optional keyboard firmware, which
                // can be slow or unavailable.
                power.Enter();
                if (lockSession) locker.Lock();

                screens.Finished += session.OnScreensFinished;
                if (!screens.Start())
                    throw new InvalidOperationException(
                        "Windows would not provide the display and lid notifications AFK mode needs.");

                // Lighting is deliberately non-fatal: a firmware mismatch must
                // not stop the lock and display guarantees.
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

            // Judged by what is still saved, not by which call threw: an
            // overlapping session may own the lid values, and lighting can
            // report an error after it was restored.
            if (_recovery != null && RestoreError == null &&
                !_power.HasPendingRestore && !_lighting.HasPendingRestore)
            {
                try
                {
                    _recovery.Disarm();
                }
                catch (Exception)
                {
                }
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

    /// <summary>
    /// Undoes a session that never reached its own cleanup.
    ///
    /// Restoring on exit covers input and unlock. It does not cover a killed
    /// process, a crash, or Windows restarting for an update overnight while AFK
    /// is active - and then the lid would stay set to "Do nothing" until the next
    /// AFK session, which may never come. So a session arms a one-shot RunOnce
    /// entry before it changes anything and removes it once everything is back.
    /// If it never gets that far, Windows runs "AFKLocker.exe --recover" at the
    /// next sign-in.
    /// </summary>
    public sealed class AfkRecovery
    {
        public const string Argument = "--recover";
        public const string EntryName = "AFKLocker recovery";

        private readonly IAutostartRegistry _registry;
        private readonly string _command;

        public AfkRecovery(IAutostartRegistry registry, string executablePath)
        {
            if (registry == null) throw new ArgumentNullException("registry");
            if (string.IsNullOrEmpty(executablePath))
                throw new ArgumentException("executable path must not be empty", "executablePath");
            _registry = registry;
            _command = "\"" + executablePath + "\" " + Argument;
        }

        /// <summary>Null when the executable is not there to run.</summary>
        public static AfkRecovery ForExecutable(string executablePath)
        {
            return File.Exists(executablePath)
                ? new AfkRecovery(RunKeyAutostartRegistry.RunOnce(EntryName), executablePath)
                : null;
        }

        public void Arm()
        {
            _registry.Register(_command);
        }

        public void Disarm()
        {
            _registry.Unregister();
        }
    }
}
