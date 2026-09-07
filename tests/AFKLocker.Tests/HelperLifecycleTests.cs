using System.Linq;
using AFKLocker.Core;

namespace AFKLocker.Tests
{
    /// <summary>
    /// Whether a background helper exists, across the four combinations of
    /// automatic locking and the global hotkey.
    ///
    /// The contract this fixes in place: residency follows the features switched
    /// on, not the lock mode. Manual used to mean "no helper, ever"; it now means
    /// "no helper unless the hotkey needs one". The dangerous mistakes are both
    /// silent - killing a helper the hotkey still needs, and leaving one running
    /// when nothing does - so both directions are tested at every transition.
    /// </summary>
    internal static class HelperLifecycleTests
    {
        private static readonly string ExistingHelper = typeof(HelperLifecycleTests).Assembly.Location;

        private static readonly HotkeyBinding MenuKey =
            new HotkeyBinding(HotkeyModifiers.None, 0x5D);          // VK_APPS
        private static readonly HotkeyBinding CtrlAltL =
            new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4C);

        private static AutoLockManager Manager(FakeSettingsStore settings,
            FakeAutostartRegistry autostart, FakeWatcherProcess watcher)
        {
            return new AutoLockManager(settings, autostart, watcher, ExistingHelper,
                TestElevation.No);
        }

        private static void AssertNoHelper(FakeAutostartRegistry autostart,
            FakeWatcherProcess watcher, string context)
        {
            Assert.False(autostart.IsRegistered, context + ": no sign-in entry");
            Assert.Equal(WatcherState.NotRunning, watcher.GetState(), context + ": nothing running");
        }

        private static void AssertHelperReady(FakeAutostartRegistry autostart,
            FakeWatcherProcess watcher, string context)
        {
            Assert.True(autostart.IsRegistered, context + ": sign-in entry present");
            Assert.Equal(AutostartState.Correct,
                AutostartInspector.Inspect(autostart, ExistingHelper).State,
                context + ": and it points at this helper");
            Assert.Equal(WatcherState.Ready, watcher.GetState(), context + ": helper ready");
        }

        // ------------------------------------------------- the four states ---

        [Test("Manual with the hotkey off runs no helper at all")]
        private static void ManualNoHotkey()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();

            AutoLockResult result = Manager(settings, autostart, watcher)
                .Apply(new AutoLockSettings());

            Assert.True(result.Success, "applied");
            AssertNoHelper(autostart, watcher, "manual, no hotkey");
        }

        [Test("Manual with the hotkey on runs a helper, purely for the key")]
        private static void ManualWithHotkey()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();

            AutoLockResult result = Manager(settings, autostart, watcher).Apply(new AutoLockSettings
            {
                Mode = LockMode.Manual,
                HotkeyEnabled = true,
                Hotkey = MenuKey
            });

            Assert.True(result.Success, "applied");
            AssertHelperReady(autostart, watcher, "manual with hotkey");
            Assert.Equal(LockMode.Manual, settings.Load().Mode, "and the lock mode is untouched");
        }

        [Test("Automatic with the hotkey off runs a helper for the lid")]
        private static void AutomaticNoHotkey()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();

            AutoLockResult result = Manager(settings, autostart, watcher).Enable();

            Assert.True(result.Success, "applied");
            AssertHelperReady(autostart, watcher, "automatic");
            Assert.False(settings.Load().HotkeyEnabled, "hotkey untouched");
        }

        [Test("Automatic with the hotkey on runs one helper doing both")]
        private static void AutomaticWithHotkey()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();

            AutoLockResult result = Manager(settings, autostart, watcher).Apply(new AutoLockSettings
            {
                Mode = LockMode.Automatic,
                HotkeyEnabled = true,
                Hotkey = CtrlAltL
            });

            Assert.True(result.Success, "applied");
            AssertHelperReady(autostart, watcher, "automatic with hotkey");

            var status = Manager(settings, autostart, watcher).GetStatus();
            Assert.Equal(HelperFeatures.LidLock | HelperFeatures.GlobalHotkey,
                status.RequiredFeatures, "both features are the helper's job");
            Assert.True(status.IsConsistent, "and that is a consistent machine");
        }

        // ----------------------------------------------------- transitions ---

        [Test("Turning the hotkey off while automatic stays on does NOT kill the helper")]
        private static void DisablingHotkeyKeepsHelperForLid()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();
            AutoLockManager manager = Manager(settings, autostart, watcher);

            manager.Apply(new AutoLockSettings
            {
                Mode = LockMode.Automatic,
                HotkeyEnabled = true,
                Hotkey = MenuKey
            });

            AutoLockResult result = manager.SetHotkey(false, MenuKey);

            Assert.True(result.Success, "hotkey switched off");
            AssertHelperReady(autostart, watcher, "after switching the hotkey off");
            Assert.Equal(LockMode.Automatic, settings.Load().Mode, "automatic survived");
        }

        [Test("Turning automatic off while the hotkey stays on does NOT kill the helper")]
        private static void DisablingAutomaticKeepsHelperForHotkey()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();
            AutoLockManager manager = Manager(settings, autostart, watcher);

            manager.Apply(new AutoLockSettings
            {
                Mode = LockMode.Automatic,
                HotkeyEnabled = true,
                Hotkey = MenuKey
            });

            AutoLockResult result = manager.Disable();

            Assert.True(result.Success, "automatic switched off");
            AssertHelperReady(autostart, watcher, "after switching automatic off");
            Assert.True(settings.Load().HotkeyEnabled, "the hotkey survived");
        }

        [Test("Turning the last feature off stands everything down")]
        private static void LastFeatureOffRemovesEverything()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();
            AutoLockManager manager = Manager(settings, autostart, watcher);

            manager.Apply(new AutoLockSettings
            {
                Mode = LockMode.Automatic,
                HotkeyEnabled = true,
                Hotkey = MenuKey
            });

            manager.Disable();                       // hotkey still holds it up
            AutoLockResult result = manager.SetHotkey(false, MenuKey);

            Assert.True(result.Success, "stood down");
            AssertNoHelper(autostart, watcher, "nothing needs a helper now");
        }

        [Test("Enabling the hotkey from a clean manual machine starts the helper")]
        private static void EnablingHotkeyFromManualStartsHelper()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();

            AutoLockResult result = Manager(settings, autostart, watcher).SetHotkey(true, MenuKey);

            Assert.True(result.Success, "hotkey on");
            AssertHelperReady(autostart, watcher, "hotkey on from manual");
        }

        [Test("Rebinding restarts the helper, because it reads its settings at startup")]
        private static void RebindingRestartsHelper()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();
            AutoLockManager manager = Manager(settings, autostart, watcher);

            manager.SetHotkey(true, MenuKey);
            int startsBefore = watcher.StartCount;

            AutoLockResult result = manager.SetHotkey(true, CtrlAltL);

            Assert.True(result.Success, "rebound");
            Assert.True(watcher.StopCount > 0, "the old helper was stopped");
            Assert.True(watcher.StartCount > startsBefore,
                "a helper still configured for the old key would answer the wrong one");
            Assert.Equal(CtrlAltL, settings.Load().Hotkey, "and the new key is saved");
        }

        // -------------------------------------------------------- failures ---

        [Test("A hotkey Windows refuses rolls the whole change back")]
        private static void HotkeyConflictRollsBack()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess
            {
                StartResult = WatcherStartResult.Failed(WatcherStartFailure.HotkeyRegistrationFailed,
                    "This hotkey is already registered by another application.")
            };

            AutoLockResult result = Manager(settings, autostart, watcher).SetHotkey(true, CtrlAltL);

            Assert.False(result.Success, "not switched on");
            Assert.Equal(AutoLockFailure.HotkeyUnavailable, result.Failure, "named for what happened");
            Assert.True(result.Message.Contains("already registered"), "and says so plainly");
            Assert.False(settings.Load().HotkeyEnabled, "the setting was rolled back");
            AssertNoHelper(autostart, watcher, "and nothing was left behind");
        }

        [Test("A hotkey that could never work is refused before anything is touched")]
        private static void UnusableHotkeyChangesNothing()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();

            // Windows accepts a bare modifier in RegisterHotKey - measured - and
            // then nothing ever fires. Refusing here is the honest answer.
            var ctrlAlone = new HotkeyBinding(HotkeyModifiers.Control, 0x11);

            AutoLockResult result = Manager(settings, autostart, watcher).SetHotkey(true, ctrlAlone);

            Assert.False(result.Success, "refused");
            Assert.Equal(AutoLockFailure.HotkeyUnavailable, result.Failure, "named");
            Assert.Equal(0, settings.SaveCount, "nothing was written");
            Assert.Equal(0, watcher.StartCount, "and nothing was started");
        }

        [Test("Switching a feature on restores the previous helper when the new one fails")]
        private static void FailedChangeRestoresPreviousHelper()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();
            AutoLockManager manager = Manager(settings, autostart, watcher);

            manager.Enable();                                   // automatic is working
            Assert.Equal(WatcherState.Ready, watcher.GetState(), "helper up");

            // The conflict belongs to the hotkey, so starting without it works -
            // which is exactly what the rollback has to do.
            watcher.StartResult = WatcherStartResult.Failed(WatcherStartFailure.HotkeyRegistrationFailed,
                "This hotkey is already registered by another application.");
            watcher.FailStartWhile = () => settings.Load().HotkeyEnabled;

            AutoLockResult result = manager.SetHotkey(true, CtrlAltL);

            Assert.False(result.Success, "the change failed");
            Assert.Equal(LockMode.Automatic, settings.Load().Mode, "automatic is still on");
            Assert.False(settings.Load().HotkeyEnabled, "and the hotkey is not");

            // The rollback restarts the helper with the settings that were put
            // back, so lid locking keeps working. Losing it would silently break
            // a feature the user never touched.
            Assert.True(result.IsClean, "the rollback left nothing behind: " + string.Join("; ", result.Residue.ToArray()));
            Assert.Equal(WatcherState.Ready, watcher.GetState(), "and the helper is running again");
        }

        // ---------------------------------------------------- reconciling ---

        [Test("Reconcile rewrites a sign-in entry that points somewhere else")]
        private static void ReconcileRepairsWrongAutostart()
        {
            var settings = new FakeSettingsStore();
            settings.Save(new AutoLockSettings { Mode = LockMode.Automatic });

            var autostart = new FakeAutostartRegistry();
            autostart.Preset("\"D:\\OldAFKLocker\\AFKLockerWatcher.exe\"");
            var watcher = new FakeWatcherProcess { State = WatcherState.Ready };

            AutoLockManager manager = Manager(settings, autostart, watcher);
            Assert.False(manager.GetStatus().IsConsistent, "a wrong entry is a real inconsistency");

            AutoLockResult result = manager.Reconcile();

            Assert.True(result.Success, "repaired");
            Assert.Equal(AutostartState.Correct,
                AutostartInspector.Inspect(autostart, ExistingHelper).State,
                "and the entry now points at this installation");
        }

        [Test("Reconcile leaves a hotkey-only machine with its helper running")]
        private static void ReconcileKeepsHotkeyOnlyHelper()
        {
            var settings = new FakeSettingsStore();
            settings.Save(new AutoLockSettings
            {
                Mode = LockMode.Manual,
                HotkeyEnabled = true,
                Hotkey = MenuKey
            });

            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess { State = WatcherState.NotRunning };

            AutoLockResult result = Manager(settings, autostart, watcher).Reconcile();

            Assert.True(result.Success, "repaired");
            AssertHelperReady(autostart, watcher, "manual, hotkey on");
            Assert.True(settings.Load().HotkeyEnabled, "the hotkey stayed on");
        }

        [Test("Reconcile switches off a hotkey with nothing usable bound, rather than starting a helper for it")]
        private static void ReconcileDropsUnusableHotkey()
        {
            var settings = new FakeSettingsStore();
            settings.Save(new AutoLockSettings
            {
                Mode = LockMode.Manual,
                HotkeyEnabled = true,
                Hotkey = HotkeyBinding.Empty
            });

            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();

            AutoLockResult result = Manager(settings, autostart, watcher).Reconcile();

            Assert.True(result.Success, "resolved");
            Assert.False(settings.Load().HotkeyEnabled,
                "a helper that would register nothing must not be the reason a process exists");
            AssertNoHelper(autostart, watcher, "hotkey on with no key");
        }

        // ------------------------------------------- failing open, and loudly ---

        [Test("Settings that cannot be read never become a teardown")]
        private static void UnreadableSettingsNeverStandDown()
        {
            var settings = new FakeSettingsStore();
            settings.Save(new AutoLockSettings
            {
                Mode = LockMode.Automatic,
                HotkeyEnabled = true,
                Hotkey = MenuKey
            });
            int savesBefore = settings.SaveCount;

            settings.FailLoadWith = "the settings file is locked by another process";

            var autostart = new FakeAutostartRegistry();
            autostart.Preset(AutostartCommand.For(ExistingHelper));
            var watcher = new FakeWatcherProcess { State = WatcherState.Ready };

            AutoLockResult result = Manager(settings, autostart, watcher).Reconcile();

            // The old shape read "Manual, no hotkey" from a failed read, decided
            // nothing needed a helper, and wrote that over the real file. One
            // transient lock erased the configuration and reported success.
            Assert.False(result.Success, "an unreadable file is not a decision");
            Assert.Equal(savesBefore, settings.SaveCount, "and nothing was written over it");
            Assert.True(autostart.IsRegistered, "the sign-in entry was left alone");
            Assert.Equal(WatcherState.Ready, watcher.GetState(), "and the helper was left running");
        }

        [Test("Standing down reports failure when the sign-in entry survives")]
        private static void StandDownFailsWhenAutostartRemains()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            autostart.Preset(AutostartCommand.For(ExistingHelper));
            autostart.FailUnregisterWith = "the Run key is locked by policy";
            var watcher = new FakeWatcherProcess();

            AutoLockResult result = Manager(settings, autostart, watcher)
                .Apply(new AutoLockSettings());

            // Reporting success here left an entry that starts a helper for
            // features that are off - and after an uninstall, one pointing at a
            // program that no longer exists, with nothing left to remove it.
            Assert.False(result.Success, "an entry that survived is not success");
            Assert.Equal(AutoLockFailure.AutostartFailed, result.Failure, "named for what happened");
            Assert.False(result.IsClean, "and it is listed");
        }

        [Test("Cleanup reports failure when the sign-in entry survives")]
        private static void CleanupFailsWhenAutostartRemains()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            autostart.Preset(AutostartCommand.For(ExistingHelper));
            autostart.FailUnregisterWith = "the Run key is locked by policy";
            var watcher = new FakeWatcherProcess();

            AutoLockResult result = Manager(settings, autostart, watcher).Cleanup();

            Assert.False(result.Success, "the uninstaller must not be told this worked");
            Assert.Equal(AutoLockFailure.AutostartFailed, result.Failure, "named");
        }

        [Test("Falling back to no features keeps the key the user chose")]
        private static void FallbackRemembersTheBinding()
        {
            var settings = new FakeSettingsStore();
            settings.Save(new AutoLockSettings
            {
                Mode = LockMode.Manual,
                HotkeyEnabled = true,
                Hotkey = CtrlAltL
            });

            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();

            // No helper binary, so the features cannot be restored at all.
            var manager = new AutoLockManager(settings, autostart, watcher,
                @"C:\nowhere\AFKLockerWatcher.exe", TestElevation.No);

            AutoLockResult result = manager.Reconcile();

            Assert.False(result.Success, "it could not be repaired");

            AutoLockSettings after = settings.Load();
            Assert.False(after.HotkeyEnabled, "the feature is off, which was the point");
            Assert.Equal(CtrlAltL, after.Hotkey,
                "but forgetting the key they chose was never asked for - the product shows "
                + "\"remembered but not active\" precisely because that state exists");
        }

        [Test("A stale readiness signal does not block configuration changes")]
        private static void StaleSignalDoesNotBlockChanges()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();

            // Unhealthy means a readiness signal outlived the process that set
            // it. There is no process, so Stop truthfully returns false - and
            // treating that as a failed stop used to make a stale signal block
            // every change, including the reconciliation meant to clear it.
            var watcher = new FakeWatcherProcess
            {
                State = WatcherState.Unhealthy,
                RefuseToStop = true
            };

            AutoLockResult result = Manager(settings, autostart, watcher).SetHotkey(true, MenuKey);

            Assert.True(result.Success,
                "a leftover signal is not a running helper, and must not veto the repair");
            Assert.Equal(WatcherState.Ready, watcher.GetState(), "the new helper started");
        }

        [Test("A helper that genuinely will not stop does block the change")]
        private static void RunningHelperThatWillNotStopBlocks()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess
            {
                State = WatcherState.Ready,
                RefuseToStop = true
            };

            AutoLockResult result = Manager(settings, autostart, watcher).SetHotkey(true, MenuKey);

            Assert.False(result.Success,
                "reconfiguring around a process still holding the old settings would be a lie");
            Assert.Equal(AutoLockFailure.StopFailed, result.Failure, "named for what happened");
            Assert.Equal(0, settings.SaveCount, "and nothing was written");
        }

        [Test("A hotkey switched on with no key bound is reported as a contradiction")]
        private static void HotkeyWithoutKeyIsInconsistent()
        {
            var settings = new FakeSettingsStore();
            settings.Save(new AutoLockSettings
            {
                Mode = LockMode.Manual,
                HotkeyEnabled = true,
                Hotkey = HotkeyBinding.Empty
            });

            AutoLockStatus status = Manager(settings, new FakeAutostartRegistry(),
                new FakeWatcherProcess()).GetStatus();

            Assert.False(status.IsConsistent, "the settings disagree with themselves");
            Assert.True(status.Inconsistency.Contains("no usable key"), "and it says which way");
            Assert.False(status.HelperRequired, "and nothing should be resident for it");
        }
    }
}
