using System;
using System.IO;
using AFKLocker.Core;

namespace AFKLocker.Tests
{
    internal static class TemporaryPowerModeTests
    {
        private sealed class FakeExecutionState : IExecutionStateController
        {
            public int PreventCount;
            public int RestoreCount;

            public void PreventSystemSleep() { PreventCount++; }
            public void RestoreDefault() { RestoreCount++; }
        }

        private static FakePowerConfiguration StockPower()
        {
            var power = new FakePowerConfiguration();
            power.Set(PowerSettings.LidCloseAc, (uint)LidAction.Sleep);
            power.Set(PowerSettings.LidCloseDc, (uint)LidAction.Hibernate);
            power.Set(PowerSettings.SleepAc, 900);
            power.Set(PowerSettings.SleepDc, 600);
            return power;
        }

        private static string NewSnapshotPath()
        {
            return Path.Combine(Path.GetTempPath(),
                "afklocker-power-session-" + Guid.NewGuid().ToString("N") + ".txt");
        }

        [Test("AFK changes only lid-close and holds a process-scoped sleep request")]
        private static void EnterIsTemporaryAndNarrow()
        {
            FakePowerConfiguration power = StockPower();
            var execution = new FakeExecutionState();
            var mode = new TemporaryPowerMode(power, execution, NewSnapshotPath());

            mode.Enter();

            Assert.Equal((uint)LidAction.DoNothing, power.Get(PowerSettings.LidCloseAc), "AC lid");
            Assert.Equal((uint)LidAction.DoNothing, power.Get(PowerSettings.LidCloseDc), "DC lid");
            Assert.Equal(900u, power.Get(PowerSettings.SleepAc), "normal AC idle timeout untouched");
            Assert.Equal(600u, power.Get(PowerSettings.SleepDc), "normal DC idle timeout untouched");
            Assert.Equal(1, execution.PreventCount, "one execution request");
        }

        [Test("leaving AFK restores both lid values and normal execution state")]
        private static void DisposeRestoresEverything()
        {
            FakePowerConfiguration power = StockPower();
            var execution = new FakeExecutionState();

            using (var mode = new TemporaryPowerMode(power, execution, NewSnapshotPath()))
            {
                mode.Enter();
            }

            Assert.Equal((uint)LidAction.Sleep, power.Get(PowerSettings.LidCloseAc), "AC restored");
            Assert.Equal((uint)LidAction.Hibernate, power.Get(PowerSettings.LidCloseDc), "DC restored");
            Assert.Equal(1, execution.RestoreCount, "execution state restored");
        }

        [Test("a missing battery lid setting does not prevent plugged-in AFK mode")]
        private static void MissingDcIsAllowed()
        {
            FakePowerConfiguration power = StockPower();
            power.MissingSettings.Add(PowerSettings.LidCloseDc.Key);
            var execution = new FakeExecutionState();

            using (var mode = new TemporaryPowerMode(power, execution, NewSnapshotPath()))
            {
                mode.Enter();
                Assert.Equal((uint)LidAction.DoNothing, power.Get(PowerSettings.LidCloseAc), "AC still changed");
            }

            Assert.Equal((uint)LidAction.Sleep, power.Get(PowerSettings.LidCloseAc), "AC still restored");
        }

        [Test("disposing twice cannot restore or release twice")]
        private static void DisposeIsIdempotent()
        {
            FakePowerConfiguration power = StockPower();
            var execution = new FakeExecutionState();
            var mode = new TemporaryPowerMode(power, execution, NewSnapshotPath());
            mode.Enter();

            mode.Dispose();
            mode.Dispose();

            Assert.Equal(1, execution.RestoreCount, "one release");
        }

        [Test("a crash snapshot is restored before a new AFK session is captured")]
        private static void StaleSnapshotRecoversOriginalLidValues()
        {
            string path = NewSnapshotPath();
            FakePowerConfiguration power = StockPower();
            var stale = new PowerBackup { Scheme = power.ActiveScheme, SchemeName = power.SchemeName };
            stale.RecordOriginal(PowerSettings.LidCloseAc.Key, (uint)LidAction.Sleep);
            stale.RecordOriginal(PowerSettings.LidCloseDc.Key, (uint)LidAction.Hibernate);
            File.WriteAllText(path, stale.Serialize());

            // Simulate the values left behind by a killed AFKLocker process.
            power.Set(PowerSettings.LidCloseAc, (uint)LidAction.DoNothing);
            power.Set(PowerSettings.LidCloseDc, (uint)LidAction.DoNothing);

            using (var mode = new TemporaryPowerMode(power, new FakeExecutionState(), path))
            {
                mode.Enter();
                Assert.True(File.Exists(path), "the new session has its own recovery snapshot");
            }

            Assert.Equal((uint)LidAction.Sleep, power.Get(PowerSettings.LidCloseAc), "stale AC restored");
            Assert.Equal((uint)LidAction.Hibernate, power.Get(PowerSettings.LidCloseDc), "stale DC restored");
            Assert.False(File.Exists(path), "a successful exit removes the recovery snapshot");
        }

        [Test("overlapping AFK sessions cannot overwrite the original lid snapshot")]
        private static void OverlappingSessionsShareOnePowerOwner()
        {
            string path = NewSnapshotPath();
            FakePowerConfiguration power = StockPower();
            var firstExecution = new FakeExecutionState();
            var secondExecution = new FakeExecutionState();
            var first = new TemporaryPowerMode(power, firstExecution, path);
            var second = new TemporaryPowerMode(power, secondExecution, path);

            first.Enter();
            second.Enter();
            second.Dispose();

            Assert.Equal((uint)LidAction.DoNothing, power.Get(PowerSettings.LidCloseAc),
                "the non-owner cannot restore while the owner is still AFK");
            Assert.True(File.Exists(path), "the first session's original snapshot remains");

            first.Dispose();

            Assert.Equal((uint)LidAction.Sleep, power.Get(PowerSettings.LidCloseAc), "AC restored once");
            Assert.Equal((uint)LidAction.Hibernate, power.Get(PowerSettings.LidCloseDc), "DC restored once");
            Assert.Equal(1, firstExecution.RestoreCount, "owner execution request released");
            Assert.Equal(1, secondExecution.RestoreCount, "secondary execution request released");
        }

        [Test("sign-in recovery restores lid values left by a session that never finished")]
        private static void RecoverRestoresInterruptedSession()
        {
            string path = NewSnapshotPath();
            FakePowerConfiguration power = StockPower();
            var stale = new PowerBackup { Scheme = power.ActiveScheme, SchemeName = power.SchemeName };
            stale.RecordOriginal(PowerSettings.LidCloseAc.Key, (uint)LidAction.Sleep);
            stale.RecordOriginal(PowerSettings.LidCloseDc.Key, (uint)LidAction.Hibernate);
            File.WriteAllText(path, stale.Serialize());
            power.Set(PowerSettings.LidCloseAc, (uint)LidAction.DoNothing);
            power.Set(PowerSettings.LidCloseDc, (uint)LidAction.DoNothing);

            var recovery = new TemporaryPowerMode(power, new FakeExecutionState(), path);
            Assert.True(recovery.HasPendingRestore, "the interrupted session is visible");
            recovery.Recover();

            Assert.Equal((uint)LidAction.Sleep, power.Get(PowerSettings.LidCloseAc), "AC restored");
            Assert.Equal((uint)LidAction.Hibernate, power.Get(PowerSettings.LidCloseDc), "DC restored");
            Assert.False(recovery.HasPendingRestore, "nothing left to recover");
        }

        [Test("sign-in recovery leaves a session that is still running alone")]
        private static void RecoverLeavesActiveSessionAlone()
        {
            string path = NewSnapshotPath();
            FakePowerConfiguration power = StockPower();
            var active = new TemporaryPowerMode(power, new FakeExecutionState(), path);
            active.Enter();

            new TemporaryPowerMode(power, new FakeExecutionState(), path).Recover();

            Assert.Equal((uint)LidAction.DoNothing, power.Get(PowerSettings.LidCloseAc),
                "the running session still owns the lid");
            Assert.True(File.Exists(path), "its snapshot is untouched");

            active.Dispose();
            Assert.Equal((uint)LidAction.Sleep, power.Get(PowerSettings.LidCloseAc), "restored by its owner");
        }
    }
}
