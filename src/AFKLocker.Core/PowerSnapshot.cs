using System;
using System.Collections.Generic;

namespace AFKLocker.Core
{
    /// <summary>
    /// The power-related state of the machine at one point in time: which scheme
    /// is active, what the relevant settings say, and what the hardware supports.
    /// </summary>
    public sealed class PowerSnapshot
    {
        private readonly Dictionary<string, SettingValue> _values =
            new Dictionary<string, SettingValue>(StringComparer.OrdinalIgnoreCase);

        public Guid Scheme { get; set; }
        public string SchemeName { get; set; }
        public SystemCapabilities Capabilities { get; set; }

        public SettingValue this[PowerSettingRef setting]
        {
            get
            {
                if (setting == null) throw new ArgumentNullException("setting");
                SettingValue value;
                return _values.TryGetValue(setting.Key, out value) ? value : SettingValue.Absent;
            }
            set
            {
                if (setting == null) throw new ArgumentNullException("setting");
                _values[setting.Key] = value;
            }
        }

        /// <summary>Reads the current state of the machine.</summary>
        public static PowerSnapshot Read(IPowerConfiguration power, IPowerInformation info)
        {
            if (power == null) throw new ArgumentNullException("power");
            if (info == null) throw new ArgumentNullException("info");

            Guid scheme = power.GetActiveScheme();
            var snapshot = new PowerSnapshot
            {
                Scheme = scheme,
                SchemeName = power.GetSchemeName(scheme),
                Capabilities = info.GetCapabilities()
            };

            foreach (PowerSettingRef setting in PowerSettings.All)
            {
                try
                {
                    snapshot[setting] = SettingValue.Of(
                        power.ReadValue(scheme, setting.Subgroup, setting.Setting, setting.Source));
                }
                catch (PowerSettingNotFoundException)
                {
                    // Expected on machines where the setting does not exist.
                    snapshot[setting] = SettingValue.Absent;
                }
            }

            return snapshot;
        }
    }
}
