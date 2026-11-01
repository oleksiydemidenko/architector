using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor
{
    public sealed partial class ArchitectorLayerCanvas
    {
        private ArchitectorRelationshipMerging relationshipMerging;
        private ArchitectorRouteNetwork edgeNetwork;
        private ArchitectorRouteNetwork edgeBaseNetwork;
        private Dictionary<string, List<Vector2>> edgeStrokeWorldRoutes;
        private readonly Dictionary<string, List<Vector2>> edgeStrokeCache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ArchitectorRouteStroke>> strokesByEdge = new(StringComparer.Ordinal);
        private readonly List<(ArchitectorRouteStroke stroke, ArchitectorEdgeRecord edge, int tier)> visibleStrokes = new();
        private readonly Dictionary<string, ArchitectorEdgeRecord> mergedEdgesById = new(StringComparer.Ordinal);

        public IReadOnlyList<string> SelectedTrunkEdges
        {
            get
            {
                if (string.IsNullOrEmpty(selectedEdgeId) || edgeNetwork == null || !strokesByEdge.TryGetValue(selectedEdgeId, out var strokes))
                    return Array.Empty<string>();
                return strokes.OrderByDescending(stroke => stroke.EdgeIds.Length).FirstOrDefault()?.EdgeIds ?? Array.Empty<string>();
            }
        }

        private void SetRouteNetwork(ArchitectorRouteNetwork network, Dictionary<string, List<Vector2>> drawn)
        {
            edgeNetwork = network;
            edgeWorldRoutes = network.Routes;
            edgeStrokeWorldRoutes = drawn;
            edgeRouteCache.Clear();
            edgeStrokeCache.Clear();
            strokesByEdge.Clear();
            foreach (var stroke in network.Strokes)
            {
                foreach (var id in stroke.EdgeIds)
                {
                    if (!strokesByEdge.TryGetValue(id, out var strokes))
                    {
                        strokes = new List<ArchitectorRouteStroke>();
                        strokesByEdge.Add(id, strokes);
                    }
                    strokes.Add(stroke);
                }
            }
        }

        private void CacheStrokeScreenRoutes()
        {
            if (edgeNetwork == null)
                return;
            foreach (var pair in edgeStrokeWorldRoutes)
            {
                if (!edgeStrokeCache.TryGetValue(pair.Key, out var route))
                {
                    route = new List<Vector2>(pair.Value.Count);
                    edgeStrokeCache.Add(pair.Key, route);
                }
                route.Clear();
                foreach (var point in pair.Value)
                    route.Add(WorldToScreen(point));
            }
        }

        private float MergedHitDistance(string edgeId, Vector2 mouse)
        {
            var distance = float.MaxValue;
            if (!strokesByEdge.TryGetValue(edgeId, out var strokes))
                return distance;
            foreach (var stroke in strokes)
                if (edgeStrokeCache.TryGetValue(stroke.Id, out var route))
                    for (var index = 1; index < route.Count; index++)
                        distance = Mathf.Min(distance, DistanceToSegment(mouse, route[index - 1], route[index]));
            return distance;
        }

        private bool IsMergedJunctionVisible(ArchitectorRouteStroke stroke)
        {
            if (!stroke.JunctionAtStart)
                return false;
            if (hiddenRouteNodeIds.Count == 0)
                return true;
            return edgeNetwork.Strokes.Count(branch => branch.Points.Last() == stroke.Points[0]
                && branch.EdgeIds.Any(id => Array.IndexOf(stroke.EdgeIds, id) >= 0
                    && mergedEdgesById.TryGetValue(id, out var edge) && !IsRouteHidden(edge))) > 1;
        }

        private void DrawMergedEdges(ArchitectorLayerDocuments documents, ArchitectorThemePaletteSettings palette, Rect viewRect)
        {
            visibleStrokes.Clear();
            mergedEdgesById.Clear();
            foreach (var edge in documents.semantic.edges)
                mergedEdgesById[edge.id] = edge;
            foreach (var stroke in edgeNetwork.Strokes)
            {
                ArchitectorEdgeRecord representative = null;
                var tier = -1;
                foreach (var id in stroke.EdgeIds)
                {
                    if (!mergedEdgesById.TryGetValue(id, out var edge) || IsRouteHidden(edge))
                        continue;
                    var nextTier = EdgeTier(edge);
                    if (nextTier <= tier)
                        continue;
                    tier = nextTier;
                    representative = edge;
                }
                if (representative != null && edgeStrokeCache.TryGetValue(stroke.Id, out var route) && route.Count >= 2 && RouteIsVisible(route, viewRect))
                    visibleStrokes.Add((stroke, representative, tier));
            }
            var hasSelection = selectedNodeIds.Count > 0 || !string.IsNullOrEmpty(selectedEdgeId);
            Handles.BeginGUI();
            for (var tier = EdgeTierUnrelated; tier <= EdgeTierSelected; tier++)
            {
                foreach (var item in visibleStrokes)
                {
                    if (item.tier != tier)
                        continue;
                    var stroke = item.stroke;
                    var route = edgeStrokeCache[stroke.Id];
                    var width = (tier == EdgeTierSelected ? 3f : tier == EdgeTierUnrelated ? 1.75f : 2.5f) + (stroke.EdgeIds.Length > 1 ? 1.5f : 0f);
                    var arrowLength = (tier == EdgeTierUnrelated ? 9f : 12f) + (stroke.EdgeIds.Length > 1 ? 2f : 0f);
                    var filled = stroke.Kind == ArchitectorEdgeKinds.Dependency;
                    var count = FillRouteBuffer(route, stroke.Arrow && filled ? arrowLength * 0.8f : 0f);
                    if (tier != EdgeTierUnrelated)
                    {
                        Handles.color = palette.workspace.ToColor();
                        Handles.DrawAAPolyLine(width + 3f, count, routeBuffer);
                    }
                    var color = EdgeDrawColor(item.edge, palette, tier, hasSelection);
                    Handles.color = color;
                    DrawEdgeRoute(count, stroke.Kind, width);
                    if (IsMergedJunctionVisible(stroke))
                        Handles.DrawSolidDisc(route[0], Vector3.forward, 3f);
                    if (stroke.Arrow)
                        DrawArrowHead(route[route.Count - 2], route[route.Count - 1], color, width, arrowLength, filled);
                }
            }
            Handles.color = Color.white;
            Handles.EndGUI();
        }
    }
}
