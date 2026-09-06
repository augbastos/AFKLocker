using System;
using System.IO;
using System.Linq;
using AFKLocker.Core;

namespace AFKLocker.Tests
{
    internal static class PowerBackupTests
    {
        [Test("A backup survives a serialize/deserialize round trip")]
        private static void RoundTrip()
        {
            var backup = new PowerBackup
            {
                Scheme = new Guid("22222222-2222-2222-2222-222222222222"),
                SchemeName = "Balanced"
            };
            backup.RecordOriginal(PowerSettings.LidCloseAc.Key, 1);
            backup.RecordOriginal(PowerSettings.SleepAc.Key, 1800);

            PowerBackup restored = PowerBackup.Deserialize(backup.Serialize());

            Assert.Equal(backup.Scheme, restored.Scheme, "scheme");
            Assert.Equal("Balanced", restored.SchemeName, "scheme name");
            Assert.Equal(2, restored.Count, "entry count");

            uint lid, sleep;
            Assert.True(restored.TryGet(PowerSettings.LidCloseAc.Key, out lid), "lid entry present");
            Assert.True(restored.TryGet(PowerSettings.SleepAc.Key, out sleep), "sleep entry present");
            Assert.Equal(1u, lid, "lid value");
            Assert.Equal(1800u, sleep, "sleep value");
        }

        [Test("The first recorded value wins, so re-running never loses the original")]
        private static void RecordOriginalDoesNotOverwrite()
        {
            var backup = new PowerBackup();

            Assert.True(backup.RecordOriginal(PowerSettings.SleepAc.Key, 1800), "first record");
            Assert.False(backup.RecordOriginal(PowerSettings.SleepAc.Key, 0), "second record is ignored");

            uint value;
            backup.TryGet(PowerSettings.SleepAc.Key, out value);
            Assert.Equal(1800u, value, "the pre-AFKLocker value is kept");
        }

        [Test("AC and DC values of the same setting are stored separately")]
        private static void AcAndDcAreIndependent()
        {
            var backup = new PowerBackup();
            backup.RecordOriginal(PowerSettings.SleepAc.Key, 0);
            backup.RecordOriginal(PowerSettings.SleepDc.Key, 900);

            PowerBackup restored = PowerBackup.Deserialize(backup.Serialize());

            uint ac, dc;
            restored.TryGet(PowerSettings.SleepAc.Key, out ac);
            restored.TryGet(PowerSettings.SleepDc.Key, out dc);
            Assert.Equal(0u, ac, "AC value");
            Assert.Equal(900u, dc, "DC value");
        }

        [Test("Comments and blank lines in a backup file are ignored")]
        private static void CommentsAreIgnored()
        {
            string text = "# a comment\n\nversion=1\nscheme=33333333-3333-3333-3333-333333333333\n"
                          + "   # indented comment\nsleep-ac=120\n";

            PowerBackup backup = PowerBackup.Deserialize(text);

            uint value;
            backup.TryGet(PowerSettings.SleepAc.Key, out value);
            Assert.Equal(120u, value, "value read past the comments");
        }

        [Test("Unknown keys are ignored so a newer file does not break an older build")]
        private static void UnknownKeysAreIgnored()
        {
            string text = "version=1\nscheme=33333333-3333-3333-3333-333333333333\n"
                          + "some-future-setting=42\nsleep-ac=60\n";

            PowerBackup backup = PowerBackup.Deserialize(text);

            Assert.Equal(1, backup.Count, "only the known key is kept");
            Assert.False(backup.Contains("some-future-setting"), "unknown key not stored");
        }

        [Test("A backup file with no version line is rejected")]
        private static void MissingVersionIsRejected()
        {
            Assert.Throws<BackupFormatException>(
                () => PowerBackup.Deserialize("scheme=33333333-3333-3333-3333-333333333333\nsleep-ac=0\n"),
                "missing version");
        }

        [Test("A backup file from a future version is rejected rather than misread")]
        private static void FutureVersionIsRejected()
        {
            BackupFormatException error = Assert.Throws<BackupFormatException>(
                () => PowerBackup.Deserialize("version=99\nscheme=33333333-3333-3333-3333-333333333333\n"),
                "future version");
            Assert.True(error.Message.Contains("99"), "the message names the version it found");
        }

        [Test("Corrupted values are rejected rather than silently treated as zero")]
        private static void CorruptValuesAreRejected()
        {
            Assert.Throws<BackupFormatException>(
                () => PowerBackup.Deserialize("version=1\nscheme=33333333-3333-3333-3333-333333333333\nsleep-ac=abc\n"),
                "non-numeric setting value");

            Assert.Throws<BackupFormatException>(
                () => PowerBackup.Deserialize("version=1\nscheme=not-a-guid\n"),
                "invalid scheme guid");

            Assert.Throws<BackupFormatException>(
                () => PowerBackup.Deserialize("version=1\nthis line has no separator\n"),
                "malformed line");
        }

        [Test("The file store keeps one backup per power scheme")]
        private static void FileStoreSeparatesSchemes()
        {
            string directory = Path.Combine(Path.GetTempPath(), "afklocker-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FileBackupStore(directory);
                var first = new Guid("44444444-4444-4444-4444-444444444444");
                var second = new Guid("55555555-5555-5555-5555-555555555555");

                Assert.Null(store.Load(first), "nothing stored yet");
                Assert.Equal(0, store.ListSchemes().Count(), "no schemes yet");

                var backupA = new PowerBackup { Scheme = first, SchemeName = "Balanced" };
                backupA.RecordOriginal(PowerSettings.SleepAc.Key, 1800);
                store.Save(backupA);

                var backupB = new PowerBackup { Scheme = second, SchemeName = "High performance" };
                backupB.RecordOriginal(PowerSettings.SleepAc.Key, 600);
                store.Save(backupB);

                Assert.Equal(2, store.ListSchemes().Count(), "both schemes listed");

                uint value;
                store.Load(first).TryGet(PowerSettings.SleepAc.Key, out value);
                Assert.Equal(1800u, value, "first scheme value");
                store.Load(second).TryGet(PowerSettings.SleepAc.Key, out value);
                Assert.Equal(600u, value, "second scheme value");

                store.Delete(first);
                Assert.Null(store.Load(first), "deleted");
                Assert.Equal(1, store.ListSchemes().Count(), "one scheme left");
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test("Listing backups in a directory that does not exist returns nothing")]
        private static void MissingDirectoryIsNotAnError()
        {
            var store = new FileBackupStore(Path.Combine(Path.GetTempPath(),
                "afklocker-missing-" + Guid.NewGuid().ToString("N")));

            Assert.Equal(0, store.ListSchemes().Count(), "no schemes");
            Assert.Null(store.Load(Guid.NewGuid()), "no backup");
        }
    }
}
