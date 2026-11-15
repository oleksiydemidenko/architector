using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Architector.CodeFacts;

// The local call log, Library/Architector/calls.jsonl: one line per MCP tool call and command line run with time,
// session id (when the client gives one), caller (mcp, cli, hook), action, arguments, reply size with a token estimate
// and duration, so evaluations measure adoption and cost without reading transcripts. Capped at 4 MB: the full file
// moves to calls.1.jsonl and a new one starts. ARCHITECTOR_CALL_LOG=0 turns it off.
public static partial class McpServer
{
    private const long CallLogLimit = 4L * 1024 * 1024;
    private const int ArgumentsLimit = 600;
    private static string? callAction;

    internal static string CallLogPath(string project) => Path.Combine(project, "Library", "Architector", "calls.jsonl");

    private static string? SessionFromEnvironment() =>
        new[] { "ARCHITECTOR_SESSION_ID", "CLAUDE_SESSION_ID", "CLAUDE_CODE_SESSION_ID", "CODEX_SESSION_ID" }
            .Select(Environment.GetEnvironmentVariable)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static void LogCall(string caller, string action, JsonNode? arguments, long characters, long milliseconds, string? session)
    {
        if (root == null || Environment.GetEnvironmentVariable("ARCHITECTOR_CALL_LOG") is "0" or "off" or "false")
            return;
        try
        {
            var path = CallLogPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var text = arguments?.ToJsonString(FactsQueries.Compact) ?? "{}";
            var line = new JsonObject
            {
                ["time"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ["session"] = session ?? SessionFromEnvironment(),
                ["caller"] = caller,
                ["action"] = action,
                ["arguments"] = text.Length <= ArgumentsLimit ? arguments?.DeepClone() ?? new JsonObject() : text[..ArgumentsLimit] + "…",
                ["chars"] = characters,
                // A rough estimate for English and JSON text: about four characters per token.
                ["tokens"] = (characters + 3) / 4,
                ["ms"] = milliseconds,
                ["version"] = Version
            }.ToJsonString(FactsQueries.Compact) + "\n";
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (File.Exists(path) && new FileInfo(path).Length > CallLogLimit)
                        File.Move(path, Path.Combine(Path.GetDirectoryName(path)!, "calls.1.jsonl"), true);
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    var bytes = Encoding.UTF8.GetBytes(line);
                    stream.Write(bytes, 0, bytes.Length);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(20);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Counts what the command line writes, for the call log.
    private sealed class CountingWriter : TextWriter
    {
        private readonly TextWriter inner;
        public long Count { get; private set; }

        public CountingWriter(TextWriter inner) => this.inner = inner;

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            Count++;
            inner.Write(value);
        }

        public override void Write(string? value)
        {
            Count += value?.Length ?? 0;
            inner.Write(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            Count += count;
            inner.Write(buffer, index, count);
        }

        public override void WriteLine(string? value)
        {
            Count += (value?.Length ?? 0) + 1;
            inner.WriteLine(value);
        }

        public override void Flush() => inner.Flush();
    }

    private static Stopwatch StartCall() => Stopwatch.StartNew();
}
