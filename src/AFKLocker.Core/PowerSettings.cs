using System;
using System.Collections.Generic;
using System.Linq;

namespace AFKLocker.Core
{
    /// <summary>
    /// One addressable power setting: a subgroup, a setting and a power source.
    /// The <see cref="Key"/> is the stable identifier used in backup files, so
    /// it must never change once released.
    /// </summary>
    public sealed class PowerSettingRef
    {
        public string Key { get; private set; }
        public string DisplayName { get; private set; }
        public Guid Subgroup { get; private set; }
        public Guid Setting { get; private set; }
        public PowerSource Source { get; private set; }

        public PowerSettingRef(string key, string displayName, Guid subgroup, Guid setting, PowerSource source)
        {
            Key = key;
            DisplayName = displayName;
            Subgroup = subgroup;
            Setting = setting;
            Source = source;
        }

        public override string ToString()
        {
            return Key;
        }
    }

    /// <summary>The settings AFKLocker reads, and may change.</summary>
    public static class PowerSettings
    {
        public static readonly PowerSettingRef LidCloseAc = new PowerSettingRef(
            "lid-ac", "Lid close action (plugged in)",
            PowerSettingIds.ButtonsSubgroup, PowerSettingIds.LidCloseAction, PowerSource.AC);

        public static readonly PowerSettingRef LidCloseDc = new PowerSettingRef(
            "lid-dc", "Lid close action (on battery)",
            PowerSettingIds.ButtonsSubgroup, PowerSettingIds.LidCloseAction, PowerSource.DC);

        public static readonly PowerSettingRef SleepAc = new PowerSettingRef(
            "sleep-ac", "System sleep timeout (plugged in)",
            PowerSettingIds.SleepSubgroup, PowerSettingIds.StandbyTimeout, PowerSource.AC);

        public static readonly PowerSettingRef SleepDc = new PowerSettingRef(
            "sleep-dc", "System sleep timeout (on battery)",
            PowerSettingIds.SleepSubgroup, PowerSettingIds.StandbyTimeout, PowerSource.DC);

        public static readonly PowerSettingRef HibernateAc = new PowerSettingRef(
            "hibernate-ac", "Hibernate timeout (plugged in)",
            PowerSettingIds.SleepSubgroup, PowerSettingIds.HibernateTimeout, PowerSource.AC);

        public static readonly PowerSettingRef HibernateDc = new PowerSettingRef(
            "hibernate-dc", "Hibernate timeout (on battery)",
            PowerSettingIds.SleepSubgroup, PowerSettingIds.HibernateTimeout, PowerSource.DC);

        private static readonly PowerSettingRef[] AllSettings =
        {
            LidCloseAc, LidCloseDc, SleepAc, SleepDc, HibernateAc, HibernateDc
        };

        public static IEnumerable<PowerSettingRef> All
        {
            get { return AllSettings; }
        }

        /// <summary>Looks up a setting by its stable key. Returns null when unknown.</summary>
        public static PowerSettingRef ByKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            return AllSettings.FirstOrDefault(s =>
                string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// A setting value that may be absent. Absence is normal: a desktop has no
    /// lid setting, and hibernate settings disappear when hibernation is off.
    /// </summary>
    public struct SettingValue
    {
        public bool IsPresent { get; private set; }
        public uint Value { get; private set; }

        public static readonly SettingValue Absent = new SettingValue();

        public static SettingValue Of(uint value)
        {
            return new SettingValue { IsPresent = true, Value = value };
        }

        public bool Is(uint expected)
        {
            return IsPresent && Value == expected;
        }

        public override string ToString()
        {
            return IsPresent ? Value.ToString() : "(absent)";
        }
    }

    /// <summary>Formatting helpers shared by the UI and the report text.</summary>
    public static class PowerValueFormatter
    {
        /// <summary>Renders a timeout in seconds the way Windows describes it.</summary>
        public static string Timeout(SettingValue value)
        {
            if (!value.IsPresent) return "not available";
            if (value.Value == 0) return "Never";

            uint seconds = value.Value;
            if (seconds % 3600 == 0)
            {
                uint hours = seconds / 3600;
                return hours == 1 ? "1 hour" : hours + " hours";
            }
            if (seconds % 60 == 0)
            {
                uint minutes = seconds / 60;
                return minutes == 1 ? "1 minute" : minutes + " minutes";
            }
            return seconds + " seconds";
        }

        /// <summary>Renders a lid close action value.</summary>
        public static string Lid(SettingValue value)
        {
            if (!value.IsPresent) return "not available";
            switch (value.Value)
            {
                case (uint)LidAction.DoNothing: return "Do nothing";
                case (uint)LidAction.Sleep: return "Sleep";
                case (uint)LidAction.Hibernate: return "Hibernate";
                case (uint)LidAction.Shutdown: return "Shut down";
                default: return "Unknown (" + value.Value + ")";
            }
        }
    }
}
