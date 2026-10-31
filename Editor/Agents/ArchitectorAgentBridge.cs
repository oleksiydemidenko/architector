using System;
using System.Collections.Concurrent;
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
    [InitializeOnLoad]
    public static class ArchitectorAgentBridge
    {
        public const string IntegrationId = "architector-mcp";
        private const int RequestTimeoutMilliseconds = 120000;
        private static readonly ConcurrentQueue<Action> Pending = new();
        private static TcpListener listener;
        private static bool started;
        private static string token;

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

        private static void Start()
        {
            if (!ArchitectorSession.Instance.Repository.IsAgentIntegrationEnabled(IntegrationId))
                return;
            token = NewToken();
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
            File.WriteAllText(StatePath, new JObject { ["port"] = Port, ["token"] = token }.ToString());
            new Thread(() => Accept(listener)) { IsBackground = true, Name = "Architector agent bridge" }.Start();
        }

        private static void Stop()
        {
            listener?.Stop();
            listener = null;
            Port = 0;
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
            var parameters = request["parameters"] as JObject ?? new JObject();
            var completion = new TaskCompletionSource<string>();
            Pending.Enqueue(() => completion.SetResult(Run(tool, parameters)));
            return completion.Task.Wait(RequestTimeoutMilliseconds)
                ? completion.Task.Result
                : ArchitectorAgentResult.Fail("editor_timeout", new { message = "The Unity Editor did not answer. It may be compiling, showing a dialog, or paused without Run In Background." }).ToJson();
        }

        private static string Run(string tool, JObject parameters)
        {
            try
            {
                return tool switch
                {
                    "query" => ArchitectorAgentApi.Query(parameters, IntegrationId).ToJson(),
                    "edit" => ArchitectorAgentApi.Edit(parameters, IntegrationId).ToJson(),
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
                Start();
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
