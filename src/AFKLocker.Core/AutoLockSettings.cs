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
        public const int CurrentVersion = 1;

        public LockMode Mode { get; set; }

        public AutoLockSettings()
        {
            // Manual is the default and stays the default. A machine with no
            // settings file behaves exactly like AFKLocker 0.1.x.
            Mode = LockMode.Manual;
        }

        public string Serialize()
        {
            var text = new StringBuilder();
            text.AppendLine("# AFKLocker settings");
            text.AppendLine("# lock-mode: manual (double-click to lock) or automatic (lock when the lid closes)");
            text.AppendLine("version=" + CurrentVersion.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("lock-mode=" + (Mode == LockMode.Automatic ? "automatic" : "manual"));
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

        public AutoLockSettings Load()
        {
            try
            {
                if (!File.Exists(_path)) return new AutoLockSettings();
                return AutoLockSettings.Deserialize(File.ReadAllText(_path, Encoding.UTF8));
            }
            catch (IOException)
            {
                return new AutoLockSettings();
            }
            catch (UnauthorizedAccessException)
            {
                return new AutoLockSettings();
            }
        }

        public void Save(AutoLockSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            AtomicFile.WriteAllText(_path, settings.Serialize());
        }
    }
}
