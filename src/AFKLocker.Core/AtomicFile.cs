using System.IO;
using System.Text;

namespace AFKLocker.Core
{
    /// <summary>
    /// Writes small text files without ever leaving a half-written one behind.
    ///
    /// Both files AFKLocker persists - the power settings backup and the lock
    /// mode - are the only copy of something the user cannot reconstruct.
    /// Writing in place would truncate the existing file before the new content
    /// lands, so a crash or power loss at that moment would destroy it.
    /// </summary>
    internal static class AtomicFile
    {
        public static void WriteAllText(string path, string content)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string temporary = path + ".tmp";

            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(true);   // through to disk, not just the OS cache
            }

            if (File.Exists(path))
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path);
        }
    }
}
