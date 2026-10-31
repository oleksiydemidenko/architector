using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor
{
    public sealed class ArchitectorLayerCanvas
    {
        private const float MinimumZoom = 0.4f;
        private const float MaximumZoom = 2f;
        private const float MemberVisibilityZoom = 0.72f;
        private const float CompactTitleZoom = 0.62f;
        private const float DragMovementThresholdSquared = 0.0001f;
        private static readonly int BoxSelectionControlHint = "ArchitectorBoxSelection".GetHashCode();
        private static readonly int PanControlHint = "ArchitectorPan".GetHashCode();
        private const float RightButtonPanThresholdSquared = 16f;

        private readonly string viewId;
        private string layerId = string.Empty;
        private readonly HashSet<string> selectedNodeIds = new(StringComparer.Ordinal);
        private string selectedEdgeId = string.Empty;
        private Vector2 selectedEdgeActionWorldPosition;
        private bool showSelectedEdgeDeleteAction;
        private Vector2 lastEdgeCyclePosition = new(float.PositiveInfinity, float.PositiveInfinity);
        private List<string> lastEdgeCycleIds = new();
        private int lastEdgeCycleIndex = -1;
        private readonly Dictionary<string, List<Vector2>> edgeRouteCache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, float> sourceLaneOffsetByEdgeId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, float> targetLaneOffsetByEdgeId = new(StringComparer.Ordinal);
        private int edgeLaneCacheRevision = -1;
        private int edgeRouteCacheRevision = -1;
        private Vector2 edgeRouteCachePan;
        private float edgeRouteCacheZoom = -1f;
        private int currentRepositoryRevision;
        private bool highQualityDragRouting;
        private bool gridRouting;
        private float routingGridSize = 75f;
        private bool dragPreviewOverlapsNode;
        private Vector2 pan = new(48f, 48f);
        private float zoom = 1f;
        private bool draggingNode;
        private Vector2 dragStartMouse;
        private readonly Dictionary<string, Vector2> dragStartWorldByNode = new(StringComparer.Ordinal);
        private Vector2 dragPreviewDelta;
        private bool boxSelecting;
        private Vector2 boxStart;
        private Vector2 boxCurrent;
        private HashSet<string> boxInitialSelection = new(StringComparer.Ordinal);
        private bool panning;
        private bool rightButtonDown;
        private bool rightButtonPanned;
        private bool suppressContextClick;
        private bool contextClickDeferred;
        private Vector2 rightButtonStart;
        private ArchitectorLayerDocuments cachedDocuments;
        private bool hasCachedDocuments;
        private int cachedDocumentsRevision = -1;
        private IReadOnlyDictionary<string, IReadOnlyList<ArchitectorPortalRelationshipRow>> cachedPortalRelationshipRows =
            new Dictionary<string, IReadOnlyList<ArchitectorPortalRelationshipRow>>(StringComparer.Ordinal);
        private int cachedPortalRelationshipRowsRevision = -1;
        private string lastVisibilitySelectionKey = string.Empty;
        private bool selectionVisibilityPending;
        private bool selectionVisibilitySawLayout;
        private GUIStyle centeredBoldLabel;
        private GUIStyle centeredMiniBoldLabel;
        private GUIStyle responsibilityStyle;

        public ArchitectorLayerCanvas(string viewId)
        {
            this.viewId = viewId;
        }

        public string SelectedNodeId => selectedNodeIds.Count == 1 ? selectedNodeIds.First() : string.Empty;
        public string SelectedEdgeId => selectedEdgeId;
        public int SelectionCount => selectedNodeIds.Count;
        public bool IsBoxSelecting => boxSelecting;

        public void SelectOnly(string nodeId)
        {
            selectedEdgeId = string.Empty;
            showSelectedEdgeDeleteAction = false;
            selectedNodeIds.Clear();
            if (!string.IsNullOrEmpty(nodeId))
                selectedNodeIds.Add(nodeId);
        }

        public void Draw(
            Rect rect,
            ArchitectorLayerRecord layer,
            ArchitectorRepository repository,
            ArchitectorTypeIndex typeIndex,
            ArchitectorUserStateStore userState,
            ArchitectorNodeClipboard clipboard,
            Action<Action> execute,
            Action requestRepaint,
            Action<GenericMenu, Vector2> requestContextMenu,
            Action<string> openLayer,
            bool interactive = true)
        {
            if (layer == null || rect.width <= 1f || rect.height <= 1f)
                return;
            if (layerId != layer.id)
                ResetForLayer(layer.id, userState);

            currentRepositoryRevision = repository.Revision;
            if (!hasCachedDocuments || cachedDocumentsRevision != currentRepositoryRevision)
            {
                cachedDocuments = repository.LoadLayerView(layer.id);
                hasCachedDocuments = true;
                cachedDocumentsRevision = currentRepositoryRevision;
            }
            var documents = cachedDocuments;
            if (cachedPortalRelationshipRowsRevision != currentRepositoryRevision)
            {
                cachedPortalRelationshipRows = repository.GetLayerPortalRelationshipRows(layer.id);
                cachedPortalRelationshipRowsRevision = currentRepositoryRevision;
            }
            highQualityDragRouting = repository.Settings.optimization.highQualityDragRouting;
            gridRouting = repository.Settings.optimization.gridRouting;
            routingGridSize = repository.Settings.geometry.gridSize;
            var palette = EditorGUIUtility.isProSkin
                ? repository.Settings.palette.dark
                : repository.Settings.palette.light;
            var validNodeIds = new HashSet<string>(documents.semantic.nodes.Select(node => node.id), StringComparer.Ordinal);
            selectedNodeIds.RemoveWhere(nodeId => !validNodeIds.Contains(nodeId));
            if (!string.IsNullOrEmpty(selectedEdgeId) && documents.semantic.edges.All(edge => edge.id != selectedEdgeId))
            {
                selectedEdgeId = string.Empty;
                showSelectedEdgeDeleteAction = false;
            }
            UpdateSelectionVisibilityRequest();
            if (selectionVisibilityPending && Event.current.type == EventType.Layout)
            {
                selectionVisibilitySawLayout = true;
            }
            else if (selectionVisibilityPending
                     && selectionVisibilitySawLayout
                     && Event.current.type == EventType.Repaint
                     && !draggingNode
                     && !boxSelecting)
            {
                EnsureSelectedObjectVisible(rect.size, documents, userState);
                selectionVisibilityPending = false;
                selectionVisibilitySawLayout = false;
            }

            GUI.BeginGroup(rect);
            var localRect = new Rect(Vector2.zero, rect.size);
            if (interactive)
                HandleInput(localRect, documents, repository, typeIndex, userState, clipboard, execute, requestRepaint, requestContextMenu, openLayer);
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(localRect, palette.workspace.ToColor());
                DrawGrid(localRect, repository.Settings.geometry.gridSize, palette.grid.ToColor());
                dragPreviewOverlapsNode = highQualityDragRouting && HasDragMovement && DoesDragPreviewOverlapNode(documents.layout);
                PrepareEdgeAnchorLanes(documents);
                DrawEdges(documents, palette);
                DrawNodes(documents, palette, repository);
                DrawConnectionAffordance(documents);
                DrawSelectedEdgeAction(documents);
                DrawSelectionMarquee(palette.selection.ToColor());

                if (documents.semantic.nodes.Count == 0)
                {
                    var hintRect = new Rect(0f, localRect.height * 0.5f - 20f, localRect.width, 40f);
                    GUI.Label(hintRect, "Right-click empty space to create a node", EditorStyles.centeredGreyMiniLabel);
                }
            }
            GUI.EndGroup();
        }

        public void CancelInteraction()
        {
            draggingNode = false;
            dragStartWorldByNode.Clear();
            dragPreviewDelta = Vector2.zero;
            boxSelecting = false;
            boxInitialSelection.Clear();
            panning = false;
            rightButtonDown = false;
            rightButtonPanned = false;
            contextClickDeferred = false;
            if (GUIUtility.hotControl != 0)
                GUIUtility.hotControl = 0;
        }

        private void HandleInput(
            Rect canvasRect,
            ArchitectorLayerDocuments documents,
            ArchitectorRepository repository,
            ArchitectorTypeIndex typeIndex,
            ArchitectorUserStateStore userState,
            ArchitectorNodeClipboard clipboard,
            Action<Action> execute,
            Action requestRepaint,
            Action<GenericMenu, Vector2> requestContextMenu,
            Action<string> openLayer)
        {
            var current = Event.current;
            var mouse = current.mousePosition;
            var boxSelectionControlId = GUIUtility.GetControlID(BoxSelectionControlHint, FocusType.Passive);
            var panControlId = GUIUtility.GetControlID(PanControlHint, FocusType.Passive);
            if (panning && current.rawType == EventType.MouseUp && current.button == 2)
            {
                panning = false;
                if (GUIUtility.hotControl == panControlId)
                    GUIUtility.hotControl = 0;
                userState.SaveLayerView(viewId, layerId, pan.x, pan.y, zoom);
                requestRepaint?.Invoke();
                current.Use();
                return;
            }
            var contextFromRelease = false;
            if (rightButtonDown && current.rawType == EventType.MouseUp && current.button == 1)
            {
                rightButtonDown = false;
                contextFromRelease = contextClickDeferred && !rightButtonPanned;
                contextClickDeferred = false;
                if (GUIUtility.hotControl == panControlId)
                    GUIUtility.hotControl = 0;
                if (rightButtonPanned)
                {
                    rightButtonPanned = false;
                    suppressContextClick = true;
                    userState.SaveLayerView(viewId, layerId, pan.x, pan.y, zoom);
                    requestRepaint?.Invoke();
                    current.Use();
                    return;
                }
            }
            if (rightButtonDown && current.type == EventType.MouseDrag && current.button == 1)
            {
                if (!rightButtonPanned && (mouse - rightButtonStart).sqrMagnitude < RightButtonPanThresholdSquared)
                {
                    current.Use();
                    return;
                }
                pan += rightButtonPanned ? current.delta : mouse - rightButtonStart;
                rightButtonPanned = true;
                requestRepaint?.Invoke();
                current.Use();
                return;
            }
            if (current.type == EventType.ContextClick && rightButtonDown)
            {
                contextClickDeferred = true;
                current.Use();
                return;
            }
            if (current.type == EventType.ContextClick && suppressContextClick)
            {
                suppressContextClick = false;
                current.Use();
                return;
            }
            if (boxSelecting && current.rawType == EventType.MouseUp && current.button == 0)
            {
                boxCurrent = ClampToRect(mouse, canvasRect);
                UpdateBoxSelection(documents.layout);
                EndBoxSelection(boxSelectionControlId, requestRepaint);
                current.Use();
                return;
            }
            if (boxSelecting && current.type == EventType.MouseDrag && current.button == 0)
            {
                boxCurrent = ClampToRect(mouse, canvasRect);
                UpdateBoxSelection(documents.layout);
                requestRepaint?.Invoke();
                current.Use();
                return;
            }
            if (draggingNode && current.rawType == EventType.MouseUp && current.button == 0)
            {
                CompleteNodeDrag(repository, execute);
                current.Use();
                return;
            }
            if (!canvasRect.Contains(mouse))
                return;

            if (current.type == EventType.KeyDown && (current.control || current.command))
            {
                if (current.keyCode == KeyCode.C && selectedNodeIds.Count > 0)
                {
                    var copiedIds = selectedNodeIds.ToArray();
                    execute(() => clipboard.Copy(repository, layerId, copiedIds));
                    current.Use();
                    return;
                }
                if (current.keyCode == KeyCode.V && clipboard.HasContent)
                {
                    PasteAndSelect(repository, clipboard, execute, ScreenToWorld(mouse));
                    current.Use();
                    return;
                }
                if (current.keyCode == KeyCode.D && selectedNodeIds.Count > 0)
                {
                    DuplicateAndSelect(repository, execute, selectedNodeIds.ToArray());
                    current.Use();
                    return;
                }
            }

            if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Delete && !string.IsNullOrEmpty(selectedEdgeId))
            {
                var edgeId = selectedEdgeId;
                execute(() => repository.DeleteEdge(layerId, edgeId));
                selectedEdgeId = string.Empty;
                current.Use();
                return;
            }

            if (current.type == EventType.ScrollWheel)
            {
                var worldAtMouse = ScreenToWorld(mouse);
                zoom = Mathf.Clamp(zoom * (1f - current.delta.y * 0.03f), MinimumZoom, MaximumZoom);
                pan = mouse - worldAtMouse * zoom;
                userState.SaveLayerView(viewId, layerId, pan.x, pan.y, zoom);
                current.Use();
                return;
            }

            if (current.type == EventType.MouseDown && current.button == 1)
            {
                rightButtonDown = true;
                rightButtonPanned = false;
                suppressContextClick = false;
                contextClickDeferred = false;
                rightButtonStart = mouse;
                GUIUtility.hotControl = panControlId;
                return;
            }
            if (current.type == EventType.MouseDown && current.button == 2)
            {
                panning = true;
                GUIUtility.hotControl = panControlId;
                current.Use();
                return;
            }
            if (current.type == EventType.MouseDrag && current.button == 2 && panning)
            {
                pan += current.delta;
                requestRepaint?.Invoke();
                current.Use();
                return;
            }

            var hitNodeId = HitTestNode(mouse, documents.layout);
            if (current.type == EventType.MouseDown && current.button == 0)
            {
                var portalRelationshipId = HitTestPortalRelationshipRow(mouse, documents);
                if (!string.IsNullOrEmpty(portalRelationshipId))
                {
                    selectedNodeIds.Clear();
                    selectedEdgeId = portalRelationshipId;
                    selectedEdgeActionWorldPosition = ScreenToWorld(mouse);
                    showSelectedEdgeDeleteAction = false;
                    current.Use();
                    return;
                }
                if (current.clickCount >= 2 && !string.IsNullOrEmpty(hitNodeId))
                {
                    var portal = documents.semantic.nodes.FirstOrDefault(node =>
                        node.id == hitNodeId && IsPortal(node));
                    if (portal != null)
                    {
                        SelectOnly(portal.id);
                        openLayer?.Invoke(portal.portal.targetLayerId);
                        current.Use();
                        return;
                    }
                }

                if (!string.IsNullOrEmpty(selectedEdgeId)
                    && showSelectedEdgeDeleteAction
                    && SelectedEdgeDeleteRect(documents).Contains(mouse))
                {
                    var edgeId = selectedEdgeId;
                    execute(() => repository.DeleteEdge(layerId, edgeId));
                    selectedEdgeId = string.Empty;
                    current.Use();
                    return;
                }

                var affordance = GetConnectionAffordance(mouse, documents);
                if (affordance.visible && affordance.buttonRect.Contains(mouse))
                {
                    ShowRelationshipKindMenu(
                        repository,
                        execute,
                        requestContextMenu,
                        SelectedNodeId,
                        affordance.targetNodeId,
                        affordance.targetSide,
                        mouse);
                    selectedEdgeId = string.Empty;
                    current.Use();
                    return;
                }

                var hitEdgeId = HitTestEdge(mouse, documents);
                if (!string.IsNullOrEmpty(hitEdgeId) && string.IsNullOrEmpty(hitNodeId))
                {
                    selectedNodeIds.Clear();
                    selectedEdgeId = hitEdgeId;
                    selectedEdgeActionWorldPosition = ScreenToWorld(mouse);
                    showSelectedEdgeDeleteAction = true;
                    current.Use();
                    return;
                }

                if (string.IsNullOrEmpty(hitNodeId))
                {
                    selectedEdgeId = string.Empty;
                    draggingNode = false;
                    boxSelecting = true;
                    boxStart = mouse;
                    boxCurrent = mouse;
                    boxInitialSelection = IsAdditiveSelection(current)
                        ? new HashSet<string>(selectedNodeIds, StringComparer.Ordinal)
                        : new HashSet<string>(StringComparer.Ordinal);
                    selectedNodeIds.Clear();
                    selectedNodeIds.UnionWith(boxInitialSelection);
                    GUIUtility.hotControl = boxSelectionControlId;
                }
                else
                {
                    selectedEdgeId = string.Empty;
                    boxSelecting = false;
                    if (IsAdditiveSelection(current))
                    {
                        if (!selectedNodeIds.Add(hitNodeId))
                        {
                            selectedNodeIds.Remove(hitNodeId);
                            draggingNode = false;
                            current.Use();
                            return;
                        }
                    }
                    else if (!selectedNodeIds.Contains(hitNodeId))
                    {
                        selectedNodeIds.Clear();
                        selectedNodeIds.Add(hitNodeId);
                    }
                    BeginNodeDrag(mouse, documents.layout);
                }
                current.Use();
                return;
            }

            if (current.type == EventType.MouseDrag && current.button == 0 && draggingNode)
            {
                dragPreviewDelta = (mouse - dragStartMouse) / zoom;
                requestRepaint?.Invoke();
                current.Use();
                return;
            }

            if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Delete && selectedNodeIds.Count > 0)
            {
                var nodeIds = selectedNodeIds.ToArray();
                execute(() => repository.DeleteNodes(layerId, nodeIds));
                selectedNodeIds.Clear();
                current.Use();
                return;
            }

            if (current.type != EventType.ContextClick && !contextFromRelease)
                return;

            var pickerScreenPosition = GUIUtility.GUIToScreenPoint(mouse);
            var menu = new GenericMenu();
            if (!string.IsNullOrEmpty(hitNodeId))
            {
                selectedEdgeId = string.Empty;
                if (!selectedNodeIds.Contains(hitNodeId))
                {
                    selectedNodeIds.Clear();
                    selectedNodeIds.Add(hitNodeId);
                }
                var selectedForDelete = selectedNodeIds.ToArray();
                var selectedRecords = documents.semantic.nodes.Where(node => selectedNodeIds.Contains(node.id)).ToArray();
                var containsPortal = selectedRecords.Any(IsPortal);
                var copyLabel = selectedForDelete.Length == 1 ? "Copy Node" : $"Copy {selectedForDelete.Length} Nodes";
                var duplicateLabel = selectedForDelete.Length == 1 ? "Duplicate Node" : $"Duplicate {selectedForDelete.Length} Nodes";
                if (containsPortal)
                {
                    menu.AddDisabledItem(new GUIContent($"{copyLabel} (Portals cannot be copied)"));
                    menu.AddDisabledItem(new GUIContent($"{duplicateLabel} (Portals cannot be duplicated)"));
                }
                else
                {
                    menu.AddItem(new GUIContent(copyLabel), false,
                        () => execute(() => clipboard.Copy(repository, layerId, selectedForDelete)));
                    menu.AddItem(new GUIContent(duplicateLabel), false,
                        () => DuplicateAndSelect(repository, execute, selectedForDelete));
                }
                if (selectedForDelete.Length == 1 && !containsPortal)
                {
                    var localNodeId = selectedForDelete[0];
                    foreach (var remoteLayer in repository.Project.layers.Where(candidate => candidate.id != layerId))
                    foreach (var remoteNode in repository.LoadLayer(remoteLayer.id).semantic.nodes.Where(node => !IsPortal(node)))
                    foreach (var relationshipKind in ArchitectorEdgeKinds.All)
                    {
                        var capturedLayerId = remoteLayer.id;
                        var capturedNodeId = remoteNode.id;
                        var capturedKind = relationshipKind;
                        var kindName = ArchitectorEdgeKinds.DisplayName(capturedKind);
                        menu.AddItem(
                            new GUIContent($"Create Relationship/{kindName}/Outgoing/{remoteLayer.name}/{remoteNode.title}"),
                            false,
                            () => execute(() => selectedEdgeId = repository.CreateCrossLayerEdge(
                                layerId, localNodeId, capturedLayerId, capturedNodeId, capturedKind)));
                        menu.AddItem(
                            new GUIContent($"Create Relationship/{kindName}/Incoming/{remoteLayer.name}/{remoteNode.title}"),
                            false,
                            () => execute(() => selectedEdgeId = repository.CreateCrossLayerEdge(
                                capturedLayerId, capturedNodeId, layerId, localNodeId, capturedKind)));
                    }
                }
                menu.AddItem(new GUIContent(selectedForDelete.Length == 1 ? "Clear Relationships" : $"Clear Relationships ({selectedForDelete.Length})"), false,
                    () => execute(() => repository.ClearNodeEdges(layerId, selectedForDelete)));
                menu.AddSeparator(string.Empty);
                if (selectedForDelete.Length == 1)
                {
                    var selectedNode = documents.semantic.nodes.First(node => node.id == selectedForDelete[0]);
                    if (IsPortal(selectedNode))
                    {
                        var destination = repository.FindLayer(selectedNode.portal.targetLayerId);
                        if (destination == null)
                            menu.AddDisabledItem(new GUIContent("Open Destination (missing layer)"));
                        else
                            menu.AddItem(new GUIContent($"Open Destination/{destination.name}"), false,
                                () => openLayer?.Invoke(destination.id));
                        menu.AddSeparator(string.Empty);
                    }
                    else if (string.Equals(selectedNode.type?.mode, "none", StringComparison.Ordinal))
                    {
                        menu.AddItem(new GUIContent("Convert to Manual Type"), false, () =>
                        {
                            string replacementId = null;
                            execute(() => replacementId = repository.ConvertEmptyNodeToManual(layerId, selectedNode.id));
                            if (!string.IsNullOrEmpty(replacementId))
                                SelectOnly(replacementId);
                        });
                        menu.AddItem(new GUIContent("Link Existing Type"), false, () =>
                        {
                            var available = AvailableTypes(typeIndex, documents.semantic);
                            EditorApplication.delayCall += () =>
                                ArchitectorTypePickerWindow.Show(pickerScreenPosition, available, indexedType =>
                                {
                                    string replacementId = null;
                                    execute(() => replacementId = repository.ConvertEmptyNodeToLinked(layerId, selectedNode.id, indexedType));
                                    if (!string.IsNullOrEmpty(replacementId))
                                        SelectOnly(replacementId);
                                });
                        });
                        menu.AddSeparator(string.Empty);
                    }
                }
                var deleteLabel = selectedForDelete.Length == 1 ? "Delete Node" : $"Delete {selectedForDelete.Length} Nodes";
                menu.AddItem(new GUIContent(deleteLabel), false, () =>
                {
                    execute(() => repository.DeleteNodes(layerId, selectedForDelete));
                    selectedNodeIds.Clear();
                });
            }
            else
            {
                selectedEdgeId = string.Empty;
                selectedNodeIds.Clear();
                var worldAtMouse = ScreenToWorld(mouse);
                if (clipboard.HasContent)
                {
                    menu.AddItem(new GUIContent("Paste"), false, () => PasteAndSelect(repository, clipboard, execute, worldAtMouse));
                    menu.AddSeparator(string.Empty);
                }
                else
                {
                    menu.AddDisabledItem(new GUIContent("Paste"));
                    menu.AddSeparator(string.Empty);
                }
                var requested = worldAtMouse - new Vector2(140f, 60f);
                menu.AddItem(new GUIContent("Create Empty Node"), false, () => execute(() =>
                {
                    selectedNodeIds.Clear();
                    selectedNodeIds.Add(repository.CreateEmptyNode(layerId, requested.x, requested.y));
                }));
                menu.AddItem(new GUIContent("Create Manual Type"), false, () => execute(() =>
                {
                    selectedNodeIds.Clear();
                    selectedNodeIds.Add(repository.CreateManualNode(layerId, requested.x, requested.y));
                }));
                menu.AddItem(new GUIContent("From Existing Type"), false, () =>
                {
                    var available = AvailableTypes(typeIndex, documents.semantic);
                    EditorApplication.delayCall += () =>
                        ArchitectorTypePickerWindow.Show(pickerScreenPosition, available, indexedType =>
                        {
                            string createdId = null;
                            execute(() => createdId = repository.CreateLinkedNode(layerId, requested.x, requested.y, indexedType));
                            if (!string.IsNullOrEmpty(createdId))
                                SelectOnly(createdId);
                        });
                });
            }
            requestContextMenu(menu, GUIUtility.GUIToScreenPoint(mouse));
            current.Use();
        }

        private static IReadOnlyList<ArchitectorIndexedType> AvailableTypes(
            ArchitectorTypeIndex typeIndex,
            ArchitectorLayerDocument layer)
        {
            var represented = new HashSet<string>(layer.nodes
                .Where(node => string.Equals(node.type?.mode, "linked", StringComparison.Ordinal))
                .Select(node => ArchitectorTypeIdentity.Key(node.type.assemblyName, node.type.metadataName)),
                StringComparer.Ordinal);
            return typeIndex.Types.Where(type => !represented.Contains(type.Key)).ToArray();
        }

        private void EndBoxSelection(int controlId, Action requestRepaint)
        {
            boxSelecting = false;
            if (GUIUtility.hotControl == controlId)
                GUIUtility.hotControl = 0;
            requestRepaint?.Invoke();
        }

        private void UpdateSelectionVisibilityRequest()
        {
            var selectionKey = selectedNodeIds.Count == 1
                ? $"node:{selectedNodeIds.First()}"
                : !string.IsNullOrEmpty(selectedEdgeId)
                    ? $"edge:{selectedEdgeId}"
                    : string.Empty;
            if (string.Equals(selectionKey, lastVisibilitySelectionKey, StringComparison.Ordinal))
                return;
            lastVisibilitySelectionKey = selectionKey;
            selectionVisibilityPending = !string.IsNullOrEmpty(selectionKey);
            selectionVisibilitySawLayout = false;
        }

        private void EnsureSelectedObjectVisible(
            Vector2 canvasSize,
            ArchitectorLayerDocuments documents,
            ArchitectorUserStateStore userState)
        {
            Rect focusRect;
            if (selectedNodeIds.Count == 1)
            {
                var nodeLayout = documents.layout.nodes.FirstOrDefault(node => selectedNodeIds.Contains(node.nodeId));
                if (nodeLayout == null)
                    return;
                focusRect = WorldToScreen(new Rect(
                    nodeLayout.x,
                    nodeLayout.y,
                    nodeLayout.width,
                    nodeLayout.height));
            }
            else if (!string.IsNullOrEmpty(selectedEdgeId))
            {
                var focusPoint = WorldToScreen(selectedEdgeActionWorldPosition);
                focusRect = new Rect(focusPoint.x - 24f, focusPoint.y - 24f, 48f, 48f);
            }
            else
            {
                return;
            }

            var delta = VisibilityPanDelta(focusRect, canvasSize, 16f);
            if (delta.sqrMagnitude < 0.01f)
                return;
            pan += delta;
            userState.SaveLayerView(viewId, layerId, pan.x, pan.y, zoom);
        }

        private static Vector2 VisibilityPanDelta(Rect focusRect, Vector2 canvasSize, float margin)
        {
            var visibleRect = new Rect(
                margin,
                margin,
                Mathf.Max(0f, canvasSize.x - margin * 2f),
                Mathf.Max(0f, canvasSize.y - margin * 2f));
            var delta = Vector2.zero;
            if (focusRect.width > visibleRect.width)
                delta.x = visibleRect.center.x - focusRect.center.x;
            else if (focusRect.xMin < visibleRect.xMin)
                delta.x = visibleRect.xMin - focusRect.xMin;
            else if (focusRect.xMax > visibleRect.xMax)
                delta.x = visibleRect.xMax - focusRect.xMax;

            if (focusRect.height > visibleRect.height)
                delta.y = visibleRect.center.y - focusRect.center.y;
            else if (focusRect.yMin < visibleRect.yMin)
                delta.y = visibleRect.yMin - focusRect.yMin;
            else if (focusRect.yMax > visibleRect.yMax)
                delta.y = visibleRect.yMax - focusRect.yMax;
            return delta;
        }

        private void PasteAndSelect(
            ArchitectorRepository repository,
            ArchitectorNodeClipboard clipboard,
            Action<Action> execute,
            Vector2 center)
        {
            IReadOnlyList<string> insertedIds = Array.Empty<string>();
            execute(() => insertedIds = repository.PasteNodes(layerId, clipboard, center.x, center.y));
            if (insertedIds.Count == 0)
                return;
            selectedEdgeId = string.Empty;
            selectedNodeIds.Clear();
            selectedNodeIds.UnionWith(insertedIds);
        }

        private void DuplicateAndSelect(
            ArchitectorRepository repository,
            Action<Action> execute,
            IReadOnlyCollection<string> nodeIds)
        {
            IReadOnlyList<string> insertedIds = Array.Empty<string>();
            execute(() => insertedIds = repository.DuplicateNodes(layerId, nodeIds));
            if (insertedIds.Count == 0)
                return;
            selectedEdgeId = string.Empty;
            selectedNodeIds.Clear();
            selectedNodeIds.UnionWith(insertedIds);
        }

        private void BeginNodeDrag(Vector2 mouse, ArchitectorLayerLayoutDocument layout)
        {
            draggingNode = true;
            dragStartMouse = mouse;
            dragPreviewDelta = Vector2.zero;
            dragStartWorldByNode.Clear();
            foreach (var node in layout.nodes.Where(node => selectedNodeIds.Contains(node.nodeId)))
                dragStartWorldByNode[node.nodeId] = new Vector2(node.x, node.y);
        }

        private void CompleteNodeDrag(ArchitectorRepository repository, Action<Action> execute)
        {
            draggingNode = false;
            if (dragPreviewDelta.sqrMagnitude > DragMovementThresholdSquared)
            {
                var requestedPositions = dragStartWorldByNode.ToDictionary(
                    pair => pair.Key,
                    pair => new ArchitectorPoint(pair.Value.x + dragPreviewDelta.x, pair.Value.y + dragPreviewDelta.y),
                    StringComparer.Ordinal);
                execute(() => repository.MoveNodes(layerId, requestedPositions));
            }
            dragStartWorldByNode.Clear();
            dragPreviewDelta = Vector2.zero;
        }

        private void UpdateBoxSelection(ArchitectorLayerLayoutDocument layout)
        {
            selectedNodeIds.Clear();
            selectedNodeIds.UnionWith(boxInitialSelection);
            var selectionRect = MakeRect(boxStart, boxCurrent);
            foreach (var node in layout.nodes)
            {
                var nodeRect = WorldToScreen(new Rect(node.x, node.y, node.width, node.height));
                if (selectionRect.Overlaps(nodeRect, true))
                    selectedNodeIds.Add(node.nodeId);
            }
        }

        private static bool IsAdditiveSelection(Event current)
        {
            return current.control || current.command;
        }

        private static Rect MakeRect(Vector2 first, Vector2 second)
        {
            return Rect.MinMaxRect(
                Mathf.Min(first.x, second.x),
                Mathf.Min(first.y, second.y),
                Mathf.Max(first.x, second.x),
                Mathf.Max(first.y, second.y));
        }

        private static Vector2 ClampToRect(Vector2 point, Rect rect)
        {
            return new Vector2(
                Mathf.Clamp(point.x, rect.xMin, rect.xMax),
                Mathf.Clamp(point.y, rect.yMin, rect.yMax));
        }

        private void DrawEdges(ArchitectorLayerDocuments documents, ArchitectorThemePaletteSettings palette)
        {
            Handles.BeginGUI();
            foreach (var edge in documents.semantic.edges.OrderBy(edge =>
                         string.Equals(edge.id, selectedEdgeId, StringComparison.Ordinal) ? 1 : 0))
            {
                var route = GetEdgeRoute(edge, documents);
                if (route.Count < 2)
                    continue;
                var color = EdgeColor(edge, palette);
                Handles.color = color;
                var selected = string.Equals(edge.id, selectedEdgeId, StringComparison.Ordinal);
                var width = selected
                    ? 3f
                    : string.Equals(edge.kind, ArchitectorEdgeKinds.Dependency, StringComparison.Ordinal) ? 2f : 1.75f;
                DrawEdgeRoute(route, edge.kind, width);
                DrawArrowHead(
                    route[route.Count - 2],
                    route[route.Count - 1],
                    color,
                    width,
                    string.Equals(edge.kind, ArchitectorEdgeKinds.Dependency, StringComparison.Ordinal));
            }
            Handles.color = Color.white;
            Handles.EndGUI();
        }

        private Color EdgeColor(ArchitectorEdgeRecord edge, ArchitectorThemePaletteSettings palette)
        {
            if (string.Equals(edge.id, selectedEdgeId, StringComparison.Ordinal))
                return palette.selectedEdge.ToColor();
            if (selectedNodeIds.Contains(edge.sourceNodeId))
                return palette.selectedEdge.ToColor();
            if (selectedNodeIds.Contains(edge.targetNodeId))
                return palette.incomingEdgeHighlight.ToColor();
            var color = edge.kind switch
            {
                ArchitectorEdgeKinds.DataFlow => palette.dataFlowEdge,
                ArchitectorEdgeKinds.Event => palette.eventEdge,
                ArchitectorEdgeKinds.Callback => palette.callbackEdge,
                _ => palette.edge
            };
            return color.ToColor();
        }

        private static void DrawEdgeRoute(IReadOnlyList<Vector2> route, string kind, float width)
        {
            switch (kind)
            {
                case ArchitectorEdgeKinds.DataFlow:
                    DrawPatternedRoute(route, width, 10f, 6f);
                    return;
                case ArchitectorEdgeKinds.Event:
                    DrawPatternedRoute(route, width, 1.5f, 4.5f);
                    return;
                case ArchitectorEdgeKinds.Callback:
                    DrawPatternedRoute(route, width, 9f, 4f, 1.5f, 4f);
                    return;
                default:
                    Handles.DrawAAPolyLine(width, route.Select(point => new Vector3(point.x, point.y)).ToArray());
                    return;
            }
        }

        private static void DrawPatternedRoute(IReadOnlyList<Vector2> route, float width, params float[] pattern)
        {
            var patternIndex = 0;
            var patternRemaining = pattern[0];
            var drawing = true;
            for (var routeIndex = 1; routeIndex < route.Count; routeIndex++)
            {
                var cursor = route[routeIndex - 1];
                var segment = route[routeIndex] - cursor;
                var segmentRemaining = segment.magnitude;
                if (segmentRemaining <= 0.001f)
                    continue;
                var direction = segment / segmentRemaining;
                while (segmentRemaining > 0.001f)
                {
                    var step = Mathf.Min(segmentRemaining, patternRemaining);
                    var next = cursor + direction * step;
                    if (drawing)
                        Handles.DrawAAPolyLine(width, new Vector3(cursor.x, cursor.y), new Vector3(next.x, next.y));
                    cursor = next;
                    segmentRemaining -= step;
                    patternRemaining -= step;
                    if (patternRemaining > 0.001f)
                        continue;
                    patternIndex = (patternIndex + 1) % pattern.Length;
                    patternRemaining = pattern[patternIndex];
                    drawing = patternIndex % 2 == 0;
                }
            }
        }

        private static void DrawArrowHead(Vector2 previous, Vector2 tip, Color color, float width, bool filled)
        {
            var direction = (tip - previous).normalized;
            if (direction.sqrMagnitude < 0.5f)
                return;
            var perpendicular = new Vector2(-direction.y, direction.x);
            var basePoint = tip - direction * 9f;
            Handles.color = color;
            var firstWing = basePoint + perpendicular * 4.5f;
            var secondWing = basePoint - perpendicular * 4.5f;
            if (filled)
                Handles.DrawAAConvexPolygon(tip, firstWing, secondWing);
            else
                Handles.DrawAAPolyLine(
                    width,
                    new Vector3(firstWing.x, firstWing.y),
                    new Vector3(tip.x, tip.y),
                    new Vector3(secondWing.x, secondWing.y));
        }

        private List<Vector2> GetEdgeRoute(ArchitectorEdgeRecord edge, ArchitectorLayerDocuments documents)
        {
            EnsureEdgeRouteCache(documents);
            if (!HasDragMovement)
                return edgeRouteCache.TryGetValue(edge.id, out var cached) ? cached : new List<Vector2>();

            var sourceMoves = selectedNodeIds.Contains(edge.sourceNodeId);
            var targetMoves = selectedNodeIds.Contains(edge.targetNodeId);
            if (!sourceMoves && !targetMoves)
                return edgeRouteCache.TryGetValue(edge.id, out var unchanged) ? unchanged : BuildEdgeRoute(edge, documents, true);
            if (sourceMoves && targetMoves && edgeRouteCache.TryGetValue(edge.id, out var internalRoute))
            {
                var screenDelta = dragPreviewDelta * zoom;
                return internalRoute.Select(point => point + screenDelta).ToList();
            }

            return BuildEdgeRoute(edge, documents, highQualityDragRouting && !dragPreviewOverlapsNode);
        }

        private bool HasDragMovement => draggingNode && dragPreviewDelta.sqrMagnitude > DragMovementThresholdSquared;

        private bool DoesDragPreviewOverlapNode(ArchitectorLayerLayoutDocument layout)
        {
            const float previewClearance = 8f;
            foreach (var movingNode in layout.nodes.Where(node => selectedNodeIds.Contains(node.nodeId)))
            {
                var movingRect = GetScreenNodeRect(movingNode);
                foreach (var stationaryNode in layout.nodes.Where(node => !selectedNodeIds.Contains(node.nodeId)))
                {
                    var stationaryRect = GetScreenNodeRect(stationaryNode);
                    stationaryRect.xMin -= previewClearance;
                    stationaryRect.xMax += previewClearance;
                    stationaryRect.yMin -= previewClearance;
                    stationaryRect.yMax += previewClearance;
                    if (movingRect.Overlaps(stationaryRect))
                        return true;
                }
            }

            return false;
        }

        private void EnsureEdgeRouteCache(ArchitectorLayerDocuments documents)
        {
            var sameRevisionAndZoom = edgeRouteCacheRevision == currentRepositoryRevision
                                      && Mathf.Approximately(edgeRouteCacheZoom, zoom);
            if (sameRevisionAndZoom && edgeRouteCache.Count > 0)
            {
                var panDelta = pan - edgeRouteCachePan;
                if (panDelta.sqrMagnitude >= 0.001f)
                {
                    foreach (var route in edgeRouteCache.Values)
                    {
                        for (var index = 0; index < route.Count; index++)
                            route[index] += panDelta;
                    }
                    edgeRouteCachePan = pan;
                }
                return;
            }

            var cacheValid = sameRevisionAndZoom && (edgeRouteCachePan - pan).sqrMagnitude < 0.001f;
            if (cacheValid || HasDragMovement && edgeRouteCache.Count > 0)
                return;

            edgeRouteCache.Clear();
            foreach (var edge in documents.semantic.edges)
                edgeRouteCache[edge.id] = BuildEdgeRoute(edge, documents, true);
            edgeRouteCacheRevision = currentRepositoryRevision;
            edgeRouteCacheZoom = zoom;
            edgeRouteCachePan = pan;
        }

        private List<Vector2> BuildEdgeRoute(
            ArchitectorEdgeRecord edge,
            ArchitectorLayerDocuments documents,
            bool useObstacleRouting)
        {
            var sourceLayout = documents.layout.nodes.FirstOrDefault(node => node.nodeId == edge.sourceNodeId);
            var targetLayout = documents.layout.nodes.FirstOrDefault(node => node.nodeId == edge.targetNodeId);
            if (sourceLayout == null || targetLayout == null)
                return new List<Vector2>();
            var edgeLayout = documents.layout.edges.FirstOrDefault(candidate => candidate.edgeId == edge.id);
            var sourceRect = GetWorldNodeRect(sourceLayout);
            var targetRect = GetWorldNodeRect(targetLayout);
            var sourceSide = edgeLayout == null || edgeLayout.preferredSourceSide == "auto"
                ? FacingSide(sourceRect.center, targetRect.center)
                : edgeLayout.preferredSourceSide;
            var targetSide = edgeLayout == null || edgeLayout.preferredTargetSide == "auto"
                ? FacingSide(targetRect.center, sourceRect.center)
                : edgeLayout.preferredTargetSide;
            var sourceAnchor = SideAnchor(
                sourceRect,
                sourceSide,
                sourceLaneOffsetByEdgeId.TryGetValue(edge.id, out var sourceLaneOffset) ? sourceLaneOffset : 0f);
            var targetAnchor = SideAnchor(
                targetRect,
                targetSide,
                targetLaneOffsetByEdgeId.TryGetValue(edge.id, out var targetLaneOffset) ? targetLaneOffset : 0f);
            var sourceOut = sourceAnchor + SideDirection(sourceSide) * 18f;
            var targetOut = targetAnchor + SideDirection(targetSide) * 18f;
            var route = new List<Vector2> { sourceAnchor, sourceOut };

            if (useObstacleRouting)
            {
                var obstacles = documents.layout.nodes.Select(GetWorldNodeRect).ToList();
                if (gridRouting)
                {
                    var sourceGateway = GridGateway(sourceAnchor, sourceSide, routingGridSize);
                    var targetGateway = GridGateway(targetAnchor, targetSide, routingGridSize);
                    var protectedObstacles = obstacles.Select(rect =>
                    {
                        if (rect == sourceRect || rect == targetRect)
                            return rect;
                        return ExpandRect(rect, routingGridSize);
                    }).ToList();
                    var gridPath = FindGridRoute(sourceGateway, targetGateway, protectedObstacles, routingGridSize);
                    if (gridPath != null)
                    {
                        route.Clear();
                        AddSourceAttachmentPath(route, sourceAnchor, sourceGateway, sourceSide);
                        route.AddRange(gridPath.Skip(1));
                        AddTargetAttachmentPath(route, targetGateway, targetAnchor, targetSide);
                        RemoveDuplicateRoutePoints(route);
                        RemoveCollinearRoutePoints(route);
                        return WorldRouteToScreen(route);
                    }
                }
                var obstacleAwarePath = FindObstacleAwareRoute(sourceOut, targetOut, obstacles);
                if (obstacleAwarePath != null)
                {
                    route.AddRange(obstacleAwarePath.Skip(1));
                    route.Add(targetAnchor);
                    RemoveDuplicateRoutePoints(route);
                    RemoveCollinearRoutePoints(route);
                    return WorldRouteToScreen(route);
                }
            }

            var sourceHorizontal = sourceSide == "left" || sourceSide == "right";
            var targetHorizontal = targetSide == "left" || targetSide == "right";
            if (sourceHorizontal && targetHorizontal)
            {
                var middleX = (sourceOut.x + targetOut.x) * 0.5f;
                route.Add(new Vector2(middleX, sourceOut.y));
                route.Add(new Vector2(middleX, targetOut.y));
            }
            else if (!sourceHorizontal && !targetHorizontal)
            {
                var middleY = (sourceOut.y + targetOut.y) * 0.5f;
                route.Add(new Vector2(sourceOut.x, middleY));
                route.Add(new Vector2(targetOut.x, middleY));
            }
            else if (sourceHorizontal)
            {
                route.Add(new Vector2(targetOut.x, sourceOut.y));
            }
            else
            {
                route.Add(new Vector2(sourceOut.x, targetOut.y));
            }
            route.Add(targetOut);
            route.Add(targetAnchor);
            RemoveDuplicateRoutePoints(route);
            return WorldRouteToScreen(route);
        }

        private void PrepareEdgeAnchorLanes(ArchitectorLayerDocuments documents)
        {
            if (edgeLaneCacheRevision == currentRepositoryRevision)
                return;

            sourceLaneOffsetByEdgeId.Clear();
            targetLaneOffsetByEdgeId.Clear();
            var layoutByNodeId = documents.layout.nodes.ToDictionary(node => node.nodeId, StringComparer.Ordinal);
            var edgeLayoutById = documents.layout.edges.ToDictionary(edge => edge.edgeId, StringComparer.Ordinal);
            var groups = new Dictionary<(string nodeId, string side), EdgeLaneGroup>();

            foreach (var edge in documents.semantic.edges)
            {
                if (!layoutByNodeId.TryGetValue(edge.sourceNodeId, out var sourceLayout)
                    || !layoutByNodeId.TryGetValue(edge.targetNodeId, out var targetLayout))
                {
                    continue;
                }

                var sourceRect = GetWorldNodeRect(sourceLayout);
                var targetRect = GetWorldNodeRect(targetLayout);
                edgeLayoutById.TryGetValue(edge.id, out var edgeLayout);
                var sourceSide = edgeLayout == null || edgeLayout.preferredSourceSide == "auto"
                    ? FacingSide(sourceRect.center, targetRect.center)
                    : edgeLayout.preferredSourceSide;
                var targetSide = edgeLayout == null || edgeLayout.preferredTargetSide == "auto"
                    ? FacingSide(targetRect.center, sourceRect.center)
                    : edgeLayout.preferredTargetSide;
                AddLaneEndpoint(groups, edge.sourceNodeId, sourceSide, edge.id, true, TangentDelta(sourceRect, sourceSide, targetRect.center));
                AddLaneEndpoint(groups, edge.targetNodeId, targetSide, edge.id, false, TangentDelta(targetRect, targetSide, sourceRect.center));
            }

            var kindByEdgeId = documents.semantic.edges.ToDictionary(edge => edge.id, edge => edge.kind, StringComparer.Ordinal);
            foreach (var pair in groups)
            {
                var group = pair.Value;
                if (!layoutByNodeId.TryGetValue(pair.Key.nodeId, out var nodeLayout))
                    continue;

                var nodeRect = GetWorldNodeRect(nodeLayout);
                var edgeLength = pair.Key.side == "left" || pair.Key.side == "right" ? nodeRect.height : nodeRect.width;
                var occupiedPorts = BuildOccupiedPorts(group, kindByEdgeId);
                for (var portIndex = 0; portIndex < occupiedPorts.Count; portIndex++)
                {
                    var port = occupiedPorts[portIndex];
                    var offset = CenteredOccupiedPortOffset(edgeLength, portIndex, occupiedPorts.Count);
                    foreach (var edgeId in port.edgeIds)
                    {
                        if (port.outgoing)
                            sourceLaneOffsetByEdgeId[edgeId] = offset;
                        else
                            targetLaneOffsetByEdgeId[edgeId] = offset;
                    }
                }
            }
            edgeLaneCacheRevision = currentRepositoryRevision;
        }

        private static List<OccupiedEdgePort> BuildOccupiedPorts(
            EdgeLaneGroup group,
            IReadOnlyDictionary<string, string> kindByEdgeId)
        {
            var ports = new List<OccupiedEdgePort>();
            AddOccupiedPorts(ports, group.outgoingTangentByEdgeId, true, kindByEdgeId);
            AddOccupiedPorts(ports, group.incomingTangentByEdgeId, false, kindByEdgeId);
            ports.Sort((left, right) =>
            {
                var tangentComparison = left.averageTangent.CompareTo(right.averageTangent);
                if (tangentComparison != 0)
                    return tangentComparison;
                var kindComparison = EdgeKindOrder(left.kind).CompareTo(EdgeKindOrder(right.kind));
                if (kindComparison != 0)
                    return kindComparison;
                return left.outgoing.CompareTo(right.outgoing);
            });
            return ports;
        }

        private static void AddOccupiedPorts(
            ICollection<OccupiedEdgePort> ports,
            IReadOnlyDictionary<string, float> tangentByEdgeId,
            bool outgoing,
            IReadOnlyDictionary<string, string> kindByEdgeId)
        {
            foreach (var kindGroup in tangentByEdgeId.GroupBy(
                         pair => kindByEdgeId[pair.Key],
                         StringComparer.Ordinal))
            {
                var edgeIds = kindGroup.Select(pair => pair.Key).OrderBy(id => id, StringComparer.Ordinal).ToList();
                ports.Add(new OccupiedEdgePort(
                    outgoing,
                    kindGroup.Key,
                    edgeIds,
                    kindGroup.Average(pair => pair.Value)));
            }
        }

        private static int EdgeKindOrder(string kind)
        {
            return kind switch
            {
                ArchitectorEdgeKinds.Dependency => 0,
                ArchitectorEdgeKinds.DataFlow => 1,
                ArchitectorEdgeKinds.Event => 2,
                ArchitectorEdgeKinds.Callback => 3,
                _ => 4
            };
        }

        private static float CenteredOccupiedPortOffset(float edgeLength, int portIndex, int occupiedPortCount)
        {
            if (occupiedPortCount <= 1)
                return 0f;

            var margin = Mathf.Min(18f, edgeLength * 0.1f);
            var usableLength = Mathf.Max(0f, edgeLength - margin * 2f);
            var regionLength = usableLength / occupiedPortCount;
            return -edgeLength * 0.5f + margin + regionLength * (portIndex + 0.5f);
        }

        private static void AddLaneEndpoint(
            IDictionary<(string nodeId, string side), EdgeLaneGroup> groups,
            string nodeId,
            string side,
            string edgeId,
            bool outgoing,
            float tangentDelta)
        {
            var key = (nodeId, side);
            if (!groups.TryGetValue(key, out var group))
            {
                group = new EdgeLaneGroup();
                groups.Add(key, group);
            }

            if (outgoing)
                group.outgoingTangentByEdgeId[edgeId] = tangentDelta;
            else
                group.incomingTangentByEdgeId[edgeId] = tangentDelta;
        }

        private static float TangentDelta(Rect rect, string side, Vector2 oppositeCenter)
        {
            return side == "left" || side == "right"
                ? oppositeCenter.y - rect.center.y
                : oppositeCenter.x - rect.center.x;
        }

        private List<Vector2> WorldRouteToScreen(IEnumerable<Vector2> route)
        {
            return route.Select(WorldToScreen).ToList();
        }

        private static Rect ExpandRect(Rect rect, float amount)
        {
            return new Rect(rect.xMin - amount, rect.yMin - amount, rect.width + amount * 2f, rect.height + amount * 2f);
        }

        private static Vector2 GridGateway(Vector2 anchor, string side, float gridSize)
        {
            var sample = anchor + SideDirection(side) * gridSize * 0.5f;
            return new Vector2(
                (Mathf.Floor(sample.x / gridSize) + 0.5f) * gridSize,
                (Mathf.Floor(sample.y / gridSize) + 0.5f) * gridSize);
        }

        private static void AddSourceAttachmentPath(ICollection<Vector2> route, Vector2 start, Vector2 end, string side)
        {
            route.Add(start);
            var horizontal = side == "left" || side == "right";
            route.Add(horizontal ? new Vector2(end.x, start.y) : new Vector2(start.x, end.y));
            route.Add(end);
        }

        private static void AddTargetAttachmentPath(ICollection<Vector2> route, Vector2 start, Vector2 end, string side)
        {
            route.Add(start);
            var horizontal = side == "left" || side == "right";
            route.Add(horizontal ? new Vector2(start.x, end.y) : new Vector2(end.x, start.y));
            route.Add(end);
        }

        private static List<Vector2> FindGridRoute(
            Vector2 start,
            Vector2 target,
            IReadOnlyList<Rect> blockedRects,
            float gridSize)
        {
            if (gridSize <= 0f)
                return null;
            var startCell = WorldToGridCell(start, gridSize);
            var targetCell = WorldToGridCell(target, gridSize);
            var corridor = Rect.MinMaxRect(
                Mathf.Min(start.x, target.x) - gridSize * 3f,
                Mathf.Min(start.y, target.y) - gridSize * 3f,
                Mathf.Max(start.x, target.x) + gridSize * 3f,
                Mathf.Max(start.y, target.y) + gridSize * 3f);
            var relevant = blockedRects.Where(rect => rect.Overlaps(corridor)).ToArray();
            var minX = Mathf.Min(startCell.x, targetCell.x) - 3;
            var maxX = Mathf.Max(startCell.x, targetCell.x) + 3;
            var minY = Mathf.Min(startCell.y, targetCell.y) - 3;
            var maxY = Mathf.Max(startCell.y, targetCell.y) + 3;
            foreach (var rect in relevant)
            {
                minX = Mathf.Min(minX, Mathf.FloorToInt(rect.xMin / gridSize) - 1);
                maxX = Mathf.Max(maxX, Mathf.FloorToInt(rect.xMax / gridSize) + 1);
                minY = Mathf.Min(minY, Mathf.FloorToInt(rect.yMin / gridSize) - 1);
                maxY = Mathf.Max(maxY, Mathf.FloorToInt(rect.yMax / gridSize) + 1);
            }

            var costs = new Dictionary<RouteState, float>();
            var previous = new Dictionary<RouteState, RouteState>();
            var open = new SortedSet<RouteOpenItem>();
            var serial = 0;
            var initial = new RouteState(startCell.x, startCell.y, -1);
            costs[initial] = 0f;
            open.Add(new RouteOpenItem(GridManhattan(startCell.x, startCell.y, targetCell.x, targetCell.y), serial++, initial));
            RouteState? completed = null;
            while (open.Count > 0)
            {
                var item = open.Min;
                open.Remove(item);
                var current = item.state;
                if (current.x == targetCell.x && current.y == targetCell.y)
                {
                    completed = current;
                    break;
                }
                TryGridNeighbor(current, current.x - 1, current.y, 0, minX, maxX, minY, maxY, targetCell, gridSize, relevant, startCell, costs, previous, open, ref serial);
                TryGridNeighbor(current, current.x + 1, current.y, 1, minX, maxX, minY, maxY, targetCell, gridSize, relevant, startCell, costs, previous, open, ref serial);
                TryGridNeighbor(current, current.x, current.y - 1, 2, minX, maxX, minY, maxY, targetCell, gridSize, relevant, startCell, costs, previous, open, ref serial);
                TryGridNeighbor(current, current.x, current.y + 1, 3, minX, maxX, minY, maxY, targetCell, gridSize, relevant, startCell, costs, previous, open, ref serial);
            }
            if (!completed.HasValue)
                return null;
            var path = new List<Vector2>();
            var cursor = completed.Value;
            while (true)
            {
                path.Add(GridCellCenter(cursor.x, cursor.y, gridSize));
                if (!previous.TryGetValue(cursor, out cursor))
                    break;
            }
            path.Reverse();
            RemoveCollinearRoutePoints(path);
            return path;
        }

        private static void TryGridNeighbor(
            RouteState current, int x, int y, int direction,
            int minX, int maxX, int minY, int maxY,
            Vector2Int target, float gridSize, IReadOnlyList<Rect> blockedRects, Vector2Int start,
            IDictionary<RouteState, float> costs, IDictionary<RouteState, RouteState> previous,
            ISet<RouteOpenItem> open, ref int serial)
        {
            if (x < minX || x > maxX || y < minY || y > maxY)
                return;
            var point = GridCellCenter(x, y, gridSize);
            if ((x != start.x || y != start.y) && (x != target.x || y != target.y)
                && PointInsideAnyObstacle(point, blockedRects))
                return;
            var next = new RouteState(x, y, direction);
            var bendPenalty = current.direction < 0 || current.direction == direction ? 0f : gridSize * 0.35f;
            var nextCost = costs[current] + gridSize + bendPenalty;
            if (costs.TryGetValue(next, out var known) && known <= nextCost)
                return;
            costs[next] = nextCost;
            previous[next] = current;
            open.Add(new RouteOpenItem(nextCost + GridManhattan(x, y, target.x, target.y) * gridSize, serial++, next));
        }

        private static Vector2Int WorldToGridCell(Vector2 point, float gridSize)
        {
            return new Vector2Int(Mathf.FloorToInt(point.x / gridSize), Mathf.FloorToInt(point.y / gridSize));
        }

        private static Vector2 GridCellCenter(int x, int y, float gridSize)
        {
            return new Vector2((x + 0.5f) * gridSize, (y + 0.5f) * gridSize);
        }

        private static int GridManhattan(int x, int y, int targetX, int targetY)
        {
            return Mathf.Abs(x - targetX) + Mathf.Abs(y - targetY);
        }

        private static List<Vector2> FindObstacleAwareRoute(Vector2 start, Vector2 target, IReadOnlyList<Rect> nodeRects)
        {
            const float clearance = 8f;
            var obstacles = nodeRects
                .Select(rect => new Rect(rect.xMin - clearance, rect.yMin - clearance, rect.width + clearance * 2f, rect.height + clearance * 2f))
                .ToArray();
            var xValues = new List<float> { start.x, target.x };
            var yValues = new List<float> { start.y, target.y };
            foreach (var obstacle in obstacles)
            {
                xValues.Add(obstacle.xMin);
                xValues.Add(obstacle.xMax);
                yValues.Add(obstacle.yMin);
                yValues.Add(obstacle.yMax);
            }
            var xs = DistinctSorted(xValues);
            var ys = DistinctSorted(yValues);
            var startX = IndexOfApproximate(xs, start.x);
            var startY = IndexOfApproximate(ys, start.y);
            var targetX = IndexOfApproximate(xs, target.x);
            var targetY = IndexOfApproximate(ys, target.y);
            var costs = new Dictionary<RouteState, float>();
            var previous = new Dictionary<RouteState, RouteState>();
            var open = new SortedSet<RouteOpenItem>();
            var serial = 0;
            var startState = new RouteState(startX, startY, -1);
            costs[startState] = 0f;
            open.Add(new RouteOpenItem(Manhattan(start, target), serial++, startState));
            RouteState? completed = null;

            while (open.Count > 0)
            {
                var currentItem = open.Min;
                open.Remove(currentItem);
                var current = currentItem.state;
                if (!costs.TryGetValue(current, out var currentCost))
                    continue;
                var currentPoint = new Vector2(xs[current.x], ys[current.y]);
                var expectedPriority = currentCost + Manhattan(currentPoint, target);
                if (currentItem.priority > expectedPriority + 0.01f)
                    continue;
                if (current.x == targetX && current.y == targetY)
                {
                    completed = current;
                    break;
                }

                TryRouteNeighbor(current, current.x - 1, current.y, 0, xs, ys, obstacles, target, costs, previous, open, ref serial);
                TryRouteNeighbor(current, current.x + 1, current.y, 1, xs, ys, obstacles, target, costs, previous, open, ref serial);
                TryRouteNeighbor(current, current.x, current.y - 1, 2, xs, ys, obstacles, target, costs, previous, open, ref serial);
                TryRouteNeighbor(current, current.x, current.y + 1, 3, xs, ys, obstacles, target, costs, previous, open, ref serial);
            }

            if (!completed.HasValue)
                return null;
            var path = new List<Vector2>();
            var cursor = completed.Value;
            while (true)
            {
                path.Add(new Vector2(xs[cursor.x], ys[cursor.y]));
                if (!previous.TryGetValue(cursor, out cursor))
                    break;
            }
            path.Reverse();
            RemoveCollinearRoutePoints(path);
            return path;
        }

        private static void TryRouteNeighbor(
            RouteState current,
            int nextX,
            int nextY,
            int direction,
            IReadOnlyList<float> xs,
            IReadOnlyList<float> ys,
            IReadOnlyList<Rect> obstacles,
            Vector2 target,
            IDictionary<RouteState, float> costs,
            IDictionary<RouteState, RouteState> previous,
            ISet<RouteOpenItem> open,
            ref int serial)
        {
            if (nextX < 0 || nextX >= xs.Count || nextY < 0 || nextY >= ys.Count)
                return;
            var from = new Vector2(xs[current.x], ys[current.y]);
            var to = new Vector2(xs[nextX], ys[nextY]);
            if (PointInsideAnyObstacle(to, obstacles) || SegmentCrossesObstacle(from, to, obstacles))
                return;
            var next = new RouteState(nextX, nextY, direction);
            var bendPenalty = current.direction < 0 || current.direction == direction ? 0f : 24f;
            var nextCost = costs[current] + Vector2.Distance(from, to) + bendPenalty;
            if (costs.TryGetValue(next, out var knownCost) && knownCost <= nextCost)
                return;
            costs[next] = nextCost;
            previous[next] = current;
            open.Add(new RouteOpenItem(nextCost + Manhattan(to, target), serial++, next));
        }

        private static bool PointInsideAnyObstacle(Vector2 point, IEnumerable<Rect> obstacles)
        {
            const float epsilon = 0.01f;
            return obstacles.Any(rect => point.x > rect.xMin + epsilon && point.x < rect.xMax - epsilon
                                         && point.y > rect.yMin + epsilon && point.y < rect.yMax - epsilon);
        }

        private static bool SegmentCrossesObstacle(Vector2 start, Vector2 end, IEnumerable<Rect> obstacles)
        {
            const float epsilon = 0.01f;
            if (Mathf.Abs(start.y - end.y) < epsilon)
            {
                var minX = Mathf.Min(start.x, end.x);
                var maxX = Mathf.Max(start.x, end.x);
                return obstacles.Any(rect => start.y > rect.yMin + epsilon && start.y < rect.yMax - epsilon
                                             && maxX > rect.xMin + epsilon && minX < rect.xMax - epsilon);
            }
            var minY = Mathf.Min(start.y, end.y);
            var maxY = Mathf.Max(start.y, end.y);
            return obstacles.Any(rect => start.x > rect.xMin + epsilon && start.x < rect.xMax - epsilon
                                         && maxY > rect.yMin + epsilon && minY < rect.yMax - epsilon);
        }

        private static List<float> DistinctSorted(IEnumerable<float> values)
        {
            var sorted = values.OrderBy(value => value).ToList();
            var result = new List<float>();
            foreach (var value in sorted)
            {
                if (result.Count == 0 || Mathf.Abs(result[result.Count - 1] - value) > 0.01f)
                    result.Add(value);
            }
            return result;
        }

        private static int IndexOfApproximate(IReadOnlyList<float> values, float value)
        {
            for (var index = 0; index < values.Count; index++)
            {
                if (Mathf.Abs(values[index] - value) <= 0.01f)
                    return index;
            }
            return 0;
        }

        private static float Manhattan(Vector2 first, Vector2 second)
        {
            return Mathf.Abs(first.x - second.x) + Mathf.Abs(first.y - second.y);
        }

        private static void RemoveCollinearRoutePoints(List<Vector2> route)
        {
            for (var index = route.Count - 2; index > 0; index--)
            {
                var before = route[index] - route[index - 1];
                var after = route[index + 1] - route[index];
                if (Mathf.Abs(Vector2.Dot(before.normalized, after.normalized)) > 0.999f)
                    route.RemoveAt(index);
            }
        }

        private string HitTestEdge(Vector2 mouse, ArchitectorLayerDocuments documents)
        {
            var hits = new List<(string edgeId, float distance)>();
            for (var edgeIndex = documents.semantic.edges.Count - 1; edgeIndex >= 0; edgeIndex--)
            {
                var edge = documents.semantic.edges[edgeIndex];
                var route = GetEdgeRoute(edge, documents);
                var bestDistance = float.MaxValue;
                for (var index = 1; index < route.Count; index++)
                    bestDistance = Mathf.Min(bestDistance, DistanceToSegment(mouse, route[index - 1], route[index]));
                if (bestDistance <= 6f)
                    hits.Add((edge.id, bestDistance));
            }
            if (hits.Count == 0)
            {
                ResetEdgeCycle();
                return string.Empty;
            }

            var orderedIds = hits.OrderBy(hit => hit.distance).ThenBy(hit => hit.edgeId, StringComparer.Ordinal).Select(hit => hit.edgeId).ToList();
            if (orderedIds.Count == 1)
            {
                ResetEdgeCycle();
                return orderedIds[0];
            }

            var sameCycle = Vector2.Distance(mouse, lastEdgeCyclePosition) <= 8f
                            && orderedIds.SequenceEqual(lastEdgeCycleIds, StringComparer.Ordinal);
            lastEdgeCycleIndex = sameCycle ? (lastEdgeCycleIndex + 1) % orderedIds.Count : 0;
            lastEdgeCyclePosition = mouse;
            lastEdgeCycleIds = orderedIds;
            return orderedIds[lastEdgeCycleIndex];
        }

        private string HitTestPortalRelationshipRow(
            Vector2 mouse,
            ArchitectorLayerDocuments documents)
        {
            if (zoom < MemberVisibilityZoom)
                return string.Empty;
            foreach (var nodeLayout in documents.layout.nodes)
            {
                var portal = documents.semantic.nodes.FirstOrDefault(node => node.id == nodeLayout.nodeId && IsPortal(node));
                if (portal == null)
                    continue;
                if (!cachedPortalRelationshipRows.TryGetValue(portal.id, out var rows))
                    continue;
                var rect = GetScreenNodeRect(nodeLayout);
                var firstRowY = rect.y + 10f + EditorGUIUtility.singleLineHeight;
                var visibleRows = Mathf.Min(rows.Count,
                    Mathf.Max(0, Mathf.FloorToInt((rect.yMax - firstRowY - 5f) / EditorGUIUtility.singleLineHeight)));
                for (var index = 0; index < visibleRows; index++)
                {
                    var rowRect = new Rect(rect.x + 8f, firstRowY + index * EditorGUIUtility.singleLineHeight,
                        rect.width - 16f, EditorGUIUtility.singleLineHeight);
                    if (rowRect.Contains(mouse))
                        return rows[index].Relationship.id;
                }
            }
            return string.Empty;
        }

        private ConnectionAffordance GetConnectionAffordance(Vector2 mouse, ArchitectorLayerDocuments documents)
        {
            if (selectedNodeIds.Count != 1 || draggingNode || boxSelecting)
                return default;
            var sourceNodeId = SelectedNodeId;
            if (documents.semantic.nodes.Any(node => node.id == sourceNodeId && IsPortal(node)))
                return default;
            var best = default(ConnectionAffordance);
            var bestDistance = float.MaxValue;
            foreach (var nodeLayout in documents.layout.nodes)
            {
                if (nodeLayout.nodeId == sourceNodeId)
                    continue;
                if (documents.semantic.nodes.Any(node => node.id == nodeLayout.nodeId && IsPortal(node)))
                    continue;
                var rect = GetScreenNodeRect(nodeLayout);
                var activation = new Rect(rect.x - 32f, rect.y - 32f, rect.width + 64f, rect.height + 64f);
                if (!activation.Contains(mouse) || rect.Contains(mouse))
                    continue;

                var left = Mathf.Abs(mouse.x - rect.xMin);
                var right = Mathf.Abs(mouse.x - rect.xMax);
                var top = Mathf.Abs(mouse.y - rect.yMin);
                var bottom = Mathf.Abs(mouse.y - rect.yMax);
                var distance = Mathf.Min(left, right, top, bottom);
                if (distance >= bestDistance)
                    continue;
                var side = distance == left ? "left" : distance == right ? "right" : distance == top ? "top" : "bottom";
                var anchor = SideAnchor(rect, side);
                var center = anchor + SideDirection(side) * 12f;
                best = new ConnectionAffordance
                {
                    visible = true,
                    targetNodeId = nodeLayout.nodeId,
                    targetSide = side,
                    buttonRect = new Rect(center.x - 9f, center.y - 9f, 18f, 18f)
                };
                bestDistance = distance;
            }
            return best;
        }

        private void DrawConnectionAffordance(ArchitectorLayerDocuments documents)
        {
            var affordance = GetConnectionAffordance(Event.current.mousePosition, documents);
            if (affordance.visible)
                GUI.Box(affordance.buttonRect, new GUIContent("+", "Create a relationship to this node"), EditorStyles.miniButton);
        }

        private void ShowRelationshipKindMenu(
            ArchitectorRepository repository,
            Action<Action> execute,
            Action<GenericMenu, Vector2> requestContextMenu,
            string sourceNodeId,
            string targetNodeId,
            string targetSide,
            Vector2 mouse)
        {
            var menu = new GenericMenu();
            AddRelationshipKindItem(menu, repository, execute, sourceNodeId, targetNodeId, targetSide, ArchitectorEdgeKinds.Dependency);
            AddRelationshipKindItem(menu, repository, execute, sourceNodeId, targetNodeId, targetSide, ArchitectorEdgeKinds.DataFlow);
            AddRelationshipKindItem(menu, repository, execute, sourceNodeId, targetNodeId, targetSide, ArchitectorEdgeKinds.Event);
            AddRelationshipKindItem(menu, repository, execute, sourceNodeId, targetNodeId, targetSide, ArchitectorEdgeKinds.Callback);
            requestContextMenu(menu, GUIUtility.GUIToScreenPoint(mouse));
        }

        private void AddRelationshipKindItem(
            GenericMenu menu,
            ArchitectorRepository repository,
            Action<Action> execute,
            string sourceNodeId,
            string targetNodeId,
            string targetSide,
            string kind)
        {
            menu.AddItem(new GUIContent(ArchitectorEdgeKinds.DisplayName(kind)), false, () =>
                execute(() => repository.CreateEdge(layerId, sourceNodeId, targetNodeId, kind, targetSide)));
        }

        private void DrawSelectedEdgeAction(ArchitectorLayerDocuments documents)
        {
            if (!string.IsNullOrEmpty(selectedEdgeId) && showSelectedEdgeDeleteAction)
                GUI.Box(SelectedEdgeDeleteRect(documents), new GUIContent("×", "Delete relationship"), EditorStyles.miniButton);
        }

        private Rect SelectedEdgeDeleteRect(ArchitectorLayerDocuments documents)
        {
            var edge = documents.semantic.edges.FirstOrDefault(candidate =>
                string.Equals(candidate.id, selectedEdgeId, StringComparison.Ordinal));
            var screenPosition = edge == null
                ? Vector2.zero
                : RouteMidpoint(GetEdgeRoute(edge, documents));
            return new Rect(screenPosition.x - 9f, screenPosition.y - 9f, 18f, 18f);
        }

        private static Vector2 RouteMidpoint(IReadOnlyList<Vector2> route)
        {
            if (route == null || route.Count == 0)
                return Vector2.zero;
            if (route.Count == 1)
                return route[0];
            var totalLength = 0f;
            for (var index = 1; index < route.Count; index++)
                totalLength += Vector2.Distance(route[index - 1], route[index]);
            var remaining = totalLength * 0.5f;
            for (var index = 1; index < route.Count; index++)
            {
                var segmentLength = Vector2.Distance(route[index - 1], route[index]);
                if (remaining <= segmentLength)
                    return Vector2.Lerp(route[index - 1], route[index], segmentLength <= 0.001f ? 0f : remaining / segmentLength);
                remaining -= segmentLength;
            }
            return route[route.Count - 1];
        }

        private Rect GetScreenNodeRect(ArchitectorNodeLayoutRecord node)
        {
            return WorldToScreen(GetWorldNodeRect(node));
        }

        private Rect GetWorldNodeRect(ArchitectorNodeLayoutRecord node)
        {
            var worldPosition = draggingNode && dragStartWorldByNode.TryGetValue(node.nodeId, out var dragStart)
                ? dragStart + dragPreviewDelta
                : new Vector2(node.x, node.y);
            return new Rect(worldPosition.x, worldPosition.y, node.width, node.height);
        }

        private static string FacingSide(Vector2 origin, Vector2 target)
        {
            var delta = target - origin;
            if (Mathf.Abs(delta.x) >= Mathf.Abs(delta.y))
                return delta.x >= 0f ? "right" : "left";
            return delta.y >= 0f ? "bottom" : "top";
        }

        private static Vector2 SideAnchor(Rect rect, string side, float laneOffset = 0f)
        {
            return side switch
            {
                "left" => new Vector2(rect.xMin, rect.center.y + laneOffset),
                "right" => new Vector2(rect.xMax, rect.center.y + laneOffset),
                "top" => new Vector2(rect.center.x + laneOffset, rect.yMin),
                _ => new Vector2(rect.center.x + laneOffset, rect.yMax)
            };
        }

        private static Vector2 ProjectToSide(Rect rect, Vector2 point, string side)
        {
            const float padding = 12f;
            return side switch
            {
                "left" => new Vector2(rect.xMin, Mathf.Clamp(point.y, rect.yMin + padding, rect.yMax - padding)),
                "right" => new Vector2(rect.xMax, Mathf.Clamp(point.y, rect.yMin + padding, rect.yMax - padding)),
                "top" => new Vector2(Mathf.Clamp(point.x, rect.xMin + padding, rect.xMax - padding), rect.yMin),
                _ => new Vector2(Mathf.Clamp(point.x, rect.xMin + padding, rect.xMax - padding), rect.yMax)
            };
        }

        private static Vector2 SideDirection(string side)
        {
            return side switch
            {
                "left" => Vector2.left,
                "right" => Vector2.right,
                "top" => Vector2.down,
                _ => Vector2.up
            };
        }

        private void ResetEdgeCycle()
        {
            lastEdgeCyclePosition = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            lastEdgeCycleIds.Clear();
            lastEdgeCycleIndex = -1;
        }

        private static void RemoveDuplicateRoutePoints(List<Vector2> route)
        {
            for (var index = route.Count - 1; index > 0; index--)
            {
                if ((route[index] - route[index - 1]).sqrMagnitude < 0.01f)
                    route.RemoveAt(index);
            }
        }

        private static float DistanceToSegment(Vector2 point, Vector2 start, Vector2 end)
        {
            var segment = end - start;
            if (segment.sqrMagnitude < 0.001f)
                return Vector2.Distance(point, start);
            var t = Mathf.Clamp01(Vector2.Dot(point - start, segment) / segment.sqrMagnitude);
            return Vector2.Distance(point, start + segment * t);
        }

        private void DrawNodes(
            ArchitectorLayerDocuments documents,
            ArchitectorThemePaletteSettings palette,
            ArchitectorRepository repository)
        {
            EnsureNodeStyles();
            foreach (var nodeLayout in documents.layout.nodes)
            {
                var node = documents.semantic.nodes.FirstOrDefault(candidate => candidate.id == nodeLayout.nodeId);
                if (node == null)
                    continue;

                var worldPosition = draggingNode && dragStartWorldByNode.TryGetValue(node.id, out var dragStart)
                    ? dragStart + dragPreviewDelta
                    : new Vector2(nodeLayout.x, nodeLayout.y);
                var rect = WorldToScreen(new Rect(worldPosition.x, worldPosition.y, nodeLayout.width, nodeLayout.height));
                var membersVisible = zoom >= MemberVisibilityZoom;
                EditorGUI.DrawRect(rect, palette.node.ToColor());
                var headerBackgroundRect = membersVisible
                    ? new Rect(rect.x + 1f, rect.y + 1f, Mathf.Max(0f, rect.width - 2f), Mathf.Min(rect.height - 2f, EditorGUIUtility.singleLineHeight + 10f))
                    : new Rect(rect.x + 1f, rect.y + 1f, Mathf.Max(0f, rect.width - 2f), Mathf.Max(0f, rect.height - 2f));
                EditorGUI.DrawRect(headerBackgroundRect, palette.nodeHeader.ToColor());
                DrawBorder(rect, palette.nodeBorder.ToColor(), 1f);

                if (IsPortal(node))
                {
                    var portalOutlineRect = new Rect(
                        rect.x + 2f,
                        rect.y + 2f,
                        Mathf.Max(0f, rect.width - 4f),
                        Mathf.Max(0f, rect.height - 4f));
                    var portalOutline = string.Equals(node.portal.role, ArchitectorPortalRoles.Entry, StringComparison.Ordinal)
                        ? palette.entryPortalOutline.ToColor()
                        : palette.exitPortalOutline.ToColor();
                    DrawBorder(portalOutlineRect, portalOutline, 2f);
                }

                if (selectedNodeIds.Contains(node.id))
                    DrawBorder(rect, palette.selection.ToColor(), 2f);

                var horizontalPadding = Mathf.Clamp(8f * zoom, 4f, 8f);
                var headerY = membersVisible
                    ? rect.y + 5f
                    : rect.center.y - EditorGUIUtility.singleLineHeight * 0.5f;
                var headerRect = new Rect(rect.x + horizontalPadding, headerY, rect.width - horizontalPadding * 2f, EditorGUIUtility.singleLineHeight);
                var previousContentColor = GUI.contentColor;
                GUI.contentColor = palette.titleText.ToColor();
                var titleStyle = zoom < CompactTitleZoom ? centeredMiniBoldLabel : centeredBoldLabel;
                var title = TitleCaption(node, zoom < CompactTitleZoom, repository);
                GUI.Label(headerRect, new GUIContent(FitText(title, headerRect.width, titleStyle), node.responsibility ?? string.Empty), titleStyle);
                GUI.contentColor = palette.typeText.ToColor();
                if (!membersVisible)
                {
                    GUI.contentColor = previousContentColor;
                    continue;
                }

                var memberY = headerRect.yMax + 5f;
                if (IsPortal(node))
                {
                    if (!cachedPortalRelationshipRows.TryGetValue(node.id, out var rows))
                        rows = Array.Empty<ArchitectorPortalRelationshipRow>();
                    var portalAvailableRows = Mathf.Max(0,
                        Mathf.FloorToInt((rect.yMax - memberY - 5f) / EditorGUIUtility.singleLineHeight));
                    var displayed = Mathf.Min(rows.Count, portalAvailableRows);
                    for (var index = 0; index < displayed; index++)
                    {
                        var rowRect = new Rect(rect.x + 8f, memberY + index * EditorGUIUtility.singleLineHeight,
                            rect.width - 16f, EditorGUIUtility.singleLineHeight);
                        var row = rows[index];
                        var relationship = row.Relationship;
                        if (string.Equals(relationship.id, selectedEdgeId, StringComparison.Ordinal))
                        {
                            var selectionColor = palette.selectedEdge.ToColor();
                            selectionColor.a = 0.28f;
                            EditorGUI.DrawRect(rowRect, selectionColor);
                        }
                        GUI.Label(rowRect, FitText(row.Caption, rowRect.width, EditorStyles.miniLabel), EditorStyles.miniLabel);
                    }
                    if (rows.Count == 0)
                        GUI.Label(new Rect(rect.x + 8f, memberY, rect.width - 16f, EditorGUIUtility.singleLineHeight),
                            "Double-click to open", EditorStyles.centeredGreyMiniLabel);
                    GUI.contentColor = previousContentColor;
                    continue;
                }
                if (!string.IsNullOrEmpty(node.responsibility)
                    && memberY + EditorGUIUtility.singleLineHeight <= rect.yMax - 5f)
                {
                    var responsibilityRect = new Rect(rect.x + 8f, memberY, rect.width - 16f, EditorGUIUtility.singleLineHeight);
                    responsibilityStyle ??= new GUIStyle(EditorStyles.miniLabel) { fontStyle = FontStyle.Italic };
                    GUI.Label(
                        responsibilityRect,
                        new GUIContent(FitText(node.responsibility, responsibilityRect.width, responsibilityStyle), node.responsibility),
                        responsibilityStyle);
                    memberY += EditorGUIUtility.singleLineHeight;
                }
                var availableRows = Mathf.Max(0, Mathf.FloorToInt((rect.yMax - memberY - 5f) / EditorGUIUtility.singleLineHeight));
                var typeFacts = new List<string>();
                if (node.type?.baseType != null)
                    typeFacts.Add($"base · {ArchitectorTypeReference.FullName(node.type.baseType)}");
                typeFacts.AddRange((node.type?.interfaces ?? new List<ArchitectorTypeReferenceRecord>())
                    .Select(reference => $"interface · {ArchitectorTypeReference.FullName(reference)}"));
                var orderedMembers = ArchitectorMemberOrder.SortStable(node.members);
                var totalRows = typeFacts.Count + orderedMembers.Count;
                var displayedCount = Mathf.Min(totalRows, availableRows);
                for (var index = 0; index < displayedCount; index++)
                {
                    var memberRect = new Rect(rect.x + 8f, memberY + index * EditorGUIUtility.singleLineHeight, rect.width - 16f, EditorGUIUtility.singleLineHeight);
                    var value = index < typeFacts.Count
                        ? typeFacts[index]
                        : orderedMembers[index - typeFacts.Count].declaration;
                    var declaration = FitText(value, memberRect.width, EditorStyles.miniLabel);
                    GUI.Label(memberRect, declaration, EditorStyles.miniLabel);
                }
                if (totalRows > displayedCount && availableRows > 0)
                {
                    var moreRect = new Rect(rect.x + 8f, memberY + (availableRows - 1) * EditorGUIUtility.singleLineHeight, rect.width - 16f, EditorGUIUtility.singleLineHeight);
                    GUI.Label(moreRect, $"+{totalRows - displayedCount + 1} more…", EditorStyles.miniLabel);
                }
                GUI.contentColor = previousContentColor;
            }
        }

        private void DrawGrid(Rect rect, float configuredGridSize, Color color)
        {
            var spacing = Mathf.Max(8f, configuredGridSize * zoom);
            Handles.BeginGUI();
            Handles.color = color;
            var offsetX = Mathf.Repeat(pan.x, spacing);
            var offsetY = Mathf.Repeat(pan.y, spacing);
            for (var x = offsetX; x < rect.width; x += spacing)
                Handles.DrawLine(new Vector3(x, 0f), new Vector3(x, rect.height));
            for (var y = offsetY; y < rect.height; y += spacing)
                Handles.DrawLine(new Vector3(0f, y), new Vector3(rect.width, y));
            Handles.color = Color.white;
            Handles.EndGUI();
        }

        private static void DrawBorder(Rect rect, Color color, float thickness)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, thickness), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, thickness, rect.height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
        }

        private void DrawSelectionMarquee(Color selectionColor)
        {
            if (!boxSelecting)
                return;
            var rect = MakeRect(boxStart, boxCurrent);
            var fill = selectionColor;
            fill.a = 0.12f;
            EditorGUI.DrawRect(rect, fill);
            DrawBorder(rect, selectionColor, 1f);
        }

        private static string TitleCaption(
            ArchitectorNodeRecord node,
            bool compact,
            ArchitectorRepository repository)
        {
            if (IsPortal(node))
                return repository.FindLayer(node.portal.targetLayerId)?.name ?? $"Missing Layer ({node.title})";
            var genericCount = node.type?.genericParameters?.Count ?? 0;
            var genericSuffix = genericCount == 0
                ? string.Empty
                : compact
                    ? "<>"
                    : $"<{string.Join(", ", Enumerable.Range(1, genericCount).Select(index => $"T{index}"))}>";
            return $"{node.title}{genericSuffix}";
        }

        public Vector2 CanvasToWorld(Vector2 canvasPoint)
        {
            return ScreenToWorld(canvasPoint);
        }

        private static bool IsPortal(ArchitectorNodeRecord node)
        {
            return node != null
                   && string.Equals(node.kind, ArchitectorNodeKinds.LayerPortal, StringComparison.Ordinal);
        }


        private static string FitText(string value, float availableWidth, GUIStyle style)
        {
            if (string.IsNullOrEmpty(value) || availableWidth <= 0f)
                return string.Empty;
            if (style.CalcSize(new GUIContent(value)).x <= availableWidth)
                return value;

            const string ellipsis = "…";
            if (style.CalcSize(new GUIContent(ellipsis)).x > availableWidth)
                return string.Empty;
            var low = 0;
            var high = value.Length;
            while (low < high)
            {
                var middle = (low + high + 1) / 2;
                var candidate = value.Substring(0, middle).TrimEnd() + ellipsis;
                if (style.CalcSize(new GUIContent(candidate)).x <= availableWidth)
                    low = middle;
                else
                    high = middle - 1;
            }
            return value.Substring(0, low).TrimEnd() + ellipsis;
        }

        private void EnsureNodeStyles()
        {
            centeredBoldLabel ??= new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                clipping = TextClipping.Clip
            };
            centeredMiniBoldLabel ??= new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                clipping = TextClipping.Clip
            };
        }

        private string HitTestNode(Vector2 screenPosition, ArchitectorLayerLayoutDocument layout)
        {
            for (var index = layout.nodes.Count - 1; index >= 0; index--)
            {
                var node = layout.nodes[index];
                var worldPosition = draggingNode && dragStartWorldByNode.TryGetValue(node.nodeId, out var dragStart)
                    ? dragStart + dragPreviewDelta
                    : new Vector2(node.x, node.y);
                if (WorldToScreen(new Rect(worldPosition.x, worldPosition.y, node.width, node.height)).Contains(screenPosition))
                    return node.nodeId;
            }
            return string.Empty;
        }

        private Rect WorldToScreen(Rect world)
        {
            return new Rect(pan.x + world.x * zoom, pan.y + world.y * zoom, world.width * zoom, world.height * zoom);
        }

        private Vector2 WorldToScreen(Vector2 world)
        {
            return pan + world * zoom;
        }

        private Vector2 ScreenToWorld(Vector2 screen)
        {
            return (screen - pan) / zoom;
        }

        private void ResetForLayer(string newLayerId, ArchitectorUserStateStore userState)
        {
            layerId = newLayerId;
            selectedNodeIds.Clear();
            selectedEdgeId = string.Empty;
            ResetEdgeCycle();
            lastVisibilitySelectionKey = string.Empty;
            selectionVisibilityPending = false;
            selectionVisibilitySawLayout = false;
            edgeRouteCache.Clear();
            edgeRouteCacheRevision = -1;
            edgeLaneCacheRevision = -1;
            hasCachedDocuments = false;
            cachedDocumentsRevision = -1;
            cachedPortalRelationshipRows =
                new Dictionary<string, IReadOnlyList<ArchitectorPortalRelationshipRow>>(StringComparer.Ordinal);
            cachedPortalRelationshipRowsRevision = -1;
            var state = userState.GetLayerView(viewId, layerId);
            pan = new Vector2(state.panX, state.panY);
            zoom = Mathf.Clamp(state.zoom, MinimumZoom, MaximumZoom);
            draggingNode = false;
            dragStartWorldByNode.Clear();
            dragPreviewDelta = Vector2.zero;
            boxSelecting = false;
            panning = false;
        }

        private struct ConnectionAffordance
        {
            public bool visible;
            public string targetNodeId;
            public string targetSide;
            public Rect buttonRect;
        }

        private sealed class EdgeLaneGroup
        {
            public readonly Dictionary<string, float> outgoingTangentByEdgeId = new(StringComparer.Ordinal);
            public readonly Dictionary<string, float> incomingTangentByEdgeId = new(StringComparer.Ordinal);
        }

        private sealed class OccupiedEdgePort
        {
            public OccupiedEdgePort(bool outgoing, string kind, List<string> edgeIds, float averageTangent)
            {
                this.outgoing = outgoing;
                this.kind = kind;
                this.edgeIds = edgeIds;
                this.averageTangent = averageTangent;
            }

            public readonly bool outgoing;
            public readonly string kind;
            public readonly List<string> edgeIds;
            public readonly float averageTangent;
        }

        private readonly struct RouteState : IEquatable<RouteState>
        {
            public RouteState(int x, int y, int direction)
            {
                this.x = x;
                this.y = y;
                this.direction = direction;
            }

            public readonly int x;
            public readonly int y;
            public readonly int direction;

            public bool Equals(RouteState other)
            {
                return x == other.x && y == other.y && direction == other.direction;
            }

            public override bool Equals(object obj)
            {
                return obj is RouteState other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((x * 397) ^ y) * 397 ^ direction;
                }
            }
        }

        private readonly struct RouteOpenItem : IComparable<RouteOpenItem>
        {
            public RouteOpenItem(float priority, int serial, RouteState state)
            {
                this.priority = priority;
                this.serial = serial;
                this.state = state;
            }

            public readonly float priority;
            public readonly int serial;
            public readonly RouteState state;

            public int CompareTo(RouteOpenItem other)
            {
                var priorityComparison = priority.CompareTo(other.priority);
                return priorityComparison != 0 ? priorityComparison : serial.CompareTo(other.serial);
            }
        }
    }
}
