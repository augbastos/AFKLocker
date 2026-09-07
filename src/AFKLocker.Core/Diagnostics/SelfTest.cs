using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace AFKLocker.Core.Diagnostics
{
    /// <summary>Facts about the machine that the self-test needs but cannot infer.</summary>
    public interface IEnvironmentProbe
    {
        string OsDescription { get; }
        string OsBuild { get; }
        bool Is64BitOperatingSystem { get; }
        bool Is64BitProcess { get; }
        string ClrVersion { get; }
        bool IsElevated { get; }

        /// <summary>Where the program is installed, already redacted.</summary>
        string InstallLocation { get; }

        /// <summary>Manufacturer and model, or null when the user did not opt in.</summary>
        string DeviceModel { get; }
    }

    /// <summary>Options the user controls before a report is produced.</summary>
    public sealed class SelfTestOptions
    {
        /// <summary>
        /// Start and stop a watcher to prove the handshake works. Restores the
        /// previous state afterwards. Off by default because it touches the
        /// running system, even if only briefly.
        /// </summary>
        public bool TestWatcherLifecycle { get; set; }

        /// <summary>
        /// Include manufacturer and model. Off by default: useful for a
        /// compatibility picture, but it is information about the person's
        /// hardware and so is theirs to volunteer.
        /// </summary>
        public bool IncludeDeviceModel { get; set; }
    }

    /// <summary>
    /// Runs every check that can be made safely, and leaves the machine exactly
    /// as it found it.
    ///
    /// Deliberately free of any UI, so the whole thing - including the awkward
    /// paths - can be driven from tests with fakes.
    ///
    /// Nothing here reads the network, enumerates processes or programs, or
    /// touches the registry outside AFKLocker's own two values. Paths are
    /// redacted before they reach the report.
    /// </summary>
    public sealed class SelfTest
    {
        // Power scheme GUIDs Microsoft ships. Anything else is reported as
        // "custom" without its name, which a user may well have personalised.
        private static readonly Guid BalancedScheme = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
        private static readonly Guid HighPerformanceScheme = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        private static readonly Guid PowerSaverScheme = new Guid("a1841308-3541-4fab-bc81-f71556f20b4a");

        private readonly IPowerConfiguration _power;
        private readonly IPowerInformation _powerInfo;
        private readonly IBackupStore _backups;
        private readonly ISettingsStore _settings;
        private readonly IAutostartRegistry _autostart;
        private readonly IWatcherProcess _watcher;
        private readonly IEnvironmentProbe _environment;
        private readonly PathRedactor _redactor;
        private readonly string _watcherPath;

        public SelfTest(IPowerConfiguration power, IPowerInformation powerInfo, IBackupStore backups,
            ISettingsStore settings, IAutostartRegistry autostart, IWatcherProcess watcher,
            IEnvironmentProbe environment, PathRedactor redactor, string watcherPath)
        {
            _power = power;
            _powerInfo = powerInfo;
            _backups = backups;
            _settings = settings;
            _autostart = autostart;
            _watcher = watcher;
            _environment = environment;
            _redactor = redactor ?? new PathRedactor();
            _watcherPath = watcherPath;
        }

        public DiagnosticReport Run(SelfTestOptions options)
        {
            options = options ?? new SelfTestOptions();

            var report = new DiagnosticReport
            {
                GeneratedUtc = DateTime.UtcNow,
                AfkLockerVersion = GetVersion()
            };

            CheckEnvironment(report, options);
            PowerSnapshot snapshot = CheckPower(report);
            CheckAfkLocker(report, snapshot);

            if (options.TestWatcherLifecycle)
                TestWatcherLifecycle(report);
            else
                report.Add("Watcher lifecycle", "watcher.lifecycle", "Watcher start/stop test",
                    CheckOutcome.NotTested, "Not run. Enable it in Diagnostics to test the handshake.");

            report.Add("Lid detection", "lid.detection", "Physical lid test",
                CheckOutcome.NotTested, "Run \"Test lid detection\" and close the lid when asked.");
            report.Fact("lidTest.result", "NotTested");

            return report;
        }

        private static string GetVersion()
        {
            try
            {
                return Assembly.GetExecutingAssembly().GetName().Version.ToString();
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        // ------------------------------------------------------- environment ---

        private void CheckEnvironment(DiagnosticReport report, SelfTestOptions options)
        {
            const string Section = "Environment";

            report.Add(Section, "env.os", "Windows version", CheckOutcome.Info,
                _environment.OsDescription + " (build " + _environment.OsBuild + ")");
            report.Fact("environment.osDescription", _environment.OsDescription);
            report.Fact("environment.osBuild", _environment.OsBuild);

            string architecture = _environment.Is64BitOperatingSystem ? "x64" : "x86";
            report.Add(Section, "env.arch", "Architecture", CheckOutcome.Pass, architecture
                + (_environment.Is64BitProcess ? " (64-bit process)" : " (32-bit process)"));
            report.Fact("environment.architecture", architecture);
            report.Fact("environment.is64BitProcess", _environment.Is64BitProcess);

            report.Add(Section, "env.clr", ".NET runtime", CheckOutcome.Info, _environment.ClrVersion);
            report.Fact("environment.clrVersion", _environment.ClrVersion);

            report.Add(Section, "env.elevation", "Running elevated", CheckOutcome.Info,
                _environment.IsElevated ? "Yes" : "No - which is the normal case");
            report.Fact("environment.elevated", _environment.IsElevated);

            string install = _redactor.RedactPath(_environment.InstallLocation);
            report.Add(Section, "env.install", "Install location", CheckOutcome.Info,
                install ?? "unknown");
            report.Fact("environment.installLocation", install);
            report.Fact("environment.installScope", DescribeInstallScope(install));

            report.Fact("afklocker.version", report.AfkLockerVersion);

            if (options.IncludeDeviceModel && !string.IsNullOrEmpty(_environment.DeviceModel))
            {
                report.Add(Section, "env.device", "Device model", CheckOutcome.Info,
                    _environment.DeviceModel + " (included at your request)");
                report.Fact("environment.deviceModel", _environment.DeviceModel);
            }
            else
            {
                report.Fact("environment.deviceModel", null);
            }
        }

        private static string DescribeInstallScope(string redactedPath)
        {
            if (string.IsNullOrEmpty(redactedPath)) return "unknown";
            if (redactedPath.IndexOf("%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase) >= 0
                || redactedPath.IndexOf("%USERPROFILE%", StringComparison.OrdinalIgnoreCase) >= 0)
                return "per-user";
            if (redactedPath.IndexOf("%PROGRAMFILES", StringComparison.OrdinalIgnoreCase) >= 0)
                return "all-users";
            return "other";
        }

        // ------------------------------------------------------------- power ---

        private PowerSnapshot CheckPower(DiagnosticReport report)
        {
            const string Section = "Power";

            PowerSnapshot snapshot;
            try
            {
                snapshot = PowerSnapshot.Read(_power, _powerInfo);
            }
            catch (Exception ex)
            {
                report.Add(Section, "power.read", "Read power configuration", CheckOutcome.Fail,
                    _redactor.Redact(ex.Message));
                report.Fact("power.readable", false);
                return null;
            }

            report.Fact("power.readable", true);

            SystemCapabilities caps = snapshot.Capabilities ?? new SystemCapabilities();

            report.Add(Section, "power.lid", "Lid device reported",
                caps.LidPresent ? CheckOutcome.Pass : CheckOutcome.Warning,
                caps.LidPresent
                    ? "Windows reports a lid, so lid events can be delivered."
                    : "Windows reports no lid device. Automatic lock cannot fire; on a laptop this "
                      + "usually means the ACPI Lid device is disabled in Device Manager.");
            report.Fact("power.lidReported", caps.LidPresent);

            report.Add(Section, "power.battery", "Battery detected",
                caps.BatteryPresent ? CheckOutcome.Pass : CheckOutcome.NotApplicable,
                caps.BatteryPresent ? "Yes" : "No battery - desktop or always-plugged machine.");
            report.Fact("power.batteryReported", caps.BatteryPresent);

            report.Add(Section, "power.modernStandby", "Modern Standby",
                caps.ModernStandby ? CheckOutcome.Warning : CheckOutcome.Pass,
                caps.ModernStandby
                    ? "This machine uses S0 low power idle and may still enter a low power state "
                      + "with the lid closed, whatever the timeouts say."
                    : "Not used - classic sleep timeouts apply.");
            report.Fact("power.modernStandby", caps.ModernStandby);

            report.Add(Section, "power.s3", "S3 standby supported", CheckOutcome.Info,
                caps.SupportsStandbyS3 ? "Yes" : "No");
            report.Fact("power.supportsS3", caps.SupportsStandbyS3);

            report.Add(Section, "power.hibernate", "Hibernation available", CheckOutcome.Info,
                caps.HibernateFilePresent ? "Enabled" : "Not enabled");
            report.Fact("power.hibernateAvailable", caps.HibernateFilePresent);

            string schemeName = DescribeScheme(snapshot.Scheme);
            report.Add(Section, "power.scheme", "Active power plan", CheckOutcome.Info, schemeName);
            report.Fact("power.activeScheme", schemeName);

            ReportSetting(report, snapshot, PowerSettings.LidCloseAc, "Lid close (plugged in)", true);
            ReportSetting(report, snapshot, PowerSettings.SleepAc, "Sleep timeout (plugged in)", true);
            ReportSetting(report, snapshot, PowerSettings.LidCloseDc, "Lid close (on battery)", false);
            ReportSetting(report, snapshot, PowerSettings.SleepDc, "Sleep timeout (on battery)", false);
            ReportSetting(report, snapshot, PowerSettings.HibernateAc, "Hibernate (plugged in)", false);

            ReadinessReport readiness = ReadinessEvaluator.Evaluate(snapshot);
            report.Add(Section, "power.readiness", "Ready for closed-lid operation",
                readiness.IsReady ? CheckOutcome.Pass : CheckOutcome.Fail,
                readiness.Summary);
            report.Fact("power.closedLidReady", readiness.IsReady);

            return snapshot;
        }

        private static string DescribeScheme(Guid scheme)
        {
            if (scheme == BalancedScheme) return "Balanced";
            if (scheme == HighPerformanceScheme) return "High performance";
            if (scheme == PowerSaverScheme) return "Power saver";
            // A custom plan's name is the user's own words - report only that it
            // is custom.
            return "custom";
        }

        private void ReportSetting(DiagnosticReport report, PowerSnapshot snapshot,
            PowerSettingRef setting, string title, bool required)
        {
            SettingValue value = snapshot[setting];
            string text = setting.Setting == PowerSettingIds.LidCloseAction
                ? PowerValueFormatter.Lid(value)
                : PowerValueFormatter.Timeout(value);

            CheckOutcome outcome;
            if (!value.IsPresent) outcome = required ? CheckOutcome.Warning : CheckOutcome.NotApplicable;
            else outcome = CheckOutcome.Info;

            report.Add("Power", "power." + setting.Key, title, outcome,
                value.IsPresent ? text : "Not available on this machine");
            report.Fact("power." + ToCamel(setting.Key), value.IsPresent ? (object)value.Value : null);
        }

        private static string ToCamel(string key)
        {
            string[] parts = key.Split('-');
            var text = parts[0];
            for (int i = 1; i < parts.Length; i++)
                text += char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
            return text;
        }

        // -------------------------------------------------------- afklocker ---

        private void CheckAfkLocker(DiagnosticReport report, PowerSnapshot snapshot)
        {
            const string Section = "AFKLocker";

            LockMode mode;
            try
            {
                mode = _settings.Load().Mode;
                report.Add(Section, "afk.settings", "Settings readable", CheckOutcome.Pass,
                    "Lock mode: " + (mode == LockMode.Automatic ? "Automatic" : "Manual"));
            }
            catch (Exception ex)
            {
                mode = LockMode.Manual;
                report.Add(Section, "afk.settings", "Settings readable", CheckOutcome.Fail,
                    _redactor.Redact(ex.Message));
            }
            report.Fact("afklocker.lockMode", mode.ToString());

            try
            {
                int count = _backups.ListSchemes().Count();
                report.Add(Section, "afk.backups", "Power settings backups", CheckOutcome.Info,
                    count == 0 ? "None - AFKLocker has not changed any power settings"
                               : count + " backup file(s)");
                report.Fact("afklocker.backupCount", count);
            }
            catch (Exception ex)
            {
                report.Add(Section, "afk.backups", "Power settings backups", CheckOutcome.Warning,
                    _redactor.Redact(ex.Message));
                report.Fact("afklocker.backupCount", null);
            }

            bool watcherInstalled = !string.IsNullOrEmpty(_watcherPath) && File.Exists(_watcherPath);
            report.Add(Section, "afk.watcherBinary", "Watcher installed",
                watcherInstalled ? CheckOutcome.Pass : CheckOutcome.Fail,
                watcherInstalled ? _redactor.RedactPath(_watcherPath) : "AFKLockerWatcher.exe was not found");
            report.Fact("watcher.installed", watcherInstalled);

            WatcherState state = SafeWatcherState();
            report.Add(Section, "afk.watcherState", "Watcher state",
                WatcherOutcome(mode, state), DescribeWatcherState(state));
            report.Fact("watcher.state", state.ToString());
            report.Fact("watcher.ready", state == WatcherState.Ready);

            bool autostartRegistered;
            string autostartCommand;
            try
            {
                autostartCommand = _autostart.RegisteredCommand;
                autostartRegistered = autostartCommand != null;
            }
            catch (Exception)
            {
                autostartCommand = null;
                autostartRegistered = false;
            }

            bool autostartMatches = autostartRegistered && watcherInstalled
                && autostartCommand.IndexOf(WatcherController.WatcherFileName,
                    StringComparison.OrdinalIgnoreCase) >= 0;

            report.Add(Section, "afk.autostart", "Autostart entry",
                DescribeAutostartOutcome(mode, autostartRegistered, autostartMatches),
                autostartRegistered
                    ? _redactor.RedactPath(autostartCommand.Trim('"'))
                    : "Not registered");
            report.Fact("autostart.registered", autostartRegistered);
            report.Fact("autostart.pointsAtWatcher", autostartMatches);

            var status = new AutoLockStatus
            {
                Mode = mode,
                AutostartRegistered = autostartRegistered,
                WatcherState = state,
                WatcherInstalled = watcherInstalled
            };

            report.Add(Section, "afk.consistency", "Configuration consistent",
                status.IsConsistent ? CheckOutcome.Pass : CheckOutcome.Fail,
                status.IsConsistent ? "Settings, autostart and watcher agree." : status.Inconsistency);
            report.Fact("afklocker.consistent", status.IsConsistent);

            if (snapshot != null && mode == LockMode.Automatic)
            {
                AutoLockWarning advice = AutoLockAdvisor.Evaluate(mode, snapshot);
                if (advice != AutoLockWarning.None)
                    report.Add(Section, "afk.advice", "Automatic lock note",
                        advice == AutoLockWarning.NoLidReported ? CheckOutcome.Fail : CheckOutcome.Warning,
                        AutoLockAdvisor.Describe(advice));
            }
        }

        private WatcherState SafeWatcherState()
        {
            try
            {
                return _watcher.GetState();
            }
            catch (Exception)
            {
                return WatcherState.Unhealthy;
            }
        }

        private static CheckOutcome WatcherOutcome(LockMode mode, WatcherState state)
        {
            if (mode == LockMode.Manual)
                return state == WatcherState.NotRunning ? CheckOutcome.Pass : CheckOutcome.Warning;
            return state == WatcherState.Ready ? CheckOutcome.Pass : CheckOutcome.Fail;
        }

        private static string DescribeWatcherState(WatcherState state)
        {
            switch (state)
            {
                case WatcherState.Ready: return "Running and listening for lid events";
                case WatcherState.Starting: return "Running, but not yet listening for lid events";
                case WatcherState.Unhealthy: return "Unhealthy - a readiness signal outlived its process";
                default: return "Not running";
            }
        }

        private static CheckOutcome DescribeAutostartOutcome(LockMode mode, bool registered, bool matches)
        {
            if (mode == LockMode.Automatic)
                return registered && matches ? CheckOutcome.Pass : CheckOutcome.Fail;
            return registered ? CheckOutcome.Warning : CheckOutcome.Pass;
        }

        // ----------------------------------------------- watcher lifecycle ---

        /// <summary>
        /// Proves the start/ready/stop handshake on this machine, then puts the
        /// previous state back.
        ///
        /// The restore is the point: a diagnostic that leaves automatic mode off
        /// because it was on when the test began would be worse than no
        /// diagnostic at all.
        /// </summary>
        private void TestWatcherLifecycle(DiagnosticReport report)
        {
            const string Section = "Watcher lifecycle";

            if (string.IsNullOrEmpty(_watcherPath) || !File.Exists(_watcherPath))
            {
                report.Add(Section, "watcher.lifecycle", "Watcher start/stop test", CheckOutcome.Fail,
                    "The watcher program is missing, so it cannot be tested.");
                report.Fact("watcher.lifecycleTest", "Fail");
                return;
            }

            WatcherState before = SafeWatcherState();
            bool wasRunning = before != WatcherState.NotRunning;

            try
            {
                if (wasRunning)
                {
                    report.Add(Section, "watcher.alreadyRunning", "Watcher already running",
                        before == WatcherState.Ready ? CheckOutcome.Pass : CheckOutcome.Warning,
                        DescribeWatcherState(before));
                    report.Fact("watcher.lifecycleTest", before == WatcherState.Ready ? "Pass" : "Warning");
                    return;
                }

                WatcherStartResult start = _watcher.Start(_watcherPath);
                report.Add(Section, "watcher.start", "Watcher starts",
                    start.Success ? CheckOutcome.Pass : CheckOutcome.Fail,
                    start.Success ? "Started" : _redactor.Redact(start.Message));

                if (!start.Success)
                {
                    report.Fact("watcher.lifecycleTest", "Fail");
                    report.Fact("watcher.startFailure", start.Failure.ToString());
                    return;
                }

                WatcherState state = SafeWatcherState();
                report.Add(Section, "watcher.ready", "Watcher reports READY",
                    state == WatcherState.Ready ? CheckOutcome.Pass : CheckOutcome.Fail,
                    DescribeWatcherState(state));

                bool stopped = _watcher.Stop(TimeSpan.FromSeconds(5));
                report.Add(Section, "watcher.stop", "Watcher stops on request",
                    stopped ? CheckOutcome.Pass : CheckOutcome.Fail,
                    stopped ? "Stopped cleanly" : "Did not stop within 5 seconds");

                report.Fact("watcher.lifecycleTest",
                    state == WatcherState.Ready && stopped ? "Pass" : "Fail");
            }
            catch (Exception ex)
            {
                report.Add(Section, "watcher.lifecycle", "Watcher start/stop test", CheckOutcome.Fail,
                    _redactor.Redact(ex.Message));
                report.Fact("watcher.lifecycleTest", "Fail");
            }
            finally
            {
                RestoreWatcher(report, wasRunning);
            }
        }

        private void RestoreWatcher(DiagnosticReport report, bool wasRunning)
        {
            try
            {
                bool runningNow = SafeWatcherState() != WatcherState.NotRunning;

                if (wasRunning && !runningNow)
                    _watcher.Start(_watcherPath);
                else if (!wasRunning && runningNow)
                    _watcher.Stop(TimeSpan.FromSeconds(5));

                bool restored = (SafeWatcherState() != WatcherState.NotRunning) == wasRunning;
                report.Add("Watcher lifecycle", "watcher.restore", "Previous state restored",
                    restored ? CheckOutcome.Pass : CheckOutcome.Warning,
                    restored ? "The machine is as it was before the test."
                             : "The watcher could not be put back the way it was.");
            }
            catch (Exception ex)
            {
                report.Add("Watcher lifecycle", "watcher.restore", "Previous state restored",
                    CheckOutcome.Warning, _redactor.Redact(ex.Message));
            }
        }
    }
}
