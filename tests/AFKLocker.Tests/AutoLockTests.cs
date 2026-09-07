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

        [Test("Displays are dimmed only on a lock, never on lid-open")]
        private static void DisplaysDimOnlyWhenLocking()
        {
            var locker = new FakeSessionLocker();
            var displays = new FakeDisplayController();
            var policy = Policy(locker, new FakeSessionState());

            // The watcher's rule: dim only when Handle reports it locked.
            Action<LidState> handle = delegate(LidState state)
            {
                if (policy.Handle(state) == AutoLockDecision.Locked) displays.TurnOff();
            };

            handle(LidState.Opened);   // initial position
            Assert.Equal(0, displays.TurnOffCount, "nothing on the starting position");

            handle(LidState.Closed);
            Assert.Equal(1, displays.TurnOffCount, "dimmed once, with the lock");

            handle(LidState.Opened);
            Assert.Equal(1, displays.TurnOffCount, "opening the lid never dims");

            handle(LidState.Closed);
            Assert.Equal(2, displays.TurnOffCount, "and again on the next real close");
        }

        [Test("A close that does not lock does not dim either")]
        private static void NoLockMeansNoDim()
        {
            var displays = new FakeDisplayController();
            var policy = Policy(new FakeSessionLocker(), new FakeSessionState { IsLocked = true });

            Action<LidState> handle = delegate(LidState state)
            {
                if (policy.Handle(state) == AutoLockDecision.Locked) displays.TurnOff();
            };

            handle(LidState.Opened);
            handle(LidState.Closed);   // session already locked, so no lock happens

            Assert.Equal(0, displays.TurnOffCount,
                "an already-locked session is left entirely alone, screens included");
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

    internal static class AutoLockAdvisorTests
    {
        [Test("Manual mode is never warned about")]
        private static void ManualSaysNothing()
        {
            PowerSnapshot snapshot = SnapshotBuilder.ReadyLaptop();
            snapshot.Capabilities.LidPresent = false;

            Assert.Equal(AutoLockWarning.None,
                AutoLockAdvisor.Evaluate(LockMode.Manual, snapshot),
                "nothing is running in manual mode, so there is nothing to warn about");
        }

        [Test("A machine that reports no lid is called out, because it will silently never lock")]
        private static void NoLidIsWarned()
        {
            PowerSnapshot snapshot = SnapshotBuilder.ReadyLaptop();
            snapshot.Capabilities.LidPresent = false;

            AutoLockWarning warning = AutoLockAdvisor.Evaluate(LockMode.Automatic, snapshot);

            Assert.Equal(AutoLockWarning.NoLidReported, warning, "the important one");
            Assert.True(AutoLockAdvisor.Describe(warning).Contains("ACPI Lid"),
                "and it names the device to check, rather than just saying it will not work");
        }

        [Test("No lid outranks the battery note - it stops locking entirely")]
        private static void NoLidTakesPriority()
        {
            PowerSnapshot snapshot = SnapshotBuilder.ReadyLaptop();   // battery sleeps after 30 min
            snapshot.Capabilities.LidPresent = false;

            Assert.Equal(AutoLockWarning.NoLidReported,
                AutoLockAdvisor.Evaluate(LockMode.Automatic, snapshot),
                "a machine that cannot lock at all is the bigger problem");
        }

        [Test("A machine that sleeps on battery gets the battery note")]
        private static void BatterySleepIsWarned()
        {
            AutoLockWarning warning = AutoLockAdvisor.Evaluate(
                LockMode.Automatic, SnapshotBuilder.ReadyLaptop());

            Assert.Equal(AutoLockWarning.BatteryMaySleep, warning, "battery note");
            Assert.True(AutoLockAdvisor.Describe(warning).Contains("battery"), "mentions battery");
        }

        [Test("A machine configured for battery too gets no warning at all")]
        private static void FullyConfiguredIsSilent()
        {
            PowerSnapshot snapshot = SnapshotBuilder.ReadyLaptop();
            snapshot[PowerSettings.LidCloseDc] = SettingValue.Of((uint)LidAction.DoNothing);
            snapshot[PowerSettings.SleepDc] = SettingValue.Of(0);

            Assert.Equal(AutoLockWarning.None,
                AutoLockAdvisor.Evaluate(LockMode.Automatic, snapshot), "nothing to say");
            Assert.Null(AutoLockAdvisor.Describe(AutoLockWarning.None), "and no text for it");
        }

        [Test("A null snapshot produces no warning rather than an exception")]
        private static void NullSnapshotIsSafe()
        {
            Assert.Equal(AutoLockWarning.None,
                AutoLockAdvisor.Evaluate(LockMode.Automatic, null),
                "the window must still open when power settings cannot be read");
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


}
