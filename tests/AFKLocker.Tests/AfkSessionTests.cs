using System;
using System.IO;
using AFKLocker.Core;

namespace AFKLocker.Tests
{
    internal static class AfkSessionTests
    {
        private sealed class FakeExecutionState : IExecutionStateController
        {
            public void PreventSystemSleep() { }
            public void RestoreDefault() { }
        }

        private sealed class FakeInputMonitor : IUserInputMonitor
        {
            public uint LastInputTick { get { return 0; } }
        }

        private sealed class FakeLightingSession : IKeyboardLightingSession
        {
            public bool Pending;
            public bool HasPendingRestore { get { return Pending; } }
            public void Enter() { }
            public void Dispose() { }
        }

        private static FakePowerConfiguration StockPower()
        {
            var power = new FakePowerConfiguration();
            power.Set(PowerSettings.LidCloseAc, (uint)LidAction.Sleep);
            power.Set(PowerSettings.LidCloseDc, (uint)LidAction.Hibernate);
            return power;
        }

        private static string NewSnapshotPath()
        {
            return Path.Combine(Path.GetTempPath(),
                "afklocker-session-test-" + Guid.NewGuid().ToString("N") + ".txt");
        }

        private static void Cleanup(string snapshotPath)
        {
            foreach (string path in new[] { snapshotPath, snapshotPath + ".lock" })
            {
                try { File.Delete(path); } catch (IOException) { }
            }
        }

        private static AfkSession Start(FakeSessionLocker locker, FakePowerConfiguration power,
            string snapshotPath, IKeyboardLightingSession lighting, AfkRecovery recovery, bool lockSession)
        {
            return AfkSession.Start(locker, new FakeDisplayController(), new FakeInputMonitor(),
                new TemporaryPowerMode(power, new FakeExecutionState(), snapshotPath),
                lighting, recovery, lockSession);
        }

        [Test("The PC is still locked when Windows refuses the temporary power change")]
        private static void LocksEvenWhenPowerRefuses()
        {
            string path = NewSnapshotPath();
            try
            {
                FakePowerConfiguration power = StockPower();
                power.WriteFailsWithErrorCode = 5;
                var locker = new FakeSessionLocker();

                Assert.Throws<PowerConfigurationException>(
                    () => Start(locker, power, path, null, null, true), "the failure is still reported");

                Assert.Equal(1, locker.LockCount, "but the lock went through first");
            }
            finally
            {
                Cleanup(path);
            }
        }

        [Test("The lid path locks nothing itself and leaves power alone")]
        private static void LidPathLeavesPowerAlone()
        {
            string path = NewSnapshotPath();
            try
            {
                FakePowerConfiguration power = StockPower();
                var locker = new FakeSessionLocker();

                using (Start(locker, power, path, null, null, false))
                {
                    Assert.Equal(0, locker.LockCount, "the helper already locked");
                    Assert.Equal(0, power.Writes.Count, "no temporary power mode on the lid path");
                    Assert.False(File.Exists(path), "and no snapshot to restore");
                }
            }
            finally
            {
                Cleanup(path);
            }
        }

        [Test("Manual and hotkey sessions lock once and enter the temporary power mode")]
        private static void LockingPathEntersPowerMode()
        {
            string path = NewSnapshotPath();
            try
            {
                FakePowerConfiguration power = StockPower();
                var locker = new FakeSessionLocker();

                using (Start(locker, power, path, null, null, true))
                {
                    Assert.Equal(1, locker.LockCount, "locked once");
                    Assert.Equal((uint)LidAction.DoNothing, power.Get(PowerSettings.LidCloseDc),
                        "lid does nothing while AFK, on battery too");
                }

                Assert.Equal((uint)LidAction.Hibernate, power.Get(PowerSettings.LidCloseDc), "restored");
            }
            finally
            {
                Cleanup(path);
            }
        }

        [Test("The sign-in repair stays armed while keyboard lighting is still pending")]
        private static void RecoveryStaysArmedWhileLightingPending()
        {
            string path = NewSnapshotPath();
            try
            {
                var registry = new FakeAutostartRegistry();
                var lighting = new FakeLightingSession { Pending = true };

                Start(new FakeSessionLocker(), StockPower(), path, lighting,
                    new AfkRecovery(registry, @"C:\Tools\AFKLocker.exe"), true).Dispose();

                Assert.Equal(1, registry.RegisterCount, "armed before anything changed");
                Assert.Equal(0, registry.UnregisterCount, "not removed while lighting is not back");
            }
            finally
            {
                Cleanup(path);
            }
        }

        [Test("The sign-in repair is removed once everything is back")]
        private static void RecoveryIsRemovedWhenNothingIsPending()
        {
            string path = NewSnapshotPath();
            try
            {
                var registry = new FakeAutostartRegistry();

                Start(new FakeSessionLocker(), StockPower(), path, new FakeLightingSession(),
                    new AfkRecovery(registry, @"C:\Tools\AFKLocker.exe"), true).Dispose();

                Assert.Equal(1, registry.UnregisterCount, "removed after a clean restore");
            }
            finally
            {
                Cleanup(path);
            }
        }
    }
}
