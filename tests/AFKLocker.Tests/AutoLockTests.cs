using System;
using AFKLocker.Core;

namespace AFKLocker.Tests
{
    internal static class AutoLockPolicyTests
    {
        private static AutoLockPolicy Policy(FakeSessionLocker locker, FakeSessionState session)
        {
            return new AutoLockPolicy(locker, session);
        }

        [Test("Closing the lid locks the session exactly once")]
        private static void ClosingTheLidLocks()
        {
            var locker = new FakeSessionLocker();
            var policy = Policy(locker, new FakeSessionState());

            policy.Handle(LidState.Opened);                              // initial state
            AutoLockDecision decision = policy.Handle(LidState.Closed);

            Assert.Equal(AutoLockDecision.Locked, decision, "closing locks");
            Assert.Equal(1, locker.LockCount, "locked exactly once");
        }

        [Test("The first event is a starting position, not a transition, and never locks")]
        private static void FirstEventIsIgnored()
        {
            var locker = new FakeSessionLocker();
            var policy = Policy(locker, new FakeSessionState());

            // A laptop docked shut with an external monitor: the watcher starts
            // and Windows immediately reports the lid as closed. Locking there
            // would interrupt someone who is actively working.
            AutoLockDecision decision = policy.Handle(LidState.Closed);

            Assert.Equal(AutoLockDecision.IgnoredInitialState, decision, "initial state ignored");
            Assert.Equal(0, locker.LockCount, "nothing locked");
        }

        [Test("A closed lid after that initial closed state still locks once it reopens and shuts")]
        private static void StartingClosedStillWorksLater()
        {
            var locker = new FakeSessionLocker();
            var policy = Policy(locker, new FakeSessionState());

            policy.Handle(LidState.Closed);   // initial, ignored
            policy.Handle(LidState.Opened);   // user opens it
            AutoLockDecision decision = policy.Handle(LidState.Closed);

            Assert.Equal(AutoLockDecision.Locked, decision, "the next real close locks");
            Assert.Equal(1, locker.LockCount, "locked once");
        }

        [Test("Opening the lid never unlocks and never locks")]
        private static void OpeningDoesNothing()
        {
            var locker = new FakeSessionLocker();
            var policy = Policy(locker, new FakeSessionState());

            policy.Handle(LidState.Closed);                              // initial
            policy.Handle(LidState.Opened);
            AutoLockDecision decision = policy.Handle(LidState.Opened);

            Assert.Equal(AutoLockDecision.IgnoredLidOpened, decision, "opening is inert");
            Assert.Equal(0, locker.LockCount, "AFKLocker never unlocks anything");
        }

        [Test("Repeated close events lock only once")]
        private static void DuplicateCloseEventsLockOnce()
        {
            var locker = new FakeSessionLocker();
            var policy = Policy(locker, new FakeSessionState());

            policy.Handle(LidState.Opened);
            policy.Handle(LidState.Closed);
            AutoLockDecision second = policy.Handle(LidState.Closed);
            AutoLockDecision third = policy.Handle(LidState.Closed);

            Assert.Equal(AutoLockDecision.IgnoredAlreadyClosed, second, "second close ignored");
            Assert.Equal(AutoLockDecision.IgnoredAlreadyClosed, third, "third close ignored");
            Assert.Equal(1, locker.LockCount, "still only one lock");
        }

        [Test("Open, close, open in quick succession locks exactly once")]
        private static void RapidCycleLocksOnce()
        {
            var locker = new FakeSessionLocker();
            var policy = Policy(locker, new FakeSessionState());

            policy.Handle(LidState.Opened);   // initial
            policy.Handle(LidState.Closed);
            policy.Handle(LidState.Opened);

            Assert.Equal(1, locker.LockCount, "one close, one lock");
        }

        [Test("An already locked session is not locked again")]
        private static void AlreadyLockedSessionIsLeftAlone()
        {
            var locker = new FakeSessionLocker();
            var session = new FakeSessionState { IsLocked = true };
            var policy = Policy(locker, session);

            policy.Handle(LidState.Opened);
            AutoLockDecision decision = policy.Handle(LidState.Closed);

            Assert.Equal(AutoLockDecision.IgnoredSessionAlreadyLocked, decision, "nothing to do");
            Assert.Equal(0, locker.LockCount, "no redundant lock");
        }

        [Test("After resume the next event is treated as a starting position again")]
        private static void ResetAfterResume()
        {
            var locker = new FakeSessionLocker();
            var policy = Policy(locker, new FakeSessionState());

            policy.Handle(LidState.Opened);
            policy.Handle(LidState.Closed);
            Assert.Equal(1, locker.LockCount, "locked before suspending");

            // Coming back from sleep, the lid may have moved unobserved.
            policy.Reset();
            AutoLockDecision decision = policy.Handle(LidState.Closed);

            Assert.Equal(AutoLockDecision.IgnoredInitialState, decision, "re-establish, do not act");
            Assert.Equal(1, locker.LockCount, "no extra lock");
        }

        [Test("A locked session that is unlocked again still locks on the next close")]
        private static void UnlockingRestoresNormalBehaviour()
        {
            var locker = new FakeSessionLocker();
            var session = new FakeSessionState { IsLocked = true };
            var policy = Policy(locker, session);

            policy.Handle(LidState.Opened);
            policy.Handle(LidState.Closed);      // ignored, already locked
            session.IsLocked = false;            // user came back and signed in
            policy.Handle(LidState.Opened);
            AutoLockDecision decision = policy.Handle(LidState.Closed);

            Assert.Equal(AutoLockDecision.Locked, decision, "locks again once unlocked");
        }

        [Test("The policy rejects null dependencies")]
        private static void NullDependenciesRejected()
        {
            Assert.Throws<ArgumentNullException>(
                () => new AutoLockPolicy(null, new FakeSessionState()), "null locker");
            Assert.Throws<ArgumentNullException>(
                () => new AutoLockPolicy(new FakeSessionLocker(), null), "null session state");
        }

        [Test("A machine that never reports a lid never locks")]
        private static void NoLidEventsMeansNoLocking()
        {
            var locker = new FakeSessionLocker();
            var policy = Policy(locker, new FakeSessionState());
            var provider = new FakeLidEventProvider();
            provider.LidStateChanged += (s, e) => policy.Handle(e.State);

            // A desktop: registration succeeds, but Windows never reports a lid
            // position, so no event is ever raised.
            Assert.True(provider.Start(), "registration itself succeeds");
            Assert.Equal(0, locker.LockCount, "nothing happens without lid events");
        }

        [Test("A provider that cannot register reports failure instead of pretending")]
        private static void UnsupportedProviderReportsFailure()
        {
            var provider = new FakeLidEventProvider { StartSucceeds = false };

            Assert.False(provider.Start(), "start fails");
            Assert.False(provider.Started, "and says so");
        }

        [Test("Lid events wired through a provider reach the policy")]
        private static void ProviderDrivesPolicy()
        {
            var locker = new FakeSessionLocker();
            var policy = Policy(locker, new FakeSessionState());
            var provider = new FakeLidEventProvider();
            provider.LidStateChanged += (s, e) => policy.Handle(e.State);
            provider.Start();

            provider.Emit(LidState.Opened);
            provider.Emit(LidState.Closed);

            Assert.Equal(1, locker.LockCount, "the close reached the policy");
        }
    }

    internal static class AutoLockSettingsTests
    {
        [Test("Manual is the default when no settings have ever been saved")]
        private static void DefaultIsManual()
        {
            Assert.Equal(LockMode.Manual, new AutoLockSettings().Mode, "fresh settings");
            Assert.Equal(LockMode.Manual, new FakeSettingsStore().Load().Mode, "empty store");
        }

        [Test("An upgrade from 0.1.x, with no settings file, behaves as manual")]
        private static void UpgradeFromEarlierVersionIsManual()
        {
            var store = new FakeSettingsStore();

            Assert.True(store.IsEmpty, "0.1.x left no settings file");
            Assert.Equal(LockMode.Manual, store.Load().Mode, "so the machine stays manual");
        }

        [Test("The chosen mode survives a save and load")]
        private static void ModePersists()
        {
            var store = new FakeSettingsStore();

            store.Save(new AutoLockSettings { Mode = LockMode.Automatic });
            Assert.Equal(LockMode.Automatic, store.Load().Mode, "automatic persisted");

            store.Save(new AutoLockSettings { Mode = LockMode.Manual });
            Assert.Equal(LockMode.Manual, store.Load().Mode, "manual persisted");
        }

        [Test("A damaged settings file falls back to manual instead of throwing")]
        private static void CorruptSettingsFallBackToManual()
        {
            var store = new FakeSettingsStore();
            store.SetRaw("this is not a settings file\nlock-mode\n\0\0garbage");

            Assert.Equal(LockMode.Manual, store.Load().Mode,
                "unreadable settings must not enable a background process");
        }

        [Test("An unknown mode value is treated as manual")]
        private static void UnknownModeIsManual()
        {
            Assert.Equal(LockMode.Manual,
                AutoLockSettings.Deserialize("version=1\nlock-mode=something-else\n").Mode,
                "only a known value turns automatic on");
        }

        [Test("Comments and unknown keys in the settings file are ignored")]
        private static void CommentsAndUnknownKeysIgnored()
        {
            AutoLockSettings settings = AutoLockSettings.Deserialize(
                "# a comment\n\nversion=1\nfuture-key=42\nlock-mode=automatic\n");

            Assert.Equal(LockMode.Automatic, settings.Mode, "the known key is still read");
        }
    }

    internal static class AutoLockManagerTests
    {
        private const string WatcherPath = "AFKLockerWatcher.exe";

        private static AutoLockManager Manager(FakeSettingsStore settings, FakeAutostartRegistry autostart,
            FakeWatcherProcess watcher, string path)
        {
            return new AutoLockManager(settings, autostart, watcher, path);
        }

        [Test("Manual mode means no autostart and no watcher")]
        private static void ManualLeavesNothingResident()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();

            AutoLockStatus status = Manager(settings, autostart, watcher, null).GetStatus();

            Assert.Equal(LockMode.Manual, status.Mode, "manual by default");
            Assert.False(status.AutostartRegistered, "nothing registered to start");
            Assert.False(status.WatcherRunning, "nothing running");
        }

        [Test("Enabling automatic records the mode, registers autostart and starts the watcher")]
        private static void EnableDoesAllThree()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();
            // Use this test assembly as a stand-in for an existing watcher file.
            string existingFile = typeof(AutoLockManagerTests).Assembly.Location;

            bool enabled = Manager(settings, autostart, watcher, existingFile).Enable();

            Assert.True(enabled, "enable succeeded");
            Assert.Equal(LockMode.Automatic, settings.Load().Mode, "mode recorded");
            Assert.True(autostart.IsRegistered, "registered to start at sign-in");
            Assert.True(autostart.RegisteredCommand.Contains(existingFile), "registered the watcher path");
            Assert.Equal(1, watcher.StartCount, "watcher started now, not only at next sign-in");
        }

        [Test("Enabling without a watcher binary fails cleanly and changes nothing")]
        private static void EnableWithoutWatcherBinaryFails()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();

            bool enabled = Manager(settings, autostart, watcher, @"Z:\does\not\exist.exe").Enable();

            Assert.False(enabled, "reports failure");
            Assert.True(settings.IsEmpty, "no mode recorded");
            Assert.False(autostart.IsRegistered, "nothing registered");
            Assert.Equal(0, watcher.StartCount, "nothing started");
        }

        [Test("Disabling stops the watcher, removes autostart and records manual")]
        private static void DisableUndoesEverything()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess { IsRunning = true };
            autostart.Register("\"" + WatcherPath + "\"");

            bool disabled = Manager(settings, autostart, watcher, WatcherPath).Disable();

            Assert.True(disabled, "watcher stopped");
            Assert.False(watcher.IsRunning, "nothing resident");
            Assert.False(autostart.IsRegistered, "will not come back at sign-in");
            Assert.Equal(LockMode.Manual, settings.Load().Mode, "back to manual");
        }

        [Test("Autostart is removed even when the watcher refuses to stop")]
        private static void AutostartRemovedEvenIfStopFails()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess { IsRunning = true, RefuseToStop = true };
            autostart.Register("\"" + WatcherPath + "\"");

            bool disabled = Manager(settings, autostart, watcher, WatcherPath).Disable();

            Assert.False(disabled, "reports that it did not stop");
            Assert.False(autostart.IsRegistered, "but it will not start again");
            Assert.Equal(LockMode.Manual, settings.Load().Mode, "and the mode is manual");
        }

        [Test("Uninstall cleanup stops the watcher and removes autostart")]
        private static void CleanupRemovesEverything()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess { IsRunning = true };
            autostart.Register("\"" + WatcherPath + "\"");

            bool cleaned = Manager(settings, autostart, watcher, WatcherPath).Cleanup();

            Assert.True(cleaned, "stopped");
            Assert.False(autostart.IsRegistered, "autostart gone");
            Assert.Equal(1, watcher.StopCount, "watcher asked to stop");
            Assert.Equal(0, settings.SaveCount, "uninstall does not rewrite the user's settings");
        }

        [Test("Status reports the watcher as missing when the binary is not there")]
        private static void StatusReportsMissingWatcher()
        {
            AutoLockStatus status = Manager(new FakeSettingsStore(), new FakeAutostartRegistry(),
                new FakeWatcherProcess(), @"Z:\does\not\exist.exe").GetStatus();

            Assert.False(status.WatcherInstalled, "not installed");
        }

        [Test("Status reports a running watcher when automatic is on")]
        private static void StatusReportsRunningWatcher()
        {
            var settings = new FakeSettingsStore();
            settings.Save(new AutoLockSettings { Mode = LockMode.Automatic });
            var watcher = new FakeWatcherProcess { IsRunning = true };
            string existingFile = typeof(AutoLockManagerTests).Assembly.Location;

            AutoLockStatus status = Manager(settings, new FakeAutostartRegistry(), watcher, existingFile)
                .GetStatus();

            Assert.Equal(LockMode.Automatic, status.Mode, "automatic");
            Assert.True(status.WatcherRunning, "running");
            Assert.True(status.WatcherInstalled, "installed");
        }

        [Test("The manager rejects null dependencies")]
        private static void NullDependenciesRejected()
        {
            Assert.Throws<ArgumentNullException>(
                () => new AutoLockManager(null, new FakeAutostartRegistry(), new FakeWatcherProcess(), null),
                "null settings");
            Assert.Throws<ArgumentNullException>(
                () => new AutoLockManager(new FakeSettingsStore(), null, new FakeWatcherProcess(), null),
                "null autostart");
            Assert.Throws<ArgumentNullException>(
                () => new AutoLockManager(new FakeSettingsStore(), new FakeAutostartRegistry(), null, null),
                "null watcher");
        }
    }
}
