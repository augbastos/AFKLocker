using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace AFKLocker.Core
{
    /// <summary>
    /// Modifier keys, with the same bit values Windows uses for RegisterHotKey,
    /// so no translation table is needed between this and the API.
    /// </summary>
    [Flags]
    public enum HotkeyModifiers
    {
        None = 0,
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Windows = 0x0008
    }

    /// <summary>A chosen key combination. Immutable; comparing two is comparing what they mean.</summary>
    public sealed class HotkeyBinding : IEquatable<HotkeyBinding>
    {
        // Virtual keys that are only ever modifiers. Windows accepts these in
        // RegisterHotKey - measured, not assumed - and then nothing sensible
        // ever fires, which is worse than a refusal because it looks configured.
        private static readonly uint[] ModifierOnlyKeys =
        {
            0x10, 0x11, 0x12,               // Shift, Control, Alt
            0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5,  // the left/right variants
            0x5B, 0x5C                      // Left Windows, Right Windows
        };

        public static readonly HotkeyBinding Empty = new HotkeyBinding(HotkeyModifiers.None, 0);

        public HotkeyModifiers Modifiers { get; private set; }

        /// <summary>The Windows virtual-key code. Zero means nothing is bound.</summary>
        public uint VirtualKey { get; private set; }

        public HotkeyBinding(HotkeyModifiers modifiers, uint virtualKey)
        {
            Modifiers = modifiers;
            VirtualKey = virtualKey;
        }

        public bool IsEmpty
        {
            get { return VirtualKey == 0; }
        }

        /// <summary>
        /// Whether this is worth trying to register at all. The real test is
        /// asking Windows, but two cases are worth catching first because the
        /// API says yes to both and neither works.
        /// </summary>
        public string Problem
        {
            get
            {
                if (VirtualKey == 0)
                    return "Choose a key.";

                foreach (uint modifierKey in ModifierOnlyKeys)
                {
                    if (VirtualKey != modifierKey) continue;
                    return "A modifier on its own cannot be a hotkey. "
                        + "Hold it together with another key, or pick a different key.";
                }

                return null;
            }
        }

        public bool IsUsable
        {
            get { return Problem == null; }
        }

        /// <summary>What the user sees: "Menu", "Ctrl + Alt + L".</summary>
        public string Describe()
        {
            if (IsEmpty) return "None";

            var parts = new List<string>();
            if ((Modifiers & HotkeyModifiers.Control) != 0) parts.Add("Ctrl");
            if ((Modifiers & HotkeyModifiers.Alt) != 0) parts.Add("Alt");
            if ((Modifiers & HotkeyModifiers.Shift) != 0) parts.Add("Shift");
            if ((Modifiers & HotkeyModifiers.Windows) != 0) parts.Add("Win");
            parts.Add(HotkeyKeyNames.Describe(VirtualKey));

            return string.Join(" + ", parts.ToArray());
        }

        /// <summary>
        /// The settings-file form. Numeric for the key so that a keyboard layout
        /// or a Windows version with different key names can never silently
        /// change which key is bound.
        /// </summary>
        public string Serialize()
        {
            if (IsEmpty) return string.Empty;
            return ((int)Modifiers).ToString(CultureInfo.InvariantCulture)
                + ":" + VirtualKey.ToString(CultureInfo.InvariantCulture);
        }

        public static bool TryParse(string text, out HotkeyBinding binding)
        {
            binding = Empty;
            if (string.IsNullOrEmpty(text)) return false;

            string[] parts = text.Trim().Split(':');
            if (parts.Length != 2) return false;

            int modifiers;
            uint key;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out modifiers))
                return false;
            if (!uint.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out key))
                return false;

            // Anything outside the four documented modifier bits is not something
            // this wrote, and guessing at it would bind a key the user never chose.
            const int KnownModifiers = 0x0F;
            if (modifiers != (modifiers & KnownModifiers)) return false;
            if (key == 0 || key > 0xFF) return false;

            binding = new HotkeyBinding((HotkeyModifiers)modifiers, key);
            return true;
        }

        public bool Equals(HotkeyBinding other)
        {
            if (other == null) return false;
            return Modifiers == other.Modifiers && VirtualKey == other.VirtualKey;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as HotkeyBinding);
        }

        public override int GetHashCode()
        {
            return ((int)Modifiers * 397) ^ (int)VirtualKey;
        }

        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>
    /// Names for the keys a person might bind. Only the ones whose Windows name
    /// is unhelpful or absent are listed; everything else falls back to the
    /// framework's own name, and anything unknown is shown as its code rather
    /// than invented.
    /// </summary>
    public static class HotkeyKeyNames
    {
        private static readonly Dictionary<uint, string> Names = BuildNames();

        private static Dictionary<uint, string> BuildNames()
        {
            var names = new Dictionary<uint, string>();
            names[0x5D] = "Menu";           // VK_APPS - the Application key
            names[0x08] = "Backspace";
            names[0x09] = "Tab";
            names[0x0D] = "Enter";
            names[0x13] = "Pause";
            names[0x14] = "Caps Lock";
            names[0x1B] = "Esc";
            names[0x20] = "Space";
            names[0x21] = "Page Up";
            names[0x22] = "Page Down";
            names[0x23] = "End";
            names[0x24] = "Home";
            names[0x25] = "Left";
            names[0x26] = "Up";
            names[0x27] = "Right";
            names[0x28] = "Down";
            names[0x2C] = "Print Screen";
            names[0x2D] = "Insert";
            names[0x2E] = "Delete";
            names[0x90] = "Num Lock";
            names[0x91] = "Scroll Lock";
            names[0xBA] = ";";
            names[0xBB] = "=";
            names[0xBC] = ",";
            names[0xBD] = "-";
            names[0xBE] = ".";
            names[0xBF] = "/";
            names[0xC0] = "`";
            names[0xDB] = "[";
            names[0xDC] = "\\";
            names[0xDD] = "]";
            names[0xDE] = "'";
            return names;
        }

        public static string Describe(uint virtualKey)
        {
            string name;
            if (Names.TryGetValue(virtualKey, out name)) return name;

            if (virtualKey >= 0x30 && virtualKey <= 0x39)   // 0-9
                return ((char)virtualKey).ToString(CultureInfo.InvariantCulture);
            if (virtualKey >= 0x41 && virtualKey <= 0x5A)   // A-Z
                return ((char)virtualKey).ToString(CultureInfo.InvariantCulture);
            if (virtualKey >= 0x70 && virtualKey <= 0x87)   // F1-F24
                return "F" + (virtualKey - 0x6F).ToString(CultureInfo.InvariantCulture);
            if (virtualKey >= 0x60 && virtualKey <= 0x69)   // numpad 0-9
                return "Num " + (virtualKey - 0x60).ToString(CultureInfo.InvariantCulture);

            return "Key " + virtualKey.ToString(CultureInfo.InvariantCulture);
        }
    }

    public enum HotkeyRegistrationFailure
    {
        None,

        /// <summary>Nothing is bound, or what is bound could never work.</summary>
        NotUsable,

        /// <summary>Another program - or Windows itself - already owns this combination.</summary>
        AlreadyInUse,

        /// <summary>Windows refused for some other reason, carried in the message.</summary>
        Refused
    }

    public sealed class HotkeyRegistrationResult
    {
        public bool Success { get; private set; }
        public HotkeyRegistrationFailure Failure { get; private set; }
        public string Message { get; private set; }

        private HotkeyRegistrationResult(bool success, HotkeyRegistrationFailure failure, string message)
        {
            Success = success;
            Failure = failure;
            Message = message;
        }

        public static HotkeyRegistrationResult Ok()
        {
            return new HotkeyRegistrationResult(true, HotkeyRegistrationFailure.None, null);
        }

        public static HotkeyRegistrationResult Failed(HotkeyRegistrationFailure failure, string message)
        {
            return new HotkeyRegistrationResult(false, failure, message);
        }

        public override string ToString()
        {
            return Success ? "ok" : Failure + ": " + Message;
        }
    }

    /// <summary>
    /// Asks Windows to reserve a key combination. An interface so the lifecycle
    /// around it can be tested without a real keyboard or a real desktop.
    /// </summary>
    public interface IHotkeyRegistrar
    {
        HotkeyRegistrationResult Register(HotkeyBinding binding);
        void Unregister();
        bool IsRegistered { get; }
    }

    /// <summary>
    /// RegisterHotKey, and nothing else.
    ///
    /// This is the whole reason AFKLocker can say it does not watch what you
    /// type. A global keyboard hook would see every keystroke on the machine and
    /// have to decide which one mattered. RegisterHotKey inverts that: Windows
    /// is told one combination, keeps the keyboard to itself, and posts a single
    /// WM_HOTKEY message when that exact combination is pressed. Nothing else
    /// about the keyboard ever reaches this process.
    ///
    /// MOD_NOREPEAT is set so holding the key down locks once rather than
    /// repeating for as long as a finger rests on it.
    /// </summary>
    public sealed class WindowsHotkeyRegistrar : IHotkeyRegistrar, IDisposable
    {
        /// <summary>Any id works as long as it is ours; this one is arbitrary and stable.</summary>
        public const int HotkeyId = 0xAF01;

        private const uint MOD_NOREPEAT = 0x4000;
        private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private readonly IntPtr _window;
        private readonly int _id;
        private bool _registered;

        public WindowsHotkeyRegistrar(IntPtr window)
            : this(window, HotkeyId)
        {
        }

        public WindowsHotkeyRegistrar(IntPtr window, int id)
        {
            _window = window;
            _id = id;
        }

        public bool IsRegistered
        {
            get { return _registered; }
        }

        public HotkeyRegistrationResult Register(HotkeyBinding binding)
        {
            if (binding == null || binding.IsEmpty)
                return HotkeyRegistrationResult.Failed(HotkeyRegistrationFailure.NotUsable,
                    "No hotkey is set.");

            string problem = binding.Problem;
            if (problem != null)
                return HotkeyRegistrationResult.Failed(HotkeyRegistrationFailure.NotUsable, problem);

            // Never hold two at once: a rebind that left the old one registered
            // would leak a reservation nobody can name or release.
            Unregister();

            if (RegisterHotKey(_window, _id, (uint)binding.Modifiers | MOD_NOREPEAT, binding.VirtualKey))
            {
                _registered = true;
                return HotkeyRegistrationResult.Ok();
            }

            int error = Marshal.GetLastWin32Error();

            if (error == ERROR_HOTKEY_ALREADY_REGISTERED)
                return HotkeyRegistrationResult.Failed(HotkeyRegistrationFailure.AlreadyInUse,
                    "This hotkey is already registered by another application.");

            return HotkeyRegistrationResult.Failed(HotkeyRegistrationFailure.Refused,
                "Windows refused this hotkey: " + new Win32Exception(error).Message);
        }

        public void Unregister()
        {
            if (!_registered) return;
            _registered = false;

            try
            {
                UnregisterHotKey(_window, _id);
            }
            catch (Exception)
            {
                // The window is already gone, which released it anyway.
            }
        }

        public void Dispose()
        {
            Unregister();
        }
    }

    /// <summary>
    /// Answers "could this be registered right now?" without keeping anything.
    ///
    /// Used by Setup before saving, so a conflict is a sentence at configuration
    /// time rather than a helper that fails to start later. It registers against
    /// the calling thread rather than a window, which keeps the test to two
    /// calls with nothing to create or tear down, and releases immediately.
    ///
    /// A thread-associated reservation lasts until the thread exits, so the
    /// release is not optional - but UnregisterHotKey failing after a successful
    /// RegisterHotKey with the same id would mean the reservation was already
    /// gone. Either way nothing is held.
    /// </summary>
    public static class HotkeyProbe
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const uint MOD_NOREPEAT = 0x4000;
        private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
        private const int ProbeId = 0xAF02;

        public static HotkeyRegistrationResult TestAvailability(HotkeyBinding binding)
        {
            if (binding == null || binding.IsEmpty)
                return HotkeyRegistrationResult.Failed(HotkeyRegistrationFailure.NotUsable,
                    "No hotkey is set.");

            string problem = binding.Problem;
            if (problem != null)
                return HotkeyRegistrationResult.Failed(HotkeyRegistrationFailure.NotUsable, problem);

            // A null window means "this thread", which is enough to reserve and
            // release, and leaves no window behind to clean up.
            if (RegisterHotKey(IntPtr.Zero, ProbeId, (uint)binding.Modifiers | MOD_NOREPEAT,
                binding.VirtualKey))
            {
                UnregisterHotKey(IntPtr.Zero, ProbeId);
                return HotkeyRegistrationResult.Ok();
            }

            int error = Marshal.GetLastWin32Error();

            if (error == ERROR_HOTKEY_ALREADY_REGISTERED)
                return HotkeyRegistrationResult.Failed(HotkeyRegistrationFailure.AlreadyInUse,
                    "This hotkey is already registered by another application.");

            return HotkeyRegistrationResult.Failed(HotkeyRegistrationFailure.Refused,
                "Windows refused this hotkey: " + new Win32Exception(error).Message);
        }
    }
}
