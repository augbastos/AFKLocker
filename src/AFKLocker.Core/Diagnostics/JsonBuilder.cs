using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AFKLocker.Core.Diagnostics
{
    /// <summary>
    /// Writes the small, fixed JSON shape the diagnostics bundle needs.
    ///
    /// Hand-written rather than pulled in from a package: the project has no
    /// dependency restore step, and this produces one document of known shape
    /// from values this code creates. Escaping follows RFC 8259 for the cases
    /// that can occur here - quotes, backslashes and control characters.
    /// </summary>
    internal sealed class JsonBuilder
    {
        private readonly StringBuilder _text = new StringBuilder();
        private readonly Stack<bool> _hasMembers = new Stack<bool>();
        private int _indent;

        public void BeginObject()
        {
            Separate();
            _text.Append('{');
            _hasMembers.Push(false);
            _indent++;
        }

        public void BeginObjectProperty(string name)
        {
            Separate();
            NewLine();
            _text.Append(Quote(name)).Append(": {");
            MarkMember();
            _hasMembers.Push(false);
            _indent++;
        }

        public void EndObject()
        {
            _indent--;
            bool had = _hasMembers.Pop();
            if (had) NewLine();
            _text.Append('}');

            // The object just closed is itself a member of whatever contains
            // it. Without this, consecutive objects in an array are written as
            // "}{" and the document is not valid JSON.
            MarkMember();
        }

        public void BeginArrayProperty(string name)
        {
            Separate();
            NewLine();
            _text.Append(Quote(name)).Append(": [");
            MarkMember();
            _hasMembers.Push(false);
            _indent++;
        }

        public void EndArray()
        {
            _indent--;
            bool had = _hasMembers.Pop();
            if (had) NewLine();
            _text.Append(']');
            MarkMember();
        }

        public void ArrayProperty(string name, IEnumerable<string> values)
        {
            BeginArrayProperty(name);
            foreach (string value in values)
            {
                Separate();
                NewLine();
                _text.Append(Quote(value));
                MarkMember();
            }
            EndArray();
        }

        public void Property(string name, object value)
        {
            Separate();
            NewLine();
            _text.Append(Quote(name)).Append(": ").Append(Format(value));
            MarkMember();
        }

        private void Separate()
        {
            if (_hasMembers.Count > 0 && _hasMembers.Peek())
                _text.Append(',');
        }

        private void MarkMember()
        {
            if (_hasMembers.Count == 0) return;
            _hasMembers.Pop();
            _hasMembers.Push(true);
        }

        private void NewLine()
        {
            _text.AppendLine();
            _text.Append(' ', _indent * 2);
        }

        private static string Format(object value)
        {
            if (value == null) return "null";

            if (value is bool) return ((bool)value) ? "true" : "false";

            if (value is int || value is long || value is uint || value is short)
                return Convert.ToString(value, CultureInfo.InvariantCulture);

            if (value is double || value is float || value is decimal)
                return Convert.ToDouble(value, CultureInfo.InvariantCulture)
                    .ToString("R", CultureInfo.InvariantCulture);

            return Quote(Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static string Quote(string value)
        {
            if (value == null) return "null";

            var text = new StringBuilder(value.Length + 2);
            text.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    case '\b': text.Append("\\b"); break;
                    case '\f': text.Append("\\f"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\r': text.Append("\\r"); break;
                    case '\t': text.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            text.Append(c);
                        break;
                }
            }
            text.Append('"');
            return text.ToString();
        }

        public override string ToString()
        {
            return _text.ToString();
        }
    }
}
