using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace AFKLocker.Core
{
    /// <summary>One setting AFKLocker intends to change.</summary>
    public sealed class PlannedChange
    {
        public PowerSettingRef Setting { get; private set; }
        public uint CurrentValue { get; private set; }
        public uint DesiredValue { get; private set; }

        public PlannedChange(PowerSettingRef setting, uint currentValue, uint desiredValue)
        {
            Setting = setting;
            CurrentValue = currentValue;
            DesiredValue = desiredValue;
        }

        public override string ToString()
        {
            return string.Format("{0}: {1} -> {2}", Setting.Key, CurrentValue, DesiredValue);
        }
    }

    /// <summary>The full set of changes for one apply operation.</summary>
    public sealed class ConfigurationPlan
    {
        public Guid Scheme { get; private set; }
        public bool IncludesBattery { get; private set; }
        public ReadOnlyCollection<PlannedChange> Changes { get; private set; }

        public ConfigurationPlan(Guid scheme, bool includesBattery, IList<PlannedChange> changes)
        {
            Scheme = scheme;
            IncludesBattery = includesBattery;
            Changes = new ReadOnlyCollection<PlannedChange>(changes);
        }

        public bool HasChanges
        {
            get { return Changes.Count > 0; }
        }
    }

    /// <summary>
    /// Decides which settings need changing. Pure logic: it only reads a
    /// snapshot and never touches Windows.
    /// </summary>
    public static class ConfigurationPlanner
    {
        /// <summary>
        /// Builds the change set that makes closed-lid operation work.
        ///
        /// Only settings that exist and are not already correct are included, so
        /// running this on an already configured machine produces an empty plan.
        /// The display timeout is deliberately left alone: AFKLocker keeps the
        /// system awake, not the screen.
        /// </summary>
        public static ConfigurationPlan Create(PowerSnapshot snapshot, bool includeBattery)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");

            var changes = new List<PlannedChange>();

            AddIfNeeded(changes, snapshot, PowerSettings.LidCloseAc, (uint)LidAction.DoNothing);
            AddIfNeeded(changes, snapshot, PowerSettings.SleepAc, 0);
            AddIfNeeded(changes, snapshot, PowerSettings.HibernateAc, 0);

            if (includeBattery)
            {
                AddIfNeeded(changes, snapshot, PowerSettings.LidCloseDc, (uint)LidAction.DoNothing);
                AddIfNeeded(changes, snapshot, PowerSettings.SleepDc, 0);
                AddIfNeeded(changes, snapshot, PowerSettings.HibernateDc, 0);
            }

            return new ConfigurationPlan(snapshot.Scheme, includeBattery, changes);
        }

        private static void AddIfNeeded(ICollection<PlannedChange> changes, PowerSnapshot snapshot,
            PowerSettingRef setting, uint desired)
        {
            SettingValue current = snapshot[setting];
            if (!current.IsPresent) return;      // not available on this machine
            if (current.Value == desired) return; // already correct
            changes.Add(new PlannedChange(setting, current.Value, desired));
        }
    }

    public sealed class ApplyResult
    {
        public ReadOnlyCollection<PlannedChange> Applied { get; private set; }
        public ReadOnlyCollection<string> Skipped { get; private set; }

        public ApplyResult(IList<PlannedChange> applied, IList<string> skipped)
        {
            Applied = new ReadOnlyCollection<PlannedChange>(applied);
            Skipped = new ReadOnlyCollection<string>(skipped);
        }

        public bool ChangedAnything
        {
            get { return Applied.Count > 0; }
        }
    }

    public sealed class RestoreResult
    {
        public int SettingsRestored { get; private set; }
        public ReadOnlyCollection<Guid> SchemesRestored { get; private set; }
        public ReadOnlyCollection<string> Skipped { get; private set; }

        public RestoreResult(int settingsRestored, IList<Guid> schemes, IList<string> skipped)
        {
            SettingsRestored = settingsRestored;
            SchemesRestored = new ReadOnlyCollection<Guid>(schemes);
            Skipped = new ReadOnlyCollection<string>(skipped);
        }

        public bool RestoredAnything
        {
            get { return SettingsRestored > 0; }
        }
    }

    /// <summary>
    /// Applies and reverses power configuration changes, keeping a backup of the
    /// values that were there first.
    /// </summary>
    public sealed class PowerConfigurator
    {
        private readonly IPowerConfiguration _power;
        private readonly IBackupStore _backups;

        public PowerConfigurator(IPowerConfiguration power, IBackupStore backups)
        {
            if (power == null) throw new ArgumentNullException("power");
            if (backups == null) throw new ArgumentNullException("backups");
            _power = power;
            _backups = backups;
        }

        /// <summary>
        /// Writes the planned values, recording the previous ones first.
        ///
        /// The backup is saved before anything is written, so a failure halfway
        /// through still leaves a usable record of the original state.
        /// </summary>
        public ApplyResult Apply(ConfigurationPlan plan)
        {
            if (plan == null) throw new ArgumentNullException("plan");

            var applied = new List<PlannedChange>();
            var skipped = new List<string>();

            if (!plan.HasChanges)
                return new ApplyResult(applied, skipped);

            PowerBackup backup = _backups.Load(plan.Scheme) ?? new PowerBackup
            {
                Scheme = plan.Scheme,
                SchemeName = _power.GetSchemeName(plan.Scheme)
            };

            foreach (PlannedChange change in plan.Changes)
                backup.RecordOriginal(change.Setting.Key, change.CurrentValue);

            _backups.Save(backup);

            foreach (PlannedChange change in plan.Changes)
            {
                try
                {
                    _power.WriteValue(plan.Scheme, change.Setting.Subgroup, change.Setting.Setting,
                        change.Setting.Source, change.DesiredValue);
                    applied.Add(change);
                }
                catch (PowerSettingNotFoundException)
                {
                    skipped.Add(string.Format("{0}: not available on this machine.", change.Setting.DisplayName));
                    backup.Remove(change.Setting.Key);
                }
            }

            if (applied.Count > 0)
                _power.ApplyScheme(plan.Scheme);

            // Re-save so a setting that turned out to be missing is not left in
            // the backup, which would make restore report work it did not do.
            _backups.Save(backup);

            return new ApplyResult(applied, skipped);
        }

        /// <summary>Restores every scheme that has a backup.</summary>
        public RestoreResult RestoreAll()
        {
            var schemes = _backups.ListSchemes().ToList();
            int restored = 0;
            var skipped = new List<string>();
            var done = new List<Guid>();

            foreach (Guid scheme in schemes)
            {
                RestoreResult one = Restore(scheme);
                restored += one.SettingsRestored;
                skipped.AddRange(one.Skipped);
                if (one.RestoredAnything) done.Add(scheme);
            }

            return new RestoreResult(restored, done, skipped);
        }

        /// <summary>
        /// Restores one scheme from its backup and deletes the backup.
        ///
        /// Restore targets the scheme the backup was taken from, which is not
        /// necessarily the active one - the user may have switched plans since.
        /// </summary>
        public RestoreResult Restore(Guid scheme)
        {
            var skipped = new List<string>();
            PowerBackup backup = _backups.Load(scheme);

            if (backup == null)
                return new RestoreResult(0, new List<Guid>(), skipped);

            int restored = 0;
            foreach (var entry in backup.Values.ToList())
            {
                PowerSettingRef setting = PowerSettings.ByKey(entry.Key);
                if (setting == null)
                {
                    skipped.Add(string.Format("Unknown setting \"{0}\" in backup - left untouched.", entry.Key));
                    continue;
                }

                try
                {
                    _power.WriteValue(backup.Scheme, setting.Subgroup, setting.Setting, setting.Source, entry.Value);
                    restored++;
                }
                catch (PowerSettingNotFoundException)
                {
                    skipped.Add(string.Format("{0}: no longer available on this machine.", setting.DisplayName));
                }
            }

            if (restored > 0)
                _power.ApplyScheme(backup.Scheme);

            _backups.Delete(scheme);

            return new RestoreResult(restored, restored > 0 ? new List<Guid> { scheme } : new List<Guid>(), skipped);
        }

        /// <summary>True when there is anything to restore.</summary>
        public bool HasBackup
        {
            get { return _backups.ListSchemes().Any(); }
        }
    }
}
