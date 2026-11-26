using System.Text;

namespace FilmExpiryChecker.Storage;

/// <summary>
/// Writes files so a cut-off write never looks whole: everything goes to <c>&lt;file&gt;.part</c> first, which
/// replaces the real file only once it is complete and flushed to disk.
/// </summary>
public static class SafeFile
{
    public static string PartPath(string path) => path + ".part";

    public static void WriteAllText(string path, string contents) =>
        Write(path, stream =>
        {
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents);
            stream.Write(bytes);
        });

    /// <summary>Writes through <paramref name="write"/>; if it throws, the previous file is left as it was.</summary>
    public static void Write(string path, Action<Stream> write)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var part = PartPath(path);
        try
        {
            using (var stream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(part, path, overwrite: true);
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    /// <summary>Removes a <c>.part</c> file left by a write that never finished. Call before reading the real file.</summary>
    public static void DiscardLeftover(string path) => TryDelete(PartPath(path));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
