using System;
using System.Collections.Generic;
using AFKLocker.Core;

namespace AFKLocker.Tests
{
    internal sealed class FakeSessionState : ISessionState
    {
        public bool IsLocked { get; set; }
    }

    /// <summary>
    /// Settings store that can be told to fail, so the rollback paths are
    /// reachable from a test rather than only in the field.
    /// </summary>
    internal sealed class FakeSettingsStore : ISettingsStore
    {
        private string _stored;

        public int SaveCount;
        public int LoadCount;

        /// <summary>When set, Save throws with this message.</summary>
        public string FailSaveWith;

        /// <summary>When set, Load throws with this message.</summary>
        public string FailLoadWith;

        /// <summary>Fail only from the Nth save onwards (1 = the first). 0 = every save.</summary>
        public int FailSaveFromCall;

        /// <summary>True when nothing was ever written - a fresh 0.1.x machine.</summary>
        public bool IsEmpty
        {
            get { return _stored == null; }
        }

        public AutoLockSettings Load()
        {
            LoadCount++;
            if (FailLoadWith != null) throw new InvalidOperationException(FailLoadWith);
            // Round-trips through the real serializer so the tests exercise it.
            return _stored == null ? new AutoLockSettings() : AutoLockSettings.Deserialize(_stored);
        }

        public void Save(AutoLockSettings settings)
        {
            SaveCount++;
            if (FailSaveWith != null && SaveCount >= Math.Max(FailSaveFromCall, 1))
                throw new InvalidOperationException(FailSaveWith);
            _stored = settings.Serialize();
        }

        /// <summary>Injects raw file content, including damaged content.</summary>
        public void SetRaw(string content)
        {
            _stored = content;
        }
    }

    internal sealed class FakeAutostartRegistry : IAutostartRegistry
    {
        private string _command;

        public int RegisterCount;
        public int UnregisterCount;

        /// <summary>When set, Register throws with this message.</summary>
        public string FailRegisterWith;

        /// <summary>When set, Unregister throws with this message.</summary>
        public string FailUnregisterWith;

        /// <summary>Fail only from the Nth register onwards (used to break rollback).</summary>
        public int FailRegisterFromCall;

        public bool IsRegistered
        {
            get { return _command != null; }
        }

        public string RegisteredCommand
        {
            get { return _command; }
        }

        public void Register(string command)
        {
            RegisterCount++;
            if (FailRegisterWith != null && RegisterCount >= Math.Max(FailRegisterFromCall, 1))
                throw new InvalidOperationException(FailRegisterWith);
            _command = command;
        }

        public void Unregister()
        {
            UnregisterCount++;
            if (FailUnregisterWith != null) throw new InvalidOperationException(FailUnregisterWith);
            _command = null;
        }

        /// <summary>Sets the value directly, bypassing the failure switches.</summary>
        public void Preset(string command)
        {
            _command = command;
        }
    }

    /// <summary>
    /// Watcher process that can be driven through every outcome: ready,
    /// started-but-never-ready, exited early, refusing to stop.
    /// </summary>
    internal sealed class FakeWatcherProcess : IWatcherProcess
    {
        public WatcherState State = WatcherState.NotRunning;
        public int StartCount;
        public int StopCount;
        public string LastStartedPath;

        /// <summary>What Start should return. Defaults to success.</summary>
        public WatcherStartResult StartResult;

        /// <summary>When set, Start throws with this message.</summary>
        public string FailStartWith;

        /// <summary>When set, Stop throws with this message.</summary>
        public string FailStopWith;

        /// <summary>When true, Stop reports that the watcher is still running.</summary>
        public bool RefuseToStop;

        public WatcherState GetState()
        {
            return State;
        }

        public WatcherStartResult Start(string watcherPath)
        {
            StartCount++;
            LastStartedPath = watcherPath;

            if (FailStartWith != null) throw new InvalidOperationException(FailStartWith);

            WatcherStartResult result = StartResult ?? WatcherStartResult.Ok();
            // A start that succeeds leaves a ready watcher; one that fails may
            // still have left a process behind, which is why the manager stops
            // it during rollback.
            State = result.Success ? WatcherState.Ready : WatcherState.Starting;
            return result;
        }

        public bool Stop(TimeSpan timeout)
        {
            StopCount++;
            if (FailStopWith != null) throw new InvalidOperationException(FailStopWith);
            if (RefuseToStop) return false;
            State = WatcherState.NotRunning;
            return true;
        }
    }

    /// <summary>Records display-off requests instead of dimming the screen.</summary>
    internal sealed class FakeDisplayController : IDisplayController
    {
        public int TurnOffCount;

        public void TurnOff()
        {
            TurnOffCount++;
        }
    }

    /// <summary>Feeds lid events to the policy without a laptop.</summary>
    internal sealed class FakeLidEventProvider : ILidEventProvider
    {
        public bool StartSucceeds = true;
        public bool Started;
        public bool Stopped;

        public event EventHandler<LidStateEventArgs> LidStateChanged;

        public bool Start()
        {
            Started = StartSucceeds;
            return StartSucceeds;
        }

        public void Stop()
        {
            Stopped = true;
        }

        public void Emit(LidState state)
        {
            EventHandler<LidStateEventArgs> handler = LidStateChanged;
            if (handler != null) handler(this, new LidStateEventArgs(state));
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
