using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AFKLocker.Core
{
    /// <summary>Raised when a backup file cannot be understood.</summary>
    public class BackupFormatException : Exception
    {
        public BackupFormatException(string message) : base(message) { }
    }

    /// <summary>
    /// The power setting values as they were before AFKLocker changed them, for
    /// one power scheme.
    ///
    /// Stored as plain key=value text on purpose: a user troubleshooting their
    /// machine should be able to read and understand this file without tools.
    /// </summary>
    public sealed class PowerBackup
    {
        public const int CurrentVersion = 1;

        private readonly Dictionary<string, uint> _values =
            new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        public int Version { get; set; }
        public DateTime CreatedUtc { get; set; }
        public Guid Scheme { get; set; }
        public string SchemeName { get; set; }

        public PowerBackup()
        {
            Version = CurrentVersion;
            CreatedUtc = DateTime.UtcNow;
        }

        public IEnumerable<KeyValuePair<string, uint>> Values
        {
            get { return _values.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase); }
        }

        public int Count
        {
            get { return _values.Count; }
        }

        public bool Contains(string key)
        {
            return key != null && _values.ContainsKey(key);
        }

        public bool TryGet(string key, out uint value)
        {
            value = 0;
            return key != null && _values.TryGetValue(key, out value);
        }

        /// <summary>
        /// Records the original value of a setting. Existing entries are never
        /// overwritten: on a second run the first recorded value is the one that
        /// actually predates AFKLocker.
        /// </summary>
        /// <returns>True when the value was recorded, false when one already existed.</returns>
        public bool RecordOriginal(string key, uint value)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("key must not be empty", "key");
            if (_values.ContainsKey(key)) return false;
            _values[key] = value;
            return true;
        }

        public void Remove(string key)
        {
            if (key != null) _values.Remove(key);
        }

        public string Serialize()
        {
            var text = new StringBuilder();
            text.AppendLine("# AFKLocker power settings backup");
            text.AppendLine("# These are the values this machine had before AFKLocker changed them.");
            text.AppendLine("# Run \"AFKLocker Setup\" and choose Restore to put them back.");
            text.AppendLine("version=" + Version.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("created-utc=" + CreatedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            text.AppendLine("scheme=" + Scheme.ToString("D"));
            if (!string.IsNullOrEmpty(SchemeName))
                text.AppendLine("scheme-name=" + SchemeName);
            foreach (var pair in Values)
                text.AppendLine(pair.Key + "=" + pair.Value.ToString(CultureInfo.InvariantCulture));
            return text.ToString();
        }

        public static PowerBackup Deserialize(string text)
        {
            if (text == null) throw new ArgumentNullException("text");

            var backup = new PowerBackup { Version = 0 };
            bool sawVersion = false;

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                int separator = line.IndexOf('=');
                if (separator <= 0)
                    throw new BackupFormatException("Malformed line in backup file: " + line);

                string key = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();

                switch (key.ToLowerInvariant())
                {
                    case "version":
                        int version;
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out version))
                            throw new BackupFormatException("Backup file has a non-numeric version: " + value);
                        if (version != CurrentVersion)
                            throw new BackupFormatException(string.Format(
                                "Backup file version {0} is not supported by this build (expected {1}).",
                                version, CurrentVersion));
                        backup.Version = version;
                        sawVersion = true;
                        break;

                    case "created-utc":
                        DateTime created;
                        if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out created))
                            backup.CreatedUtc = created;
                        break;

                    case "scheme":
                        Guid scheme;
                        if (!Guid.TryParse(value, out scheme))
                            throw new BackupFormatException("Backup file has an invalid scheme GUID: " + value);
                        backup.Scheme = scheme;
                        break;

                    case "scheme-name":
                        backup.SchemeName = value;
                        break;

                    default:
                        // Only keys we recognise as settings are restored. Anything
                        // else is ignored so a newer file does not break an older build.
                        if (PowerSettings.ByKey(key) != null)
                        {
                            uint parsed;
                            if (!uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                                throw new BackupFormatException(
                                    string.Format("Backup entry \"{0}\" has a non-numeric value: {1}", key, value));
                            backup.RecordOriginal(key, parsed);
                        }
                        break;
                }
            }

            if (!sawVersion)
                throw new BackupFormatException("Backup file has no version line.");

            return backup;
        }
    }

    /// <summary>Where backups live. Abstracted so tests never touch the disk.</summary>
    public interface IBackupStore
    {
        /// <summary>Returns the backup for a scheme, or null when there is none.</summary>
        PowerBackup Load(Guid scheme);

        void Save(PowerBackup backup);

        void Delete(Guid scheme);

        /// <summary>Schemes that currently have a backup.</summary>
        IEnumerable<Guid> ListSchemes();
    }

    /// <summary>
    /// Stores one backup file per power scheme under %LOCALAPPDATA%\AFKLocker.
    ///
    /// One file per scheme matters: a user can configure AFKLocker on "Balanced",
    /// later switch to "High performance" and configure that too. Restoring must
    /// put each scheme back the way it was.
    /// </summary>
    public sealed class FileBackupStore : IBackupStore
    {
        private const string FilePrefix = "power-backup-";
        private const string FileSuffix = ".txt";

        private readonly string _directory;

        public FileBackupStore()
            : this(DefaultDirectory)
        {
        }

        public FileBackupStore(string directory)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("directory must not be empty", "directory");
            _directory = directory;
        }

        public static string DefaultDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AFKLocker");
            }
        }

        public string Directory
        {
            get { return _directory; }
        }

        private string PathFor(Guid scheme)
        {
            return Path.Combine(_directory, FilePrefix + scheme.ToString("D") + FileSuffix);
        }

        public PowerBackup Load(Guid scheme)
        {
            string path = PathFor(scheme);
            if (!File.Exists(path)) return null;
            return PowerBackup.Deserialize(File.ReadAllText(path, Encoding.UTF8));
        }

        public void Save(PowerBackup backup)
        {
            if (backup == null) throw new ArgumentNullException("backup");
            System.IO.Directory.CreateDirectory(_directory);
            File.WriteAllText(PathFor(backup.Scheme), backup.Serialize(), new UTF8Encoding(false));
        }

        public void Delete(Guid scheme)
        {
            string path = PathFor(scheme);
            if (File.Exists(path)) File.Delete(path);
        }

        public IEnumerable<Guid> ListSchemes()
        {
            if (!System.IO.Directory.Exists(_directory))
                return Enumerable.Empty<Guid>();

            var schemes = new List<Guid>();
            foreach (string file in System.IO.Directory.GetFiles(_directory, FilePrefix + "*" + FileSuffix))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name == null) continue;
                Guid scheme;
                if (Guid.TryParse(name.Substring(FilePrefix.Length), out scheme))
                    schemes.Add(scheme);
            }
            return schemes;
        }
    }
}
