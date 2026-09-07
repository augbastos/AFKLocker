using System;
using System.Globalization;

namespace AFKLocker.Tests
{
    /// <summary>
    /// A strict-enough JSON parser, used only to prove the diagnostics document
    /// is valid.
    ///
    /// It exists because checking the output for expected substrings is not the
    /// same as checking it is JSON - and a missing comma between array elements
    /// passed every substring check while producing a document no tool could
    /// read. This walks the whole text and throws on anything malformed.
    ///
    /// Small enough to audit, and it keeps the test suite free of dependencies
    /// like the rest of the project.
    /// </summary>
    internal static class MiniJson
    {
        public static void Parse(string text)
        {
            if (text == null) throw new FormatException("JSON was null.");

            int index = 0;
            SkipWhitespace(text, ref index);
            ParseValue(text, ref index);
            SkipWhitespace(text, ref index);

            if (index != text.Length)
                throw new FormatException(string.Format(
                    "Trailing content at offset {0}: {1}", index, Excerpt(text, index)));
        }

        private static void ParseValue(string text, ref int index)
        {
            if (index >= text.Length) throw new FormatException("Unexpected end of JSON.");

            char c = text[index];
            switch (c)
            {
                case '{': ParseObject(text, ref index); return;
                case '[': ParseArray(text, ref index); return;
                case '"': ParseString(text, ref index); return;
                default:
                    if (Matches(text, index, "true")) { index += 4; return; }
                    if (Matches(text, index, "false")) { index += 5; return; }
                    if (Matches(text, index, "null")) { index += 4; return; }
                    ParseNumber(text, ref index);
                    return;
            }
        }

        private static void ParseObject(string text, ref int index)
        {
            Expect(text, ref index, '{');
            SkipWhitespace(text, ref index);

            if (Peek(text, index) == '}') { index++; return; }

            while (true)
            {
                SkipWhitespace(text, ref index);
                ParseString(text, ref index);
                SkipWhitespace(text, ref index);
                Expect(text, ref index, ':');
                SkipWhitespace(text, ref index);
                ParseValue(text, ref index);
                SkipWhitespace(text, ref index);

                char next = Peek(text, index);
                if (next == ',') { index++; continue; }
                if (next == '}') { index++; return; }

                throw new FormatException(string.Format(
                    "Expected ',' or '}}' at offset {0}: {1}", index, Excerpt(text, index)));
            }
        }

        private static void ParseArray(string text, ref int index)
        {
            Expect(text, ref index, '[');
            SkipWhitespace(text, ref index);

            if (Peek(text, index) == ']') { index++; return; }

            while (true)
            {
                SkipWhitespace(text, ref index);
                ParseValue(text, ref index);
                SkipWhitespace(text, ref index);

                char next = Peek(text, index);
                if (next == ',') { index++; continue; }
                if (next == ']') { index++; return; }

                throw new FormatException(string.Format(
                    "Expected ',' or ']' at offset {0}: {1}", index, Excerpt(text, index)));
            }
        }

        private static void ParseString(string text, ref int index)
        {
            Expect(text, ref index, '"');
            while (index < text.Length)
            {
                char c = text[index++];
                if (c == '"') return;
                if (c != '\\') continue;

                if (index >= text.Length) break;
                char escape = text[index++];
                if (escape == 'u') index += 4;
                else if ("\"\\/bfnrt".IndexOf(escape) < 0)
                    throw new FormatException("Invalid escape \\" + escape + " at offset " + index);
            }
            throw new FormatException("Unterminated string.");
        }

        private static void ParseNumber(string text, ref int index)
        {
            int start = index;
            if (Peek(text, index) == '-') index++;
            while (index < text.Length && (char.IsDigit(text[index]) || "+-.eE".IndexOf(text[index]) >= 0))
                index++;

            string number = text.Substring(start, index - start);
            double parsed;
            if (number.Length == 0 ||
                !double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                throw new FormatException(string.Format(
                    "Invalid value at offset {0}: {1}", start, Excerpt(text, start)));
        }

        private static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        }

        private static char Peek(string text, int index)
        {
            return index < text.Length ? text[index] : '\0';
        }

        private static void Expect(string text, ref int index, char expected)
        {
            if (Peek(text, index) != expected)
                throw new FormatException(string.Format(
                    "Expected '{0}' at offset {1}: {2}", expected, index, Excerpt(text, index)));
            index++;
        }

        private static bool Matches(string text, int index, string literal)
        {
            return index + literal.Length <= text.Length
                   && string.CompareOrdinal(text, index, literal, 0, literal.Length) == 0;
        }

        private static string Excerpt(string text, int index)
        {
            int length = Math.Min(40, text.Length - index);
            return length <= 0 ? "<end>" : text.Substring(index, length).Replace("\n", "\\n");
        }
    }
}
