using System;
using System.Linq;
using AFKLocker.Core;

namespace AFKLocker.Tests
{
    /// <summary>
    /// Every way enabling can fail, and what the machine looks like afterwards.
    ///
    /// The contract under test: Enable leaves exactly one of two states behind -
    /// automatic and genuinely working, or manual and clean. Anything it could
    /// not undo is reported rather than passed over.
    /// </summary>
    internal static class AutoLockTransactionTests
    {
        private static readonly string ExistingWatcher = typeof(AutoLockTransactionTests).Assembly.Location;

        private static AutoLockManager Manager(FakeSettingsStore settings, FakeAutostartRegistry autostart,
            FakeWatcherProcess watcher)
        {
            return new AutoLockManager(settings, autostart, watcher, ExistingWatcher);
        }

        private static void AssertCleanManual(FakeSettingsStore settings, FakeAutostartRegistry autostart,
            FakeWatcherProcess watcher, string context)
        {
            Assert.Equal(LockMode.Manual, settings.Load().Mode, context + ": mode is manual");
            Assert.False(autostart.IsRegistered, context + ": no autostart left");
            Assert.Equal(WatcherState.NotRunning, watcher.GetState(), context + ": no watcher left");
        }

        [Test("A successful enable leaves mode, autostart and a ready watcher")]
        private static void EnableSucceeds()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();

            AutoLockResult result = Manager(settings, autostart, watcher).Enable();

            Assert.True(result.Success, "enable succeeded");
            Assert.True(result.IsClean, "nothing left over");
            Assert.Equal(LockMode.Automatic, settings.Load().Mode, "mode saved");
            Assert.True(autostart.IsRegistered, "autostart registered");
            Assert.Equal(WatcherState.Ready, watcher.GetState(), "watcher ready");
        }

        [Test("A missing watcher binary fails before anything is changed")]
        private static void MissingWatcherChangesNothing()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();

            AutoLockResult result = new AutoLockManager(settings, autostart, watcher,
                @"Z:\does\not\exist.exe").Enable();

            Assert.False(result.Success, "reports failure");
            Assert.Equal(AutoLockFailure.WatcherMissing, result.Failure, "named failure");
            Assert.False(result.RolledBack, "there was nothing to roll back");
            Assert.True(settings.IsEmpty, "no mode written");
            Assert.Equal(0, autostart.RegisterCount, "autostart untouched");
            Assert.Equal(0, watcher.StartCount, "watcher never started");
        }

        [Test("A settings write failure leaves the machine untouched")]
        private static void SettingsWriteFailureRollsBack()
        {
            var settings = new FakeSettingsStore { FailSaveWith = "disk full" };
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();

            AutoLockResult result = Manager(settings, autostart, watcher).Enable();

            Assert.False(result.Success, "reports failure");
            Assert.Equal(AutoLockFailure.SettingsWriteFailed, result.Failure, "named failure");
            Assert.True(result.Message.Contains("disk full"), "the real cause survives");
            Assert.Equal(0, autostart.RegisterCount, "autostart never touched");
            Assert.Equal(0, watcher.StartCount, "watcher never started");
            AssertCleanManual(settings, autostart, watcher, "after settings failure");
        }

        [Test("An autostart failure rolls the saved mode back to manual")]
        private static void AutostartFailureRollsBackSettings()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry { FailRegisterWith = "registry access denied" };
            var watcher = new FakeWatcherProcess();

            AutoLockResult result = Manager(settings, autostart, watcher).Enable();

            Assert.False(result.Success, "reports failure");
            Assert.Equal(AutoLockFailure.AutostartFailed, result.Failure, "named failure");
            Assert.True(result.RolledBack, "rolled back");
            Assert.True(result.IsClean, "cleanly");
            Assert.Equal(0, watcher.StartCount, "watcher never started");
            AssertCleanManual(settings, autostart, watcher, "after autostart failure");
        }

        [Test("A watcher that never becomes ready is rolled back completely")]
        private static void WatcherNotReadyRollsBackEverything()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess
            {
                StartResult = WatcherStartResult.Failed(WatcherStartFailure.ReadyTimeout,
                    "The watcher did not become ready within 15 seconds.")
            };

            AutoLockResult result = Manager(settings, autostart, watcher).Enable();

            Assert.False(result.Success, "reports failure");
            Assert.Equal(AutoLockFailure.WatcherNotReady, result.Failure, "named failure");
            Assert.True(result.RolledBack, "rolled back");
            Assert.True(result.IsClean, "cleanly");
            Assert.Equal(1, watcher.StopCount, "the started process was stopped");
            AssertCleanManual(settings, autostart, watcher, "after readiness timeout");
        }

        [Test("A watcher that cannot register for lid events fails as not-ready")]
        private static void LidRegistrationFailureRollsBack()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess
            {
                StartResult = WatcherStartResult.Failed(WatcherStartFailure.LidNotificationFailed,
                    "Windows refused to register the watcher for lid notifications.")
            };

            AutoLockResult result = Manager(settings, autostart, watcher).Enable();

            Assert.Equal(AutoLockFailure.WatcherNotReady, result.Failure,
                "a watcher that cannot hear the lid is not ready, whatever else is true of it");
            AssertCleanManual(settings, autostart, watcher, "after lid registration failure");
        }

        [Test("A watcher that exits before readiness is rolled back")]
        private static void ExitedBeforeReadyRollsBack()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess
            {
                StartResult = WatcherStartResult.Failed(WatcherStartFailure.ExitedBeforeReady,
                    "The watcher exited with code 1 before it was ready.")
            };

            AutoLockResult result = Manager(settings, autostart, watcher).Enable();

            Assert.Equal(AutoLockFailure.WatcherStartFailed, result.Failure, "start failure");
            AssertCleanManual(settings, autostart, watcher, "after early exit");
        }

        [Test("A watcher that throws is treated as a start failure and rolled back")]
        private static void WatcherThrowingRollsBack()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess { FailStartWith = "CreateProcess failed" };

            AutoLockResult result = Manager(settings, autostart, watcher).Enable();

            Assert.Equal(AutoLockFailure.WatcherStartFailed, result.Failure, "named failure");
            Assert.True(result.Message.Contains("CreateProcess failed"), "the real cause survives");
            AssertCleanManual(settings, autostart, watcher, "after start threw");
        }

        [Test("A rollback that itself fails is reported as residue, not hidden")]
        private static void FailingRollbackIsReported()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry
            {
                // Registering works; undoing it does not.
                FailUnregisterWith = "registry locked"
            };
            var watcher = new FakeWatcherProcess
            {
                StartResult = WatcherStartResult.Failed(WatcherStartFailure.ReadyTimeout, "timed out")
            };

            AutoLockResult result = Manager(settings, autostart, watcher).Enable();

            Assert.False(result.Success, "still a failure");
            Assert.True(result.RolledBack, "rollback was attempted");
            Assert.False(result.IsClean, "and it left something behind");
            Assert.True(result.Residue.Any(r => r.Contains("autostart")),
                "the residue names what could not be undone");
            Assert.True(result.Residue.Any(r => r.Contains("registry locked")),
                "and why");
        }

        [Test("The original autostart command is restored, not just removed")]
        private static void RollbackRestoresPreviousAutostart()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            autostart.Preset("\"C:\\somewhere\\else\\AFKLockerWatcher.exe\"");
            var watcher = new FakeWatcherProcess
            {
                StartResult = WatcherStartResult.Failed(WatcherStartFailure.ReadyTimeout, "timed out")
            };

            Manager(settings, autostart, watcher).Enable();

            Assert.Equal("\"C:\\somewhere\\else\\AFKLockerWatcher.exe\"", autostart.RegisteredCommand,
                "an entry that existed before is put back as it was, not deleted");
        }

        [Test("The previously saved mode is restored, not assumed to be manual")]
        private static void RollbackRestoresPreviousMode()
        {
            var settings = new FakeSettingsStore();
            settings.Save(new AutoLockSettings { Mode = LockMode.Automatic });
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess
            {
                StartResult = WatcherStartResult.Failed(WatcherStartFailure.ReadyTimeout, "timed out")
            };

            Manager(settings, autostart, watcher).Enable();

            Assert.Equal(LockMode.Automatic, settings.Load().Mode,
                "rollback returns to what was there before, whatever that was");
        }

        [Test("A settings store that cannot even be read fails before changing anything")]
        private static void SettingsReadFailureChangesNothing()
        {
            var settings = new FakeSettingsStore { FailLoadWith = "settings file locked" };
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();

            AutoLockResult result = Manager(settings, autostart, watcher).Enable();

            Assert.False(result.Success, "reports failure");
            Assert.Equal(AutoLockFailure.SettingsWriteFailed, result.Failure, "named failure");
            Assert.Equal(0, settings.SaveCount, "nothing was written");
            Assert.Equal(0, autostart.RegisterCount, "autostart untouched");
            Assert.Equal(0, watcher.StartCount, "watcher never started");
        }

        [Test("A rollback that cannot restore the mode reports it rather than claiming success")]
        private static void FailingSettingsRollbackIsReported()
        {
            // The first save (to Automatic) works; the rollback save fails.
            var settings = new FakeSettingsStore { FailSaveWith = "disk full", FailSaveFromCall = 2 };
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess
            {
                StartResult = WatcherStartResult.Failed(WatcherStartFailure.ReadyTimeout, "timed out")
            };

            AutoLockResult result = Manager(settings, autostart, watcher).Enable();

            Assert.False(result.Success, "still a failure");
            Assert.False(result.IsClean, "and it could not fully undo itself");
            Assert.True(result.Residue.Any(r => r.Contains("saved settings")),
                "the residue names the saved settings");
            Assert.False(autostart.IsRegistered, "the parts that could be undone still were");
        }

        [Test("A rollback that cannot restore a previous autostart entry reports it")]
        private static void FailingAutostartRestoreIsReported()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry
            {
                // The enable registration works; putting the old value back does not.
                FailRegisterWith = "registry locked",
                FailRegisterFromCall = 2
            };
            autostart.Preset("\"C:\\old\\watcher.exe\"");
            var watcher = new FakeWatcherProcess
            {
                StartResult = WatcherStartResult.Failed(WatcherStartFailure.ReadyTimeout, "timed out")
            };

            AutoLockResult result = Manager(settings, autostart, watcher).Enable();

            Assert.False(result.IsClean, "residue reported");
            Assert.True(result.Residue.Any(r => r.Contains("autostart")), "and names the entry");
        }

        [Test("A rollback whose watcher stop throws still reports the rest cleanly")]
        private static void FailingWatcherStopDuringRollback()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess
            {
                StartResult = WatcherStartResult.Failed(WatcherStartFailure.ReadyTimeout, "timed out"),
                FailStopWith = "access denied"
            };

            AutoLockResult result = Manager(settings, autostart, watcher).Enable();

            Assert.False(result.Success, "reports failure");
            Assert.False(result.IsClean, "the process could not be stopped");
            Assert.True(result.Residue.Any(r => r.Contains("helper")), "residue names the helper");
            // Everything that could be undone, was.
            Assert.Equal(LockMode.Manual, settings.Load().Mode, "mode restored");
            Assert.False(autostart.IsRegistered, "autostart restored");
        }

        [Test("A watcher that refuses to stop during rollback is residue, not a clean revert")]
        private static void RefusedStopDuringRollbackIsResidue()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess
            {
                StartResult = WatcherStartResult.Failed(WatcherStartFailure.ReadyTimeout, "timed out"),
                // Returns false rather than throwing - the quiet failure mode.
                RefuseToStop = true
            };

            AutoLockResult result = Manager(settings, autostart, watcher).Enable();

            Assert.False(result.Success, "reports failure");
            Assert.False(result.IsClean,
                "a watcher still running is residue, even though Stop only returned false");
            Assert.True(result.Residue.Any(r => r.Contains("helper")), "and it is named");
            // The parts that could be undone still were.
            Assert.Equal(LockMode.Manual, settings.Load().Mode, "mode restored");
            Assert.False(autostart.IsRegistered, "autostart restored");
        }

        [Test("A stale readiness signal is described as such, not as a running process")]
        private static void StaleSignalIsDescribedAccurately()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess
            {
                State = WatcherState.Unhealthy,
                RefuseToStop = true
            };

            AutoLockResult result = Manager(settings, autostart, watcher).Disable();

            Assert.True(result.Residue.Any(r => r.Contains("stale")),
                "the message matches the actual situation");
            Assert.False(result.Residue.Any(r => r.Contains("process is still running")),
                "and does not send the user looking for a process that is gone");
        }

        // ------------------------------------------------------------ disable ---

        [Test("Disable converges to manual and clean")]
        private static void DisableConverges()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();
            Manager(settings, autostart, watcher).Enable();

            AutoLockResult result = Manager(settings, autostart, watcher).Disable();

            Assert.True(result.Success, "disable succeeded");
            Assert.True(result.IsClean, "nothing left over");
            AssertCleanManual(settings, autostart, watcher, "after disable");
        }

        [Test("Disable still removes autostart and records manual when the watcher will not stop")]
        private static void DisableKeepsGoingWhenStopFails()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess { State = WatcherState.Ready, RefuseToStop = true };
            autostart.Preset("\"watcher\"");

            AutoLockResult result = Manager(settings, autostart, watcher).Disable();

            Assert.False(result.Success, "reports the failure");
            Assert.Equal(AutoLockFailure.StopFailed, result.Failure, "named failure");
            Assert.False(autostart.IsRegistered, "but it will not come back at sign-in");
            Assert.Equal(LockMode.Manual, settings.Load().Mode, "and the mode is manual");
            Assert.False(result.IsClean, "the running watcher is reported as residue");
        }

        [Test("Disable reports a settings failure without pretending it worked")]
        private static void DisableReportsSettingsFailure()
        {
            var settings = new FakeSettingsStore { FailSaveWith = "disk full" };
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();

            AutoLockResult result = Manager(settings, autostart, watcher).Disable();

            Assert.False(result.Success, "reports failure");
            Assert.Equal(AutoLockFailure.SettingsWriteFailed, result.Failure, "named failure");
            Assert.False(result.IsClean, "and says the saved mode is still wrong");
        }

        [Test("Disable survives an autostart that cannot be removed")]
        private static void DisableReportsAutostartResidue()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry { FailUnregisterWith = "registry locked" };
            var watcher = new FakeWatcherProcess();

            AutoLockResult result = Manager(settings, autostart, watcher).Disable();

            Assert.Equal(LockMode.Manual, settings.Load().Mode, "mode still gets to manual");
            Assert.False(result.IsClean, "residue reported");
            Assert.True(result.Residue.Any(r => r.Contains("sign-in")), "and it names the leftover");
        }

        [Test("Cleanup removes everything without rewriting the user's mode")]
        private static void CleanupLeavesSettingsAlone()
        {
            var settings = new FakeSettingsStore();
            settings.Save(new AutoLockSettings { Mode = LockMode.Automatic });
            int savesBefore = settings.SaveCount;
            var autostart = new FakeAutostartRegistry();
            autostart.Preset("\"watcher\"");
            var watcher = new FakeWatcherProcess { State = WatcherState.Ready };

            AutoLockResult result = Manager(settings, autostart, watcher).Cleanup();

            Assert.True(result.Success, "cleanup succeeded");
            Assert.False(autostart.IsRegistered, "autostart gone");
            Assert.Equal(WatcherState.NotRunning, watcher.GetState(), "watcher gone");
            Assert.Equal(savesBefore, settings.SaveCount, "the user's mode was not rewritten");
        }

        // -------------------------------------------------------- reconciling ---

        [Test("A consistent machine is left alone")]
        private static void ReconcileDoesNothingWhenConsistent()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();
            Manager(settings, autostart, watcher).Enable();
            int startsBefore = watcher.StartCount;

            AutoLockResult result = Manager(settings, autostart, watcher).Reconcile();

            Assert.True(result.Success, "nothing to do");
            Assert.Equal(startsBefore, watcher.StartCount, "and nothing was restarted");
        }

        [Test("Automatic with a dead watcher is repaired by restarting it")]
        private static void ReconcileRestartsDeadWatcher()
        {
            var settings = new FakeSettingsStore();
            settings.Save(new AutoLockSettings { Mode = LockMode.Automatic });
            var autostart = new FakeAutostartRegistry();
            autostart.Preset("\"watcher\"");
            var watcher = new FakeWatcherProcess { State = WatcherState.NotRunning };

            AutoLockResult result = Manager(settings, autostart, watcher).Reconcile();

            Assert.True(result.Success, "repaired");
            Assert.Equal(WatcherState.Ready, watcher.GetState(), "watcher is back");
            Assert.Equal(LockMode.Automatic, settings.Load().Mode, "still automatic");
        }

        [Test("Automatic with a missing autostart entry is repaired")]
        private static void ReconcileRestoresMissingAutostart()
        {
            var settings = new FakeSettingsStore();
            settings.Save(new AutoLockSettings { Mode = LockMode.Automatic });
            var autostart = new FakeAutostartRegistry();          // nothing registered
            var watcher = new FakeWatcherProcess { State = WatcherState.Ready };

            AutoLockResult result = Manager(settings, autostart, watcher).Reconcile();

            Assert.True(result.Success, "repaired");
            Assert.True(autostart.IsRegistered, "autostart put back");
        }

        [Test("Manual with a leftover autostart entry is cleaned up")]
        private static void ReconcileCleansManual()
        {
            var settings = new FakeSettingsStore();
            settings.Save(new AutoLockSettings { Mode = LockMode.Manual });
            var autostart = new FakeAutostartRegistry();
            autostart.Preset("\"watcher\"");
            var watcher = new FakeWatcherProcess { State = WatcherState.Ready };

            AutoLockResult result = Manager(settings, autostart, watcher).Reconcile();

            Assert.True(result.Success, "cleaned");
            Assert.False(autostart.IsRegistered, "autostart removed");
            Assert.Equal(WatcherState.NotRunning, watcher.GetState(), "watcher stopped");
        }

        [Test("Automatic that cannot be repaired falls back to clean manual")]
        private static void ReconcileFallsBackToManual()
        {
            var settings = new FakeSettingsStore();
            settings.Save(new AutoLockSettings { Mode = LockMode.Automatic });
            var autostart = new FakeAutostartRegistry();
            autostart.Preset("\"watcher\"");
            var watcher = new FakeWatcherProcess
            {
                State = WatcherState.NotRunning,
                StartResult = WatcherStartResult.Failed(WatcherStartFailure.LidNotificationFailed,
                    "no lid notifications here")
            };

            AutoLockResult result = Manager(settings, autostart, watcher).Reconcile();

            Assert.False(result.Success, "reports it could not repair");
            AssertCleanManual(settings, autostart, watcher, "after failed repair");
        }

        [Test("Automatic without a watcher binary falls back to clean manual")]
        private static void ReconcileHandlesMissingBinary()
        {
            var settings = new FakeSettingsStore();
            settings.Save(new AutoLockSettings { Mode = LockMode.Automatic });
            var autostart = new FakeAutostartRegistry();
            autostart.Preset("\"watcher\"");
            var watcher = new FakeWatcherProcess();

            AutoLockResult result = new AutoLockManager(settings, autostart, watcher,
                @"Z:\gone.exe").Reconcile();

            Assert.False(result.Success, "reports it");
            Assert.Equal(AutoLockFailure.WatcherMissing, result.Failure, "named failure");
            Assert.Equal(LockMode.Manual, settings.Load().Mode, "switched to manual");
            Assert.False(autostart.IsRegistered, "autostart removed");
        }

        // ------------------------------------------------------------- status ---

        [Test("Status reports a healthy automatic setup as consistent")]
        private static void StatusConsistentWhenHealthy()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();
            AutoLockManager manager = Manager(settings, autostart, watcher);
            manager.Enable();

            AutoLockStatus status = manager.GetStatus();

            Assert.True(status.IsConsistent, "consistent");
            Assert.True(status.WatcherReady, "ready");
            Assert.Null(status.Inconsistency, "nothing to report");
        }

        [Test("Status names the mismatch when automatic mode has no watcher")]
        private static void StatusDetectsDeadWatcher()
        {
            var settings = new FakeSettingsStore();
            settings.Save(new AutoLockSettings { Mode = LockMode.Automatic });
            var autostart = new FakeAutostartRegistry();
            autostart.Preset(AutostartCommand.For(ExistingWatcher));
            var watcher = new FakeWatcherProcess { State = WatcherState.NotRunning };

            AutoLockStatus status = Manager(settings, autostart, watcher).GetStatus();

            Assert.False(status.IsConsistent, "inconsistent");
            Assert.True(status.Inconsistency.Contains("not running"), "and says why");
        }

        [Test("Status treats a helper that is running but not ready as a contradiction")]
        private static void StatusDistinguishesStartingFromReady()
        {
            var settings = new FakeSettingsStore();
            settings.Save(new AutoLockSettings { Mode = LockMode.Automatic });
            var autostart = new FakeAutostartRegistry();
            autostart.Preset(AutostartCommand.For(ExistingWatcher));
            var watcher = new FakeWatcherProcess { State = WatcherState.Starting };

            AutoLockStatus status = Manager(settings, autostart, watcher).GetStatus();

            Assert.False(status.WatcherReady, "starting is not ready");

            // This used to be accepted as "not yet a contradiction", which meant
            // a helper stuck half-started looked fine forever. Enable waits for
            // readiness before returning, so a helper found in this state after
            // the fact has not simply been caught mid-launch - it is stuck, and
            // reconciliation should restart it rather than wait indefinitely.
            Assert.False(status.IsConsistent, "running but never ready is a stuck helper, not a healthy one");
            Assert.True(status.Inconsistency.Contains("not reported itself ready"), "and says so");
        }

        [Test("Status notices a leftover watcher in manual mode")]
        private static void StatusDetectsResidueInManual()
        {
            var settings = new FakeSettingsStore();
            settings.Save(new AutoLockSettings { Mode = LockMode.Manual });
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess { State = WatcherState.Ready };

            AutoLockStatus status = Manager(settings, autostart, watcher).GetStatus();

            Assert.False(status.IsConsistent, "inconsistent");
            Assert.True(status.Inconsistency.Contains("still running"), "and says why");
        }

        [Test("Status notices an unhealthy watcher")]
        private static void StatusDetectsUnhealthy()
        {
            var settings = new FakeSettingsStore();
            settings.Save(new AutoLockSettings { Mode = LockMode.Automatic });
            var autostart = new FakeAutostartRegistry();
            autostart.Preset("\"watcher\"");
            var watcher = new FakeWatcherProcess { State = WatcherState.Unhealthy };

            AutoLockStatus status = Manager(settings, autostart, watcher).GetStatus();

            Assert.False(status.IsConsistent, "inconsistent");
            Assert.True(status.Inconsistency.Contains("unhealthy"), "and says so");
        }
    }
}
