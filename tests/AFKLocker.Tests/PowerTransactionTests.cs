using System.Linq;
using AFKLocker.Core;

namespace AFKLocker.Tests
{
    /// <summary>
    /// Applying power settings is all-or-nothing.
    ///
    /// A backup taken before the first write is not the same thing as
    /// atomicity. It makes recovery possible, and still leaves the machine
    /// half-configured with the user unaware - which for these particular
    /// settings means a laptop that believes it will keep running with the lid
    /// shut and will actually sleep. So a write that fails unexpectedly unwinds
    /// the writes that already landed, and says whether that unwinding worked.
    /// </summary>
    internal static class PowerTransactionTests
    {
        private static FakePowerConfiguration Laptop()
        {
            var power = new FakePowerConfiguration();
            power.Set(PowerSettings.LidCloseAc, (uint)LidAction.Sleep);
            power.Set(PowerSettings.SleepAc, 600);
            power.Set(PowerSettings.HibernateAc, 1800);
            power.Set(PowerSettings.LidCloseDc, (uint)LidAction.Sleep);
            power.Set(PowerSettings.SleepDc, 600);
            power.Set(PowerSettings.HibernateDc, 1800);
            return power;
        }

        private static ConfigurationPlan PlanFor(FakePowerConfiguration power, bool battery)
        {
            PowerSnapshot snapshot = PowerSnapshot.Read(power, new FakePowerInformation());
            return ConfigurationPlanner.Create(snapshot, battery);
        }

        private static uint Recorded(PowerBackup backup, string key)
        {
            return backup.Values.First(v => v.Key == key).Value;
        }

        private static void AssertUntouched(FakePowerConfiguration power, string context)
        {
            Assert.Equal((uint)LidAction.Sleep, power.Get(PowerSettings.LidCloseAc), context + ": lid close");
            Assert.Equal(600u, power.Get(PowerSettings.SleepAc), context + ": sleep");
            Assert.Equal(1800u, power.Get(PowerSettings.HibernateAc), context + ": hibernate");
        }

        // ------------------------------------------------------- happy path ---

        [Test("Every planned change lands, and the result says so")]
        private static void EverythingApplies()
        {
            var power = Laptop();
            var backups = new InMemoryBackupStore();
            ConfigurationPlan plan = PlanFor(power, false);

            ApplyResult result = new PowerConfigurator(power, backups).Apply(plan);

            Assert.True(result.Success, "applied");
            Assert.True(result.IsClean, "nothing left over");
            Assert.False(result.RolledBack, "and nothing to roll back");
            Assert.Equal(plan.Changes.Count, result.Applied.Count, "every change is in effect");
            Assert.Equal((uint)LidAction.DoNothing, power.Get(PowerSettings.LidCloseAc), "lid close changed");
            Assert.Equal(0u, power.Get(PowerSettings.SleepAc), "sleep changed");
        }

        // ---------------------------------------------------- write failures ---

        [Test("A failure on the FIRST write leaves the machine untouched")]
        private static void FirstWriteFails()
        {
            var power = Laptop();
            power.WritesThatFail.Add(PowerSettings.LidCloseAc.Key);
            var backups = new InMemoryBackupStore();

            ApplyResult result = new PowerConfigurator(power, backups).Apply(PlanFor(power, false));

            Assert.False(result.Success, "reported as a failure");
            Assert.Equal(0, result.Applied.Count, "nothing is in effect");
            Assert.True(result.IsClean, "and there was nothing to fail at putting back");
            AssertUntouched(power, "after a first-write failure");
        }

        [Test("A failure on a LATER write puts the earlier ones back")]
        private static void SecondWriteFailsAfterFirstSucceeded()
        {
            var power = Laptop();
            power.WritesThatFail.Add(PowerSettings.SleepAc.Key);
            var backups = new InMemoryBackupStore();

            ApplyResult result = new PowerConfigurator(power, backups).Apply(PlanFor(power, false));

            Assert.False(result.Success, "reported as a failure");
            Assert.True(result.RolledBack, "and it unwound what it had done");
            Assert.True(result.IsClean, "cleanly");
            Assert.Equal(0, result.Applied.Count, "so nothing is in effect");

            // This is the case the old code got wrong: lid close had already
            // been written, and stayed written.
            AssertUntouched(power, "after a mid-plan failure");
        }

        [Test("A failure on the LAST write puts every earlier one back")]
        private static void LastWriteFails()
        {
            var power = Laptop();
            power.WritesThatFail.Add(PowerSettings.HibernateAc.Key);
            var backups = new InMemoryBackupStore();

            ApplyResult result = new PowerConfigurator(power, backups).Apply(PlanFor(power, false));

            Assert.False(result.Success, "reported as a failure");
            Assert.True(result.IsClean, "rolled back cleanly");
            AssertUntouched(power, "after a last-write failure");
        }

        [Test("The original error is carried, not swallowed")]
        private static void OriginalErrorIsReported()
        {
            var power = Laptop();
            power.WritesThatFail.Add(PowerSettings.SleepAc.Key);

            ApplyResult result = new PowerConfigurator(power, new InMemoryBackupStore())
                .Apply(PlanFor(power, false));

            Assert.NotNull(result.Message, "there is a message");
            Assert.True(result.Message.Contains("Fake unexpected failure"),
                "and it carries what Windows actually said: " + result.Message);
        }

        // ------------------------------------------------ activation failure ---

        [Test("Values written but never activated are put back")]
        private static void ApplySchemeFails()
        {
            var power = Laptop();
            power.ApplySchemeFailsWith = "the scheme could not be activated";
            power.ApplySchemeFailures = 1;      // the rollback's activation works
            var backups = new InMemoryBackupStore();

            ApplyResult result = new PowerConfigurator(power, backups).Apply(PlanFor(power, false));

            Assert.False(result.Success, "written but not in effect is not success");
            Assert.True(result.RolledBack, "so it was undone");
            Assert.True(result.IsClean, "cleanly");
            AssertUntouched(power, "after an activation failure");
        }

        // ---------------------------------------------------- failed rollback ---

        [Test("A rollback that itself fails is reported as residue, not hidden")]
        private static void RollbackFails()
        {
            var power = Laptop();
            power.WritesThatFail.Add(PowerSettings.SleepAc.Key);
            power.RollbacksThatFail.Add(PowerSettings.LidCloseAc.Key);
            var backups = new InMemoryBackupStore();

            ApplyResult result = new PowerConfigurator(power, backups).Apply(PlanFor(power, false));

            Assert.False(result.Success, "still a failure");
            Assert.False(result.IsClean, "and it could not fully undo itself");
            Assert.True(result.Residue.Any(r => r.Contains("Lid close")),
                "the residue names what is still changed");

            // Claiming a clean revert here would be the dangerous lie: the lid
            // setting really is still changed.
            Assert.Equal((uint)LidAction.DoNothing, power.Get(PowerSettings.LidCloseAc),
                "and the test agrees with the report");
        }

        // ----------------------------------------------------------- backup ---

        [Test("The original values survive a failure, so they can still be restored by hand")]
        private static void BackupSurvivesFailure()
        {
            var power = Laptop();
            power.WritesThatFail.Add(PowerSettings.SleepAc.Key);
            var backups = new InMemoryBackupStore();

            var configurator = new PowerConfigurator(power, backups);
            ApplyResult result = configurator.Apply(PlanFor(power, false));

            Assert.False(result.Success, "it failed");
            Assert.True(configurator.HasBackup, "but the backup is still there");

            PowerBackup backup = backups.Load(power.ActiveScheme);
            Assert.NotNull(backup, "and it loads");
            Assert.Equal((uint)LidAction.Sleep, Recorded(backup, PowerSettings.LidCloseAc.Key),
                "holding the value from before AFKLocker touched anything");
            Assert.Equal(600u, Recorded(backup, PowerSettings.SleepAc.Key), "for every planned setting");
        }

        [Test("Access denied is still raised, so Setup can offer to elevate")]
        private static void AccessDeniedStillThrows()
        {
            var power = Laptop();
            power.WriteFailsWithErrorCode = 5;      // ERROR_ACCESS_DENIED
            var backups = new InMemoryBackupStore();
            ConfigurationPlan plan = PlanFor(power, false);

            // Turning this into a returned result would silently remove the
            // elevation prompt, which is the whole recovery path for a machine
            // that simply needs administrator rights.
            Assert.Throws<PowerConfigurationException>(
                () => new PowerConfigurator(power, backups).Apply(plan),
                "access denied must keep surfacing as an exception");

            AssertUntouched(power, "and nothing was left applied");
        }

        [Test("A setting this machine does not have is skipped, not treated as a failure")]
        private static void MissingSettingIsSkippedNotFailed()
        {
            var power = Laptop();
            ConfigurationPlan plan = PlanFor(power, false);

            // Absent only once the plan exists, which is how a machine that
            // changes under us behaves.
            power.MissingSettings.Add(PowerSettings.HibernateAc.Key);

            ApplyResult result = new PowerConfigurator(power, new InMemoryBackupStore()).Apply(plan);

            Assert.True(result.Success, "a missing setting is not a failure");
            Assert.True(result.Skipped.Count > 0, "it is named");
            Assert.Equal((uint)LidAction.DoNothing, power.Get(PowerSettings.LidCloseAc),
                "and the rest of the plan still applied");
        }
    }
}
