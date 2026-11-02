using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;

namespace Architector.Editor
{
    public static class ArchitectorAgentIntegrationStates
    {
        public const string Unavailable = "Unavailable";
        public const string Connected = "Connected";
        public const string Active = "Active";
        public const string Disabled = "Disabled";
    }

    public sealed class ArchitectorAgentIntegrationDescriptor
    {
        public string id = string.Empty;
        public string displayName = string.Empty;
        public string packageName = string.Empty;
        public string documentationUrl = string.Empty;
        public string installUrl = string.Empty;
        public string[] tags = { "MCP" };
        public IArchitectorIntegrationUpdateProvider updates;
        public bool builtIn;
    }

    public readonly struct ArchitectorAgentIntegrationProbeResult
    {
        public ArchitectorAgentIntegrationProbeResult(
            string state,
            string packageVersion,
            string details,
            int port = 0)
        {
            State = state;
            PackageVersion = packageVersion ?? string.Empty;
            Details = details ?? string.Empty;
            Port = port;
        }

        public string State { get; }
        public string PackageVersion { get; }
        public string Details { get; }
        public int Port { get; }
    }

    public interface IArchitectorAgentIntegrationProbe
    {
        ArchitectorAgentIntegrationDescriptor Descriptor { get; }
        ArchitectorAgentIntegrationProbeResult Probe(string packageVersion);
        void OpenSetup();
    }

    public static class ArchitectorAgentIntegrationRegistry
    {
        public const string BuiltInId = "architector-mcp";

        public static readonly ArchitectorAgentIntegrationDescriptor CoplayUnityMcp = new()
        {
            id = "coplay-unity-mcp",
            displayName = "MCP for Unity by Coplay",
            packageName = "com.coplaydev.unity-mcp",
            documentationUrl = "https://github.com/CoplayDev/unity-mcp",
            installUrl = "https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#v10.3.0"
        };

        public static readonly ArchitectorAgentIntegrationDescriptor IvanMurzakUnityMcp = new()
        {
            id = "ivanmurzak-unity-mcp",
            displayName = "Unity MCP by Ivan Murzak",
            packageName = "com.ivanmurzak.unity.mcp",
            documentationUrl = "https://github.com/IvanMurzak/Unity-MCP"
        };

        private static IReadOnlyList<ArchitectorAgentIntegrationDescriptor> descriptors;

        public static IReadOnlyList<ArchitectorAgentIntegrationDescriptor> Descriptors => descriptors ??= Discover();

        public static bool IsAdded(ArchitectorAgentSettings agent, ArchitectorAgentIntegrationDescriptor descriptor)
        {
            return descriptor.builtIn
                   || agent.integrations.Any(record => string.Equals(record.id, descriptor.id, StringComparison.Ordinal));
        }

        private static IReadOnlyList<ArchitectorAgentIntegrationDescriptor> Discover()
        {
            var known = new List<ArchitectorAgentIntegrationDescriptor> { CoplayUnityMcp, IvanMurzakUnityMcp };
            foreach (var probe in Probes())
            {
                var descriptor = probe.Descriptor;
                if (descriptor != null && known.All(candidate => candidate.id != descriptor.id))
                    known.Add(descriptor);
            }
            return known.OrderByDescending(descriptor => descriptor.builtIn).ToList();
        }

        public static ArchitectorAgentIntegrationProbeResult Probe(
            ArchitectorAgentIntegrationDescriptor descriptor,
            bool enabled)
        {
            if (!enabled)
                return new ArchitectorAgentIntegrationProbeResult(
                    ArchitectorAgentIntegrationStates.Disabled, string.Empty, "Disabled in Architector.");
            var package = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                .FirstOrDefault(candidate => string.Equals(candidate.name, descriptor.packageName, StringComparison.Ordinal));
            if (package == null)
                return new ArchitectorAgentIntegrationProbeResult(
                    ArchitectorAgentIntegrationStates.Unavailable, string.Empty, $"Package '{descriptor.packageName}' is not installed.");
            var adapter = FindProbe(descriptor.id);
            if (adapter == null)
                return new ArchitectorAgentIntegrationProbeResult(
                    ArchitectorAgentIntegrationStates.Unavailable, package.version, "The Architector adapter is not available for this package version.");
            try
            {
                return adapter.Probe(package.version);
            }
            catch (Exception exception)
            {
                return new ArchitectorAgentIntegrationProbeResult(
                    ArchitectorAgentIntegrationStates.Unavailable, package.version, exception.Message);
            }
        }

        public static void OpenSetup(ArchitectorAgentIntegrationDescriptor descriptor)
        {
            var adapter = FindProbe(descriptor.id);
            if (adapter != null)
            {
                adapter.OpenSetup();
                return;
            }
            if (!string.IsNullOrEmpty(descriptor.documentationUrl))
                UnityEngine.Application.OpenURL(descriptor.documentationUrl);
        }

        private static IArchitectorAgentIntegrationProbe FindProbe(string integrationId)
        {
            return Probes().FirstOrDefault(probe =>
                string.Equals(probe.Descriptor?.id, integrationId, StringComparison.Ordinal));
        }

        private static IEnumerable<IArchitectorAgentIntegrationProbe> Probes()
        {
            return TypeCache.GetTypesDerivedFrom<IArchitectorAgentIntegrationProbe>()
                .Where(type => !type.IsAbstract && !type.IsInterface)
                .Select(type => (IArchitectorAgentIntegrationProbe)Activator.CreateInstance(type));
        }
    }
}
