using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace AFKLocker.Core
{
    public enum CheckStatus
    {
        /// <summary>Configured the way AFKLocker needs.</summary>
        Ready,

        /// <summary>Would stop the machine from working with the lid closed.</summary>
        NeedsConfiguration,

        /// <summary>Not required. Reported so the user can decide.</summary>
        Optional,

        /// <summary>The setting or capability does not exist on this machine.</summary>
        Unavailable,

        /// <summary>Ready, but something about this machine deserves a mention.</summary>
        Warning
    }

    public sealed class ReadinessCheck
    {
        public string Id { get; private set; }
        public string Title { get; private set; }
        public CheckStatus Status { get; private set; }
        public string Detail { get; private set; }

        public ReadinessCheck(string id, string title, CheckStatus status, string detail)
        {
            Id = id;
            Title = title;
            Status = status;
            Detail = detail;
        }

        public override string ToString()
        {
            return string.Format("{0}: {1} ({2})", Title, Status, Detail);
        }
    }

    public sealed class ReadinessReport
    {
        public ReadOnlyCollection<ReadinessCheck> Checks { get; private set; }
        public PowerSnapshot Snapshot { get; private set; }

        public ReadinessReport(PowerSnapshot snapshot, IList<ReadinessCheck> checks)
        {
            Snapshot = snapshot;
            Checks = new ReadOnlyCollection<ReadinessCheck>(checks);
        }

        /// <summary>
        /// True when nothing in the required set would stop the machine from
        /// running with the lid closed while plugged in.
        /// </summary>
        public bool IsReady
        {
            get { return Checks.All(c => c.Status != CheckStatus.NeedsConfiguration); }
        }

        /// <summary>One-line verdict, as shown in the setup window.</summary>
        public string Summary
        {
            get
            {
                if (!IsReady)
                    return "Not ready - Windows would still suspend this machine";
                return Checks.Any(c => c.Status == CheckStatus.Warning)
                    ? "Ready for AFKLocker - with notes below"
                    : "Ready for AFKLocker";
            }
        }

        public ReadinessCheck Find(string id)
        {
            return Checks.FirstOrDefault(c => c.Id == id);
        }
    }

    /// <summary>
    /// Turns a <see cref="PowerSnapshot"/> into a human readable readiness
    /// verdict. Pure logic, no Windows calls - this is the part unit tests
    /// exercise most.
    /// </summary>
    public static class ReadinessEvaluator
    {
        public const string LidCheckId = "lid-ac";
        public const string SleepCheckId = "sleep-ac";
        public const string HibernateCheckId = "hibernate-ac";
        public const string BatteryCheckId = "battery";
        public const string ModernStandbyCheckId = "modern-standby";

        public static ReadinessReport Evaluate(PowerSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");

            var caps = snapshot.Capabilities ?? new SystemCapabilities();
            var checks = new List<ReadinessCheck>
            {
                EvaluateLid(snapshot, caps),
                EvaluateSleep(snapshot),
                EvaluateHibernate(snapshot, caps),
                EvaluateBattery(snapshot, caps)
            };

            if (caps.ModernStandby)
            {
                checks.Add(new ReadinessCheck(ModernStandbyCheckId, "Modern Standby", CheckStatus.Warning,
                    "This machine uses Modern Standby (S0 low power idle). It may still enter a low " +
                    "power state with the lid closed regardless of these timeouts."));
            }

            return new ReadinessReport(snapshot, checks);
        }

        private static ReadinessCheck EvaluateLid(PowerSnapshot snapshot, SystemCapabilities caps)
        {
            SettingValue value = snapshot[PowerSettings.LidCloseAc];

            if (!value.IsPresent)
            {
                // Note: readiness keys off the setting, not caps.LidPresent. Some
                // firmware reports LidPresent = false on machines that plainly do
                // have a lid, so it is only used to word this message.
                string detail = caps.LidPresent
                    ? "Windows does not expose a lid close action on this machine."
                    : "This machine has no lid close setting - nothing to configure.";
                return new ReadinessCheck(LidCheckId, "Lid close while plugged in",
                    CheckStatus.Unavailable, detail);
            }

            if (value.Is((uint)LidAction.DoNothing))
                return new ReadinessCheck(LidCheckId, "Lid close while plugged in",
                    CheckStatus.Ready, "Do nothing - the machine keeps running.");

            return new ReadinessCheck(LidCheckId, "Lid close while plugged in",
                CheckStatus.NeedsConfiguration,
                string.Format("Currently \"{0}\" - closing the lid would interrupt your work.",
                    PowerValueFormatter.Lid(value)));
        }

        private static ReadinessCheck EvaluateSleep(PowerSnapshot snapshot)
        {
            SettingValue value = snapshot[PowerSettings.SleepAc];

            if (!value.IsPresent)
                return new ReadinessCheck(SleepCheckId, "System sleep while plugged in",
                    CheckStatus.Unavailable, "Windows does not expose a sleep timeout on this machine.");

            if (value.Is(0))
                return new ReadinessCheck(SleepCheckId, "System sleep while plugged in",
                    CheckStatus.Ready, "Never - the system stays awake.");

            return new ReadinessCheck(SleepCheckId, "System sleep while plugged in",
                CheckStatus.NeedsConfiguration,
                string.Format("Sleeps after {0} - long tasks would be suspended.",
                    PowerValueFormatter.Timeout(value)));
        }

        private static ReadinessCheck EvaluateHibernate(PowerSnapshot snapshot, SystemCapabilities caps)
        {
            SettingValue value = snapshot[PowerSettings.HibernateAc];

            if (!value.IsPresent || !caps.HibernateFilePresent)
                return new ReadinessCheck(HibernateCheckId, "Hibernate while plugged in",
                    CheckStatus.Unavailable, "Hibernation is not enabled on this machine.");

            if (value.Is(0))
                return new ReadinessCheck(HibernateCheckId, "Hibernate while plugged in",
                    CheckStatus.Ready, "Never.");

            return new ReadinessCheck(HibernateCheckId, "Hibernate while plugged in",
                CheckStatus.NeedsConfiguration,
                string.Format("Hibernates after {0} - the session would be written to disk and stopped.",
                    PowerValueFormatter.Timeout(value)));
        }

        private static ReadinessCheck EvaluateBattery(PowerSnapshot snapshot, SystemCapabilities caps)
        {
            if (!caps.BatteryPresent)
                return new ReadinessCheck(BatteryCheckId, "Battery behaviour",
                    CheckStatus.Unavailable, "No battery detected.");

            SettingValue lid = snapshot[PowerSettings.LidCloseDc];
            SettingValue sleep = snapshot[PowerSettings.SleepDc];

            bool lidKeepsRunning = !lid.IsPresent || lid.Is((uint)LidAction.DoNothing);
            bool staysAwake = !sleep.IsPresent || sleep.Is(0);

            if (lidKeepsRunning && staysAwake)
                return new ReadinessCheck(BatteryCheckId, "Battery behaviour", CheckStatus.Optional,
                    "Also keeps running on battery. This drains the battery and can overheat " +
                    "in an enclosed space.");

            var parts = new List<string>();
            if (!lidKeepsRunning)
                parts.Add(string.Format("lid close is \"{0}\"", PowerValueFormatter.Lid(lid)));
            if (!staysAwake)
                parts.Add(string.Format("sleeps after {0}", PowerValueFormatter.Timeout(sleep)));

            return new ReadinessCheck(BatteryCheckId, "Battery behaviour", CheckStatus.Optional,
                string.Format("On battery, {0}. Not required by AFKLocker - this is the safer default.",
                    string.Join(" and ", parts.ToArray())));
        }
    }
}
