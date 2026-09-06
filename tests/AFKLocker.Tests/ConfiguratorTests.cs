using System;
using System.Linq;
using AFKLocker.Core;

namespace AFKLocker.Tests
{
    internal static class ConfiguratorTests
    {
        private static FakePowerConfiguration StockMachine()
        {
            var power = new FakePowerConfiguration();
            power.Set(PowerSettings.LidCloseAc, (uint)LidAction.Sleep);
            power.Set(PowerSettings.LidCloseDc, (uint)LidAction.Sleep);
            power.Set(PowerSettings.SleepAc, 1800);
            power.Set(PowerSettings.SleepDc, 900);
            power.MissingSettings.Add(PowerSettings.HibernateAc.Key);
            power.MissingSettings.Add(PowerSettings.HibernateDc.Key);
            return power;
        }

        [Test("Reading a snapshot tolerates settings that do not exist")]
        private static void SnapshotHandlesMissingSettings()
        {
            FakePowerConfiguration power = StockMachine();

            PowerSnapshot snapshot = PowerSnapshot.Read(power, new FakePowerInformation());

            Assert.True(snapshot[PowerSettings.SleepAc].IsPresent, "sleep is present");
            Assert.False(snapshot[PowerSettings.HibernateAc].IsPresent, "hibernate is absent");
            Assert.Equal("Test Plan", snapshot.SchemeName, "scheme name is read");
        }

        [Test("The plan only touches AC settings unless battery is opted in")]
        private static void PlanLeavesBatteryAloneByDefault()
        {
            PowerSnapshot snapshot = PowerSnapshot.Read(StockMachine(), new FakePowerInformation());

            ConfigurationPlan plan = ConfigurationPlanner.Create(snapshot, includeBattery: false);

            Assert.Equal(2, plan.Changes.Count, "lid-ac and sleep-ac only");
            Assert.False(plan.Changes.Any(c => c.Setting.Source == PowerSource.DC),
                "no battery settings in the default plan");
        }

        [Test("Opting into battery adds the DC settings")]
        private static void PlanIncludesBatteryWhenAsked()
        {
            PowerSnapshot snapshot = PowerSnapshot.Read(StockMachine(), new FakePowerInformation());

            ConfigurationPlan plan = ConfigurationPlanner.Create(snapshot, includeBattery: true);

            Assert.Equal(4, plan.Changes.Count, "both AC and DC settings");
            Assert.True(plan.IncludesBattery, "flagged as including battery");
        }

        [Test("A machine that is already configured produces an empty plan")]
        private static void AlreadyConfiguredMeansNothingToDo()
        {
            ConfigurationPlan plan = ConfigurationPlanner.Create(
                SnapshotBuilder.ReadyLaptop(), includeBattery: false);

            Assert.False(plan.HasChanges, "nothing to change");
        }

        [Test("The display timeout is never part of a plan")]
        private static void DisplayTimeoutIsNeverChanged()
        {
            PowerSnapshot snapshot = PowerSnapshot.Read(StockMachine(), new FakePowerInformation());

            ConfigurationPlan plan = ConfigurationPlanner.Create(snapshot, includeBattery: true);

            Assert.False(plan.Changes.Any(c => c.Setting.Setting == PowerSettingIds.VideoTimeout),
                "AFKLocker keeps the system awake, not the screen");
        }

        [Test("Applying writes the new values and records the old ones")]
        private static void ApplyWritesAndBacksUp()
        {
            FakePowerConfiguration power = StockMachine();
            var store = new InMemoryBackupStore();
            PowerSnapshot snapshot = PowerSnapshot.Read(power, new FakePowerInformation());
            ConfigurationPlan plan = ConfigurationPlanner.Create(snapshot, includeBattery: false);

            ApplyResult result = new PowerConfigurator(power, store).Apply(plan);

            Assert.Equal(2, result.Applied.Count, "two settings applied");
            Assert.Equal((uint)LidAction.DoNothing, power.Get(PowerSettings.LidCloseAc), "lid now does nothing");
            Assert.Equal(0u, power.Get(PowerSettings.SleepAc), "sleep now never");
            Assert.Equal((uint)LidAction.Sleep, power.Get(PowerSettings.LidCloseDc), "battery untouched");
            Assert.Equal(1, power.ApplySchemeCallCount, "the scheme is committed once");

            PowerBackup backup = store.Load(power.ActiveScheme);
            Assert.NotNull(backup, "a backup was written");
            uint originalSleep;
            backup.TryGet(PowerSettings.SleepAc.Key, out originalSleep);
            Assert.Equal(1800u, originalSleep, "the original sleep timeout was recorded");
        }

        [Test("Re-running apply keeps the original backup rather than the values it already set")]
        private static void ReapplyPreservesTheOriginalBackup()
        {
            FakePowerConfiguration power = StockMachine();
            var store = new InMemoryBackupStore();
            var configurator = new PowerConfigurator(power, store);

            configurator.Apply(ConfigurationPlanner.Create(
                PowerSnapshot.Read(power, new FakePowerInformation()), false));

            // Simulate the user changing a setting back by hand, then re-running setup.
            power.Set(PowerSettings.SleepAc, 300);
            configurator.Apply(ConfigurationPlanner.Create(
                PowerSnapshot.Read(power, new FakePowerInformation()), false));

            uint recorded;
            store.Load(power.ActiveScheme).TryGet(PowerSettings.SleepAc.Key, out recorded);
            Assert.Equal(1800u, recorded, "still the value from before AFKLocker ever ran");
        }

        [Test("Restore puts every recorded value back and drops the backup")]
        private static void RestoreReturnsTheMachineToItsOriginalState()
        {
            FakePowerConfiguration power = StockMachine();
            var store = new InMemoryBackupStore();
            var configurator = new PowerConfigurator(power, store);
            configurator.Apply(ConfigurationPlanner.Create(
                PowerSnapshot.Read(power, new FakePowerInformation()), true));

            RestoreResult result = configurator.RestoreAll();

            Assert.Equal(4, result.SettingsRestored, "four settings restored");
            Assert.Equal((uint)LidAction.Sleep, power.Get(PowerSettings.LidCloseAc), "lid back to sleep");
            Assert.Equal(1800u, power.Get(PowerSettings.SleepAc), "AC sleep back to 30 minutes");
            Assert.Equal(900u, power.Get(PowerSettings.SleepDc), "DC sleep back to 15 minutes");
            Assert.False(configurator.HasBackup, "the backup is consumed");
        }

        [Test("Restore with no backup is a no-op, not an error")]
        private static void RestoreWithoutBackupDoesNothing()
        {
            FakePowerConfiguration power = StockMachine();
            var configurator = new PowerConfigurator(power, new InMemoryBackupStore());

            RestoreResult result = configurator.RestoreAll();

            Assert.False(result.RestoredAnything, "nothing restored");
            Assert.Equal(0, power.Writes.Count, "nothing written");
        }

        [Test("Restore targets the scheme the backup came from, not the active one")]
        private static void RestoreFollowsTheBackupsScheme()
        {
            FakePowerConfiguration power = StockMachine();
            var configuredScheme = power.ActiveScheme;
            var store = new InMemoryBackupStore();
            var configurator = new PowerConfigurator(power, store);

            configurator.Apply(ConfigurationPlanner.Create(
                PowerSnapshot.Read(power, new FakePowerInformation()), false));

            // The user switches to a different power plan before uninstalling.
            var otherScheme = new Guid("99999999-9999-9999-9999-999999999999");
            power.ActiveScheme = otherScheme;
            power.SetForScheme(otherScheme, PowerSettings.LidCloseAc, (uint)LidAction.Shutdown);
            power.SetForScheme(otherScheme, PowerSettings.SleepAc, 60);

            configurator.RestoreAll();

            Assert.Equal((uint)LidAction.Sleep, power.GetForScheme(configuredScheme, PowerSettings.LidCloseAc),
                "the scheme AFKLocker changed is the one restored");
            Assert.Equal((uint)LidAction.Shutdown, power.GetForScheme(otherScheme, PowerSettings.LidCloseAc),
                "the plan the user switched to is left alone");
        }

        [Test("Access denied surfaces as an access denied error, not a generic failure")]
        private static void AccessDeniedIsRecognisable()
        {
            FakePowerConfiguration power = StockMachine();
            power.WriteFailsWithErrorCode = 5;
            var configurator = new PowerConfigurator(power, new InMemoryBackupStore());
            ConfigurationPlan plan = ConfigurationPlanner.Create(
                PowerSnapshot.Read(power, new FakePowerInformation()), false);

            PowerConfigurationException error = Assert.Throws<PowerConfigurationException>(
                () => configurator.Apply(plan), "write refused");

            Assert.True(error.IsAccessDenied, "recognised as an elevation problem");
        }

        [Test("A backup is written before any setting is changed")]
        private static void BackupIsWrittenBeforeWriting()
        {
            FakePowerConfiguration power = StockMachine();
            power.WriteFailsWithErrorCode = 1359; // an internal error, not access denied
            var store = new InMemoryBackupStore();
            ConfigurationPlan plan = ConfigurationPlanner.Create(
                PowerSnapshot.Read(power, new FakePowerInformation()), false);

            try
            {
                new PowerConfigurator(power, store).Apply(plan);
            }
            catch (PowerConfigurationException)
            {
                // expected
            }

            Assert.NotNull(store.Load(power.ActiveScheme),
                "the original values survive a failure halfway through");
        }

        [Test("A setting that disappears between plan and apply is skipped, not fatal")]
        private static void VanishedSettingIsSkipped()
        {
            FakePowerConfiguration power = StockMachine();
            var store = new InMemoryBackupStore();
            ConfigurationPlan plan = ConfigurationPlanner.Create(
                PowerSnapshot.Read(power, new FakePowerInformation()), false);

            power.MissingSettings.Add(PowerSettings.LidCloseAc.Key);

            ApplyResult result = new PowerConfigurator(power, store).Apply(plan);

            Assert.Equal(1, result.Applied.Count, "the remaining setting still applies");
            Assert.Equal(1, result.Skipped.Count, "the missing one is reported");
            Assert.False(store.Load(power.ActiveScheme).Contains(PowerSettings.LidCloseAc.Key),
                "a setting that was never changed is not left in the backup");
        }

        [Test("An empty plan writes nothing and creates no backup")]
        private static void EmptyPlanIsInert()
        {
            var power = new FakePowerConfiguration();
            var store = new InMemoryBackupStore();
            var plan = ConfigurationPlanner.Create(SnapshotBuilder.ReadyLaptop(), false);

            ApplyResult result = new PowerConfigurator(power, store).Apply(plan);

            Assert.False(result.ChangedAnything, "nothing applied");
            Assert.Equal(0, store.SaveCount, "no backup file created");
            Assert.Equal(0, power.ApplySchemeCallCount, "the scheme is not touched");
        }

        [Test("The configurator rejects null dependencies instead of failing later")]
        private static void NullDependenciesAreRejected()
        {
            Assert.Throws<ArgumentNullException>(
                () => new PowerConfigurator(null, new InMemoryBackupStore()), "null power");
            Assert.Throws<ArgumentNullException>(
                () => new PowerConfigurator(new FakePowerConfiguration(), null), "null store");
        }
    }

    internal static class SessionLockerTests
    {
        [Test("Locking goes through an abstraction so tests never lock the machine")]
        private static void LockIsAbstracted()
        {
            var locker = new FakeSessionLocker();

            locker.Lock();
            locker.Lock();

            Assert.Equal(2, locker.LockCount, "both requests recorded");
            Assert.True(typeof(ISessionLocker).IsAssignableFrom(typeof(WindowsSessionLocker)),
                "the real locker implements the same interface");
        }
    }
}
