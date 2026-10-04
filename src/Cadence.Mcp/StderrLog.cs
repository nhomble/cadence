using System.Text;

namespace Cadence.Mcp;

/// <summary>The server's only log channel: stderr lines with a prefix the host chooses (stdout carries protocol messages ONLY).</summary>
public static class StderrLog
{
    /// <summary>The prefix when the host does not choose one.</summary>
    public const string DefaultPrefix = "mcp: ";

    /// <summary>A thread-safe UTF-8 (no BOM) writer on the raw stderr stream, flushed per line.</summary>
    public static TextWriter Open() =>
        TextWriter.Synchronized(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

    /// <summary>Writes one line (line breaks inside the message are flattened). Never throws: a closed stderr must not stop the server.</summary>
    public static void Line(TextWriter log, string message, string prefix = DefaultPrefix)
    {
        try
        {
            lock (log)
            {
                log.Write(prefix);
                log.Write(message.Replace("\r\n", " | ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' '));
                log.Write('\n');
                log.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException) { }
    }
}
