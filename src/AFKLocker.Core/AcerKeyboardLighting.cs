using System;
using System.Globalization;
using System.Linq;
using System.Management;

namespace AFKLocker.Core
{
    /// <summary>The firmware calls the Acer backend needs, so its logic can be tested without the firmware.</summary>
    public interface IAcerGamingFirmware
    {
        bool IsPresent();

        /// <summary>The keyboard backlight configuration: mode, speed, brightness, direction, colour, ...</summary>
        byte[] ReadBacklight();

        /// <summary>Writes a full 16-byte backlight configuration.</summary>
        void WriteBacklight(byte[] configuration);

        /// <summary>The raw colour value of one zone (1, 2, 4 or 8).</summary>
        ulong ReadZone(uint zone);

        /// <summary>Writes zone | red &lt;&lt; 8 | green &lt;&lt; 16 | blue &lt;&lt; 24.</summary>
        void WriteZone(uint value);
    }

    /// <summary>
    /// Keyboard lighting on Acer gaming laptops, through the firmware's gaming
    /// WMI interface - the same one the vendor's own control software uses.
    ///
    /// It changes as little as possible: off rewrites the captured backlight
    /// configuration with only the brightness byte set to zero, and restore
    /// writes the captured bytes back. Each step is read back, because firmware
    /// revisions differ and a write the firmware silently ignored must not be
    /// reported as success.
    /// </summary>
    public sealed class AcerGamingKeyboardBackend : IKeyboardLightingBackend
    {
        private static readonly uint[] Zones = { 1, 2, 4, 8 };
        private const int ModeIndex = 0;
        private const int BrightnessIndex = 2;
        private const byte StaticMode = 0;

        private readonly IAcerGamingFirmware _firmware;

        public AcerGamingKeyboardBackend()
            : this(new AcerGamingWmiFirmware())
        {
        }

        public AcerGamingKeyboardBackend(IAcerGamingFirmware firmware)
        {
            if (firmware == null) throw new ArgumentNullException("firmware");
            _firmware = firmware;
        }

        public string Id
        {
            get { return "acer-gaming-wmi"; }
        }

        public bool IsPresent()
        {
            return _firmware.IsPresent();
        }

        public string Capture()
        {
            byte[] backlight = _firmware.ReadBacklight();
            if (backlight == null || backlight.Length <= BrightnessIndex)
                throw new InvalidOperationException("The keyboard lighting controller returned no usable state.");
            return new AcerLightingState(backlight, ReadZones()).Serialize();
        }

        /// <summary>
        /// Zone colours are captured only so restore can check them, and put them
        /// back if writing the backlight disturbed them. A keyboard without
        /// per-zone colour simply has none to protect.
        /// </summary>
        private ulong[] ReadZones()
        {
            try
            {
                return Zones.Select(zone => _firmware.ReadZone(zone)).ToArray();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void TurnOff(string captured)
        {
            AcerLightingState state = AcerLightingState.Parse(captured);

            byte[] dark = state.Configuration();
            dark[BrightnessIndex] = 0;
            _firmware.WriteBacklight(dark);

            byte[] now = _firmware.ReadBacklight();
            if (now == null || now.Length <= BrightnessIndex || now[BrightnessIndex] != 0)
                throw new InvalidOperationException(
                    "The keyboard lighting controller accepted the request but did not turn the lighting off.");
        }

        public void Restore(string captured)
        {
            AcerLightingState state = AcerLightingState.Parse(captured);
            _firmware.WriteBacklight(state.Configuration());

            // Zones only mean something in static mode; writing them in an effect
            // mode could switch the effect off. Rewriting happens only when the
            // read-back proves the colours actually changed.
            if (state.Zones != null && state.Backlight[ModeIndex] == StaticMode && !ZonesMatch(state.Zones))
            {
                for (int index = 0; index < Zones.Length; index++)
                    _firmware.WriteZone(ZoneWriteValue(Zones[index], state.Zones[index]));

                if (!ZonesMatch(state.Zones))
                    throw new InvalidOperationException(
                        "Keyboard lighting was restored, but its colours differ from before AFK.");
            }

            byte[] now = _firmware.ReadBacklight();
            if (now == null || now.Length != state.Backlight.Length || !now.SequenceEqual(state.Backlight))
                throw new InvalidOperationException(
                    "Keyboard lighting was restored, but the controller reports a different state than before AFK.");
        }

        private bool ZonesMatch(ulong[] expected)
        {
            try
            {
                for (int index = 0; index < Zones.Length; index++)
                    if (_firmware.ReadZone(Zones[index]) != expected[index]) return false;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// A zone read carries a status byte below red, green and blue - the shape
        /// every getter on this interface uses - and the setter takes the zone bit
        /// in that byte. Only used after a read-back showed the colours changed,
        /// and verified by another read-back afterwards.
        /// </summary>
        internal static uint ZoneWriteValue(uint zone, ulong read)
        {
            return (uint)((read & 0xFFFFFF00UL) | zone);
        }
    }

    internal sealed class AcerLightingState
    {
        public const int ConfigurationLength = 16;

        public AcerLightingState(byte[] backlight, ulong[] zones)
        {
            if (backlight == null || backlight.Length == 0 || backlight.Length > ConfigurationLength)
                throw new ArgumentException("backlight state has an unexpected length", "backlight");
            if (zones != null && zones.Length != 4)
                throw new ArgumentException("exactly four zones are expected", "zones");
            Backlight = (byte[])backlight.Clone();
            Zones = zones == null ? null : (ulong[])zones.Clone();
        }

        public byte[] Backlight { get; private set; }

        /// <summary>Null when the keyboard reported no per-zone colour.</summary>
        public ulong[] Zones { get; private set; }

        /// <summary>
        /// The setter takes 16 bytes. The getter returns the first 15, with the
        /// status in a separate output, so the final reserved byte stays zero.
        /// </summary>
        public byte[] Configuration()
        {
            var configuration = new byte[ConfigurationLength];
            Array.Copy(Backlight, configuration, Backlight.Length);
            return configuration;
        }

        public string Serialize()
        {
            string zones = Zones == null
                ? "-"
                : string.Join(",", Zones.Select(zone => zone.ToString(CultureInfo.InvariantCulture)).ToArray());
            return "backlight=" + Convert.ToBase64String(Backlight) + ";zones=" + zones;
        }

        public static AcerLightingState Parse(string text)
        {
            if (text == null) throw new ArgumentNullException("text");

            string[] parts = text.Split(';');
            if (parts.Length != 2 || !parts[0].StartsWith("backlight=", StringComparison.Ordinal)
                || !parts[1].StartsWith("zones=", StringComparison.Ordinal))
                throw new FormatException("Saved keyboard lighting state is malformed.");

            byte[] backlight = Convert.FromBase64String(parts[0].Substring("backlight=".Length));
            string zoneText = parts[1].Substring("zones=".Length);

            ulong[] zones = null;
            if (zoneText != "-")
            {
                string[] values = zoneText.Split(',');
                if (values.Length != 4) throw new FormatException("Saved keyboard lighting zones are malformed.");
                zones = new ulong[4];
                for (int index = 0; index < 4; index++)
                    if (!ulong.TryParse(values[index], NumberStyles.None, CultureInfo.InvariantCulture, out zones[index]))
                        throw new FormatException("Saved keyboard lighting zones are malformed.");
            }

            return new AcerLightingState(backlight, zones);
        }
    }

    /// <summary>
    /// The real firmware, through WMI. Windows only lets administrators call
    /// these methods; reading the class definition needs no rights, which is
    /// what makes detection work from a normal process.
    /// </summary>
    internal sealed class AcerGamingWmiFirmware : IAcerGamingFirmware
    {
        private const string Scope = @"\\.\root\WMI";
        private const string ClassName = "AcerGamingFunction";

        private static readonly string[] RequiredMethods =
        {
            "GetGamingKBBacklight", "SetGamingKBBacklight", "GetGamingRgbKb", "SetGamingRgbKb"
        };

        public bool IsPresent()
        {
            try
            {
                using (var definition = new ManagementClass(new ManagementScope(Scope),
                    new ManagementPath(ClassName), null))
                {
                    definition.Get();
                    var methods = definition.Methods.Cast<MethodData>().Select(method => method.Name).ToList();
                    return RequiredMethods.All(methods.Contains);
                }
            }
            catch (ManagementException ex)
            {
                if (ex.ErrorCode == ManagementStatus.NotFound ||
                    ex.ErrorCode == ManagementStatus.InvalidClass ||
                    ex.ErrorCode == ManagementStatus.InvalidNamespace)
                    return false;
                throw;
            }
        }

        public byte[] ReadBacklight()
        {
            // Some firmware revisions take a selector byte here (1 = keyboard);
            // others declare no input at all. Invoke only fills in what exists.
            using (ManagementBaseObject output = Invoke("GetGamingKBBacklight", (byte)1))
            {
                EnsureStatus(Value(output, "gmReturn"));
                return Value(output, "gmOutput") as byte[];
            }
        }

        public void WriteBacklight(byte[] configuration)
        {
            using (ManagementBaseObject output = Invoke("SetGamingKBBacklight", configuration))
                EnsureStatus(Value(output, "gmOutput"));
        }

        public ulong ReadZone(uint zone)
        {
            using (ManagementBaseObject output = Invoke("GetGamingRgbKb", zone))
            {
                EnsureStatus(Value(output, "gmReturn"));
                object raw = Value(output, "gmOutput");
                if (raw == null) throw new InvalidOperationException("The keyboard lighting controller returned no colour.");
                return Convert.ToUInt64(raw, CultureInfo.InvariantCulture);
            }
        }

        public void WriteZone(uint value)
        {
            using (ManagementBaseObject output = Invoke("SetGamingRgbKb", value))
                EnsureStatus(Value(output, "gmOutput"));
        }

        private static ManagementBaseObject Invoke(string method, object input)
        {
            var scope = new ManagementScope(Scope);
            scope.Connect();

            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM " + ClassName)))
            using (ManagementObjectCollection instances = searcher.Get())
            {
                foreach (ManagementObject instance in instances)
                {
                    using (instance)
                    {
                        ManagementBaseObject parameters = instance.GetMethodParameters(method);
                        if (parameters != null && HasProperty(parameters, "gmInput"))
                            parameters["gmInput"] = input;

                        ManagementBaseObject output = instance.InvokeMethod(method, parameters, null);
                        if (output == null)
                            throw new InvalidOperationException("The keyboard lighting controller did not respond.");
                        return output;
                    }
                }
            }

            throw new InvalidOperationException("The keyboard lighting controller is not available.");
        }

        private static bool HasProperty(ManagementBaseObject value, string name)
        {
            return value.Properties.Cast<PropertyData>().Any(property => property.Name == name);
        }

        private static object Value(ManagementBaseObject output, string name)
        {
            return HasProperty(output, name) ? output[name] : null;
        }

        /// <summary>Every call on this interface reports success as a zero low byte.</summary>
        private static void EnsureStatus(object raw)
        {
            if (raw == null) return;
            ulong status = Convert.ToUInt64(raw, CultureInfo.InvariantCulture) & 0xFF;
            if (status != 0)
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "The keyboard lighting controller rejected the request (status 0x{0:X2}).", status));
        }
    }
}
