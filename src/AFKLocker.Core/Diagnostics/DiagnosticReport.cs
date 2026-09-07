using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AFKLocker.Core.Diagnostics
{
    public enum CheckOutcome
    {
        Pass,
        Warning,
        Fail,

        /// <summary>The check does not apply to this machine (a desktop has no lid).</summary>
        NotApplicable,

        /// <summary>Not run - typically an interactive test the user did not do.</summary>
        NotTested,

        /// <summary>Neutral fact, recorded for context rather than judged.</summary>
        Info
    }

    /// <summary>One line of the report.</summary>
    public sealed class DiagnosticCheck
    {
        public string Section { get; private set; }
        public string Id { get; private set; }
        public string Title { get; private set; }
        public CheckOutcome Outcome { get; private set; }
        public string Detail { get; private set; }

        public DiagnosticCheck(string section, string id, string title, CheckOutcome outcome, string detail)
        {
            Section = section;
            Id = id;
            Title = title;
            Outcome = outcome;
            Detail = detail;
        }

        public override string ToString()
        {
            return string.Format("[{0}] {1}", Outcome.ToString().ToUpperInvariant(), Title);
        }
    }

    public enum OverallResult
    {
        Passed,
        PassedWithWarnings,
        Failed
    }

    /// <summary>
    /// The result of a self-test: a list of checks, plus the structured facts
    /// that make a compatibility picture possible across machines.
    ///
    /// Everything in here is already redacted. Nothing is added to a report that
    /// has not been through <see cref="PathRedactor"/> or is not a plain
    /// technical fact.
    /// </summary>
    public sealed class DiagnosticReport
    {
        /// <summary>Bump when the JSON shape changes in a way a reader must know about.</summary>
        public const int SchemaVersion = 1;

        private readonly List<DiagnosticCheck> _checks = new List<DiagnosticCheck>();
        private readonly List<string> _warnings = new List<string>();
        private readonly List<string> _errors = new List<string>();
        private readonly Dictionary<string, object> _facts =
            new Dictionary<string, object>(StringComparer.Ordinal);

        public DateTime GeneratedUtc { get; set; }
        public string AfkLockerVersion { get; set; }

        public ReadOnlyCollection<DiagnosticCheck> Checks
        {
            get { return new ReadOnlyCollection<DiagnosticCheck>(_checks); }
        }

        public ReadOnlyCollection<string> Warnings
        {
            get { return new ReadOnlyCollection<string>(_warnings); }
        }

        public ReadOnlyCollection<string> Errors
        {
            get { return new ReadOnlyCollection<string>(_errors); }
        }

        /// <summary>
        /// Flat technical facts, grouped by dotted key ("power.lidReported").
        /// These are what make a compatibility matrix possible if several people
        /// send bundles in - so they are deliberately plain values, never
        /// anything that could identify a machine.
        /// </summary>
        public IEnumerable<KeyValuePair<string, object>> Facts
        {
            get { return _facts.OrderBy(f => f.Key, StringComparer.Ordinal); }
        }

        public void Add(DiagnosticCheck check)
        {
            _checks.Add(check);
            if (check.Outcome == CheckOutcome.Warning) _warnings.Add(check.Title + ": " + check.Detail);
            if (check.Outcome == CheckOutcome.Fail) _errors.Add(check.Title + ": " + check.Detail);
        }

        public void Add(string section, string id, string title, CheckOutcome outcome, string detail)
        {
            Add(new DiagnosticCheck(section, id, title, outcome, detail));
        }

        public void Fact(string key, object value)
        {
            _facts[key] = value;
        }

        public OverallResult Overall
        {
            get
            {
                if (_checks.Any(c => c.Outcome == CheckOutcome.Fail)) return OverallResult.Failed;
                if (_checks.Any(c => c.Outcome == CheckOutcome.Warning)) return OverallResult.PassedWithWarnings;
                return OverallResult.Passed;
            }
        }

        public string OverallText
        {
            get
            {
                switch (Overall)
                {
                    case OverallResult.Failed: return "One or more tests failed";
                    case OverallResult.PassedWithWarnings: return "Passed with warnings";
                    default: return "All tests passed";
                }
            }
        }

        /// <summary>Section names in the order they were first seen.</summary>
        public IEnumerable<string> Sections
        {
            get
            {
                var seen = new List<string>();
                foreach (DiagnosticCheck check in _checks)
                    if (!seen.Contains(check.Section)) seen.Add(check.Section);
                return seen;
            }
        }

        /// <summary>The human-readable report, as shown on screen and saved as summary.txt.</summary>
        public string ToText()
        {
            var text = new StringBuilder();
            text.AppendLine("AFKLocker Diagnostics");
            text.AppendLine("Version " + (AfkLockerVersion ?? "unknown")
                            + "   generated " + GeneratedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC");
            text.AppendLine();

            foreach (string section in Sections)
            {
                text.AppendLine(section);
                foreach (DiagnosticCheck check in _checks.Where(c => c.Section == section))
                {
                    text.AppendLine(string.Format("  [{0,-14}] {1}",
                        check.Outcome.ToString().ToUpperInvariant(), check.Title));
                    if (!string.IsNullOrEmpty(check.Detail))
                        text.AppendLine("                   " + check.Detail);
                }
                text.AppendLine();
            }

            text.AppendLine("Result");
            text.AppendLine("  " + OverallText);
            return text.ToString();
        }

        /// <summary>The machine-readable report, saved as diagnostics.json.</summary>
        public string ToJson()
        {
            var json = new JsonBuilder();
            json.BeginObject();
            json.Property("schemaVersion", SchemaVersion);
            json.Property("afklockerVersion", AfkLockerVersion);
            json.Property("generatedUtc", GeneratedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            json.Property("overall", Overall.ToString());

            // Facts are emitted as nested objects from their dotted keys, so a
            // reader gets environment{}, power{}, watcher{} and so on.
            var groups = new List<string>();
            foreach (KeyValuePair<string, object> fact in Facts)
            {
                string group = fact.Key.Contains(".") ? fact.Key.Substring(0, fact.Key.IndexOf('.')) : "other";
                if (!groups.Contains(group)) groups.Add(group);
            }

            foreach (string group in groups)
            {
                json.BeginObjectProperty(group);
                foreach (KeyValuePair<string, object> fact in Facts)
                {
                    string factGroup = fact.Key.Contains(".") ? fact.Key.Substring(0, fact.Key.IndexOf('.')) : "other";
                    if (factGroup != group) continue;
                    string name = fact.Key.Contains(".") ? fact.Key.Substring(fact.Key.IndexOf('.') + 1) : fact.Key;
                    json.Property(name, fact.Value);
                }
                json.EndObject();
            }

            json.BeginArrayProperty("tests");
            foreach (DiagnosticCheck check in _checks)
            {
                json.BeginObject();
                json.Property("section", check.Section);
                json.Property("id", check.Id);
                json.Property("title", check.Title);
                json.Property("outcome", check.Outcome.ToString());
                json.Property("detail", check.Detail);
                json.EndObject();
            }
            json.EndArray();

            json.ArrayProperty("warnings", _warnings);
            json.ArrayProperty("errors", _errors);
            json.EndObject();
            return json.ToString();
        }
    }
}
