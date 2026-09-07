using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AFKLocker.Core;
using AFKLocker.Core.Diagnostics;

namespace AFKLocker.Tests
{
    internal sealed class FakeEnvironmentProbe : IEnvironmentProbe
    {
        public string OsDescription { get; set; }
        public string OsBuild { get; set; }
        public bool Is64BitOperatingSystem { get; set; }
        public bool Is64BitProcess { get; set; }
        public string ClrVersion { get; set; }
        public bool IsElevated { get; set; }
        public string InstallLocation { get; set; }
        public string DeviceModel { get; set; }

        public static FakeEnvironmentProbe Typical()
        {
            return new FakeEnvironmentProbe
            {
                OsDescription = "Windows 11 Pro 24H2",
                OsBuild = "26100.1742",
                Is64BitOperatingSystem = true,
                Is64BitProcess = true,
                ClrVersion = ".NET Framework CLR 4.0.30319.42000",
                IsElevated = false,
                InstallLocation = @"C:\Users\marta\AppData\Local\Programs\AFKLocker\",
                DeviceModel = "CONTOSO ThinkPad X1"
            };
        }
    }

    /// <summary>A redactor describing someone else's machine, so tests are stable.</summary>
    internal static class TestRedactor
    {
        public const string UserName = "marta";

        public static PathRedactor Create()
        {
            var folders = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(@"C:\Users\marta\AppData\Local", "%LOCALAPPDATA%"),
                new KeyValuePair<string, string>(@"C:\Users\marta\AppData\Roaming", "%APPDATA%"),
                new KeyValuePair<string, string>(@"C:\Users\marta", "%USERPROFILE%"),
                new KeyValuePair<string, string>(@"C:\Program Files", "%PROGRAMFILES%"),
                new KeyValuePair<string, string>(@"C:\Windows", "%WINDIR%")
            };
            return new PathRedactor(UserName, folders);
        }
    }

    internal static class PathRedactorTests
    {
        [Test("Known folders become placeholders, longest match first")]
        private static void KnownFoldersAreReplaced()
        {
            PathRedactor redactor = TestRedactor.Create();

            Assert.Equal(@"%LOCALAPPDATA%\AFKLocker\settings.txt",
                redactor.Redact(@"C:\Users\marta\AppData\Local\AFKLocker\settings.txt"),
                "local app data wins over the profile folder");
            Assert.Equal(@"%USERPROFILE%\Desktop",
                redactor.Redact(@"C:\Users\marta\Desktop"), "profile folder");
            Assert.Equal(@"%PROGRAMFILES%\AFKLocker",
                redactor.Redact(@"C:\Program Files\AFKLocker"), "program files");
        }

        [Test("A user name is removed even outside a known folder")]
        private static void UserNameIsRemovedAnywhere()
        {
            PathRedactor redactor = TestRedactor.Create();

            string result = redactor.Redact(@"D:\backup\marta\notes.txt");

            Assert.False(result.Contains("marta"), "the name is gone");
            Assert.True(result.Contains("<user>"), "and replaced by a placeholder");
        }

        [Test("Redaction is case-insensitive, as Windows paths are")]
        private static void RedactionIgnoresCase()
        {
            PathRedactor redactor = TestRedactor.Create();

            string result = redactor.Redact(@"c:\users\MARTA\AppData\Local\AFKLocker");

            Assert.False(result.ToLowerInvariant().Contains("marta"), "no name at any casing");
        }

        [Test("A path outside every known folder is reduced to its file name")]
        private static void UnknownPathsAreReducedNotLeaked()
        {
            PathRedactor redactor = TestRedactor.Create();

            // A portable copy on a second drive, in a folder named after a
            // project, a company, or a person.
            string result = redactor.RedactPath(@"E:\Consultoria Silva\tools\AFKLockerWatcher.exe");

            Assert.Equal(@"<path>\AFKLockerWatcher.exe", result,
                "only the file name survives - the directory is not worth the risk");
            Assert.False(result.Contains("Silva"), "no folder names leak");
        }

        [Test("A UNC path is reduced too")]
        private static void UncPathsAreReduced()
        {
            PathRedactor redactor = TestRedactor.Create();

            string result = redactor.RedactPath(@"\\fileserver01\share\AFKLocker\AFKLockerWatcher.exe");

            Assert.False(result.Contains("fileserver01"), "no server names leak");
            Assert.True(result.StartsWith("<path>"), "reduced");
        }

        [Test("A known-folder path keeps its useful shape")]
        private static void KnownPathsKeepTheirShape()
        {
            PathRedactor redactor = TestRedactor.Create();

            Assert.Equal(@"%LOCALAPPDATA%\Programs\AFKLocker\AFKLockerWatcher.exe",
                redactor.RedactPath(@"C:\Users\marta\AppData\Local\Programs\AFKLocker\AFKLockerWatcher.exe"),
                "still readable, still anonymous");
        }

        [Test("Null and empty survive unchanged")]
        private static void NullIsSafe()
        {
            PathRedactor redactor = TestRedactor.Create();
            Assert.Null(redactor.Redact(null), "null");
            Assert.Null(redactor.RedactPath(null), "null path");
            Assert.Equal("", redactor.Redact(""), "empty");
        }
    }

    internal static class SelfTestTests
    {
        private static SelfTest Build(FakePowerConfiguration power, FakePowerInformation info,
            FakeSettingsStore settings, FakeAutostartRegistry autostart, FakeWatcherProcess watcher,
            string watcherPath)
        {
            return new SelfTest(power, info, new InMemoryBackupStore(), settings, autostart, watcher,
                FakeEnvironmentProbe.Typical(), TestRedactor.Create(), watcherPath);
        }

        private static FakePowerConfiguration ReadyMachine()
        {
            var power = new FakePowerConfiguration();
            power.Set(PowerSettings.LidCloseAc, (uint)LidAction.DoNothing);
            power.Set(PowerSettings.LidCloseDc, (uint)LidAction.Sleep);
            power.Set(PowerSettings.SleepAc, 0);
            power.Set(PowerSettings.SleepDc, 1800);
            power.MissingSettings.Add(PowerSettings.HibernateAc.Key);
            power.MissingSettings.Add(PowerSettings.HibernateDc.Key);
            return power;
        }

        private static string ExistingFile()
        {
            return typeof(SelfTestTests).Assembly.Location;
        }

        [Test("A healthy machine in manual mode passes")]
        private static void HealthyMachinePasses()
        {
            DiagnosticReport report = Build(ReadyMachine(), new FakePowerInformation(),
                new FakeSettingsStore(), new FakeAutostartRegistry(),
                new FakeWatcherProcess(), ExistingFile()).Run(new SelfTestOptions());

            Assert.Equal(OverallResult.Passed, report.Overall, "passed");
            Assert.Equal(0, report.Errors.Count, "no errors");
        }

        [Test("A machine that reports no lid fails, and says why")]
        private static void NoLidIsAFailure()
        {
            var info = new FakePowerInformation();
            info.Capabilities.LidPresent = false;

            DiagnosticReport report = Build(ReadyMachine(), info, new FakeSettingsStore(),
                new FakeAutostartRegistry(), new FakeWatcherProcess(), ExistingFile())
                .Run(new SelfTestOptions());

            DiagnosticCheck lid = report.Checks.First(c => c.Id == "power.lid");
            Assert.Equal(CheckOutcome.Warning, lid.Outcome, "flagged");
            Assert.True(lid.Detail.Contains("ACPI Lid"), "names the likely cause");
            Assert.Equal(false, report.Facts.First(f => f.Key == "power.lidReported").Value, "recorded as a fact");
        }

        [Test("Modern Standby is reported as a warning, not a pass")]
        private static void ModernStandbyWarns()
        {
            var info = new FakePowerInformation();
            info.Capabilities.ModernStandby = true;

            DiagnosticReport report = Build(ReadyMachine(), info, new FakeSettingsStore(),
                new FakeAutostartRegistry(), new FakeWatcherProcess(), ExistingFile())
                .Run(new SelfTestOptions());

            Assert.Equal(CheckOutcome.Warning,
                report.Checks.First(c => c.Id == "power.modernStandby").Outcome, "warned");
            Assert.Equal(OverallResult.PassedWithWarnings, report.Overall, "overall reflects it");
        }

        [Test("A missing watcher binary is a failure")]
        private static void MissingWatcherFails()
        {
            DiagnosticReport report = Build(ReadyMachine(), new FakePowerInformation(),
                new FakeSettingsStore(), new FakeAutostartRegistry(), new FakeWatcherProcess(),
                @"Z:\gone.exe").Run(new SelfTestOptions());

            Assert.Equal(CheckOutcome.Fail,
                report.Checks.First(c => c.Id == "afk.watcherBinary").Outcome, "failed");
            Assert.Equal(OverallResult.Failed, report.Overall, "overall failed");
        }

        [Test("Automatic mode with a dead watcher is reported as inconsistent")]
        private static void InconsistentConfigurationIsReported()
        {
            var settings = new FakeSettingsStore();
            settings.Save(new AutoLockSettings { Mode = LockMode.Automatic });
            var autostart = new FakeAutostartRegistry();
            autostart.Preset("\"" + ExistingFile() + "\"");

            DiagnosticReport report = Build(ReadyMachine(), new FakePowerInformation(), settings,
                autostart, new FakeWatcherProcess { State = WatcherState.NotRunning }, ExistingFile())
                .Run(new SelfTestOptions());

            Assert.Equal(CheckOutcome.Fail,
                report.Checks.First(c => c.Id == "afk.consistency").Outcome, "inconsistent");
        }

        [Test("Corrupted settings are reported without stopping the rest of the test")]
        private static void CorruptSettingsAreSurvivable()
        {
            var settings = new FakeSettingsStore { FailLoadWith = "settings file unreadable" };

            DiagnosticReport report = Build(ReadyMachine(), new FakePowerInformation(), settings,
                new FakeAutostartRegistry(), new FakeWatcherProcess(), ExistingFile())
                .Run(new SelfTestOptions());

            Assert.Equal(CheckOutcome.Fail,
                report.Checks.First(c => c.Id == "afk.settings").Outcome, "reported");
            Assert.True(report.Checks.Any(c => c.Id == "power.lid"), "and the rest still ran");
        }

        [Test("Power configuration that cannot be read is a failure, not a crash")]
        private static void UnreadablePowerIsSurvivable()
        {
            var power = new FakePowerConfiguration();
            // Nothing set: every read throws PowerSettingNotFound, and the
            // scheme read still works, so the snapshot survives - but a machine
            // where the whole read fails must not take the report down either.
            power.MissingSettings.UnionWith(PowerSettings.All.Select(s => s.Key));

            DiagnosticReport report = Build(power, new FakePowerInformation(), new FakeSettingsStore(),
                new FakeAutostartRegistry(), new FakeWatcherProcess(), ExistingFile())
                .Run(new SelfTestOptions());

            Assert.True(report.Checks.Any(), "a report was still produced");
        }

        [Test("The watcher lifecycle test restores a machine that had no watcher")]
        private static void LifecycleTestRestoresManual()
        {
            var watcher = new FakeWatcherProcess { State = WatcherState.NotRunning };

            DiagnosticReport report = Build(ReadyMachine(), new FakePowerInformation(),
                new FakeSettingsStore(), new FakeAutostartRegistry(), watcher, ExistingFile())
                .Run(new SelfTestOptions { TestWatcherLifecycle = true });

            Assert.Equal(WatcherState.NotRunning, watcher.GetState(),
                "the watcher is stopped again, as it was found");
            Assert.Equal(CheckOutcome.Pass,
                report.Checks.First(c => c.Id == "watcher.restore").Outcome, "restore reported");
        }

        [Test("The watcher lifecycle test leaves a running watcher running")]
        private static void LifecycleTestLeavesAutomaticAlone()
        {
            var watcher = new FakeWatcherProcess { State = WatcherState.Ready };

            Build(ReadyMachine(), new FakePowerInformation(), new FakeSettingsStore(),
                new FakeAutostartRegistry(), watcher, ExistingFile())
                .Run(new SelfTestOptions { TestWatcherLifecycle = true });

            Assert.Equal(WatcherState.Ready, watcher.GetState(),
                "a machine already in automatic mode stays in automatic mode");
            Assert.Equal(0, watcher.StopCount, "and its watcher was never stopped");
        }

        [Test("A watcher that will not start is reported and still restores state")]
        private static void LifecycleTestHandlesStartFailure()
        {
            var watcher = new FakeWatcherProcess
            {
                State = WatcherState.NotRunning,
                StartResult = WatcherStartResult.Failed(WatcherStartFailure.LidNotificationFailed,
                    "no lid notifications")
            };

            DiagnosticReport report = Build(ReadyMachine(), new FakePowerInformation(),
                new FakeSettingsStore(), new FakeAutostartRegistry(), watcher, ExistingFile())
                .Run(new SelfTestOptions { TestWatcherLifecycle = true });

            Assert.Equal(CheckOutcome.Fail,
                report.Checks.First(c => c.Id == "watcher.start").Outcome, "failure reported");
            Assert.Equal(WatcherState.NotRunning, watcher.GetState(), "and nothing left running");
        }

        [Test("The device model is left out unless the user opts in")]
        private static void DeviceModelIsOptIn()
        {
            DiagnosticReport without = Build(ReadyMachine(), new FakePowerInformation(),
                new FakeSettingsStore(), new FakeAutostartRegistry(), new FakeWatcherProcess(),
                ExistingFile()).Run(new SelfTestOptions());

            Assert.Null(without.Facts.First(f => f.Key == "environment.deviceModel").Value,
                "not included by default");
            Assert.False(without.ToJson().Contains("ThinkPad"), "and nowhere in the JSON");

            DiagnosticReport with = Build(ReadyMachine(), new FakePowerInformation(),
                new FakeSettingsStore(), new FakeAutostartRegistry(), new FakeWatcherProcess(),
                ExistingFile()).Run(new SelfTestOptions { IncludeDeviceModel = true });

            Assert.True(with.ToJson().Contains("ThinkPad"), "included when asked for");
        }

        [Test("A custom power plan's name is not reported")]
        private static void CustomSchemeNameIsNotReported()
        {
            var power = ReadyMachine();
            power.ActiveScheme = new Guid("dddddddd-dddd-dddd-dddd-dddddddddddd");
            power.SchemeName = "Marta's gaming plan";

            DiagnosticReport report = Build(power, new FakePowerInformation(), new FakeSettingsStore(),
                new FakeAutostartRegistry(), new FakeWatcherProcess(), ExistingFile())
                .Run(new SelfTestOptions());

            string json = report.ToJson();
            Assert.False(json.Contains("Marta"), "a personalised plan name never reaches the report");
            Assert.True(json.Contains("custom"), "it is reported as custom instead");
        }
    }

    /// <summary>
    /// The check that matters most: nothing personal reaches the exported file.
    ///
    /// This is written as a search for categories that must not appear, rather
    /// than a check that the expected fields do - because the risk is what gets
    /// added later without thinking, not what is there today.
    /// </summary>
    internal static class DiagnosticsPrivacyTests
    {
        private static DiagnosticReport BuildReport(bool includeDevice)
        {
            var power = new FakePowerConfiguration();
            power.Set(PowerSettings.LidCloseAc, (uint)LidAction.DoNothing);
            power.Set(PowerSettings.LidCloseDc, (uint)LidAction.Sleep);
            power.Set(PowerSettings.SleepAc, 0);
            power.Set(PowerSettings.SleepDc, 1800);
            power.MissingSettings.Add(PowerSettings.HibernateAc.Key);
            power.MissingSettings.Add(PowerSettings.HibernateDc.Key);

            var autostart = new FakeAutostartRegistry();
            autostart.Preset("\"C:\\Users\\marta\\AppData\\Local\\Programs\\AFKLocker\\AFKLockerWatcher.exe\"");

            var selfTest = new SelfTest(power, new FakePowerInformation(), new InMemoryBackupStore(),
                new FakeSettingsStore(), autostart, new FakeWatcherProcess(),
                FakeEnvironmentProbe.Typical(), TestRedactor.Create(),
                typeof(DiagnosticsPrivacyTests).Assembly.Location);

            return selfTest.Run(new SelfTestOptions { IncludeDeviceModel = includeDevice });
        }

        /// <summary>
        /// Literal strings that must never appear in an exported bundle.
        ///
        /// Deliberately values, not topic words: the privacy notice itself says
        /// "IP or MAC addresses" while promising not to include any, so
        /// searching for that phrase would flag the promise instead of a leak.
        /// </summary>
        private static readonly string[] ForbiddenLiterals =
        {
            "marta",            // the user name from the fake environment
            @"C:\Users\",       // any un-redacted profile path
            "SerialNumber",
            "MachineGuid"
        };

        /// <summary>
        /// Shapes of personal data, matched as values so explanatory text
        /// cannot trip them.
        /// </summary>
        private static readonly KeyValuePair<string, string>[] ForbiddenPatterns =
        {
            new KeyValuePair<string, string>("a MAC address",
                @"\b([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b"),
            new KeyValuePair<string, string>("an IPv4 address",
                @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b"),
            new KeyValuePair<string, string>("an email address",
                @"\b[\w.%+-]+@[\w.-]+\.[A-Za-z]{2,}\b")
        };

        private static void AssertNoPersonalData(string content, string where)
        {
            // A four-part version number ("0.3.0.0") has the same shape as an
            // IPv4 address. Take the known version out first so the check tests
            // what it means to test rather than tripping over the product's own
            // version string.
            string subject = Regex.Replace(content, @"\b\d+\.\d+\.\d+\.\d+\b",
                delegate(Match m) { return IsVersionLike(content, m) ? "<version>" : m.Value; });

            foreach (string forbidden in ForbiddenLiterals)
            {
                Assert.False(subject.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase) >= 0,
                    where + " must not contain: " + forbidden);
            }

            foreach (KeyValuePair<string, string> pattern in ForbiddenPatterns)
            {
                Match match = Regex.Match(subject, pattern.Value);
                Assert.False(match.Success, where + " must not contain " + pattern.Key
                    + (match.Success ? " (found: " + match.Value + ")" : ""));
            }
        }

        /// <summary>
        /// True when a dotted-quad is a version rather than an address. Real
        /// addresses have octets of 0-255; a version freely goes past that, and
        /// AFKLocker's own is the only dotted quad that legitimately appears.
        /// </summary>
        private static bool IsVersionLike(string content, Match match)
        {
            string[] parts = match.Value.Split('.');
            foreach (string part in parts)
            {
                int value;
                if (!int.TryParse(part, out value) || value > 255) return true;
            }

            // Within reach of a version label, treat it as a version.
            int start = Math.Max(0, match.Index - 40);
            string context = content.Substring(start, match.Index - start);
            return context.IndexOf("version", StringComparison.OrdinalIgnoreCase) >= 0
                   || context.IndexOf("CLR", StringComparison.Ordinal) >= 0;
        }

        [Test("The exported report contains no user name or personal path")]
        private static void NoPersonalDataInReport()
        {
            DiagnosticReport report = BuildReport(false);
            string everything = report.ToText() + report.ToJson()
                                + DiagnosticsBundle.BuildSummary(report);

            AssertNoPersonalData(everything, "the export");
        }

        [Test("Personal paths in the autostart command are redacted")]
        private static void AutostartPathIsRedacted()
        {
            DiagnosticReport report = BuildReport(false);
            string text = report.ToText();

            Assert.True(text.Contains("%LOCALAPPDATA%"), "the path is shown as a placeholder");
            Assert.False(text.ToLowerInvariant().Contains("marta"), "and the name is gone");
        }

        [Test("The written zip contains exactly the three expected files")]
        private static void BundleContainsOnlyWhatIsPromised()
        {
            string path = Path.Combine(Path.GetTempPath(),
                "afklocker-bundle-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                new DiagnosticsBundle().Write(BuildReport(false), path);

                using (ZipArchive archive = ZipFile.OpenRead(path))
                {
                    string[] names = archive.Entries.Select(e => e.FullName).OrderBy(n => n).ToArray();
                    Assert.Equal(3, names.Length, "three files, no more");
                    Assert.Equal("diagnostics.json", names[0], "json");
                    Assert.Equal("self-test.txt", names[1], "self-test");
                    Assert.Equal("summary.txt", names[2], "summary");
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test("Every file inside the zip is free of personal data")]
        private static void ZipContentsAreClean()
        {
            string path = Path.Combine(Path.GetTempPath(),
                "afklocker-bundle-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                new DiagnosticsBundle().Write(BuildReport(true), path);

                using (ZipArchive archive = ZipFile.OpenRead(path))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
                        {
                            AssertNoPersonalData(reader.ReadToEnd(), entry.FullName);
                        }
                    }
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test("Export to an unwritable location fails cleanly")]
        private static void ExportFailureIsReported()
        {
            // The exact exception type is the framework's business; what matters
            // is that an impossible destination fails loudly rather than leaving
            // the user believing a report was saved.
            Assert.Throws<Exception>(
                () => new DiagnosticsBundle().Write(BuildReport(false), @"\\?\nonexistent\x.zip"),
                "an impossible path throws rather than silently doing nothing");
        }

        [Test("The privacy notice names what is and is not collected")]
        private static void PrivacyNoticeIsSpecific()
        {
            string notice = DiagnosticsBundle.PrivacyNotice;

            Assert.True(notice.Contains("Not included"), "it says what is left out");
            Assert.True(notice.Contains("username"), "including the username");
            Assert.True(notice.Contains("%LOCALAPPDATA%"), "and explains path placeholders");
            Assert.True(notice.Contains("Nothing is ever sent anywhere"), "and that nothing is transmitted");
        }

        [Test("The JSON actually parses - the whole point is that a tool can read it")]
        private static void JsonIsWellFormed()
        {
            string json = BuildReport(true).ToJson();

            // Checking for expected substrings would have missed a missing comma
            // between array elements, which is exactly the bug this catches.
            MiniJson.Parse(json);

            Assert.False(json.Contains("}{"), "objects in an array are separated");
            Assert.False(json.Contains("]["), "arrays are separated");
        }

        [Test("The JSON carries a schema version so it can be read later")]
        private static void JsonHasSchemaVersion()
        {
            string json = BuildReport(false).ToJson();

            Assert.True(json.Contains("\"schemaVersion\": 1"), "schema version present");
            Assert.True(json.Contains("\"overall\""), "overall result present");
            Assert.True(json.Contains("\"tests\""), "individual tests present");
        }

        [Test("The JSON exposes the fields a compatibility matrix needs")]
        private static void JsonCarriesCompatibilityFacts()
        {
            string json = BuildReport(false).ToJson();

            foreach (string field in new[]
                     {
                         "osBuild", "architecture", "lidReported", "batteryReported",
                         "modernStandby", "supportsS3", "lockMode", "state"
                     })
            {
                Assert.True(json.Contains("\"" + field + "\""), "json exposes " + field);
            }
        }
    }

    internal static class LidDetectionTestTests
    {
        [Test("Seeing a close and an open is a pass")]
        private static void BothEventsPass()
        {
            var provider = new FakeLidEventProvider();
            var test = new LidDetectionTest(provider);
            test.Start();

            provider.Emit(LidState.Closed);
            provider.Emit(LidState.Opened);

            Assert.Equal(CheckOutcome.Pass, test.Outcome, "passed");
            Assert.True(test.SawClose && test.SawOpen, "both seen");
        }

        [Test("Seeing only a close still counts as working for locking")]
        private static void CloseOnlyIsAWarning()
        {
            var provider = new FakeLidEventProvider();
            var test = new LidDetectionTest(provider);
            test.Start();

            provider.Emit(LidState.Closed);

            Assert.Equal(CheckOutcome.Warning, test.Outcome, "warned");
            Assert.True(test.Detail.Contains("should"), "but explains locking will work");
        }

        [Test("No events at all fails, and names the likely cause")]
        private static void NoEventsFails()
        {
            var test = new LidDetectionTest(new FakeLidEventProvider());
            test.Start();

            Assert.Equal(CheckOutcome.Fail, test.Outcome, "failed");
            Assert.True(test.Detail.Contains("ACPI Lid"), "names the likely cause");
        }

        [Test("A provider that cannot register fails immediately")]
        private static void RegistrationFailureFails()
        {
            var test = new LidDetectionTest(new FakeLidEventProvider { StartSucceeds = false });

            Assert.False(test.Start(), "start reports failure");
            Assert.Equal(CheckOutcome.Fail, test.Outcome, "and the outcome is a failure");
            Assert.False(test.Registered, "registration did not happen");
        }

        [Test("The test never locks anything - it has no session locker at all")]
        private static void LidTestCannotLock()
        {
            var provider = new FakeLidEventProvider();
            var test = new LidDetectionTest(provider);
            test.Start();
            provider.Emit(LidState.Closed);

            // Nothing to assert against a locker, because the type does not have
            // one: the only way this test could lock the machine is if someone
            // added that dependency, and this test would then stop compiling.
            Assert.True(test.SawClose, "the event was observed, and nothing else happened");
        }

        [Test("Stopping detaches from the provider")]
        private static void StopDetaches()
        {
            var provider = new FakeLidEventProvider();
            var test = new LidDetectionTest(provider);
            test.Start();
            test.Stop();

            provider.Emit(LidState.Closed);

            Assert.False(test.SawClose, "events after stopping are not counted");
            Assert.True(provider.Stopped, "and the provider was released");
        }

        [Test("Null provider is rejected")]
        private static void NullProviderRejected()
        {
            Assert.Throws<ArgumentNullException>(() => new LidDetectionTest(null), "null provider");
        }
    }
}
