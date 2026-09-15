using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;

namespace AFKLocker.Core
{
    public interface IKeyboardLightingSession : IDisposable
    {
        void Enter();
    }

    public sealed class NoKeyboardLightingSession : IKeyboardLightingSession
    {
        public void Enter() { }
        public void Dispose() { }
    }

    /// <summary>
    /// Acer Nitro/Predator keyboard lighting through the same AcerGamingFunction
    /// WMI surface NitroSense uses. The full backlight payload and every zone
    /// colour are captured before brightness is set to zero, then restored on
    /// exit. AFKLocker never invents or persists a replacement colour profile.
    /// </summary>
    public sealed class AcerKeyboardLightingSession : IKeyboardLightingSession
    {
        private static readonly uint[] Zones = { 1, 2, 4, 8 };
        private readonly string _snapshotPath;
        private bool _entered;
        private bool _disposed;

        public AcerKeyboardLightingSession()
            : this(Path.Combine(FileBackupStore.DefaultDirectory, "keyboard-lighting-session.txt"))
        {
        }

        public AcerKeyboardLightingSession(string snapshotPath)
        {
            if (string.IsNullOrEmpty(snapshotPath))
                throw new ArgumentException("snapshot path must not be empty", "snapshotPath");
            _snapshotPath = snapshotPath;
        }

        public void Enter()
        {
            if (_entered) return;

            // A previous process may have been killed while AFK mode was
            // active. Never overwrite the only copy of the real user profile
            // with a snapshot of the already-dark keyboard.
            if (File.Exists(_snapshotPath)) RestoreSnapshot();

            using (ManagementObject gaming = OpenGamingInterface())
            {
                KeyboardLightingSnapshot snapshot = Capture(gaming);
                AtomicFile.WriteAllText(_snapshotPath, snapshot.Serialize());

                byte[] off = snapshot.CreateSetterPayload();
                if (off.Length < 3)
                    throw new InvalidOperationException("Acer returned an incomplete keyboard payload.");

                // Brightness is a separate byte in Acer's documented 16-byte
                // firmware payload. Keeping every other byte intact means the
                // user's mode, speed, direction and colours are not replaced.
                off[2] = 0;
                InvokeSet(gaming, "SetGamingKBBacklight", off);
            }

            _entered = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_entered && !File.Exists(_snapshotPath)) return;

            RestoreSnapshot();
            _entered = false;
        }

        private void RestoreSnapshot()
        {

            KeyboardLightingSnapshot snapshot =
                KeyboardLightingSnapshot.Deserialize(File.ReadAllText(_snapshotPath, Encoding.UTF8));

            using (ManagementObject gaming = OpenGamingInterface())
            {
                for (int index = 0; index < Zones.Length; index++)
                    InvokeSet(gaming, "SetGamingRgbKb", snapshot.ZoneValues[index] | Zones[index]);

                InvokeSet(gaming, "SetGamingKBBacklight", snapshot.CreateSetterPayload());
            }

            File.Delete(_snapshotPath);
        }

        private static KeyboardLightingSnapshot Capture(ManagementObject gaming)
        {
            ManagementBaseObject response = Invoke(gaming, "GetGamingKBBacklight", (uint)1);
            EnsureReturnSuccess(response, "GetGamingKBBacklight");

            byte[] backlight = response["gmOutput"] as byte[];
            if (backlight == null || backlight.Length < 3)
                throw new InvalidOperationException("GetGamingKBBacklight returned no usable payload.");

            var colours = new List<ulong>();
            foreach (uint zone in Zones)
            {
                using (ManagementBaseObject colour = Invoke(gaming, "GetGamingRgbKb", zone))
                {
                    EnsureReturnSuccess(colour, "GetGamingRgbKb");
                    object raw = colour["gmOutput"];
                    if (raw == null)
                        throw new InvalidOperationException("GetGamingRgbKb returned no value.");
                    colours.Add(Convert.ToUInt64(raw, CultureInfo.InvariantCulture));
                }
            }

            return new KeyboardLightingSnapshot(backlight, colours.ToArray());
        }

        private static ManagementObject OpenGamingInterface()
        {
            var scope = new ManagementScope(@"\\.\root\WMI");
            scope.Connect();

            using (var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT * FROM AcerGamingFunction")))
            using (ManagementObjectCollection matches = searcher.Get())
            {
                foreach (ManagementObject match in matches)
                    return match;
            }

            throw new InvalidOperationException(
                "AcerGamingFunction was not found. NitroSense and the Acer system interface must be installed.");
        }

        private static ManagementBaseObject Invoke(ManagementObject gaming, string method, object inputValue)
        {
            ManagementBaseObject input = gaming.GetMethodParameters(method);
            if (input == null)
                throw new InvalidOperationException("This Acer firmware does not expose " + method + ".");
            input["gmInput"] = inputValue;

            ManagementBaseObject output = gaming.InvokeMethod(method, input, null);
            if (output == null)
                throw new InvalidOperationException(method + " returned no response.");
            return output;
        }

        private static void InvokeSet(ManagementObject gaming, string method, object inputValue)
        {
            using (ManagementBaseObject output = Invoke(gaming, method, inputValue))
            {
                object raw = output["gmOutput"];
                if (raw == null)
                    throw new InvalidOperationException(method + " returned no status.");

                uint status = Convert.ToUInt32(raw, CultureInfo.InvariantCulture);
                if ((status & 0xFF) != 0)
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                        "{0} rejected the request with status 0x{1:X}.", method, status));
            }
        }

        private static void EnsureReturnSuccess(ManagementBaseObject output, string method)
        {
            object raw = output["gmReturn"];
            if (raw == null) return;
            byte status = Convert.ToByte(raw, CultureInfo.InvariantCulture);
            if (status != 0)
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "{0} returned status 0x{1:X2}.", method, status));
        }
    }

    public sealed class KeyboardLightingSnapshot
    {
        public const int CurrentVersion = 1;

        public byte[] Backlight { get; private set; }
        public ulong[] ZoneValues { get; private set; }

        public KeyboardLightingSnapshot(byte[] backlight, ulong[] zoneValues)
        {
            if (backlight == null || backlight.Length < 3)
                throw new ArgumentException("backlight payload is incomplete", "backlight");
            if (zoneValues == null || zoneValues.Length != 4)
                throw new ArgumentException("exactly four zone values are required", "zoneValues");

            Backlight = (byte[])backlight.Clone();
            ZoneValues = (ulong[])zoneValues.Clone();
        }

        /// <summary>Acer's setter takes 16 bytes; the getter returns 15 plus a status byte.</summary>
        public byte[] CreateSetterPayload()
        {
            var payload = new byte[16];
            Array.Copy(Backlight, payload, Math.Min(Backlight.Length, payload.Length));
            return payload;
        }

        public string Serialize()
        {
            var text = new StringBuilder();
            text.AppendLine("# AFKLocker temporary Acer keyboard state");
            text.AppendLine("version=" + CurrentVersion.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("backlight=" + Convert.ToBase64String(Backlight));
            for (int index = 0; index < ZoneValues.Length; index++)
                text.AppendLine("zone" + index.ToString(CultureInfo.InvariantCulture) + "=" +
                    ZoneValues[index].ToString(CultureInfo.InvariantCulture));
            return text.ToString();
        }

        public static KeyboardLightingSnapshot Deserialize(string text)
        {
            if (text == null) throw new ArgumentNullException("text");

            int version = 0;
            byte[] backlight = null;
            var zones = new ulong[4];
            var sawZone = new bool[4];

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                int separator = line.IndexOf('=');
                if (separator <= 0) throw new FormatException("Malformed keyboard snapshot line.");

                string key = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();
                if (key == "version")
                {
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out version))
                        throw new FormatException("Keyboard snapshot version is invalid.");
                }
                else if (key == "backlight")
                {
                    backlight = Convert.FromBase64String(value);
                }
                else if (key.StartsWith("zone", StringComparison.Ordinal) && key.Length == 5)
                {
                    int index = key[4] - '0';
                    ulong parsed;
                    if (index < 0 || index >= zones.Length ||
                        !ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                        throw new FormatException("Keyboard snapshot zone is invalid.");
                    zones[index] = parsed;
                    sawZone[index] = true;
                }
            }

            if (version != CurrentVersion)
                throw new FormatException("Keyboard snapshot version is not supported.");
            if (backlight == null || backlight.Length < 3)
                throw new FormatException("Keyboard snapshot has no backlight payload.");
            if (sawZone.Any(value => !value))
                throw new FormatException("Keyboard snapshot does not contain all four zones.");

            return new KeyboardLightingSnapshot(backlight, zones);
        }
    }
}
