using System;
using System.Collections.Generic;
using System.Linq;
using AFKLocker.Core;

namespace AFKLocker.Tests
{
    /// <summary>
    /// An in-memory power configuration. Lets the tests describe any machine -
    /// a desktop with no lid, a laptop that hibernates, one where Windows
    /// refuses writes - without touching the real one.
    /// </summary>
    internal sealed class FakePowerConfiguration : IPowerConfiguration
    {
        private readonly Dictionary<string, uint> _values =
            new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        public Guid ActiveScheme = new Guid("11111111-1111-1111-1111-111111111111");
        public string SchemeName = "Test Plan";

        /// <summary>Settings that should behave as if absent on this machine.</summary>
        public readonly HashSet<string> MissingSettings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>When set, every write fails with this error code.</summary>
        public int WriteFailsWithErrorCode;

        public int ApplySchemeCallCount;
        public readonly List<string> Writes = new List<string>();

        public void Set(PowerSettingRef setting, uint value)
        {
            _values[Key(ActiveScheme, setting)] = value;
        }

        public void SetForScheme(Guid scheme, PowerSettingRef setting, uint value)
        {
            _values[Key(scheme, setting)] = value;
        }

        public uint Get(PowerSettingRef setting)
        {
            return _values[Key(ActiveScheme, setting)];
        }

        public uint GetForScheme(Guid scheme, PowerSettingRef setting)
        {
            return _values[Key(scheme, setting)];
        }

        private static string Key(Guid scheme, PowerSettingRef setting)
        {
            return scheme.ToString("N") + "|" + setting.Key;
        }

        private static string Key(Guid scheme, Guid subgroup, Guid setting, PowerSource source)
        {
            PowerSettingRef match = PowerSettings.All.FirstOrDefault(s =>
                s.Subgroup == subgroup && s.Setting == setting && s.Source == source);
            if (match == null)
                throw new InvalidOperationException("Test fake was asked about an unknown setting.");
            return Key(scheme, match);
        }

        private static string NameOf(Guid subgroup, Guid setting, PowerSource source)
        {
            PowerSettingRef match = PowerSettings.All.FirstOrDefault(s =>
                s.Subgroup == subgroup && s.Setting == setting && s.Source == source);
            return match == null ? "unknown" : match.Key;
        }

        public Guid GetActiveScheme()
        {
            return ActiveScheme;
        }

        public string GetSchemeName(Guid scheme)
        {
            return SchemeName;
        }

        public uint ReadValue(Guid scheme, Guid subgroup, Guid setting, PowerSource source)
        {
            string name = NameOf(subgroup, setting, source);
            if (MissingSettings.Contains(name))
                throw new PowerSettingNotFoundException(name + " is absent in this fake.");

            uint value;
            if (_values.TryGetValue(Key(scheme, subgroup, setting, source), out value))
                return value;

            throw new PowerSettingNotFoundException(name + " was never set in this fake.");
        }

        public void WriteValue(Guid scheme, Guid subgroup, Guid setting, PowerSource source, uint value)
        {
            string name = NameOf(subgroup, setting, source);
            if (MissingSettings.Contains(name))
                throw new PowerSettingNotFoundException(name + " is absent in this fake.");

            if (WriteFailsWithErrorCode != 0)
                throw new PowerConfigurationException("Fake write failure.", WriteFailsWithErrorCode);

            Writes.Add(name + "=" + value);
            _values[Key(scheme, subgroup, setting, source)] = value;
        }

        public void ApplyScheme(Guid scheme)
        {
            ApplySchemeCallCount++;
        }
    }

    internal sealed class FakePowerInformation : IPowerInformation
    {
        public SystemCapabilities Capabilities = new SystemCapabilities
        {
            LidPresent = true,
            SupportsStandbyS3 = true,
            HibernateFilePresent = false,
            ModernStandby = false,
            BatteryPresent = true
        };

        public SystemCapabilities GetCapabilities()
        {
            return Capabilities;
        }
    }

    internal sealed class InMemoryBackupStore : IBackupStore
    {
        private readonly Dictionary<Guid, string> _files = new Dictionary<Guid, string>();

        public int SaveCount;

        public PowerBackup Load(Guid scheme)
        {
            string text;
            return _files.TryGetValue(scheme, out text) ? PowerBackup.Deserialize(text) : null;
        }

        public void Save(PowerBackup backup)
        {
            SaveCount++;
            // Round-trips through the real serializer so the tests exercise it too.
            _files[backup.Scheme] = backup.Serialize();
        }

        public void Delete(Guid scheme)
        {
            _files.Remove(scheme);
        }

        public IEnumerable<Guid> ListSchemes()
        {
            return _files.Keys.ToList();
        }
    }

    /// <summary>Records lock requests instead of locking the machine.</summary>
    internal sealed class FakeSessionLocker : ISessionLocker
    {
        public int LockCount;

        public void Lock()
        {
            LockCount++;
        }
    }

    /// <summary>Builds snapshots for the tests to evaluate.</summary>
    internal static class SnapshotBuilder
    {
        /// <summary>A laptop configured the way AFKLocker wants it.</summary>
        public static PowerSnapshot ReadyLaptop()
        {
            var snapshot = new PowerSnapshot
            {
                Scheme = new Guid("11111111-1111-1111-1111-111111111111"),
                SchemeName = "Test Plan",
                Capabilities = new SystemCapabilities
                {
                    LidPresent = true,
                    SupportsStandbyS3 = true,
                    BatteryPresent = true
                }
            };
            snapshot[PowerSettings.LidCloseAc] = SettingValue.Of((uint)LidAction.DoNothing);
            snapshot[PowerSettings.SleepAc] = SettingValue.Of(0);
            snapshot[PowerSettings.HibernateAc] = SettingValue.Absent;
            snapshot[PowerSettings.LidCloseDc] = SettingValue.Of((uint)LidAction.Sleep);
            snapshot[PowerSettings.SleepDc] = SettingValue.Of(1800);
            snapshot[PowerSettings.HibernateDc] = SettingValue.Absent;
            return snapshot;
        }

        /// <summary>A stock laptop: sleeps on lid close, sleeps on idle.</summary>
        public static PowerSnapshot StockLaptop()
        {
            PowerSnapshot snapshot = ReadyLaptop();
            snapshot[PowerSettings.LidCloseAc] = SettingValue.Of((uint)LidAction.Sleep);
            snapshot[PowerSettings.SleepAc] = SettingValue.Of(1800);
            return snapshot;
        }
    }
}
