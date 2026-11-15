using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor.Agents
{
    // One loopback listener serves both built-in integrations. Each request names its client: the command line sends
    // "cli", the MCP server "mcp"; a request without a client comes from an older binary and counts as the MCP server.
    // The listener runs while either integration is on, and each client is answered only while its own one is on.
    [InitializeOnLoad]
    public static class ArchitectorAgentBridge
    {
        public const string CliClient = "cli";
        public const string McpClient = "mcp";
        private const int RequestTimeoutMilliseconds = 120000;
        private static readonly ConcurrentQueue<Action> Pending = new();
        private static TcpListener listener;
        private static bool started;
        private static string token;
        private static string writtenClients;

        static ArchitectorAgentBridge()
        {
            if (Application.isBatchMode)
                return;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
            EditorApplication.update += Tick;
        }

        public static int Port { get; private set; }
        private static string StatePath => Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "Architector", "agent-bridge.json");

        public static string IntegrationFor(string client)
        {
            return string.Equals(client, CliClient, StringComparison.Ordinal)
                ? ArchitectorAgentIntegrationRegistry.CliId
                : ArchitectorAgentIntegrationRegistry.McpServerId;
        }

        public static IReadOnlyList<string> ServedClients(ArchitectorRepository repository)
        {
            var clients = new List<string>();
            if (repository.IsAgentIntegrationEnabled(ArchitectorAgentIntegrationRegistry.CliId))
                clients.Add(CliClient);
            if (repository.IsAgentIntegrationEnabled(ArchitectorAgentIntegrationRegistry.McpServerId))
                clients.Add(McpClient);
            return clients;
        }

        public static bool ShouldRun(ArchitectorRepository repository) => ServedClients(repository).Count > 0;

        // Called on the first Editor tick and after every repository change: a settings switch starts or stops the
        // listener at once, and the state file always names the clients the bridge serves.
        private static void Sync()
        {
            try
            {
                SyncListener();
            }
            catch (Exception exception) when (exception is SocketException or IOException or UnauthorizedAccessException)
            {
                Debug.LogException(exception);
            }
        }

        private static void SyncListener()
        {
            var clients = ServedClients(ArchitectorSession.Instance.Repository);
            if (clients.Count == 0)
            {
                if (listener != null)
                    Stop();
                return;
            }
            if (listener == null)
            {
                token = NewToken();
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                new Thread(() => Accept(listener)) { IsBackground = true, Name = "Architector agent bridge" }.Start();
                writtenClients = null;
            }
            var names = string.Join(",", clients);
            if (names == writtenClients)
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
            File.WriteAllText(StatePath, new JObject { ["port"] = Port, ["token"] = token, ["clients"] = new JArray(clients) }.ToString());
            writtenClients = names;
        }

        private static void Stop()
        {
            listener?.Stop();
            listener = null;
            Port = 0;
            writtenClients = null;
            if (File.Exists(StatePath))
                File.Delete(StatePath);
        }

        private static void Accept(TcpListener server)
        {
            try
            {
                while (true)
                {
                    var client = server.AcceptTcpClient();
                    new Thread(() => Serve(client)) { IsBackground = true }.Start();
                }
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static void Serve(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
            {
                var line = reader.ReadLine();
                if (line != null)
                    writer.WriteLine(Answer(line));
            }
        }

        private static string Answer(string line)
        {
            JObject request;
            try
            {
                request = JObject.Parse(line);
            }
            catch (Exception exception)
            {
                return ArchitectorAgentResult.Fail("invalid_parameters", new { message = exception.Message }).ToJson();
            }
            if ((string)request["token"] != token)
                return ArchitectorAgentResult.Fail("unauthorized").ToJson();
            var tool = (string)request["tool"];
            var integrationId = IntegrationFor((string)request["client"]);
            var parameters = request["parameters"] as JObject ?? new JObject();
            // The command line gives a short claim time: a request the main thread does not start in it is cancelled
            // here and never runs, and the command line answers it from the files.
            var claimMilliseconds = (int?)request["claim_seconds"] is { } claimSeconds && claimSeconds > 0
                ? Math.Min(claimSeconds * 1000, RequestTimeoutMilliseconds)
                : RequestTimeoutMilliseconds;
            var completion = new TaskCompletionSource<string>();
            var claimed = 0;
            Pending.Enqueue(() =>
            {
                if (Interlocked.Exchange(ref claimed, 1) == 0)
                    completion.SetResult(Run(tool, parameters, integrationId));
            });
            if (completion.Task.Wait(claimMilliseconds))
                return completion.Task.Result;
            if (Interlocked.Exchange(ref claimed, 1) == 0)
                return ArchitectorAgentResult.Fail("editor_timeout", new { message = "The Unity Editor did not start this request, and it was cancelled. It may be compiling or paused without Run In Background." }).ToJson();
            return completion.Task.Result;
        }

        private static string Run(string tool, JObject parameters, string integrationId)
        {
            try
            {
                return tool switch
                {
                    "query" => ArchitectorAgentApi.Query(parameters, integrationId).ToJson(),
                    "edit" => ArchitectorAgentApi.Edit(parameters, integrationId).ToJson(),
                    _ => ArchitectorAgentResult.Fail("unsupported_tool").ToJson()
                };
            }
            catch (Exception exception)
            {
                return ArchitectorAgentResult.Fail("bridge_failed", new { message = exception.Message }).ToJson();
            }
        }

        private static void Tick()
        {
            if (!started)
            {
                started = true;
                ArchitectorSession.Instance.Repository.Changed += Sync;
                Sync();
            }
            while (Pending.TryDequeue(out var action))
                action();
        }

        private static string NewToken()
        {
            var bytes = new byte[24];
            using var random = RandomNumberGenerator.Create();
            random.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }
    }
}
