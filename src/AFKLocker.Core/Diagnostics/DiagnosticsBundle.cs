using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AFKLocker.Core.Diagnostics
{
    /// <summary>
    /// Writes a diagnostics report to a zip the user can attach to an email or
    /// an issue.
    ///
    /// Three files, no more: a summary a person can read, the same information
    /// as JSON for a tool to read, and the self-test output. Everything in them
    /// has already been through the report, which is where redaction happens -
    /// nothing is collected here that was not collected there.
    /// </summary>
    public sealed class DiagnosticsBundle
    {
        public const string SummaryFileName = "summary.txt";
        public const string JsonFileName = "diagnostics.json";
        public const string SelfTestFileName = "self-test.txt";

        /// <summary>
        /// Shown to the user before anything is written. Deliberately specific
        /// about what is and is not in the file, so consent is informed rather
        /// than assumed.
        /// </summary>
        public const string PrivacyNotice =
            "This diagnostic report contains technical information about Windows, power management "
            + "and AFKLocker. It does not intentionally include your name, username, files, network "
            + "information or other personal data.\r\n\r\n"
            + "Included: Windows version and build, architecture, whether this machine reports a lid "
            + "and a battery, power plan type and the specific power settings AFKLocker reads, "
            + "AFKLocker's own settings and state, and the results of the tests you ran.\r\n\r\n"
            + "Not included: your username or computer name, IP or MAC addresses, Wi-Fi networks, "
            + "your files or documents, installed programs, running processes, environment variables, "
            + "or any registry values outside AFKLocker's own. File paths are replaced with "
            + "placeholders such as %LOCALAPPDATA%.\r\n\r\n"
            + "Nothing is ever sent anywhere. The file is saved where you choose, and it is yours "
            + "to read before deciding whether to share it.";

        public static string SuggestedFileName()
        {
            return "AFKLocker-Diagnostics-"
                   + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".zip";
        }

        /// <summary>Writes the bundle. Returns the path written.</summary>
        public string Write(DiagnosticReport report, string zipPath)
        {
            if (report == null) throw new ArgumentNullException("report");
            if (string.IsNullOrEmpty(zipPath)) throw new ArgumentException("zipPath must not be empty", "zipPath");

            string directory = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            if (File.Exists(zipPath)) File.Delete(zipPath);

            using (FileStream stream = File.Create(zipPath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteEntry(archive, SummaryFileName, BuildSummary(report));
                WriteEntry(archive, JsonFileName, report.ToJson());
                WriteEntry(archive, SelfTestFileName, report.ToText());
            }

            return zipPath;
        }

        private static void WriteEntry(ZipArchive archive, string name, string content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using (Stream stream = entry.Open())
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
            }
        }

        /// <summary>The human-facing file: what happened, and what is in here.</summary>
        public static string BuildSummary(DiagnosticReport report)
        {
            var text = new StringBuilder();
            text.AppendLine("AFKLocker diagnostics summary");
            text.AppendLine("=============================");
            text.AppendLine();
            text.AppendLine("Result: " + report.OverallText);
            text.AppendLine("AFKLocker version: " + (report.AfkLockerVersion ?? "unknown"));
            text.AppendLine("Generated (UTC): "
                            + report.GeneratedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            text.AppendLine();

            if (report.Errors.Count > 0)
            {
                text.AppendLine("Failures");
                text.AppendLine("--------");
                foreach (string error in report.Errors) text.AppendLine("  - " + error);
                text.AppendLine();
            }

            if (report.Warnings.Count > 0)
            {
                text.AppendLine("Warnings");
                text.AppendLine("--------");
                foreach (string warning in report.Warnings) text.AppendLine("  - " + warning);
                text.AppendLine();
            }

            text.AppendLine(report.ToText());
            text.AppendLine();
            text.AppendLine("About this file");
            text.AppendLine("---------------");
            text.AppendLine(PrivacyNotice);
            return text.ToString();
        }
    }
}
