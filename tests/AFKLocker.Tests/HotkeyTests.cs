using AFKLocker.Core;

namespace AFKLocker.Tests
{
    /// <summary>
    /// The hotkey binding: what it means, how it survives a save, and which
    /// combinations are refused.
    ///
    /// The refusals here are deliberately few, and every one of them comes from
    /// something measured rather than assumed. Windows itself reports the
    /// combinations it reserves - F12, Win+L, Ctrl+Esc, Alt+Tab all come back as
    /// "already registered" - so there is no hand-written list of those. The one
    /// case Windows does NOT catch is a bare modifier: RegisterHotKey accepts it
    /// and then nothing ever fires, which looks configured and is not.
    /// </summary>
    internal static class HotkeyTests
    {
        private const uint VkApps = 0x5D;
        private const uint VkL = 0x4C;

        [Test("The Menu key on its own is a usable hotkey")]
        private static void MenuKeyAloneIsUsable()
        {
            var binding = new HotkeyBinding(HotkeyModifiers.None, VkApps);

            Assert.True(binding.IsUsable, "measured on a real machine: RegisterHotKey accepts it");
            Assert.Equal("Menu", binding.Describe(), "and it is named the way the keyboard is");
        }

        [Test("A combination reads the way a person would say it")]
        private static void CombinationDescribesInOrder()
        {
            var binding = new HotkeyBinding(
                HotkeyModifiers.Control | HotkeyModifiers.Alt, VkL);

            Assert.Equal("Ctrl + Alt + L", binding.Describe(), "modifiers first, in the usual order");
        }

        [Test("Nothing bound describes itself as nothing, not as a blank")]
        private static void EmptyDescribesItself()
        {
            Assert.True(HotkeyBinding.Empty.IsEmpty, "empty");
            Assert.Equal("None", HotkeyBinding.Empty.Describe(), "and says so");
            Assert.False(HotkeyBinding.Empty.IsUsable, "and cannot be registered");
        }

        [Test("A bare modifier is refused, because Windows accepts it and it never fires")]
        private static void BareModifierIsRefused()
        {
            uint[] modifierKeys = { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA5 };

            foreach (uint key in modifierKeys)
            {
                var binding = new HotkeyBinding(HotkeyModifiers.Control, key);
                Assert.False(binding.IsUsable, "a modifier alone cannot be a hotkey: " + key);
                Assert.NotNull(binding.Problem, "and the reason is spelled out");
            }
        }

        [Test("An ordinary letter with modifiers is fine")]
        private static void LetterWithModifiersIsUsable()
        {
            var binding = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift, VkL);

            Assert.True(binding.IsUsable, "nothing wrong with this");
            Assert.Null(binding.Problem, "so nothing to say about it");
        }

        [Test("A binding survives a save and load exactly")]
        private static void BindingRoundTrips()
        {
            var original = new HotkeyBinding(
                HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Alt, 0x79);

            HotkeyBinding restored;
            Assert.True(HotkeyBinding.TryParse(original.Serialize(), out restored), "parsed");
            Assert.Equal(original, restored, "and it is the same binding");
        }

        [Test("A damaged binding is dropped rather than guessed at")]
        private static void DamagedBindingIsDropped()
        {
            HotkeyBinding parsed;

            Assert.False(HotkeyBinding.TryParse("nonsense", out parsed), "not a binding");
            Assert.False(HotkeyBinding.TryParse("", out parsed), "empty");
            Assert.False(HotkeyBinding.TryParse("2:", out parsed), "half a binding");
            Assert.False(HotkeyBinding.TryParse("2:0", out parsed), "no key");
            Assert.False(HotkeyBinding.TryParse("2:99999", out parsed), "not a virtual key");
            Assert.False(HotkeyBinding.TryParse("999:76", out parsed),
                "modifier bits nothing here would have written");
        }

        [Test("Two bindings that mean the same thing are equal")]
        private static void EqualityIsByMeaning()
        {
            var left = new HotkeyBinding(HotkeyModifiers.Alt | HotkeyModifiers.Control, VkL);
            var right = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, VkL);

            Assert.True(left.Equals(right), "flag order is not meaning");
            Assert.Equal(left.GetHashCode(), right.GetHashCode(), "and hashing agrees");
        }

        [Test("Unnamed keys are shown as a code rather than invented")]
        private static void UnknownKeysAreNotInvented()
        {
            Assert.Equal("F5", HotkeyKeyNames.Describe(0x74), "function keys are named");
            Assert.Equal("Num 3", HotkeyKeyNames.Describe(0x63), "numpad keys are named");
            Assert.Equal("A", HotkeyKeyNames.Describe(0x41), "letters are named");
            Assert.Equal("Key 7", HotkeyKeyNames.Describe(7), "and anything else is honest about it");
        }

        // ------------------------------------------------------- settings ---

        [Test("A settings file from before the hotkey existed loads with it off")]
        private static void OldSettingsUpgradeSafely()
        {
            AutoLockSettings settings = AutoLockSettings.Deserialize(
                "version=1\nlock-mode=automatic\n");

            Assert.Equal(LockMode.Automatic, settings.Mode, "the mode still reads");
            Assert.False(settings.HotkeyEnabled, "and the hotkey defaults to off");
            Assert.True(settings.Hotkey.IsEmpty, "with nothing bound");
        }

        [Test("The hotkey survives a save and load")]
        private static void HotkeySettingsRoundTrip()
        {
            var original = new AutoLockSettings
            {
                Mode = LockMode.Manual,
                HotkeyEnabled = true,
                Hotkey = new HotkeyBinding(HotkeyModifiers.None, VkApps)
            };

            AutoLockSettings restored = AutoLockSettings.Deserialize(original.Serialize());

            Assert.Equal(LockMode.Manual, restored.Mode, "mode");
            Assert.True(restored.HotkeyEnabled, "enabled");
            Assert.Equal(original.Hotkey, restored.Hotkey, "and the same key");
        }

        [Test("A settings file with a damaged hotkey still loads, with nothing bound")]
        private static void DamagedHotkeyInSettingsIsDropped()
        {
            AutoLockSettings settings = AutoLockSettings.Deserialize(
                "lock-mode=automatic\nhotkey-enabled=true\nhotkey=garbage\n");

            Assert.Equal(LockMode.Automatic, settings.Mode, "the rest of the file still works");
            Assert.True(settings.Hotkey.IsEmpty, "the key is dropped rather than guessed");
            Assert.Equal(HelperFeatures.LidLock, settings.RequiredFeatures,
                "and it asks for no helper work it could not do");
        }

        [Test("Required features follow the switches, not the lock mode")]
        private static void FeaturesFollowTheSwitches()
        {
            var manual = new AutoLockSettings();
            Assert.Equal(HelperFeatures.None, manual.RequiredFeatures, "manual, hotkey off");

            var hotkeyOnly = new AutoLockSettings
            {
                Mode = LockMode.Manual,
                HotkeyEnabled = true,
                Hotkey = new HotkeyBinding(HotkeyModifiers.None, VkApps)
            };
            Assert.Equal(HelperFeatures.GlobalHotkey, hotkeyOnly.RequiredFeatures,
                "manual can still need a helper now");

            var both = new AutoLockSettings
            {
                Mode = LockMode.Automatic,
                HotkeyEnabled = true,
                Hotkey = new HotkeyBinding(HotkeyModifiers.None, VkApps)
            };
            Assert.Equal(HelperFeatures.LidLock | HelperFeatures.GlobalHotkey, both.RequiredFeatures,
                "and one helper covers both");
        }

        [Test("An enabled hotkey with an unusable key asks for no helper work")]
        private static void UnusableHotkeyAsksForNothing()
        {
            var settings = new AutoLockSettings
            {
                Mode = LockMode.Manual,
                HotkeyEnabled = true,
                Hotkey = new HotkeyBinding(HotkeyModifiers.Control, 0x11)   // Ctrl alone
            };

            Assert.Equal(HelperFeatures.None, settings.RequiredFeatures,
                "a helper that could register nothing must not be the reason a process exists");
        }
    }
}
