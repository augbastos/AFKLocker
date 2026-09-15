using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AFKLocker.Core;

namespace AFKLocker.Tests
{
    /// <summary>Hardware as a string: "bright" until turned off.</summary>
    internal sealed class FakeLightingBackend : IKeyboardLightingBackend
    {
        public string BackendId = "fake";
        public bool Present = true;
        public string FailDetectWith;
        public string FailTurnOffWith;
        public string FailRestoreWith;
        public string State = "bright";
        public string SnapshotPath;
        public bool SnapshotExistedAtTurnOff;
        public readonly List<string> Calls = new List<string>();

        public string Id
        {
            get { return BackendId; }
        }

        public bool IsPresent()
        {
            if (FailDetectWith != null) throw new InvalidOperationException(FailDetectWith);
            return Present;
        }

        public string Capture()
        {
            Calls.Add("capture:" + State);
            return State;
        }

        public void TurnOff(string captured)
        {
            Calls.Add("off");
            SnapshotExistedAtTurnOff = SnapshotPath != null && File.Exists(SnapshotPath);
            State = "dark";
            if (FailTurnOffWith != null) throw new InvalidOperationException(FailTurnOffWith);
        }

        public void Restore(string captured)
        {
            Calls.Add("restore:" + captured);
            if (FailRestoreWith != null) throw new InvalidOperationException(FailRestoreWith);
            State = captured;
        }
    }

    /// <summary>
    /// The Acer firmware as this backend understands it: zone reads carry a zero
    /// status byte under the colour, zone writes carry the zone bit there.
    /// </summary>
    internal sealed class FakeAcerFirmware : IAcerGamingFirmware
    {
        public byte[] Backlight = { 0, 5, 80, 0, 1, 10, 20, 30, 3, 1, 0, 0, 0, 0, 0 };
        public ulong[] Zones = { 0x11223300, 0x44556600, 0x77889900, 0xAABBCC00 };
        public bool StaticBacklightWriteResetsZones;
        public bool IgnoreBacklightWrites;
        public bool NoZones;
        public readonly List<byte[]> BacklightWrites = new List<byte[]>();
        public readonly List<uint> ZoneWrites = new List<uint>();

        private static readonly uint[] ZoneBits = { 1, 2, 4, 8 };

        public bool IsPresent()
        {
            return true;
        }

        public byte[] ReadBacklight()
        {
            return (byte[])Backlight.Clone();
        }

        public void WriteBacklight(byte[] configuration)
        {
            BacklightWrites.Add((byte[])configuration.Clone());
            if (IgnoreBacklightWrites) return;
            Backlight = configuration.Take(15).ToArray();
            if (StaticBacklightWriteResetsZones && configuration[0] == 0) Zones = new ulong[4];
        }

        public ulong ReadZone(uint zone)
        {
            if (NoZones) throw new InvalidOperationException("no per-zone colour");
            return Zones[Array.IndexOf(ZoneBits, zone)];
        }

        public void WriteZone(uint value)
        {
            ZoneWrites.Add(value);
            Zones[Array.IndexOf(ZoneBits, value & 0xFF)] = value & 0xFFFFFF00;
        }
    }

    /// <summary>Task Scheduler that runs the helper in-process, as the elevated task would.</summary>
    internal sealed class InProcessScheduledTasks : IScheduledTasks
    {
        private readonly string _directory;
        private readonly KeyboardLightingController _controller;
        public bool IgnoreRuns;
        public readonly List<string> Started = new List<string>();

        public InProcessScheduledTasks(string directory, KeyboardLightingController controller)
        {
            _directory = directory;
            _controller = controller;
        }

        public bool Exists(string name) { return true; }
        public void Create(string name, string xmlPath) { }
        public void Delete(string name) { }

        public void Run(string name)
        {
            Started.Add(name);
            if (IgnoreRuns) return;
            KeyboardLightingHelper.RunWorker(name == KeyboardLightingHelper.RestoreTaskName, _directory, _controller);
        }
    }

    internal static class KeyboardLightingTests
    {
        private static string NewDirectory()
        {
            string directory = Path.Combine(Path.GetTempPath(), "afklocker-lighting-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static KeyboardLightingController Controller(string directory, params IKeyboardLightingBackend[] backends)
        {
            return new KeyboardLightingController(KeyboardLightingHelper.SnapshotPath(directory), backends);
        }

        // ------------------------------------------------------------ detection ---

        [Test("detection picks the first backend the hardware exposes")]
        private static void DetectPicksPresentBackend()
        {
            var absent = new FakeLightingBackend { BackendId = "absent", Present = false };
            var present = new FakeLightingBackend { BackendId = "present" };

            KeyboardLightingDetection detection = Controller(NewDirectory(), absent, present).Detect();

            Assert.Equal(KeyboardLightingSupport.Supported, detection.Support, "support");
            Assert.True(ReferenceEquals(present, detection.Backend), "the matching backend is used");
        }

        [Test("hardware no backend recognises is unsupported, not an error")]
        private static void DetectUnsupported()
        {
            KeyboardLightingDetection detection =
                Controller(NewDirectory(), new FakeLightingBackend { Present = false }).Detect();

            Assert.Equal(KeyboardLightingSupport.Unsupported, detection.Support, "support");
            Assert.Null(detection.Detail, "nothing alarming to report");
        }

        [Test("a probe that cannot tell is reported as detection failed, with its reason")]
        private static void DetectFailed()
        {
            KeyboardLightingDetection detection =
                Controller(NewDirectory(), new FakeLightingBackend { FailDetectWith = "WMI is broken" }).Detect();

            Assert.Equal(KeyboardLightingSupport.DetectionFailed, detection.Support, "support");
            Assert.Equal("WMI is broken", detection.Detail, "detail");
        }

        [Test("one failing probe does not hide a later backend that matches")]
        private static void DetectFailureDoesNotHideMatch()
        {
            var broken = new FakeLightingBackend { BackendId = "broken", FailDetectWith = "boom" };
            var present = new FakeLightingBackend { BackendId = "present" };

            Assert.Equal(KeyboardLightingSupport.Supported, Controller(NewDirectory(), broken, present).Detect().Support,
                "support");
        }

        // ----------------------------------------------------------- controller ---

        [Test("the captured state is on disk before the keyboard is darkened, and removed after restore")]
        private static void SnapshotPrecedesDarkness()
        {
            string directory = NewDirectory();
            var backend = new FakeLightingBackend { SnapshotPath = KeyboardLightingHelper.SnapshotPath(directory) };
            KeyboardLightingController controller = Controller(directory, backend);

            controller.TurnOff();
            Assert.True(backend.SnapshotExistedAtTurnOff, "snapshot written first");
            Assert.Equal("dark", backend.State, "dark during AFK");

            controller.Restore();
            Assert.Equal("bright", backend.State, "restored");
            Assert.False(controller.HasPendingRestore, "snapshot removed");
        }

        [Test("a session killed while dark is restored before a new state is captured")]
        private static void StaleSnapshotIsNeverOverwritten()
        {
            string directory = NewDirectory();
            var backend = new FakeLightingBackend();

            Controller(directory, backend).TurnOff();   // then the process dies
            KeyboardLightingController next = Controller(directory, backend);
            next.TurnOff();

            Assert.Equal("restore:bright", backend.Calls[2], "the stale state goes back first");
            Assert.Equal("capture:bright", backend.Calls[3], "so the capture is the real state, not the dark one");

            next.Restore();
            Assert.Equal("bright", backend.State, "the original lighting survives");
        }

        [Test("a restore that fails keeps the snapshot for the next attempt")]
        private static void FailedRestoreKeepsSnapshot()
        {
            string directory = NewDirectory();
            var backend = new FakeLightingBackend();
            KeyboardLightingController controller = Controller(directory, backend);
            controller.TurnOff();

            backend.FailRestoreWith = "firmware busy";
            Assert.Throws<InvalidOperationException>(controller.Restore, "the failure is reported");
            Assert.True(controller.HasPendingRestore, "the only copy of the lighting is kept");
        }

        [Test("a snapshot from an unknown backend is kept, never guessed at")]
        private static void UnknownBackendSnapshotIsKept()
        {
            string directory = NewDirectory();
            Controller(directory, new FakeLightingBackend { BackendId = "old" }).TurnOff();

            KeyboardLightingController other = Controller(directory, new FakeLightingBackend { BackendId = "new" });
            Assert.Throws<InvalidOperationException>(other.Restore, "no backend claims it");
            Assert.True(other.HasPendingRestore, "kept");
        }

        [Test("unsupported hardware changes nothing and writes no snapshot")]
        private static void UnsupportedTurnOffWritesNothing()
        {
            string directory = NewDirectory();
            var backend = new FakeLightingBackend { Present = false };
            KeyboardLightingController controller = Controller(directory, backend);

            Assert.Throws<InvalidOperationException>(controller.TurnOff, "refused");
            Assert.False(controller.HasPendingRestore, "no snapshot");
            Assert.Equal(0, backend.Calls.Count, "the backend was never driven");
        }

        // --------------------------------------------------------- Acer backend ---

        [Test("Acer: off rewrites the captured configuration with only brightness at zero")]
        private static void AcerOffChangesOnlyBrightness()
        {
            var firmware = new FakeAcerFirmware();
            byte[] original = firmware.ReadBacklight();
            var backend = new AcerGamingKeyboardBackend(firmware);

            backend.TurnOff(backend.Capture());

            Assert.Equal(1, firmware.BacklightWrites.Count, "one write");
            byte[] written = firmware.BacklightWrites[0];
            Assert.Equal(16, written.Length, "the setter takes 16 bytes");
            for (int index = 0; index < original.Length; index++)
                Assert.Equal(index == 2 ? (byte)0 : original[index], written[index], "byte " + index);
            Assert.Equal(0, firmware.ZoneWrites.Count, "colours untouched");
        }

        [Test("Acer: restore writes back the exact bytes and leaves intact colours alone")]
        private static void AcerRestoreIsExact()
        {
            var firmware = new FakeAcerFirmware();
            byte[] original = firmware.ReadBacklight();
            var backend = new AcerGamingKeyboardBackend(firmware);
            string captured = backend.Capture();

            backend.TurnOff(captured);
            backend.Restore(captured);

            Assert.True(original.SequenceEqual(firmware.Backlight), "configuration restored byte for byte");
            Assert.Equal(0, firmware.ZoneWrites.Count, "no colour writes when colours never changed");
        }

        [Test("Acer: colours reset by a static-mode backlight write are written back and verified")]
        private static void AcerRestoreRepairsZones()
        {
            var firmware = new FakeAcerFirmware { StaticBacklightWriteResetsZones = true };
            ulong[] zones = (ulong[])firmware.Zones.Clone();
            var backend = new AcerGamingKeyboardBackend(firmware);
            string captured = backend.Capture();

            backend.TurnOff(captured);
            Assert.Equal(0UL, firmware.Zones[0], "the simulated firmware lost the colours");
            backend.Restore(captured);

            Assert.True(zones.SequenceEqual(firmware.Zones), "every zone colour is back");
            Assert.Equal(0x11223301u, firmware.ZoneWrites[0], "zone bit in the low byte, colour above it");
        }

        [Test("Acer: an effect mode never gets zone writes, which would replace the effect")]
        private static void AcerEffectModeSkipsZones()
        {
            var firmware = new FakeAcerFirmware { StaticBacklightWriteResetsZones = true };
            firmware.Backlight[0] = 3;
            var backend = new AcerGamingKeyboardBackend(firmware);
            string captured = backend.Capture();

            firmware.Zones = new ulong[4];   // whatever the effect reports
            backend.Restore(captured);

            Assert.Equal(0, firmware.ZoneWrites.Count, "no zone writes in an effect mode");
        }

        [Test("Acer: firmware that ignores the request is reported, not trusted")]
        private static void AcerIgnoredWriteFails()
        {
            var firmware = new FakeAcerFirmware { IgnoreBacklightWrites = true };
            var backend = new AcerGamingKeyboardBackend(firmware);

            Assert.Throws<InvalidOperationException>(() => backend.TurnOff(backend.Capture()),
                "a read-back that is still lit is a failure");
        }

        [Test("Acer: a keyboard without per-zone colour still turns off and back on")]
        private static void AcerWithoutZones()
        {
            var firmware = new FakeAcerFirmware { NoZones = true };
            byte[] original = firmware.ReadBacklight();
            var backend = new AcerGamingKeyboardBackend(firmware);
            string captured = backend.Capture();

            backend.TurnOff(captured);
            backend.Restore(captured);

            Assert.True(original.SequenceEqual(firmware.Backlight), "restored");
        }

        [Test("Acer: a malformed saved state is refused before anything is written")]
        private static void AcerRejectsMalformedState()
        {
            var firmware = new FakeAcerFirmware();
            var backend = new AcerGamingKeyboardBackend(firmware);

            Assert.Throws<FormatException>(() => backend.Restore("backlight=AAAA"), "malformed");
            Assert.Equal(0, firmware.BacklightWrites.Count, "nothing written");
        }

        [Test("Acer: the lighting survives a process restart through the controller snapshot")]
        private static void AcerSnapshotSurvivesRestart()
        {
            string directory = NewDirectory();
            var firmware = new FakeAcerFirmware();
            byte[] original = firmware.ReadBacklight();

            Controller(directory, new AcerGamingKeyboardBackend(firmware)).TurnOff();
            Controller(directory, new AcerGamingKeyboardBackend(firmware)).Restore();

            Assert.True(original.SequenceEqual(firmware.Backlight), "restored by a fresh process");
        }

        // ------------------------------------------------------- elevated helper ---

        [Test("an AFK session darkens and restores the keyboard through the helper's tasks, in order")]
        private static void SessionRunsOffThenRestore()
        {
            string directory = NewDirectory();
            var backend = new FakeLightingBackend();
            var tasks = new InProcessScheduledTasks(directory, Controller(directory, backend));

            var session = new ElevatedKeyboardLightingSession(tasks, directory, TimeSpan.FromSeconds(5));
            session.Enter();
            Assert.Equal("dark", backend.State, "dark during AFK");
            session.Dispose();

            Assert.Equal("bright", backend.State, "restored");
            Assert.Equal(KeyboardLightingHelper.OffTaskName, tasks.Started[0], "off first");
            Assert.Equal(KeyboardLightingHelper.RestoreTaskName, tasks.Started[1], "then restore");
            Assert.False(session.HasPendingRestore, "nothing left to recover");
        }

        [Test("a failed off is reported, and restore still runs")]
        private static void SessionRestoresAfterFailedOff()
        {
            string directory = NewDirectory();
            var backend = new FakeLightingBackend { FailTurnOffWith = "controller refused" };
            var tasks = new InProcessScheduledTasks(directory, Controller(directory, backend));

            var session = new ElevatedKeyboardLightingSession(tasks, directory, TimeSpan.FromSeconds(5));
            session.Enter();

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(session.Dispose, "reported");
            Assert.Equal("controller refused", error.Message, "the helper's own reason");
            Assert.Equal("bright", backend.State, "the partial change was restored");
        }

        [Test("a result from an earlier session is never mistaken for this one's")]
        private static void OldResultsAreIgnored()
        {
            string directory = NewDirectory();
            var backend = new FakeLightingBackend();
            var tasks = new InProcessScheduledTasks(directory, Controller(directory, backend));

            var first = new ElevatedKeyboardLightingSession(tasks, directory, TimeSpan.FromSeconds(5));
            first.Enter();
            first.Dispose();

            backend.FailRestoreWith = "second restore failed";
            var second = new ElevatedKeyboardLightingSession(tasks, directory, TimeSpan.FromSeconds(5));
            second.Enter();

            Assert.Throws<InvalidOperationException>(second.Dispose, "the old ok is not this restore's result");
            Assert.True(second.HasPendingRestore, "and the lighting is still recoverable");
        }

        [Test("a task that never reports back times out instead of hanging")]
        private static void SilentTaskTimesOut()
        {
            string directory = NewDirectory();
            var tasks = new InProcessScheduledTasks(directory, Controller(directory, new FakeLightingBackend()))
            {
                IgnoreRuns = true
            };

            var session = new ElevatedKeyboardLightingSession(tasks, directory, TimeSpan.FromMilliseconds(300));
            session.Enter();

            Assert.Throws<TimeoutException>(session.Dispose, "bounded wait");
        }
    }
}
