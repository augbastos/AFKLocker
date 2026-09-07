using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
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

    /// <summary>
    /// Starting, stopping and checking the watcher process. An interface so the
    /// manager's logic can be tested without launching anything.
    /// </summary>
    public interface IWatcherProcess
    {
        bool IsRunning { get; }

        /// <summary>Launches the watcher if it is not already running.</summary>
        bool Start(string watcherPath);

        /// <summary>Asks a running watcher to exit; true when none is left.</summary>
        bool Stop(TimeSpan timeout);
    }

    /// <summary>
    /// Starts and stops the watcher process, and reports whether one is running.
    ///
    /// Coordination is by two named kernel objects in the session's own
    /// namespace (Local\), so each signed-in user gets their own watcher and
    /// switching users does not cross the wires.
    /// </summary>
    public sealed class WatcherController : IWatcherProcess
    {
        /// <summary>Held for the lifetime of a running watcher.</summary>
        public const string RunningMutexName = @"Local\AFKLocker.Watcher.Running";

        /// <summary>Signalled to ask a running watcher to exit.</summary>
        public const string StopEventName = @"Local\AFKLocker.Watcher.Stop";

        public const string WatcherFileName = "AFKLockerWatcher.exe";

        /// <summary>How long to wait for a launched watcher to register itself.</summary>
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);

        /// <summary>True when a watcher is running in this session.</summary>
        public bool IsRunning
        {
            get { return IsAnyRunning; }
        }

        /// <summary>True when a watcher is running in this session.</summary>
        public static bool IsAnyRunning
        {
            get
            {
                bool createdNew;
                // Opening the mutex is enough: if we created it, nobody held it.
                using (var mutex = new Mutex(false, RunningMutexName, out createdNew))
                {
                    return !createdNew;
                }
            }
        }

        /// <summary>Full path of the watcher that sits next to the running program.</summary>
        public static string DefaultWatcherPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WatcherFileName); }
        }

        /// <summary>
        /// Launches the watcher if it is not already running.
        /// </summary>
        /// <returns>False when the watcher executable is missing.</returns>
        public bool Start(string watcherPath)
        {
            if (string.IsNullOrEmpty(watcherPath)) throw new ArgumentException("watcherPath must not be empty", "watcherPath");
            if (!File.Exists(watcherPath)) return false;
            if (IsAnyRunning) return true;

            var startInfo = new ProcessStartInfo
            {
                FileName = watcherPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(watcherPath) ?? string.Empty
            };
            Process.Start(startInfo);

            // Process.Start returns as soon as the process exists, which is well
            // before it has started the runtime and claimed the mutex. Without
            // this wait, anything that checks the status immediately afterwards
            // - the setup window refreshes right after enabling - sees "not
            // running" and reports a failure that did not happen.
            return WaitUntilRunning(StartupTimeout);
        }

        private static bool WaitUntilRunning(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (IsAnyRunning) return true;
                Thread.Sleep(50);
            }
            return IsAnyRunning;
        }

        /// <summary>
        /// Asks a running watcher to exit and waits briefly for it to do so.
        /// </summary>
        /// <returns>True when no watcher is running afterwards.</returns>
        public bool Stop(TimeSpan timeout)
        {
            if (!IsAnyRunning) return true;

            try
            {
                using (var stop = EventWaitHandle.OpenExisting(StopEventName))
                    stop.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // No watcher listening. Nothing to stop.
                return !IsAnyRunning;
            }

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (!IsAnyRunning) return true;
                Thread.Sleep(50);
            }

            return !IsAnyRunning;
        }
    }

    /// <summary>What the setup window shows about automatic locking.</summary>
    public sealed class AutoLockStatus
    {
        public LockMode Mode { get; set; }
        public bool AutostartRegistered { get; set; }
        public bool WatcherRunning { get; set; }
        public bool WatcherInstalled { get; set; }
    }

    /// <summary>
    /// Turns automatic locking on and off: the setting, the autostart entry and
    /// the running watcher are kept consistent with each other.
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

        public AutoLockStatus GetStatus()
        {
            return new AutoLockStatus
            {
                Mode = _settings.Load().Mode,
                AutostartRegistered = _autostart.IsRegistered,
                WatcherRunning = _watcher.IsRunning,
                WatcherInstalled = !string.IsNullOrEmpty(_watcherPath) && File.Exists(_watcherPath)
            };
        }

        /// <summary>
        /// Switches to automatic locking: remembers the mode, registers the
        /// watcher to start at sign-in, and starts it now.
        /// </summary>
        /// <returns>False when the watcher executable is missing.</returns>
        public bool Enable()
        {
            if (string.IsNullOrEmpty(_watcherPath) || !File.Exists(_watcherPath))
                return false;

            _settings.Save(new AutoLockSettings { Mode = LockMode.Automatic });
            _autostart.Register("\"" + _watcherPath + "\"");
            return _watcher.Start(_watcherPath);
        }

        /// <summary>
        /// Switches back to manual: stops the watcher, removes the autostart
        /// entry, and records the mode. After this nothing of AFKLocker is
        /// resident.
        /// </summary>
        public bool Disable()
        {
            bool stopped = _watcher.Stop(StopTimeout);
            _autostart.Unregister();
            _settings.Save(new AutoLockSettings { Mode = LockMode.Manual });
            return stopped;
        }

        /// <summary>
        /// Removes every trace of automatic locking without recording a mode.
        /// Used by the uninstaller.
        /// </summary>
        public bool Cleanup()
        {
            bool stopped = _watcher.Stop(StopTimeout);
            _autostart.Unregister();
            return stopped;
        }
    }
}
