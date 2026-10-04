using System.Text;
using System.Text.RegularExpressions;
using Cadence.Agent;
using Cadence.Mcp;
using Cadence.Agent.Tools;
using Cadence.TestHarness;

namespace Cadence.Tests.Mcp;

/// <summary>
/// The MCP wire bytes of Cadence as a golden (docs\REFACTOR-modules.md, Phase 3): what the server answers to initialize, tools/list, tool calls, protocol errors and an
/// internal error must stay byte-identical when the protocol code moves between projects. The goldens in Mcp\Golden were captured from the code BEFORE the move.
/// </summary>
public static class McpWireGoldenTests
{
    static string GoldenDir()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "tests", "Cadence.Tests", "Mcp", "Golden"))) dir = Path.GetDirectoryName(dir);
        return dir is null ? "" : Path.Combine(dir, "tests", "Cadence.Tests", "Mcp", "Golden");
    }

    static McpServer Make(string version, IToolHost host, TextWriter log, string? instructions) => new(McpHost.ServerInfo(version), host, log, instructions, options: McpHost.ServerOptions);

    /// <summary>Feeds the lines one at a time, waiting for each reply (so the order of the replies is deterministic), and returns the raw output and the log.</summary>
    static (byte[] Raw, string Log) Drive(Func<TextWriter, McpServer> make, params string[] lines)
    {
        var log = new StringWriter();
        McpServer server = make(TextWriter.Synchronized(log));
        var input = new FeedStream();
        var output = new CaptureStream();
        Task<int> run = Task.Run(() => server.Run(input, output));
        for (int i = 0; i < lines.Length; i++)
        {
            input.Line(lines[i]);
            output.WaitForLines(i + 1);
        }
        input.Complete();
        T.Eq("exit code", 0, run.Wait(15000) ? run.Result : -999);
        return (output.Raw(), log.ToString());
    }

    static string Call(int id, string tool, string args = "{}") =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + args + "}}";

    static string Rpc(int id, string method) => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"" + method + "\"}";

    static void Compare(string label, string file, byte[] actual)
    {
        string dir = GoldenDir();
        if (dir.Length == 0) { T.True(label + ": the golden folder was found", false); return; }
        string path = Path.Combine(dir, file);
        // Only the checkout may differ (git can turn LF into CRLF in a text file); the server never writes a CR.
        byte[] golden = File.Exists(path) ? File.ReadAllBytes(path).Where(b => b != (byte)13).ToArray() : [];
        T.True(label + ": byte-identical to " + file + " (" + actual.Length + " vs " + golden.Length + " bytes)", actual.AsSpan().SequenceEqual(golden));
    }

    public static void TheFakeHostWireIsUnchanged()
    {
        var host = new FakeToolHost();
        (byte[] raw, _) = Drive(log => Make("9.9.9", host, log, "the instructions"),
            Session.Init, Rpc(2, "ping"), Rpc(3, "tools/list"), Call(4, "echo", "{\"text\":\"hi\"}"), Call(5, "boom"), Call(6, "lines"),
            Rpc(7, "no/such"), Call(8, "nope"), "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{}}", "{nope");
        Compare("fake host, 10 exchanges (initialize, ping, tools/list, calls, errors)", "fake-host.jsonl", raw);
    }

    public static void TheRealHostWireIsUnchanged()
    {
        CadenceToolHost host = McpServerTests.Real();
        (byte[] raw, _) = Drive(log => Make("0.0.0", host, log, McpHost.Instructions), Session.Init, Rpc(2, "tools/list"), Call(3, "cadence_get_status"));
        Compare("real host, initialize + tools/list + cadence_get_status", "real-host.jsonl", raw);
    }

    public static void TheLogLineOfTheHandshakeIsUnchanged()
    {
        (_, string log) = Drive(l => Make("9.9.9", new FakeToolHost(), l, null), Session.Init);
        byte[] normalized = new UTF8Encoding(false).GetBytes(Regex.Replace(log, "session [0-9a-f]{8}", "session X"));
        Compare("the handshake log line", "handshake-log.txt", normalized);
    }
}
