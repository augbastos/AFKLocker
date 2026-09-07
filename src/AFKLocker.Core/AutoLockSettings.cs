using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace AFKLocker.Core
{
    /// <summary>
    /// The user's lock mode, persisted between runs.
    ///
    /// Same plain-text shape as the power backup file, for the same reason:
    /// someone troubleshooting should be able to read it.
    /// </summary>
    public sealed class AutoLockSettings
    {
        public const int CurrentVersion = 2;

        public LockMode Mode { get; set; }

        /// <summary>Whether a global hotkey should lock the session.</summary>
        public bool HotkeyEnabled { get; set; }

        /// <summary>The chosen combination. Never null; empty means nothing chosen.</summary>
        public HotkeyBinding Hotkey { get; set; }

        public AutoLockSettings()
        {
            // Manual with no hotkey is the default and stays the default. A
            // machine with no settings file - or with a 0.4.x settings file that
            // predates the hotkey - behaves exactly like AFKLocker 0.1.x: nothing
            // resident, nothing registered.
            Mode = LockMode.Manual;
            HotkeyEnabled = false;
            Hotkey = HotkeyBinding.Empty;
        }

        /// <summary>A copy, so callers can change one field without touching the stored object.</summary>
        public AutoLockSettings Clone()
        {
            return new AutoLockSettings
            {
                Mode = Mode,
                HotkeyEnabled = HotkeyEnabled,
                Hotkey = Hotkey ?? HotkeyBinding.Empty
            };
        }

        /// <summary>
        /// What the background helper would have to do for these settings.
        /// This, not the lock mode, is what decides whether a helper exists.
        /// </summary>
        public HelperFeatures RequiredFeatures
        {
            get
            {
                HelperFeatures features = HelperFeatures.None;
                if (Mode == LockMode.Automatic) features |= HelperFeatures.LidLock;

                // An enabled hotkey with nothing bound asks the helper for
                // nothing, and must not be the reason a process exists.
                if (HotkeyEnabled && Hotkey != null && Hotkey.IsUsable)
                    features |= HelperFeatures.GlobalHotkey;

                return features;
            }
        }

        public string Serialize()
        {
            var text = new StringBuilder();
            text.AppendLine("# AFKLocker settings");
            text.AppendLine("# lock-mode: manual (double-click to lock) or automatic (lock when the lid closes)");
            text.AppendLine("# hotkey: modifiers:virtual-key, both decimal. Set through AFKLocker Setup.");
            text.AppendLine("version=" + CurrentVersion.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("lock-mode=" + (Mode == LockMode.Automatic ? "automatic" : "manual"));
            text.AppendLine("hotkey-enabled=" + (HotkeyEnabled ? "true" : "false"));
            text.AppendLine("hotkey=" + (Hotkey == null ? string.Empty : Hotkey.Serialize()));
            return text.ToString();
        }

        /// <summary>
        /// Reads settings. Anything unreadable falls back to Manual rather than
        /// throwing: a corrupt settings file must never stop AFKLocker from
        /// working, and Manual is the safe interpretation - it means nothing
        /// resident and no surprise locking.
        /// </summary>
        public static AutoLockSettings Deserialize(string text)
        {
            var settings = new AutoLockSettings();
            if (string.IsNullOrEmpty(text)) return settings;

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                int separator = line.IndexOf('=');
                if (separator <= 0) continue;

                string key = line.Substring(0, separator).Trim().ToLowerInvariant();
                string value = line.Substring(separator + 1).Trim();

                if (key == "lock-mode" && string.Equals(value, "automatic", StringComparison.OrdinalIgnoreCase))
                    settings.Mode = LockMode.Automatic;

                if (key == "hotkey-enabled" && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
                    settings.HotkeyEnabled = true;

                if (key == "hotkey")
                {
                    HotkeyBinding binding;
                    // A binding that will not parse is left empty rather than
                    // guessed at. Guessing would bind a key the user never chose.
                    settings.Hotkey = HotkeyBinding.TryParse(value, out binding)
                        ? binding
                        : HotkeyBinding.Empty;
                }
            }

            return settings;
        }
    }

    public interface ISettingsStore
    {
        AutoLockSettings Load();
        void Save(AutoLockSettings settings);
    }

    /// <summary>Stores settings next to the power backups, under %LOCALAPPDATA%.</summary>
    public sealed class FileSettingsStore : ISettingsStore
    {
        private readonly string _path;

        public FileSettingsStore()
            : this(Path.Combine(FileBackupStore.DefaultDirectory, "settings.txt"))
        {
        }

        public FileSettingsStore(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path must not be empty", "path");
            _path = path;
        }

        /// <summary>Where the settings live. Named FilePath, not Path, so it
        /// does not shadow System.IO.Path inside this class.</summary>
        public string FilePath
        {
            get { return _path; }
        }

        /// <summary>
        /// Reads the settings. A file that is not there means a machine that has
        /// never been configured, which is genuinely the defaults.
        ///
        /// A file that IS there and cannot be read is a different thing, and it
        /// throws. It used to return the defaults too, and that came within one
        /// call of destroying people's configuration: reconciliation would read
        /// "Manual, no hotkey", conclude nothing needed a helper, tear the helper
        /// and the sign-in entry down, and then <em>write those defaults over the
        /// file it had just failed to read</em> - reporting success. One transient
        /// lock from a backup or antivirus scan was enough.
        ///
        /// Failing open is fine for showing a status. It is not fine as an input
        /// to a decision that overwrites the thing it failed to read, so the two
        /// cases stopped being the same value.
        /// </summary>
        public AutoLockSettings Load()
        {
            if (!File.Exists(_path)) return new AutoLockSettings();
            return AutoLockSettings.Deserialize(File.ReadAllText(_path, Encoding.UTF8));
        }

        public void Save(AutoLockSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            AtomicFile.WriteAllText(_path, settings.Serialize());
        }
    }
}
