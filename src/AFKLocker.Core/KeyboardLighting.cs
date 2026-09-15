using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace AFKLocker.Core
{
    public enum KeyboardLightingSupport
    {
        Unsupported,
        Supported,
        DetectionFailed
    }

    public sealed class KeyboardLightingDetection
    {
        internal KeyboardLightingDetection(KeyboardLightingSupport support,
            IKeyboardLightingBackend backend, string detail)
        {
            Support = support;
            Backend = backend;
            Detail = detail;
        }

        public KeyboardLightingSupport Support { get; private set; }

        /// <summary>The backend that will be used. Null unless supported.</summary>
        public IKeyboardLightingBackend Backend { get; private set; }

        /// <summary>Why detection failed. Diagnostic text, never a headline.</summary>
        public string Detail { get; private set; }
    }

    /// <summary>
    /// One way of controlling keyboard lighting on some hardware.
    ///
    /// Every vendor detail stays behind this interface. The controller, the AFK
    /// session and Setup only ever deal in "supported", "off" and "restore", so
    /// adding hardware never touches the UI or the session.
    /// </summary>
    public interface IKeyboardLightingBackend
    {
        /// <summary>Stable id written into the recovery snapshot. Never shown to users.</summary>
        string Id { get; }

        /// <summary>
        /// Whether this machine exposes the interface. Fast, changes nothing, and
        /// works without administrator rights. Throws when it cannot tell.
        /// </summary>
        bool IsPresent();

        /// <summary>The current lighting state as a single line of text.</summary>
        string Capture();

        /// <summary>Darkens the keyboard, starting from what Capture returned.</summary>
        void TurnOff(string captured);

        /// <summary>Puts back exactly what Capture returned, and checks that it took.</summary>
        void Restore(string captured);
    }

    /// <summary>
    /// Picks the backend that can control this machine's keyboard lighting and
    /// runs capture, persist, off and restore through it.
    ///
    /// The captured state reaches disk before anything changes, because while
    /// the keyboard is dark that file is the only copy of the user's real
    /// lighting. For the same reason TurnOff never overwrites it: a snapshot
    /// left behind by a session that was killed is restored first.
    /// </summary>
    public sealed class KeyboardLightingController
    {
        private const int SnapshotVersion = 1;

        private readonly IKeyboardLightingBackend[] _backends;
        private readonly string _snapshotPath;

        public KeyboardLightingController(string snapshotPath)
            : this(snapshotPath, KnownBackends())
        {
        }

        public KeyboardLightingController(string snapshotPath, params IKeyboardLightingBackend[] backends)
        {
            if (string.IsNullOrEmpty(snapshotPath))
                throw new ArgumentException("snapshot path must not be empty", "snapshotPath");
            if (backends == null) throw new ArgumentNullException("backends");
            _snapshotPath = snapshotPath;
            _backends = backends;
        }

        /// <summary>
        /// Every backend AFKLocker ships, most specific first. Supporting more
        /// hardware means adding an entry here and nothing anywhere else.
        /// </summary>
        public static IKeyboardLightingBackend[] KnownBackends()
        {
            return new IKeyboardLightingBackend[] { new AcerGamingKeyboardBackend() };
        }

        public bool HasPendingRestore
        {
            get { return File.Exists(_snapshotPath); }
        }

        public KeyboardLightingDetection Detect()
        {
            string failure = null;
            foreach (IKeyboardLightingBackend backend in _backends)
            {
                try
                {
                    if (backend.IsPresent())
                        return new KeyboardLightingDetection(KeyboardLightingSupport.Supported, backend, null);
                }
                catch (Exception ex)
                {
                    // Keep looking: a later backend may still match. A failure
                    // only decides the answer when nothing else does.
                    if (failure == null) failure = ex.Message;
                }
            }

            return failure == null
                ? new KeyboardLightingDetection(KeyboardLightingSupport.Unsupported, null, null)
                : new KeyboardLightingDetection(KeyboardLightingSupport.DetectionFailed, null, failure);
        }

        public void TurnOff()
        {
            Restore();

            KeyboardLightingDetection detection = Detect();
            if (detection.Support == KeyboardLightingSupport.Unsupported)
                throw new InvalidOperationException("This PC's keyboard lighting is not supported.");
            if (detection.Support == KeyboardLightingSupport.DetectionFailed)
                throw new InvalidOperationException("Keyboard lighting could not be detected: " + detection.Detail);

            IKeyboardLightingBackend backend = detection.Backend;
            string captured = backend.Capture();
            AtomicFile.WriteAllText(_snapshotPath, Serialize(backend.Id, captured));

            // If this throws the snapshot stays, so Restore still puts back
            // whatever a partial change did.
            backend.TurnOff(captured);
        }

        public void Restore()
        {
            if (!File.Exists(_snapshotPath)) return;

            string id;
            string captured;
            Parse(File.ReadAllText(_snapshotPath, Encoding.UTF8), out id, out captured);

            IKeyboardLightingBackend backend = null;
            foreach (IKeyboardLightingBackend candidate in _backends)
                if (string.Equals(candidate.Id, id, StringComparison.Ordinal)) backend = candidate;
            if (backend == null)
                throw new InvalidOperationException("The saved keyboard lighting state belongs to a controller "
                                                    + "this version of AFKLocker does not know.");

            backend.Restore(captured);
            File.Delete(_snapshotPath);
        }

        internal static string Serialize(string backendId, string captured)
        {
            if (captured == null || captured.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                throw new InvalidOperationException("A keyboard lighting backend returned a state that is not one line.");

            var text = new StringBuilder();
            text.AppendLine("# AFKLocker keyboard lighting, captured before an AFK session");
            text.AppendLine("version=" + SnapshotVersion.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("backend=" + backendId);
            text.AppendLine("state=" + captured);
            return text.ToString();
        }

        internal static void Parse(string text, out string backendId, out string captured)
        {
            string version = null;
            backendId = null;
            captured = null;

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                int separator = line.IndexOf('=');
                if (separator <= 0) throw new FormatException("Malformed keyboard lighting snapshot.");
                string key = line.Substring(0, separator);
                string value = line.Substring(separator + 1);

                if (key == "version") version = value;
                else if (key == "backend") backendId = value;
                else if (key == "state") captured = value;
            }

            if (version != SnapshotVersion.ToString(CultureInfo.InvariantCulture))
                throw new FormatException("Keyboard lighting snapshot version is not supported.");
            if (string.IsNullOrEmpty(backendId) || captured == null)
                throw new FormatException("Keyboard lighting snapshot is incomplete.");
        }
    }

    /// <summary>The keyboard lighting part of one AFK session.</summary>
    public interface IKeyboardLightingSession : IDisposable
    {
        void Enter();

        /// <summary>True while captured lighting is saved and not yet put back.</summary>
        bool HasPendingRestore { get; }
    }

    public sealed class NoKeyboardLightingSession : IKeyboardLightingSession
    {
        public void Enter() { }
        public void Dispose() { }

        public bool HasPendingRestore
        {
            get { return false; }
        }
    }
}
