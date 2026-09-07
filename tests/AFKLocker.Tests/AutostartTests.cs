using System;
using System.IO;
using AFKLocker.Core;

namespace AFKLocker.Tests
{
    /// <summary>
    /// What the sign-in entry actually is, as opposed to whether one exists.
    ///
    /// The old check asked whether the command string contained
    /// "AFKLockerWatcher.exe". That accepts an entry from an uninstalled copy in
    /// a folder that no longer exists: it looks healthy in every status screen
    /// and starts nothing. These tests fix the two directions that matters in -
    /// the same program spelled differently must still be the same program, and
    /// a different program with the same file name must not be.
    /// </summary>
    internal static class AutostartTests
    {
        /// <summary>A file that genuinely exists, so path resolution is real.</summary>
        private static readonly string Installed = typeof(AutostartTests).Assembly.Location;

        private static AutostartInspection Inspect(string rawCommand)
        {
            var registry = new FakeAutostartRegistry();
            if (rawCommand != null) registry.Preset(rawCommand);
            return AutostartInspector.Inspect(registry, Installed);
        }

        [Test("The exact command AFKLocker writes is recognised as correct")]
        private static void ExactMatchIsCorrect()
        {
            AutostartInspection result = Inspect(AutostartCommand.For(Installed));

            Assert.Equal(AutostartState.Correct, result.State, "this is what we wrote");
            Assert.Null(result.Problem, "and nothing to report");
        }

        [Test("Different casing is the same program, not a mismatch")]
        private static void CasingDoesNotMatter()
        {
            AutostartInspection result = Inspect("\"" + Installed.ToUpperInvariant() + "\"");

            Assert.Equal(AutostartState.Correct, result.State,
                "Windows paths are case-insensitive, so shouting is not a difference");
        }

        [Test("An unquoted command is still recognised")]
        private static void UnquotedIsStillRecognised()
        {
            AutostartInspection result = Inspect(Installed);

            Assert.Equal(AutostartState.Correct, result.State,
                "quoting is spelling, not meaning");
        }

        [Test("Surrounding whitespace is not a difference")]
        private static void WhitespaceDoesNotMatter()
        {
            AutostartInspection result = Inspect("   \"" + Installed + "\"   ");

            Assert.Equal(AutostartState.Correct, result.State, "trimmed, not rejected");
        }

        [Test("Redundant path segments resolve to the same program")]
        private static void RedundantSegmentsResolve()
        {
            string directory = Path.GetDirectoryName(Installed);
            string fileName = Path.GetFileName(Installed);
            string awkward = Path.Combine(directory, ".", fileName);

            AutostartInspection result = Inspect("\"" + awkward + "\"");

            Assert.Equal(AutostartState.Correct, result.State,
                "a dot segment is not a different file");
        }

        [Test("An environment variable that expands to the same program is accepted")]
        private static void EnvironmentVariableExpands()
        {
            string directory = Path.GetDirectoryName(Installed);
            string fileName = Path.GetFileName(Installed);

            Environment.SetEnvironmentVariable("AFKLOCKER_TEST_DIR", directory);
            try
            {
                AutostartInspection result = Inspect("\"%AFKLOCKER_TEST_DIR%\\" + fileName + "\"");

                Assert.Equal(AutostartState.Correct, result.State,
                    "Run entries legitimately use environment variables");
            }
            finally
            {
                Environment.SetEnvironmentVariable("AFKLOCKER_TEST_DIR", null);
            }
        }

        [Test("The same file name in another folder is NOT accepted")]
        private static void SameFileNameElsewhereIsWrong()
        {
            string fileName = Path.GetFileName(Installed);

            AutostartInspection result = Inspect("\"D:\\OldAFKLocker\\" + fileName + "\"");

            Assert.Equal(AutostartState.WrongTarget, result.State,
                "this is the leftover entry the old contains-check waved through");
            Assert.NotNull(result.Problem, "and it says so");
        }

        [Test("An entry pointing at a completely different program is wrong")]
        private static void DifferentProgramIsWrong()
        {
            AutostartInspection result = Inspect("\"C:\\Windows\\System32\\notepad.exe\"");

            Assert.Equal(AutostartState.WrongTarget, result.State, "not our helper");
        }

        [Test("A path that only shares a prefix is not a match")]
        private static void PrefixIsNotAMatch()
        {
            AutostartInspection result = Inspect("\"" + Installed + ".backup.exe\"");

            Assert.Equal(AutostartState.WrongTarget, result.State,
                "starts with the right path, is a different file");
        }

        [Test("The right program with unexpected arguments is not accepted")]
        private static void UnexpectedArgumentsAreWrong()
        {
            AutostartInspection result = Inspect("\"" + Installed + "\" --something-else");

            Assert.Equal(AutostartState.WrongTarget, result.State,
                "AFKLocker writes no arguments, so these came from somewhere else");
        }

        [Test("No entry at all is absent, not broken")]
        private static void MissingEntryIsAbsent()
        {
            AutostartInspection result = Inspect(null);

            Assert.Equal(AutostartState.Absent, result.State, "nothing there");
            Assert.False(result.IsPresent, "and nothing to clean up");
        }

        [Test("An empty entry is malformed rather than absent")]
        private static void EmptyEntryIsMalformed()
        {
            AutostartInspection result = Inspect("   ");

            Assert.Equal(AutostartState.Malformed, result.State,
                "a value that exists and says nothing is a different problem from no value");
        }

        [Test("An unterminated quote is malformed")]
        private static void UnterminatedQuoteIsMalformed()
        {
            AutostartInspection result = Inspect("\"C:\\Program Files\\AFKLocker\\helper.exe");

            Assert.Equal(AutostartState.Malformed, result.State, "Windows could not run this either");
        }

        [Test("A command with no usable path at all is malformed")]
        private static void NonsenseIsMalformed()
        {
            AutostartInspection result = Inspect("\"|||\"");

            Assert.Equal(AutostartState.Malformed, result.State, "not a path");
        }

        [Test("A registry read failure is reported as unknown, never as absent")]
        private static void ReadFailureIsItsOwnState()
        {
            var registry = new FakeAutostartRegistry();
            registry.FailReadWith = "the registry key could not be opened";

            AutostartInspection result = AutostartInspector.Inspect(registry, Installed);

            Assert.Equal(AutostartState.ReadFailed, result.State,
                "not knowing is different from knowing there is nothing");
            Assert.False(result.IsCorrect, "and it is certainly not correct");
        }

        [Test("An unquoted path with spaces resolves to the file that exists")]
        private static void UnquotedPathWithSpaces()
        {
            // Windows resolves this ambiguity by trying each prefix; so does the
            // parser, which is why a real existing file is used here.
            string executable;
            string arguments;

            bool parsed = AutostartCommand.TryParse(Installed + " --flag", out executable, out arguments);

            Assert.True(parsed, "parsed");
            Assert.True(AutostartCommand.SameExecutable(executable, Installed),
                "the program is the file that exists, not the text up to the first space");
            Assert.Equal("--flag", arguments, "and the rest is arguments");
        }

        [Test("The command AFKLocker writes is always quoted")]
        private static void WrittenCommandIsQuoted()
        {
            string command = AutostartCommand.For(@"C:\Program Files\AFKLocker\AFKLockerWatcher.exe");

            Assert.Equal("\"C:\\Program Files\\AFKLocker\\AFKLockerWatcher.exe\"", command,
                "an unquoted path with a space is ambiguous by design");
        }

        [Test("Canonicalising rubbish returns null rather than throwing")]
        private static void CanonicaliseFailsQuietly()
        {
            Assert.Null(AutostartCommand.Canonicalise("|||"), "not a path");
            Assert.Null(AutostartCommand.Canonicalise(""), "empty");
            Assert.Null(AutostartCommand.Canonicalise(null), "null");
        }

        [Test("A relative entry is never confirmed, whatever directory we are asked from")]
        private static void RelativeEntryIsNeverCorrect()
        {
            // GetFullPath would resolve this against the calling process's
            // working directory, so the same registry value would read as
            // Correct from one AFKLocker process and WrongTarget from another.
            // Two callers disagreeing is the exact failure this class exists to
            // prevent, and AFKLocker only ever writes an absolute path anyway.
            string fileName = Path.GetFileName(Installed);
            string directory = Path.GetDirectoryName(Installed);

            string previous = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = directory;

                Assert.Null(AutostartCommand.Canonicalise(fileName),
                    "a bare file name has no fixed meaning, even standing in its own folder");

                AutostartInspection result = Inspect(fileName);
                Assert.False(result.IsCorrect,
                    "and it must not be confirmed just because the working directory happens to match");
            }
            finally
            {
                Environment.CurrentDirectory = previous;
            }
        }

        [Test("A pathological entry is parsed quickly rather than probing the disk forever")]
        private static void PathologicalEntryIsBounded()
        {
            // Each prefix costs filesystem calls. Unbounded, a value like this
            // took about fifteen seconds - on the Setup UI thread, and at every
            // startup through reconciliation.
            var hostile = new System.Text.StringBuilder(@"C:\nope");
            for (int i = 0; i < 20000; i++) hostile.Append(" x");

            var clock = System.Diagnostics.Stopwatch.StartNew();
            AutostartInspection result = Inspect(hostile.ToString());
            clock.Stop();

            Assert.True(clock.ElapsedMilliseconds < 2000,
                "parsing took " + clock.ElapsedMilliseconds + "ms, which is a stall the user would feel");
            Assert.False(result.IsCorrect, "and it is certainly not our helper");
        }
    }
}
