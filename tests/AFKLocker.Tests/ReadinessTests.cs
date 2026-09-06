using System;
using AFKLocker.Core;

namespace AFKLocker.Tests
{
    internal static class ReadinessTests
    {
        [Test("A machine already configured for closed-lid work reports ready")]
        private static void ConfiguredMachineIsReady()
        {
            ReadinessReport report = ReadinessEvaluator.Evaluate(SnapshotBuilder.ReadyLaptop());

            Assert.True(report.IsReady, "a configured laptop should be ready");
            Assert.Equal(CheckStatus.Ready, report.Find(ReadinessEvaluator.LidCheckId).Status, "lid check");
            Assert.Equal(CheckStatus.Ready, report.Find(ReadinessEvaluator.SleepCheckId).Status, "sleep check");
        }

        [Test("A stock laptop that sleeps on lid close is not ready")]
        private static void StockLaptopIsNotReady()
        {
            ReadinessReport report = ReadinessEvaluator.Evaluate(SnapshotBuilder.StockLaptop());

            Assert.False(report.IsReady, "a stock laptop should not be ready");
            Assert.Equal(CheckStatus.NeedsConfiguration,
                report.Find(ReadinessEvaluator.LidCheckId).Status, "lid check");
            Assert.Equal(CheckStatus.NeedsConfiguration,
                report.Find(ReadinessEvaluator.SleepCheckId).Status, "sleep check");
        }

        [Test("Sleep timeout alone is enough to make a machine not ready")]
        private static void SleepTimeoutAloneBlocksReadiness()
        {
            PowerSnapshot snapshot = SnapshotBuilder.ReadyLaptop();
            snapshot[PowerSettings.SleepAc] = SettingValue.Of(600);

            ReadinessReport report = ReadinessEvaluator.Evaluate(snapshot);

            Assert.False(report.IsReady, "a 10 minute sleep timeout must block readiness");
            Assert.True(report.Find(ReadinessEvaluator.SleepCheckId).Detail.Contains("10 minutes"),
                "the detail should say how long");
        }

        [Test("A missing lid setting does not block readiness")]
        private static void AbsentLidSettingIsNotAFailure()
        {
            PowerSnapshot snapshot = SnapshotBuilder.ReadyLaptop();
            snapshot[PowerSettings.LidCloseAc] = SettingValue.Absent;
            snapshot.Capabilities.LidPresent = false;

            ReadinessReport report = ReadinessEvaluator.Evaluate(snapshot);

            Assert.True(report.IsReady, "a desktop with no lid is still ready");
            Assert.Equal(CheckStatus.Unavailable,
                report.Find(ReadinessEvaluator.LidCheckId).Status, "lid check");
        }

        [Test("Hibernation is only checked when it is enabled")]
        private static void HibernateIsOnlyCheckedWhenEnabled()
        {
            PowerSnapshot disabled = SnapshotBuilder.ReadyLaptop();
            Assert.Equal(CheckStatus.Unavailable,
                ReadinessEvaluator.Evaluate(disabled).Find(ReadinessEvaluator.HibernateCheckId).Status,
                "hibernate absent means not applicable");

            PowerSnapshot enabled = SnapshotBuilder.ReadyLaptop();
            enabled.Capabilities.HibernateFilePresent = true;
            enabled[PowerSettings.HibernateAc] = SettingValue.Of(7200);

            ReadinessReport report = ReadinessEvaluator.Evaluate(enabled);
            Assert.False(report.IsReady, "a machine that hibernates after 2 hours is not ready");
            Assert.True(report.Find(ReadinessEvaluator.HibernateCheckId).Detail.Contains("2 hours"),
                "the detail should say how long");
        }

        [Test("Battery behaviour is reported but never blocks readiness")]
        private static void BatteryIsOptional()
        {
            ReadinessReport report = ReadinessEvaluator.Evaluate(SnapshotBuilder.ReadyLaptop());
            ReadinessCheck battery = report.Find(ReadinessEvaluator.BatteryCheckId);

            Assert.Equal(CheckStatus.Optional, battery.Status, "battery is optional");
            Assert.True(report.IsReady, "battery settings must not block readiness");
            Assert.True(battery.Detail.Contains("30 minutes"), "should describe the battery timeout");
        }

        [Test("Battery configured like AC is reported with a warning about drain")]
        private static void BatteryMatchingAcMentionsDrain()
        {
            PowerSnapshot snapshot = SnapshotBuilder.ReadyLaptop();
            snapshot[PowerSettings.LidCloseDc] = SettingValue.Of((uint)LidAction.DoNothing);
            snapshot[PowerSettings.SleepDc] = SettingValue.Of(0);

            ReadinessCheck battery = ReadinessEvaluator.Evaluate(snapshot)
                .Find(ReadinessEvaluator.BatteryCheckId);

            Assert.Equal(CheckStatus.Optional, battery.Status, "still optional");
            Assert.True(battery.Detail.Contains("overheat"), "must warn about heat in an enclosed space");
        }

        [Test("A machine with no battery reports the battery check as not applicable")]
        private static void NoBatteryIsNotApplicable()
        {
            PowerSnapshot snapshot = SnapshotBuilder.ReadyLaptop();
            snapshot.Capabilities.BatteryPresent = false;

            Assert.Equal(CheckStatus.Unavailable,
                ReadinessEvaluator.Evaluate(snapshot).Find(ReadinessEvaluator.BatteryCheckId).Status,
                "desktop has no battery check");
        }

        [Test("Modern Standby machines get a warning without losing ready status")]
        private static void ModernStandbyAddsAWarning()
        {
            PowerSnapshot snapshot = SnapshotBuilder.ReadyLaptop();
            snapshot.Capabilities.ModernStandby = true;

            ReadinessReport report = ReadinessEvaluator.Evaluate(snapshot);
            ReadinessCheck warning = report.Find(ReadinessEvaluator.ModernStandbyCheckId);

            Assert.NotNull(warning, "modern standby machines get an extra check");
            Assert.Equal(CheckStatus.Warning, warning.Status, "it is a warning, not a failure");
            Assert.True(report.IsReady, "the machine is still usable");
            Assert.True(report.Summary.Contains("notes"), "the summary should point at the note");
        }

        [Test("Evaluating a null snapshot is rejected")]
        private static void NullSnapshotThrows()
        {
            Assert.Throws<ArgumentNullException>(
                () => ReadinessEvaluator.Evaluate(null), "null snapshot");
        }

        [Test("Timeouts are described the way Windows describes them")]
        private static void TimeoutFormatting()
        {
            Assert.Equal("Never", PowerValueFormatter.Timeout(SettingValue.Of(0)), "zero is never");
            Assert.Equal("1 minute", PowerValueFormatter.Timeout(SettingValue.Of(60)), "singular minute");
            Assert.Equal("30 minutes", PowerValueFormatter.Timeout(SettingValue.Of(1800)), "plural minutes");
            Assert.Equal("1 hour", PowerValueFormatter.Timeout(SettingValue.Of(3600)), "singular hour");
            Assert.Equal("2 hours", PowerValueFormatter.Timeout(SettingValue.Of(7200)), "plural hours");
            Assert.Equal("90 seconds", PowerValueFormatter.Timeout(SettingValue.Of(90)), "odd values");
            Assert.Equal("not available", PowerValueFormatter.Timeout(SettingValue.Absent), "absent");
        }

        [Test("Lid actions are described in words, not numbers")]
        private static void LidFormatting()
        {
            Assert.Equal("Do nothing", PowerValueFormatter.Lid(SettingValue.Of(0)), "0");
            Assert.Equal("Sleep", PowerValueFormatter.Lid(SettingValue.Of(1)), "1");
            Assert.Equal("Hibernate", PowerValueFormatter.Lid(SettingValue.Of(2)), "2");
            Assert.Equal("Shut down", PowerValueFormatter.Lid(SettingValue.Of(3)), "3");
            Assert.True(PowerValueFormatter.Lid(SettingValue.Of(9)).Contains("Unknown"), "unexpected value");
        }
    }
}
