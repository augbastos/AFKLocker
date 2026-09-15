using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AFKLocker.Core
{
    public interface IExecutionStateController
    {
        void PreventSystemSleep();
        void RestoreDefault();
    }

    /// <summary>
    /// Holds only the process-scoped execution request. AFKLocker no longer
    /// sets the user's normal sleep and hibernate timeouts to Never.
    /// </summary>
    public sealed class WindowsExecutionStateController : IExecutionStateController
    {
        [Flags]
        private enum ExecutionState : uint
        {
            SystemRequired = 0x00000001,
            Continuous = 0x80000000
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern ExecutionState SetThreadExecutionState(ExecutionState executionState);

        public void PreventSystemSleep()
        {
            ExecutionState result = SetThreadExecutionState(
                ExecutionState.Continuous | ExecutionState.SystemRequired);
            if (result == 0)
                throw new PowerConfigurationException(
                    "Windows refused the temporary keep-awake request.",
                    Marshal.GetLastWin32Error());
        }

        public void RestoreDefault()
        {
            SetThreadExecutionState(ExecutionState.Continuous);
        }
    }

    /// <summary>
    /// Makes lid-close harmless only for the lifetime of one AFK session.
    /// Original AC and battery values are written to a crash-recovery snapshot
    /// before either setting changes, and restored when input/unlock ends the
    /// mode. Normal display, sleep and hibernate timeouts are never written.
    /// </summary>
    public sealed class TemporaryPowerMode : IDisposable
    {
        private readonly IPowerConfiguration _power;
        private readonly IExecutionStateController _execution;
        private readonly string _snapshotPath;
        private readonly string _lockPath;
        private FileStream _sessionLock;
        private bool _entered;
        private bool _ownsPowerValues;
        private bool _disposed;

        public TemporaryPowerMode(IPowerConfiguration power, IExecutionStateController execution)
            : this(power, execution,
                Path.Combine(FileBackupStore.DefaultDirectory, "power-session.txt"))
        {
        }

        public TemporaryPowerMode(IPowerConfiguration power, IExecutionStateController execution,
            string snapshotPath)
        {
            if (power == null) throw new ArgumentNullException("power");
            if (execution == null) throw new ArgumentNullException("execution");
            if (string.IsNullOrEmpty(snapshotPath))
                throw new ArgumentException("snapshot path must not be empty", "snapshotPath");

            _power = power;
            _execution = execution;
            _snapshotPath = snapshotPath;
            _lockPath = snapshotPath + ".lock";
        }

        public void Enter()
        {
            if (_entered) return;

            AcquireSessionLock();
            try
            {
                // Only the lock owner writes shared power-plan values. A second
                // AFKLocker process still holds its own execution request, while
                // the first process remains responsible for restoring the plan.
                if (_ownsPowerValues)
                {
                    RestoreStaleSnapshot();
                    Guid scheme = _power.GetActiveScheme();
                    PowerBackup snapshot = CaptureOriginals(scheme);
                    if (snapshot.Count > 0)
                    {
                        AtomicFile.WriteAllText(_snapshotPath, snapshot.Serialize());
                        ApplyDoNothing(snapshot);
                    }
                }

                _execution.PreventSystemSleep();
                _entered = true;
            }
            catch
            {
                try
                {
                    if (_ownsPowerValues) RestoreStaleSnapshot();
                }
                finally
                {
                    ReleaseSessionLock();
                    _execution.RestoreDefault();
                }
                throw;
            }
        }

        private void AcquireSessionLock()
        {
            string directory = Path.GetDirectoryName(_snapshotPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            try
            {
                _sessionLock = new FileStream(_lockPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                _ownsPowerValues = true;
            }
            catch (IOException)
            {
                _sessionLock = null;
                _ownsPowerValues = false;
            }
        }

        private PowerBackup CaptureOriginals(Guid scheme)
        {
            var snapshot = new PowerBackup
            {
                Scheme = scheme,
                SchemeName = SafeSchemeName(scheme)
            };

            Capture(snapshot, PowerSettings.LidCloseAc);
            Capture(snapshot, PowerSettings.LidCloseDc);
            return snapshot;
        }

        private string SafeSchemeName(Guid scheme)
        {
            try
            {
                return _power.GetSchemeName(scheme);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void Capture(PowerBackup snapshot, PowerSettingRef setting)
        {
            try
            {
                uint current = _power.ReadValue(
                    snapshot.Scheme, setting.Subgroup, setting.Setting, setting.Source);
                snapshot.RecordOriginal(setting.Key, current);
            }
            catch (PowerSettingNotFoundException)
            {
                // Desktops and a few firmware implementations expose no lid
                // setting. The execution request still prevents idle sleep.
            }
        }

        private void ApplyDoNothing(PowerBackup snapshot)
        {
            foreach (KeyValuePair<string, uint> entry in snapshot.Values)
            {
                PowerSettingRef setting = PowerSettings.ByKey(entry.Key);
                if (!IsLidSetting(setting) || entry.Value == (uint)LidAction.DoNothing) continue;
                _power.WriteValue(snapshot.Scheme, setting.Subgroup, setting.Setting,
                    setting.Source, (uint)LidAction.DoNothing);
            }
            _power.ApplyScheme(snapshot.Scheme);
        }

        private void RestoreStaleSnapshot()
        {
            if (!File.Exists(_snapshotPath)) return;

            PowerBackup snapshot = PowerBackup.Deserialize(
                File.ReadAllText(_snapshotPath, Encoding.UTF8));
            var failures = new List<string>();
            bool wrote = false;

            foreach (KeyValuePair<string, uint> entry in snapshot.Values)
            {
                PowerSettingRef setting = PowerSettings.ByKey(entry.Key);
                if (!IsLidSetting(setting)) continue;

                try
                {
                    _power.WriteValue(snapshot.Scheme, setting.Subgroup, setting.Setting,
                        setting.Source, entry.Value);
                    wrote = true;
                }
                catch (Exception ex)
                {
                    failures.Add(setting.DisplayName + ": " + ex.Message);
                }
            }

            if (wrote)
            {
                try
                {
                    _power.ApplyScheme(snapshot.Scheme);
                }
                catch (Exception ex)
                {
                    failures.Add("activate restored power plan: " + ex.Message);
                }
            }

            if (failures.Count > 0)
                throw new PowerConfigurationException(
                    "Temporary lid settings could not be fully restored: " +
                    string.Join("; ", failures.ToArray()));

            File.Delete(_snapshotPath);
        }

        private static bool IsLidSetting(PowerSettingRef setting)
        {
            return setting != null &&
                (string.Equals(setting.Key, PowerSettings.LidCloseAc.Key,
                    StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(setting.Key, PowerSettings.LidCloseDc.Key,
                    StringComparison.OrdinalIgnoreCase));
        }

        private void ReleaseSessionLock()
        {
            if (_sessionLock != null)
            {
                _sessionLock.Dispose();
                _sessionLock = null;
            }
            _ownsPowerValues = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Exception restoreError = null;
            try
            {
                // If this was a secondary activation and the original owner
                // crashed, the file lock is now available and this process can
                // recover the snapshot before it exits.
                if (!_ownsPowerValues) AcquireSessionLock();
                if (_ownsPowerValues) RestoreStaleSnapshot();
            }
            catch (Exception ex)
            {
                restoreError = ex;
            }
            finally
            {
                ReleaseSessionLock();
                if (_entered) _execution.RestoreDefault();
                _entered = false;
            }

            if (restoreError != null) throw restoreError;
        }
    }
}
