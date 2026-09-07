using System;
using System.IO;
using AFKLocker.Core;

namespace AFKLocker.Tests
{
    /// <summary>
    /// The helper must never run with an administrator token.
    ///
    /// AFKLocker Setup relaunches itself elevated when Windows refuses a power
    /// setting, and a child process inherits its parent's token - so a helper
    /// started from that window would sit in the session as administrator,
    /// holding a global hotkey registration and locking the session with rights
    /// it has no use for. Nothing about the helper needs them.
    ///
    /// Two things are fixed in place here, because either one alone is a bug:
    /// the launch is refused at the only line that can start the process, and
    /// the manager steps aside before that refusal can be mistaken for "the
    /// configuration is broken" and used as grounds to switch the user's
    /// features off.
    /// </summary>
    internal static class ElevationTests
    {
        private static readonly string ExistingHelper = typeof(ElevationTests).Assembly.Location;

        private static readonly HotkeyBinding MenuKey =
            new HotkeyBinding(HotkeyModifiers.None, 0x5D);          // VK_APPS

        private static AutoLockManager Elevated(FakeSettingsStore settings,
            FakeAutostartRegistry autostart, FakeWatcherProcess watcher)
        {
            return new AutoLockManager(settings, autostart, watcher, ExistingHelper,
                delegate { return true; });
        }

        // --------------------------------------------------- the launch itself ---

        /// <summary>
        /// Deliberately points at a file that exists but is not a program. If the
        /// refusal is ever removed this test has to fail rather than launch
        /// something: pointing it at a real executable would fork-bomb the suite,
        /// since the only executable a test can be sure of is the test runner.
        /// </summary>
        [Test("An elevated process refuses to launch the helper")]
        private static void RefusesToStartWhenElevated()
        {
            string path = Path.GetTempFileName();
            try
            {
                WatcherStartResult result =
                    new WatcherController(TimeSpan.FromSeconds(1), delegate { return true; })
                        .Start(path);

                Assert.False(result.Success, "an elevated process does not start the helper");
                Assert.Equal(WatcherStartFailure.RequiresStandardUser, result.Failure,
                    "and says why");
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test("A standard-user process is not stopped by the check")]
        private static void UnelevatedGetsPastTheCheck()
        {
            string path = Path.GetTempFileName();
            try
            {
                WatcherStartResult result =
                    new WatcherController(TimeSpan.FromSeconds(1), delegate { return false; })
                        .Start(path);

                // What the launch then does is the machine's business - the
                // temp file is not a program, and a watcher already running on
                // the developer's machine short-circuits it entirely. The only
                // thing asserted is that it was not turned away at the door,
                // which is what stops the refusal from becoming unconditional
                // and quietly disabling the helper for everybody.
                Assert.True(result.Failure != WatcherStartFailure.RequiresStandardUser,
                    "a standard-user process is allowed to try");
            }
            finally
            {
                File.Delete(path);
            }
        }

        // ------------------------------------------------------- the manager ---

        [Test("Turning automatic locking on from an elevated window changes nothing")]
        private static void EnableRefusedWhenElevated()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();

            AutoLockResult result = Elevated(settings, autostart, watcher).Enable();

            Assert.False(result.Success, "refused");
            Assert.Equal(AutoLockFailure.RequiresStandardUser, result.Failure, "and says why");
            Assert.Equal(0, watcher.StartCount, "nothing was launched");
            Assert.False(autostart.IsRegistered, "no sign-in entry was written");
            Assert.Equal(LockMode.Manual, settings.Load().Mode, "the saved mode is untouched");
        }

        [Test("Binding a hotkey from an elevated window changes nothing")]
        private static void HotkeyRefusedWhenElevated()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();

            AutoLockResult result = Elevated(settings, autostart, watcher).SetHotkey(true, MenuKey);

            Assert.False(result.Success, "refused");
            Assert.Equal(AutoLockFailure.RequiresStandardUser, result.Failure, "and says why");
            Assert.Equal(0, watcher.StartCount, "nothing was launched");
            Assert.False(settings.Load().HotkeyEnabled, "the hotkey was not switched on");
        }

        /// <summary>
        /// The dangerous one. Reconcile repairs a broken state by re-applying the
        /// stored settings, and switches the features off when it cannot. Left
        /// alone, an elevated window would hit the refusal above, read it as
        /// "automatic mode cannot be restored", and quietly turn off the mode and
        /// hotkey the user had chosen - just for opening the wrong window.
        /// </summary>
        [Test("Opening an elevated window does not tear down the saved configuration")]
        private static void ReconcileLeavesEverythingAloneWhenElevated()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();

            // Automatic and the hotkey on, but nothing running and no sign-in
            // entry: exactly the inconsistency Reconcile exists to repair.
            var stored = new AutoLockSettings();
            stored.Mode = LockMode.Automatic;
            stored.HotkeyEnabled = true;
            stored.Hotkey = MenuKey;
            settings.Save(stored);

            AutoLockResult result = Elevated(settings, autostart, watcher).Reconcile();

            Assert.True(result.Success, "reported as nothing to do rather than as a failure");
            Assert.Equal(0, watcher.StartCount, "nothing was launched");
            Assert.Equal(0, watcher.StopCount, "nothing was stopped");
            Assert.Equal(0, autostart.UnregisterCount, "the sign-in entry was left alone");

            AutoLockSettings after = settings.Load();
            Assert.Equal(LockMode.Automatic, after.Mode, "the mode survived");
            Assert.True(after.HotkeyEnabled, "the hotkey survived");
        }

        /// <summary>
        /// Cleanup is the uninstaller's path and only ever stops and removes, so
        /// elevation cannot make it start anything. Blocking it would leave a
        /// sign-in entry pointing into a deleted folder.
        /// </summary>
        [Test("Uninstall cleanup still works from an elevated process")]
        private static void CleanupStillWorksWhenElevated()
        {
            var settings = new FakeSettingsStore();
            var autostart = new FakeAutostartRegistry();
            var watcher = new FakeWatcherProcess();

            autostart.Preset(AutostartCommand.For(ExistingHelper));
            watcher.State = WatcherState.Ready;

            AutoLockResult result = Elevated(settings, autostart, watcher).Cleanup();

            Assert.True(result.Success, "cleaned up");
            Assert.False(autostart.IsRegistered, "the sign-in entry is gone");
            Assert.Equal(WatcherState.NotRunning, watcher.GetState(), "and nothing is running");
        }
    }
}
