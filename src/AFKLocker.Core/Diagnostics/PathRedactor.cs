using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AFKLocker.Core.Diagnostics
{
    /// <summary>
    /// Turns real paths into ones that carry no identity.
    ///
    /// A diagnostics bundle is meant to be sent to a stranger, and the single
    /// most common way personal data leaks into one is a file path: almost every
    /// interesting path on Windows runs through C:\Users\&lt;someone's name&gt;.
    /// Everything that goes into a bundle passes through here first.
    ///
    /// Substitution is longest-first, so the more specific known folder wins:
    /// %LOCALAPPDATA% rather than %USERPROFILE%\AppData\Local.
    /// </summary>
    public sealed class PathRedactor
    {
        private readonly List<KeyValuePair<string, string>> _replacements =
            new List<KeyValuePair<string, string>>();

        private readonly string _userName;

        public PathRedactor()
            : this(Environment.UserName, BuildDefaultFolders())
        {
        }

        /// <summary>Constructor used by tests to describe any machine.</summary>
        public PathRedactor(string userName, IEnumerable<KeyValuePair<string, string>> folders)
        {
            _userName = userName;

            var ordered = new List<KeyValuePair<string, string>>(folders);
            // Longest path first: %LOCALAPPDATA% must win over %USERPROFILE%.
            ordered.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
            _replacements = ordered;
        }

        private static IEnumerable<KeyValuePair<string, string>> BuildDefaultFolders()
        {
            var folders = new List<KeyValuePair<string, string>>();
            Add(folders, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%");
            Add(folders, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%");
            Add(folders, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
            Add(folders, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "%PROGRAMFILES(X86)%");
            Add(folders, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "%PROGRAMFILES%");
            Add(folders, Environment.GetFolderPath(Environment.SpecialFolder.Windows), "%WINDIR%");
            return folders;
        }

        private static void Add(ICollection<KeyValuePair<string, string>> folders, string path, string token)
        {
            if (!string.IsNullOrEmpty(path))
                folders.Add(new KeyValuePair<string, string>(path.TrimEnd('\\'), token));
        }

        /// <summary>
        /// Replaces known folders with their tokens, then removes any remaining
        /// trace of the user name. Returns null unchanged.
        /// </summary>
        public string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            string result = text;

            foreach (KeyValuePair<string, string> replacement in _replacements)
            {
                result = Replace(result, replacement.Key, replacement.Value);
            }

            // Belt and braces: a user name can still appear in a path shape the
            // known folders did not cover, or in a message.
            if (!string.IsNullOrEmpty(_userName))
                result = Replace(result, _userName, "<user>");

            return result;
        }

        /// <summary>
        /// Redacts a value that is known to be a path, and never lets an
        /// unrecognised one through whole.
        ///
        /// <see cref="Redact"/> only rewrites the folders it knows. A path
        /// somewhere else entirely - a portable copy on a second drive, a folder
        /// named after a project or a person - would otherwise survive intact.
        /// For a file that is going to a stranger, the directory is not worth
        /// the risk: what matters for diagnosis is the file name and whether it
        /// was found.
        /// </summary>
        public string RedactPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;

            string redacted = Redact(path);

            // A drive letter or UNC prefix that survived means the path was not
            // under any known folder.
            bool stillAbsolute = Regex.IsMatch(redacted, @"^[a-zA-Z]:\\") || redacted.StartsWith(@"\\");
            if (!stillAbsolute) return redacted;

            string leaf = redacted.TrimEnd('\\');
            int separator = leaf.LastIndexOf('\\');
            string name = separator >= 0 ? leaf.Substring(separator + 1) : leaf;

            return string.IsNullOrEmpty(name) ? "<path>" : @"<path>\" + name;
        }

        private static string Replace(string text, string find, string replaceWith)
        {
            if (string.IsNullOrEmpty(find)) return text;
            return Regex.Replace(text, Regex.Escape(find), replaceWith.Replace("$", "$$"),
                RegexOptions.IgnoreCase);
        }
    }
}
