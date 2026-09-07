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
        /// <summary>Changes that are in effect. Empty after a rollback.</summary>
        public ReadOnlyCollection<PlannedChange> Applied { get; private set; }

        /// <summary>Settings this machine does not have, named rather than silently dropped.</summary>
        public ReadOnlyCollection<string> Skipped { get; private set; }

        /// <summary>False when the plan could not be applied in full.</summary>
        public bool Success { get; private set; }

        /// <summary>What went wrong, carrying the original error. Null on success.</summary>
        public string Message { get; private set; }

        /// <summary>True when a failure was undone, returning to the previous values.</summary>
        public bool RolledBack { get; private set; }

        /// <summary>Anything the rollback could not put back.</summary>
        public ReadOnlyCollection<string> Residue { get; private set; }

        public ApplyResult(IList<PlannedChange> applied, IList<string> skipped)
            : this(applied, skipped, true, null, false, null)
        {
        }

        internal ApplyResult(IList<PlannedChange> applied, IList<string> skipped,
            bool success, string message, bool rolledBack, IList<string> residue)
        {
            Applied = new ReadOnlyCollection<PlannedChange>(applied ?? new List<PlannedChange>());
            Skipped = new ReadOnlyCollection<string>(skipped ?? new List<string>());
            Success = success;
            Message = message;
            RolledBack = rolledBack;
            Residue = new ReadOnlyCollection<string>(residue ?? new List<string>());
        }

        public bool ChangedAnything
        {
            get { return Applied.Count > 0; }
        }

        /// <summary>True when nothing was left in an in-between state.</summary>
        public bool IsClean
        {
            get { return Residue.Count == 0; }
        }

        public override string ToString()
        {
            if (Success) return ChangedAnything ? "applied" : "nothing to do";
            return "failed: " + Message;
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
        /// Either every requested change is in effect, or none is. A backup taken
        /// before the first write is not the same thing as atomicity: it makes
        /// recovery *possible* while still leaving the machine half-configured
        /// and the user unaware. So a write that fails unexpectedly unwinds the
        /// writes that already succeeded, in reverse, and says whether that
        /// unwinding was complete.
        ///
        /// A setting this machine does not have is not a failure - it is skipped
        /// by name and the plan carries on, which is the pre-existing and correct
        /// behaviour for <see cref="PowerSettingNotFoundException"/>.
        ///
        /// The backup is never deleted on a failure, even when the rollback
        /// looked clean: it is the only record of the original values, and
        /// keeping a redundant one costs nothing next to losing a needed one.
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
                catch (Exception ex)
                {
                    ApplyResult unwound = RollBack(plan, applied, skipped, backup,
                        string.Format("{0} could not be changed: {1}",
                            change.Setting.DisplayName, ex.Message));

                    // Access denied is not a defect, it is a prompt to elevate,
                    // and Setup already has a path for it. Rethrowing keeps that
                    // path working - but only after unwinding, so elevation
                    // never restarts from a half-configured machine.
                    // Rethrow only when there is nothing left to tell. If the
                    // rollback could not put everything back, the user needs to
                    // hear what is still changed more than Setup needs to offer
                    // elevation - and rethrowing would discard exactly that.
                    if (IsAccessDenied(ex) && unwound.IsClean) throw;

                    return unwound;
                }
            }

            if (applied.Count > 0)
            {
                try
                {
                    _power.ApplyScheme(plan.Scheme);
                }
                catch (Exception ex)
                {
                    // The values were written but never activated. Leaving them
                    // is the worst of both worlds: not in effect, yet different
                    // from what the user had.
                    ApplyResult unwound = RollBack(plan, applied, skipped, backup,
                        "The settings were written but Windows would not activate them: " + ex.Message);

                    // Rethrow only when there is nothing left to tell. If the
                    // rollback could not put everything back, the user needs to
                    // hear what is still changed more than Setup needs to offer
                    // elevation - and rethrowing would discard exactly that.
                    if (IsAccessDenied(ex) && unwound.IsClean) throw;

                    return unwound;
                }
            }

            // Re-save so a setting that turned out to be missing is not left in
            // the backup, which would make restore report work it did not do.
            //
            // Deliberately not a reason to roll back, and deliberately not
            // allowed to escape. The settings are applied and working, and the
            // backup written before the first write is still on disk - this save
            // only tidies it. Undoing a good configuration because a tidy-up
            // failed would be the worse outcome by far.
            try
            {
                _backups.Save(backup);
            }
            catch (Exception ex)
            {
                return new ApplyResult(applied, skipped, true, null, false,
                    new List<string> { "the backup could not be updated: " + ex.Message });
            }

            return new ApplyResult(applied, skipped);
        }

        private static bool IsAccessDenied(Exception ex)
        {
            var power = ex as PowerConfigurationException;
            return power != null && power.IsAccessDenied;
        }

        /// <summary>
        /// Puts back everything that was already written, newest first, and
        /// reports honestly if it could not.
        /// </summary>
        private ApplyResult RollBack(ConfigurationPlan plan, List<PlannedChange> applied,
            List<string> skipped, PowerBackup backup, string message)
        {
            var residue = new List<string>();

            for (int i = applied.Count - 1; i >= 0; i--)
            {
                PlannedChange change = applied[i];
                try
                {
                    _power.WriteValue(plan.Scheme, change.Setting.Subgroup, change.Setting.Setting,
                        change.Setting.Source, change.CurrentValue);
                }
                catch (Exception ex)
                {
                    // A rollback that itself fails is exactly the state a user
                    // needs told, rather than a clean-looking failure message.
                    residue.Add(string.Format("{0} is still set to {1}: {2}",
                        change.Setting.DisplayName, change.DesiredValue, ex.Message));
                }
            }

            if (applied.Count > 0)
            {
                try
                {
                    _power.ApplyScheme(plan.Scheme);
                }
                catch (Exception ex)
                {
                    residue.Add("the restored values may not be active yet: " + ex.Message);
                }
            }

            // Kept deliberately. This is the only record of the original values,
            // and it is needed most in exactly the case that just happened.
            try
            {
                _backups.Save(backup);
            }
            catch (Exception ex)
            {
                residue.Add("the backup of your original values could not be written: " + ex.Message);
            }

            return new ApplyResult(new List<PlannedChange>(), skipped, false, message,
                applied.Count > 0, residue);
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
