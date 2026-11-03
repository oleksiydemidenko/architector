using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Architector.Editor
{
    [Serializable]
    public sealed class ArchitectorProjectDocument
    {
        public int schemaVersion = 1;
        public string projectId = string.Empty;
        public List<string> rootGroupIds = new();
        public List<string> rootLayerIds = new();
        public List<ArchitectorGroupRecord> groups = new();
        public List<ArchitectorLayerRecord> layers = new();
    }

    [Serializable]
    public sealed class ArchitectorGroupRecord
    {
        public string id = string.Empty;
        public string name = string.Empty;
        public string parentGroupId = string.Empty;
        public string orderKey = string.Empty;
        public ArchitectorGroupLockRecord lockState = new();
        public List<string> childGroupIds = new();
        public List<string> layerIds = new();
    }

    [Serializable]
    public sealed class ArchitectorLayerRecord
    {
        public string id = string.Empty;
        public string name = string.Empty;
        public string purpose = string.Empty;
        public ArchitectorLayerApprovalRecord approval = new();
        public string parentGroupId = string.Empty;
        public string orderKey = string.Empty;
        public ArchitectorLayerScopeRecord scope = new();
        public string semanticPath = string.Empty;
        public string layoutPath = string.Empty;
    }

    [Serializable]
    public sealed class ArchitectorGroupLockRecord
    {
        public bool locked;
        public string passwordHash = string.Empty;
        public string notice = string.Empty;
    }

    [Serializable]
    public sealed class ArchitectorLayerScopeRecord
    {
        public List<string> folders = new();
        public List<string> namespaces = new();
    }

    [Serializable]
    public sealed class ArchitectorSettingsDocument
    {
        public int schemaVersion = 1;
        public ArchitectorSourceIndexSettings sourceIndex = new();
        public ArchitectorGeometrySettings geometry = new();
        public ArchitectorOptimizationSettings optimization = new();
        public ArchitectorArrangeSettings arrange = new();
        public ArchitectorRuleSettings rules = new();
        public ArchitectorPaletteSettings palette = new();
        public ArchitectorAgentSettings agent = new();
    }

    [Serializable]
    public sealed class ArchitectorLayerApprovalRecord
    {
        public string semanticHash = string.Empty;
        public bool approvedByAgent;
        public ArchitectorLayerBaselineRecord baseline = new();
    }

    [Serializable]
    public sealed class ArchitectorLayerBaselineRecord
    {
        public string name = string.Empty;
        public string purpose = string.Empty;
        public List<ArchitectorNodeRecord> nodes = new();
        public List<ArchitectorNodeLayoutRecord> nodeLayouts = new();
        public List<ArchitectorEdgeRecord> relationships = new();
        public List<ArchitectorEdgeLayoutRecord> relationshipLayouts = new();
    }

    [Serializable]
    public sealed class ArchitectorAgentSettings
    {
        public List<ArchitectorAgentIntegrationRecord> integrations = new();
    }

    [Serializable]
    public sealed class ArchitectorAgentIntegrationRecord
    {
        public string id = string.Empty;
        public bool enabled = true;
    }

    [Serializable]
    public sealed class ArchitectorSourceIndexSettings
    {
        public List<string> includedRoots = new();
        public List<string> excludedRoots = new();
    }

    [Serializable]
    public sealed class ArchitectorGeometrySettings
    {
        public float minimumNodeGap = 48f;
        public float gridSize = 16f;
        public bool snapToGrid = true;
    }

    [Serializable]
    public sealed class ArchitectorRuleSettings
    {
        public ArchitectorDependencyCyclePolicy dependencyCycles;
    }

    [Serializable]
    public sealed class ArchitectorArrangeSettings
    {
        public int maxGroupRelationships = 16;
    }

    [Serializable]
    public sealed class ArchitectorOptimizationSettings
    {
        public int memberParentDepth;
        public bool includeInterfaceMembers;
        public bool includeFrameworkMembers;
        public bool hideRoutesWhileDragging = true;
        public bool routeInBackground = true;
        public ArchitectorRelationshipMerging relationshipMerging;
    }

    [Serializable]
    public sealed class ArchitectorPaletteSettings
    {
        public ArchitectorThemePaletteSettings dark = ArchitectorThemePaletteSettings.CreateDark();
        public ArchitectorThemePaletteSettings light = ArchitectorThemePaletteSettings.CreateLight();
    }

    [Serializable]
    public sealed class ArchitectorThemePaletteSettings
    {
        public ArchitectorColorRecord workspace = new();
        public ArchitectorColorRecord grid = new();
        public ArchitectorColorRecord node = new();
        public ArchitectorColorRecord nodeHeader = new(0f, 0f, 0f, -1f);
        public ArchitectorColorRecord nodeBorder = new();
        public ArchitectorColorRecord entryPortalOutline = new(0f, 0f, 0f, -1f);
        public ArchitectorColorRecord exitPortalOutline = new(0f, 0f, 0f, -1f);
        public ArchitectorColorRecord titleText = new();
        public ArchitectorColorRecord typeText = new();
        // Kept under its original serialized name so existing user palettes retain
        // their configured dependency color when typed relationship colors appear.
        public ArchitectorColorRecord edge = new();
        public ArchitectorColorRecord dataFlowEdge = new(0f, 0f, 0f, -1f);
        public ArchitectorColorRecord eventEdge = new(0f, 0f, 0f, -1f);
        public ArchitectorColorRecord callbackEdge = new(0f, 0f, 0f, -1f);
        public ArchitectorColorRecord selectedEdge = new();
        public ArchitectorColorRecord incomingEdgeHighlight = new();
        public ArchitectorColorRecord selection = new();

        public static ArchitectorThemePaletteSettings CreateDark()
        {
            return new ArchitectorThemePaletteSettings
            {
                workspace = new ArchitectorColorRecord(0.145f, 0.145f, 0.145f, 1f),
                grid = new ArchitectorColorRecord(1f, 1f, 1f, 0.055f),
                node = new ArchitectorColorRecord(0.20f, 0.20f, 0.20f, 0.96f),
                nodeHeader = new ArchitectorColorRecord(0.16f, 0.16f, 0.16f, 0.98f),
                nodeBorder = new ArchitectorColorRecord(0.31f, 0.31f, 0.31f, 1f),
                entryPortalOutline = new ArchitectorColorRecord(0.30f, 0.72f, 0.48f, 1f),
                exitPortalOutline = new ArchitectorColorRecord(0.87843138f, 0.52549022f, 0.25098041f, 1f),
                titleText = new ArchitectorColorRecord(1f, 1f, 1f, 1f),
                typeText = new ArchitectorColorRecord(0.65f, 0.65f, 0.65f, 1f),
                edge = new ArchitectorColorRecord(0.72f, 0.72f, 0.72f, 1f),
                dataFlowEdge = new ArchitectorColorRecord(0.32f, 0.72f, 0.72f, 1f),
                eventEdge = new ArchitectorColorRecord(0.90f, 0.65f, 0.25f, 1f),
                callbackEdge = new ArchitectorColorRecord(0.72f, 0.50f, 0.88f, 1f),
                selectedEdge = new ArchitectorColorRecord(0.11372549f, 0.51372552f, 0.82745099f, 1f),
                incomingEdgeHighlight = new ArchitectorColorRecord(0.82745099f, 0.82056099f, 0.11372548f, 0.50196081f),
                selection = new ArchitectorColorRecord(0.11372549f, 0.51372552f, 0.82745099f, 1f)
            };
        }

        public static ArchitectorThemePaletteSettings CreateLight()
        {
            return new ArchitectorThemePaletteSettings
            {
                workspace = new ArchitectorColorRecord(0.72f, 0.72f, 0.72f, 1f),
                grid = new ArchitectorColorRecord(0f, 0f, 0f, 0.09f),
                node = new ArchitectorColorRecord(0.82f, 0.82f, 0.82f, 0.98f),
                nodeHeader = new ArchitectorColorRecord(0.74f, 0.74f, 0.74f, 0.98f),
                nodeBorder = new ArchitectorColorRecord(0.52f, 0.52f, 0.52f, 1f),
                entryPortalOutline = new ArchitectorColorRecord(0.29803923f, 0.72156864f, 0.47843137f, 1f),
                exitPortalOutline = new ArchitectorColorRecord(0.87843138f, 0.52466226f, 0.25098044f, 1f),
                titleText = new ArchitectorColorRecord(0.16f, 0.16f, 0.16f, 1f),
                typeText = new ArchitectorColorRecord(0.32f, 0.32f, 0.32f, 1f),
                edge = new ArchitectorColorRecord(0.25f, 0.25f, 0.25f, 1f),
                dataFlowEdge = new ArchitectorColorRecord(0.05f, 0.42f, 0.48f, 1f),
                eventEdge = new ArchitectorColorRecord(0.68f, 0.36f, 0.06f, 1f),
                callbackEdge = new ArchitectorColorRecord(0.48f, 0.22f, 0.62f, 1f),
                selectedEdge = new ArchitectorColorRecord(0.11372549f, 0.51372552f, 0.82745099f, 1f),
                incomingEdgeHighlight = new ArchitectorColorRecord(0.82745099f, 0.64158475f, 0.11372548f, 0.50196081f),
                selection = new ArchitectorColorRecord(0.11372550f, 0.51372552f, 0.82745105f, 1f)
            };
        }
    }

    [Serializable]
    public sealed class ArchitectorColorRecord
    {
        public float r;
        public float g;
        public float b;
        public float a = 1f;

        public ArchitectorColorRecord()
        {
        }

        public ArchitectorColorRecord(float r, float g, float b, float a)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }

        public Color ToColor() => new(r, g, b, a);

        public void Set(Color color)
        {
            r = color.r;
            g = color.g;
            b = color.b;
            a = color.a;
        }
    }

    [Serializable]
    public sealed class ArchitectorLayerDocument
    {
        public int schemaVersion = 1;
        public string layerId = string.Empty;
        public List<ArchitectorNodeRecord> nodes = new();
        public List<ArchitectorEdgeRecord> edges = new();
    }

    [Serializable]
    public sealed class ArchitectorNodeRecord
    {
        public string id = string.Empty;
        public string title = string.Empty;
        public string responsibility = string.Empty;
        public string kind = ArchitectorNodeKinds.Standard;
        public ArchitectorTypeRecord type = new();
        public List<ArchitectorMemberRecord> members = new();
        public ArchitectorPortalRecord portal;
    }

    public static class ArchitectorNodeResponsibility
    {
        public const int MaxLength = 240;

        public static string Normalize(string value)
        {
            return string.Join(" ", (value ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0));
        }
    }

    public static class ArchitectorNodeKinds
    {
        public const string Standard = "standard";
        public const string LayerPortal = "layerPortal";
    }

    [Serializable]
    public sealed class ArchitectorPortalRecord
    {
        public string pairId = string.Empty;
        public string targetLayerId = string.Empty;
        public string counterpartNodeId = string.Empty;
        public string role = string.Empty;
    }

    public static class ArchitectorPortalRoles
    {
        public const string Entry = "entry";
        public const string Exit = "exit";
    }

    [Serializable]
    public sealed class ArchitectorTypeRecord
    {
        public string mode = "none";
        public string kind = string.Empty;
        public string typeNamespace = string.Empty;
        public string name = string.Empty;
        public List<string> genericParameters = new();
        public List<string> modifiers = new();
        public string assemblyName = string.Empty;
        public string metadataName = string.Empty;
        public List<ArchitectorSourceDeclarationRecord> sourceDeclarations = new();
        public ArchitectorTypeReferenceRecord baseType;
        public List<ArchitectorTypeReferenceRecord> interfaces = new();
    }

    [Serializable]
    public sealed class ArchitectorTypeReferenceRecord
    {
        public string mode = "declared";
        public string kind = string.Empty;
        public string typeNamespace = string.Empty;
        public string name = string.Empty;
        public string assemblyName = string.Empty;
        public string metadataName = string.Empty;
    }

    public static class ArchitectorTypeReference
    {
        public static string FullName(ArchitectorTypeReferenceRecord reference)
        {
            if (reference == null)
                return string.Empty;
            return string.IsNullOrEmpty(reference.typeNamespace)
                ? reference.name ?? string.Empty
                : $"{reference.typeNamespace}.{reference.name}";
        }

        public static string Key(ArchitectorTypeReferenceRecord reference)
        {
            if (reference == null)
                return string.Empty;
            return string.Equals(reference.mode, "linked", StringComparison.Ordinal)
                ? ArchitectorTypeIdentity.Key(reference.assemblyName, reference.metadataName)
                : $"declared:{FullName(reference)}";
        }

        public static ArchitectorTypeReferenceRecord Declared(string fullName, string kind)
        {
            fullName = fullName?.Trim() ?? string.Empty;
            var separator = fullName.LastIndexOf('.');
            return new ArchitectorTypeReferenceRecord
            {
                mode = "declared",
                kind = kind ?? string.Empty,
                typeNamespace = separator < 0 ? string.Empty : fullName.Substring(0, separator),
                name = separator < 0 ? fullName : fullName.Substring(separator + 1)
            };
        }
    }

    [Serializable]
    public sealed class ArchitectorSourceDeclarationRecord
    {
        public string scriptGuid = string.Empty;
        public string repositoryPath = string.Empty;
        public int line = -1;
        public int column = -1;
    }

    [Serializable]
    public sealed class ArchitectorMemberRecord
    {
        public string id = string.Empty;
        public string kind = string.Empty;
        public string declaration = string.Empty;
        public string symbolKey = string.Empty;
    }

    public static class ArchitectorMemberOrder
    {
        public static int Rank(string kind)
        {
            return kind switch
            {
                "constant" => 0,
                "event" => 1,
                "field" => 2,
                "property" => 3,
                "indexer" => 4,
                "constructor" => 5,
                "method" => 6,
                "operator" => 7,
                _ => int.MaxValue
            };
        }

        public static List<ArchitectorMemberRecord> SortStable(IEnumerable<ArchitectorMemberRecord> members)
        {
            return (members ?? Array.Empty<ArchitectorMemberRecord>())
                .Select((member, index) => (member, index))
                .OrderBy(item => Rank(item.member?.kind))
                .ThenBy(item => item.index)
                .Select(item => item.member)
                .ToList();
        }
    }

    public static class ArchitectorEdgeKinds
    {
        public const string Dependency = "dependency";
        public const string DataFlow = "dataFlow";
        public const string Event = "event";
        public const string Callback = "callback";

        public static readonly IReadOnlyList<string> All = new[]
        {
            Dependency,
            DataFlow,
            Event,
            Callback
        };

        public static string DisplayName(string kind)
        {
            return kind switch
            {
                Dependency => "Dependency",
                DataFlow => "Data Flow",
                Event => "Event",
                Callback => "Callback",
                _ => kind ?? string.Empty
            };
        }
    }

    [Serializable]
    public sealed class ArchitectorEdgeRecord
    {
        public string id = string.Empty;
        public string sourceLayerId = string.Empty;
        public string sourceNodeId = string.Empty;
        public string targetLayerId = string.Empty;
        public string targetNodeId = string.Empty;
        public string kind = ArchitectorEdgeKinds.Dependency;
        public string label = string.Empty;
    }

    public sealed class ArchitectorPortalRelationshipRow
    {
        public ArchitectorPortalRelationshipRow(ArchitectorEdgeRecord relationship, string caption)
        {
            Relationship = relationship ?? throw new ArgumentNullException(nameof(relationship));
            Caption = caption ?? string.Empty;
        }

        public ArchitectorEdgeRecord Relationship { get; }
        public string Caption { get; }
    }

    [Serializable]
    public sealed class ArchitectorLayerLayoutDocument
    {
        public int schemaVersion = 1;
        public string layerId = string.Empty;
        public List<ArchitectorNodeLayoutRecord> nodes = new();
        public List<ArchitectorEdgeLayoutRecord> edges = new();
        public int visibleRelationshipKinds = ArchitectorRelationshipVisibility.All;
    }

    [Serializable]
    public sealed class ArchitectorNodeLayoutRecord
    {
        public string nodeId = string.Empty;
        public float x;
        public float y;
        public float width = 280f;
        public float height = 120f;
    }

    [Serializable]
    public sealed class ArchitectorEdgeLayoutRecord
    {
        public string edgeId = string.Empty;
        public string preferredSourceSide = "auto";
        public string preferredTargetSide = "auto";
    }

    public static class ArchitectorRelationshipVisibility
    {
        public const int Dependency = 1;
        public const int DataFlow = 2;
        public const int Event = 4;
        public const int Callback = 8;
        public const int All = Dependency | DataFlow | Event | Callback;

        public static int Flag(string kind) => kind switch
        {
            ArchitectorEdgeKinds.Dependency => Dependency,
            ArchitectorEdgeKinds.DataFlow => DataFlow,
            ArchitectorEdgeKinds.Event => Event,
            ArchitectorEdgeKinds.Callback => Callback,
            _ => 0
        };
    }

    [Serializable]
    public sealed class ArchitectorUserStateDocument
    {
        public int schemaVersion = 1;
        public List<ArchitectorLayerViewStateRecord> layerViews = new();
        public List<ArchitectorLayerInspectorStateRecord> layerInspectors = new();
        public ArchitectorWindowNavigationRecord masterNavigation = new();
    }

    [Serializable]
    public sealed class ArchitectorWindowNavigationRecord
    {
        public string currentGroupId = string.Empty;
        public string activeLayerId = string.Empty;
        public List<string> openLayerIds = new();
        public List<string> layerVisitHistory = new();
        public int layerVisitHistoryIndex = -1;
        public float layerTabsScroll;
    }

    [Serializable]
    public sealed class ArchitectorLayerViewStateRecord
    {
        public string viewId = string.Empty;
        public string layerId = string.Empty;
        public float panX = 48f;
        public float panY = 48f;
        public float zoom = 1f;
    }

    [Serializable]
    public sealed class ArchitectorLayerInspectorStateRecord
    {
        public string viewId = string.Empty;
        public string layerId = string.Empty;
        public bool conformanceExpanded;
    }
}
