using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AFKLocker.Core
{
    /// <summary>
    /// What the sign-in entry actually is, as opposed to whether one exists.
    ///
    /// "A value is present" was the old test, and it accepted an entry left
    /// behind by an older install in a folder that no longer exists. That entry
    /// looks healthy in every status screen and starts nothing.
    /// </summary>
    public enum AutostartState
    {
        /// <summary>No value under the Run key.</summary>
        Absent,

        /// <summary>Present, and it launches this installation's helper.</summary>
        Correct,

        /// <summary>Present, but it launches something else, or this helper with unexpected arguments.</summary>
        WrongTarget,

        /// <summary>Present, but it is not a command anything could run.</summary>
        Malformed,

        /// <summary>The registry could not be read, so nothing is known.</summary>
        ReadFailed
    }

    /// <summary>The sign-in entry, examined.</summary>
    public sealed class AutostartInspection
    {
        public AutostartState State { get; private set; }

        /// <summary>The raw value, or null when there is none. May contain a path.</summary>
        public string RawCommand { get; private set; }

        /// <summary>The executable the command resolves to, when it could be worked out.</summary>
        public string ExecutablePath { get; private set; }

        /// <summary>One sentence about what is wrong, or null when nothing is.</summary>
        public string Problem { get; private set; }

        internal AutostartInspection(AutostartState state, string rawCommand,
            string executablePath, string problem)
        {
            State = state;
            RawCommand = rawCommand;
            ExecutablePath = executablePath;
            Problem = problem;
        }

        public bool IsCorrect
        {
            get { return State == AutostartState.Correct; }
        }

        /// <summary>True when a value exists, whatever it points at.</summary>
        public bool IsPresent
        {
            get { return State != AutostartState.Absent && State != AutostartState.ReadFailed; }
        }
    }

    /// <summary>
    /// Builds and reads back the sign-in command, and answers the only question
    /// that matters about it: does this launch <em>this</em> installation's
    /// helper, or something that merely has the same file name?
    ///
    /// Comparing the strings would be wrong in both directions. The same command
    /// can be written many ways - quoted or not, different casing, a short 8.3
    /// path, an environment variable, a trailing space - and none of those are a
    /// real difference. Meanwhile <c>D:\Old\AFKLockerWatcher.exe</c> differs from
    /// the installed one only in the part a naive contains-check throws away.
    /// So the command is parsed into an executable and arguments, the executable
    /// is canonicalised, and the comparison happens there.
    /// </summary>
    public static class AutostartCommand
    {
        private const int MaximumPathLength = 32767;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetLongPathNameW(string shortPath, StringBuilder longPath, int buffer);

        /// <summary>The command AFKLocker writes for a given helper.</summary>
        public static string For(string executablePath)
        {
            if (string.IsNullOrEmpty(executablePath))
                throw new ArgumentException("executablePath must not be empty", "executablePath");

            // Always quoted. The install path contains a space on a default
            // install, and an unquoted command with a space is ambiguous by
            // design - Windows guesses, and its guess is not ours to rely on.
            return "\"" + executablePath.Trim() + "\"";
        }

        /// <summary>
        /// Splits a Run-key command into the program and its arguments.
        ///
        /// Unquoted commands with spaces are genuinely ambiguous:
        /// <c>C:\Program Files\A B.exe</c> could be that program, or the program
        /// <c>C:\Program</c> with two arguments. Windows resolves it by trying
        /// each prefix until one exists on disk, so that is what this does; when
        /// nothing exists it falls back to the first space, which is the reading
        /// that makes a broken entry look broken instead of plausible.
        /// </summary>
        public static bool TryParse(string command, out string executable, out string arguments)
        {
            executable = null;
            arguments = null;

            if (command == null) return false;

            string text = command.Trim();
            if (text.Length == 0) return false;

            if (text[0] == '"')
            {
                int closing = text.IndexOf('"', 1);
                if (closing < 0) return false;           // opened a quote and never closed it

                executable = text.Substring(1, closing - 1).Trim();
                arguments = text.Substring(closing + 1).Trim();
                return executable.Length > 0;
            }

            int space = text.IndexOf(' ');
            if (space < 0)
            {
                executable = text;
                arguments = string.Empty;
                return true;
            }

            // Try the longest reading first: the whole string as one path.
            if (LooksLikeAFile(text))
            {
                executable = text;
                arguments = string.Empty;
                return true;
            }

            // Each probe below is up to two filesystem calls, and the registry
            // will happily hold a value with thousands of spaces in it. Left
            // unbounded, a value nothing legitimate would ever write made this
            // take fifteen seconds - on the Setup window's UI thread, and at
            // every startup through reconciliation. A real program path has a
            // handful of spaces; past that this is not a path being resolved,
            // it is work being wasted.
            const int MaximumProbes = 24;

            int at = space;
            int probes = 0;
            while (at > 0 && probes < MaximumProbes)
            {
                probes++;

                string candidate = text.Substring(0, at);
                if (LooksLikeAFile(candidate))
                {
                    executable = candidate;
                    arguments = text.Substring(at).Trim();
                    return true;
                }

                at = text.IndexOf(' ', at + 1);
            }

            executable = text.Substring(0, space);
            arguments = text.Substring(space).Trim();
            return true;
        }

        private static bool LooksLikeAFile(string candidate)
        {
            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(candidate);
                return File.Exists(expanded) || File.Exists(expanded + ".exe");
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// True when two paths name the same program, ignoring the differences
        /// that are only spelling: environment variables, casing, short 8.3
        /// names, redundant separators and <c>.</c> or <c>..</c> segments.
        /// </summary>
        public static bool SameExecutable(string left, string right)
        {
            string a = Canonicalise(left);
            string b = Canonicalise(right);
            if (a == null || b == null) return false;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The comparable form of a path, or null when it is not one at all.
        /// Deliberately does not require the file to exist: an entry pointing at
        /// a deleted folder must still be recognisable as pointing elsewhere.
        /// </summary>
        public static string Canonicalise(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            try
            {
                string text = Environment.ExpandEnvironmentVariables(path.Trim());
                text = text.Trim('"').Trim();
                if (text.Length == 0) return null;

                // A relative reference has no fixed meaning here. GetFullPath
                // would resolve it against the CURRENT PROCESS's working
                // directory, so the identical registry value would be judged
                // "correct" by one AFKLocker process and "wrong" by another
                // depending only on where each happened to be started from -
                // which is precisely the disagreement this class exists to make
                // impossible. It is also not how Windows resolves a Run entry.
                //
                // AFKLocker only ever writes a quoted absolute path, so anything
                // relative was put there by something else and cannot be
                // confirmed as ours.
                if (!Path.IsPathRooted(text)) return null;

                // GetFullPath does the . and .. and separator work, and throws on
                // anything that is not a usable path - which is the answer we
                // want for a malformed entry.
                string full = Path.GetFullPath(text);
                full = ExpandShortPath(full);

                if (full.Length > 3 && (full.EndsWith("\\", StringComparison.Ordinal)
                    || full.EndsWith("/", StringComparison.Ordinal)))
                    full = full.Substring(0, full.Length - 1);

                return full;
            }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
            catch (IOException) { return null; }
            catch (System.Security.SecurityException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>
        /// Turns PROGRA~1 into "Program Files" when the path exists. Windows
        /// leaves 8.3 names working forever, so an old entry can be spelled that
        /// way and mean exactly the same file.
        /// </summary>
        private static string ExpandShortPath(string path)
        {
            if (path.IndexOf('~') < 0) return path;

            try
            {
                var buffer = new StringBuilder(MaximumPathLength);
                int length = GetLongPathNameW(path, buffer, buffer.Capacity);
                if (length > 0 && length < buffer.Capacity)
                    return buffer.ToString();
            }
            catch (Exception)
            {
                // Path does not exist, or the call is unavailable. The short form
                // is still a usable answer, just a less canonical one.
            }

            return path;
        }
    }

    /// <summary>
    /// Reads the sign-in entry and says what it is.
    ///
    /// Separate from <see cref="IAutostartRegistry"/> on purpose: the registry
    /// reads and writes, this decides, and every caller that needs a verdict
    /// (status, reconciliation, diagnostics) goes through this one so they can
    /// never disagree with each other.
    /// </summary>
    public static class AutostartInspector
    {
        public static AutostartInspection Inspect(IAutostartRegistry registry, string expectedExecutable)
        {
            if (registry == null) throw new ArgumentNullException("registry");

            string raw;
            try
            {
                raw = registry.RegisteredCommand;
            }
            catch (Exception ex)
            {
                return new AutostartInspection(AutostartState.ReadFailed, null, null,
                    "The Windows startup entry could not be read: " + ex.Message);
            }

            if (raw == null)
                return new AutostartInspection(AutostartState.Absent, null, null, null);

            if (raw.Trim().Length == 0)
                return new AutostartInspection(AutostartState.Malformed, raw, null,
                    "The startup entry is empty.");

            string executable;
            string arguments;
            if (!AutostartCommand.TryParse(raw, out executable, out arguments))
                return new AutostartInspection(AutostartState.Malformed, raw, null,
                    "The startup entry is not a command Windows could run.");

            string canonical = AutostartCommand.Canonicalise(executable);
            if (canonical == null)
                return new AutostartInspection(AutostartState.Malformed, raw, executable,
                    "The startup entry does not contain a usable program path.");

            if (string.IsNullOrEmpty(expectedExecutable))
                return new AutostartInspection(AutostartState.WrongTarget, raw, canonical,
                    "AFKLocker does not know where its helper is, so the startup entry "
                    + "cannot be confirmed.");

            if (!AutostartCommand.SameExecutable(executable, expectedExecutable))
                return new AutostartInspection(AutostartState.WrongTarget, raw, canonical,
                    "The startup entry points at a different program from the AFKLocker "
                    + "helper installed here.");

            if (!string.IsNullOrEmpty(arguments))
                return new AutostartInspection(AutostartState.WrongTarget, raw, canonical,
                    "The startup entry launches the AFKLocker helper with unexpected arguments.");

            return new AutostartInspection(AutostartState.Correct, raw, canonical, null);
        }

        /// <summary>A short phrase for a status line.</summary>
        public static string Describe(AutostartState state)
        {
            switch (state)
            {
                case AutostartState.Correct: return "points to current AFKLocker helper";
                case AutostartState.WrongTarget: return "entry points to a different location";
                case AutostartState.Malformed: return "entry is not a runnable command";
                case AutostartState.ReadFailed: return "the startup entry could not be read";
                default: return "no startup entry";
            }
        }
    }
}
