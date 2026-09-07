using System;
using System.Collections.Generic;
using AFKLocker.Core;

namespace AFKLocker.Tests
{
    internal sealed class FakeSessionState : ISessionState
    {
        public bool IsLocked { get; set; }
    }

    internal sealed class FakeSettingsStore : ISettingsStore
    {
        private string _stored;

        public int SaveCount;

        /// <summary>True when nothing was ever written - a fresh 0.1.x machine.</summary>
        public bool IsEmpty
        {
            get { return _stored == null; }
        }

        public AutoLockSettings Load()
        {
            // Round-trips through the real serializer so the tests exercise it.
            return _stored == null ? new AutoLockSettings() : AutoLockSettings.Deserialize(_stored);
        }

        public void Save(AutoLockSettings settings)
        {
            SaveCount++;
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
            _command = command;
        }

        public void Unregister()
        {
            UnregisterCount++;
            _command = null;
        }
    }

    internal sealed class FakeWatcherProcess : IWatcherProcess
    {
        public bool IsRunning { get; set; }
        public int StartCount;
        public int StopCount;
        public bool RefuseToStop;
        public string LastStartedPath;

        public bool Start(string watcherPath)
        {
            StartCount++;
            LastStartedPath = watcherPath;
            IsRunning = true;
            return true;
        }

        public bool Stop(TimeSpan timeout)
        {
            StopCount++;
            if (RefuseToStop) return false;
            IsRunning = false;
            return true;
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
