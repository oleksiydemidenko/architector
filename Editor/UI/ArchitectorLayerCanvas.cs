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
        public const string CommandEventName = "ArchitectorCanvasCommand";
        private const float RightButtonPanThresholdSquared = 16f;
        private const float EdgeCornerRadius = 8f;
        private const int EdgeTierUnrelated = 0;
        private const int EdgeTierIncoming = 1;
        private const int EdgeTierOutgoing = 2;
        private const int EdgeTierSelected = 3;
        private static readonly List<Vector2> EmptyRoute = new();

        private readonly string viewId;
        private string layerId = string.Empty;
        private readonly HashSet<string> selectedNodeIds = new(StringComparer.Ordinal);
        private string selectedEdgeId = string.Empty;
        private Vector2 selectedEdgeActionWorldPosition;
        private bool showSelectedEdgeDeleteAction;
        private Vector2 lastEdgeCyclePosition = new(float.PositiveInfinity, float.PositiveInfinity);
        private List<string> lastEdgeCycleIds = new();
        private int lastEdgeCycleIndex = -1;
        private readonly ArchitectorEdgeRouter edgeRouter = new();
        private readonly Dictionary<string, List<Vector2>> edgeWorldRoutes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Vector2>> edgeRouteCache = new(StringComparer.Ordinal);
        private readonly List<(ArchitectorEdgeRecord edge, List<Vector2> route)> edgesInTier = new();
        private Vector3[] routeBuffer = new Vector3[64];
        private readonly Vector3[] dashBuffer = new Vector3[2];
        private bool edgeRoutesDragged;
        private Vector2 edgeRouteDragDelta;
        private int edgeRouteCacheRevision = -1;
        private Vector2 edgeRouteCachePan;
        private float edgeRouteCacheZoom = -1f;
        private int currentRepositoryRevision;
        private Vector2 pan = new(48f, 48f);
        private float zoom = 1f;
        private Vector2 canvasSize;
        private Vector2 lastPointer;
        private Vector2 lastPointerScreen;
        private bool pointerOverCanvas;
        private string pendingCommand;
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
        public bool HasPointer => pointerOverCanvas;

        public void SendCommand(string command)
        {
            pendingCommand = command;
        }
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
            canvasSize = rect.size;
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
                DrawEdges(documents, palette, localRect);
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
            if (current.isMouse || current.type == EventType.ScrollWheel)
            {
                pointerOverCanvas = canvasRect.Contains(mouse);
                lastPointer = mouse;
                lastPointerScreen = GUIUtility.GUIToScreenPoint(mouse);
            }
            else if (current.type == EventType.MouseLeaveWindow)
            {
                pointerOverCanvas = false;
            }
            if (current.type == EventType.ExecuteCommand && current.commandName == CommandEventName && pendingCommand != null)
            {
                var command = pendingCommand;
                pendingCommand = null;
                RunCommand(command, documents, repository, typeIndex, userState, execute, requestRepaint, openLayer);
                current.Use();
                return;
            }
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

            if (current.type == EventType.KeyDown && !EditorGUIUtility.editingTextField)
            {
                var reservedCommand = current.keyCode switch
                {
                    KeyCode.Escape => ArchitectorShortcuts.ClearSelection,
                    KeyCode.Return or KeyCode.KeypadEnter => ArchitectorShortcuts.OpenPortal,
                    _ => null
                };
                if (reservedCommand != null)
                {
                    RunCommand(reservedCommand, documents, repository, typeIndex, userState, execute, requestRepaint, openLayer);
                    current.Use();
                    return;
                }
            }

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
                AddArrangeItems(menu, repository, documents, userState, execute, requestRepaint, selectedForDelete);
                if (selectedForDelete.Length == 1)
                {
                    var relinkNode = documents.semantic.nodes.First(node => node.id == selectedForDelete[0]);
                    if (string.Equals(relinkNode.type?.mode, "linked", StringComparison.Ordinal))
                        menu.AddItem(new GUIContent("Relink Type"), false,
                            () => OpenRelinkPicker(relinkNode, documents, repository, typeIndex, execute, pickerScreenPosition));
                }
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
                        menu.AddItem(new GUIContent("Link Existing Type"), false,
                            () => OpenLinkPicker(selectedNode, documents, repository, typeIndex, execute, pickerScreenPosition));
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
                AddArrangeItems(menu, repository, documents, userState, execute, requestRepaint, selectedNodeIds.ToArray());
                menu.AddSeparator(string.Empty);
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

        private void AddArrangeItems(
            GenericMenu menu,
            ArchitectorRepository repository,
            ArchitectorLayerDocuments documents,
            ArchitectorUserStateStore userState,
            Action<Action> execute,
            Action requestRepaint,
            IReadOnlyCollection<string> selected)
        {
            var allNodes = documents.semantic.nodes.Select(node => node.id).ToArray();
            menu.AddItem(new GUIContent("Focus View"), false,
                () => FocusView(repository, selected.Count > 0 ? selected : allNodes, userState, requestRepaint));
            menu.AddItem(new GUIContent("Arrange"), false,
                () => ArrangeSelection(repository, documents, userState, execute, requestRepaint, selected));
            if (selected.Count > 0)
                menu.AddItem(new GUIContent("Arrange As Centers"), false,
                    () => ArrangeAndFocus(repository, userState, execute, requestRepaint, allNodes, selected));
        }

        private void ArrangeSelection(
            ArchitectorRepository repository,
            ArchitectorLayerDocuments documents,
            ArchitectorUserStateStore userState,
            Action<Action> execute,
            Action requestRepaint,
            IReadOnlyCollection<string> selected)
        {
            if (selected.Count == 0)
            {
                ArrangeAndFocus(repository, userState, execute, requestRepaint, documents.semantic.nodes.Select(node => node.id).ToArray(), null);
                return;
            }
            if (selected.Count > 1)
            {
                ArrangeAndFocus(repository, userState, execute, requestRepaint, selected, null);
                return;
            }
            var neighbours = documents.semantic.edges
                .Where(edge => selected.Contains(edge.sourceNodeId) || selected.Contains(edge.targetNodeId))
                .SelectMany(edge => new[] { edge.sourceNodeId, edge.targetNodeId });
            ArrangeAndFocus(repository, userState, execute, requestRepaint, selected.Concat(neighbours).Distinct().ToArray(), selected);
        }

        private void ArrangeAndFocus(
            ArchitectorRepository repository,
            ArchitectorUserStateStore userState,
            Action<Action> execute,
            Action requestRepaint,
            IReadOnlyCollection<string> nodes,
            IReadOnlyCollection<string> centers)
        {
            execute(() => repository.ArrangeNodes(layerId, nodes, centers));
            FocusView(repository, nodes, userState, requestRepaint);
        }

        private void OpenLinkPicker(
            ArchitectorNodeRecord node,
            ArchitectorLayerDocuments documents,
            ArchitectorRepository repository,
            ArchitectorTypeIndex typeIndex,
            Action<Action> execute,
            Vector2 screenPosition)
        {
            var available = AvailableTypes(typeIndex, documents.semantic);
            EditorApplication.delayCall += () =>
                ArchitectorTypePickerWindow.Show(screenPosition, available, indexedType =>
                {
                    string replacementId = null;
                    execute(() => replacementId = repository.ConvertEmptyNodeToLinked(layerId, node.id, indexedType));
                    if (!string.IsNullOrEmpty(replacementId))
                        SelectOnly(replacementId);
                });
        }

        private void OpenRelinkPicker(
            ArchitectorNodeRecord node,
            ArchitectorLayerDocuments documents,
            ArchitectorRepository repository,
            ArchitectorTypeIndex typeIndex,
            Action<Action> execute,
            Vector2 screenPosition)
        {
            var available = AvailableTypes(typeIndex, documents.semantic);
            EditorApplication.delayCall += () =>
                ArchitectorTypePickerWindow.Show(screenPosition, available, indexedType =>
                {
                    var record = new ArchitectorTypeRecord { mode = "linked", assemblyName = indexedType.AssemblyName, metadataName = indexedType.MetadataName };
                    execute(() => repository.RelinkNode(layerId, node.id, indexedType, typeIndex.GetMembers(record).ToArray()));
                });
        }

        private void RunCommand(
            string command,
            ArchitectorLayerDocuments documents,
            ArchitectorRepository repository,
            ArchitectorTypeIndex typeIndex,
            ArchitectorUserStateStore userState,
            Action<Action> execute,
            Action requestRepaint,
            Action<string> openLayer)
        {
            var selected = selectedNodeIds.ToArray();
            var allNodes = documents.semantic.nodes.Select(node => node.id).ToArray();
            var single = selected.Length == 1 ? documents.semantic.nodes.FirstOrDefault(node => node.id == selected[0]) : null;
            var editable = single != null && !IsPortal(single);
            switch (command)
            {
                case ArchitectorShortcuts.FocusView:
                    FocusView(repository, selected.Length == 0 ? allNodes : selected, userState, requestRepaint);
                    break;
                case ArchitectorShortcuts.FocusLayer:
                    FocusView(repository, allNodes, userState, requestRepaint);
                    break;
                case ArchitectorShortcuts.Arrange:
                    ArrangeSelection(repository, documents, userState, execute, requestRepaint, selected);
                    break;
                case ArchitectorShortcuts.ArrangeAsCenters when selected.Length > 0:
                    ArrangeAndFocus(repository, userState, execute, requestRepaint, allNodes, selected);
                    break;
                case ArchitectorShortcuts.SelectAll:
                    selectedNodeIds.Clear();
                    selectedNodeIds.UnionWith(allNodes);
                    break;
                case ArchitectorShortcuts.ClearSelection:
                    selectedNodeIds.Clear();
                    selectedEdgeId = string.Empty;
                    break;
                case ArchitectorShortcuts.ActualSize:
                {
                    var center = canvasSize * 0.5f;
                    var worldAtCenter = ScreenToWorld(center);
                    zoom = 1f;
                    pan = center - worldAtCenter;
                    userState.SaveLayerView(viewId, layerId, pan.x, pan.y, zoom);
                    break;
                }
                case ArchitectorShortcuts.NewNode:
                {
                    var requested = ScreenToWorld(lastPointer) - new Vector2(140f, 60f);
                    execute(() =>
                    {
                        selectedNodeIds.Clear();
                        selectedNodeIds.Add(repository.CreateEmptyNode(layerId, requested.x, requested.y));
                    });
                    break;
                }
                case ArchitectorShortcuts.Rename when editable:
                    ArchitectorNodeInspector.TitleFocusRequested = true;
                    break;
                case ArchitectorShortcuts.LinkType when editable:
                {
                    if (string.Equals(single.type?.mode, "linked", StringComparison.Ordinal))
                        OpenRelinkPicker(single, documents, repository, typeIndex, execute, lastPointerScreen);
                    else if (string.Equals(single.type?.mode, "none", StringComparison.Ordinal))
                        OpenLinkPicker(single, documents, repository, typeIndex, execute, lastPointerScreen);
                    break;
                }
                case ArchitectorShortcuts.OpenPortal when single != null && IsPortal(single):
                    openLayer?.Invoke(single.portal.targetLayerId);
                    break;
                case ArchitectorShortcuts.NeighbourLeft:
                    SelectNeighbour(documents, single, Vector2.left);
                    break;
                case ArchitectorShortcuts.NeighbourRight:
                    SelectNeighbour(documents, single, Vector2.right);
                    break;
                case ArchitectorShortcuts.NeighbourUp:
                    SelectNeighbour(documents, single, Vector2.down);
                    break;
                case ArchitectorShortcuts.NeighbourDown:
                    SelectNeighbour(documents, single, Vector2.up);
                    break;
            }
            requestRepaint?.Invoke();
        }

        private void SelectNeighbour(ArchitectorLayerDocuments documents, ArchitectorNodeRecord node, Vector2 direction)
        {
            const float minimumAlignment = 0.5f;
            if (node == null)
                return;
            var layouts = documents.layout.nodes.ToDictionary(item => item.nodeId, StringComparer.Ordinal);
            var origin = GetWorldNodeRect(layouts[node.id]).center;
            var neighbour = documents.semantic.edges
                .Where(edge => edge.sourceNodeId == node.id || edge.targetNodeId == node.id)
                .Select(edge => edge.sourceNodeId == node.id ? edge.targetNodeId : edge.sourceNodeId)
                .Where(id => id != node.id && layouts.ContainsKey(id))
                .Distinct()
                .Select(id => (id, delta: GetWorldNodeRect(layouts[id]).center - origin))
                .Where(item => Vector2.Dot(item.delta.normalized, direction) >= minimumAlignment)
                .OrderBy(item => item.delta.sqrMagnitude)
                .ThenBy(item => item.id, StringComparer.Ordinal)
                .Select(item => item.id)
                .FirstOrDefault();
            if (neighbour != null)
                SelectOnly(neighbour);
        }

        private void FocusView(
            ArchitectorRepository repository,
            IReadOnlyCollection<string> nodeIds,
            ArchitectorUserStateStore userState,
            Action requestRepaint)
        {
            const float margin = 48f;
            var ids = new HashSet<string>(nodeIds, StringComparer.Ordinal);
            var nodes = repository.LoadLayer(layerId).layout.nodes.Where(node => ids.Contains(node.nodeId)).ToArray();
            if (nodes.Length == 0)
                return;
            var bounds = Rect.MinMaxRect(
                nodes.Min(node => node.x),
                nodes.Min(node => node.y),
                nodes.Max(node => node.x + node.width),
                nodes.Max(node => node.y + node.height));
            var fit = Mathf.Min(
                (canvasSize.x - margin * 2f) / bounds.width,
                (canvasSize.y - margin * 2f) / bounds.height);
            zoom = Mathf.Clamp(Mathf.Min(fit, 1f), MinimumZoom, MaximumZoom);
            pan = canvasSize * 0.5f - bounds.center * zoom;
            userState.SaveLayerView(viewId, layerId, pan.x, pan.y, zoom);
            requestRepaint?.Invoke();
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

        private void DrawEdges(ArchitectorLayerDocuments documents, ArchitectorThemePaletteSettings palette, Rect viewRect)
        {
            EnsureEdgeRouteCache(documents);
            var casing = palette.workspace.ToColor();
            var hasSelection = selectedNodeIds.Count > 0 || !string.IsNullOrEmpty(selectedEdgeId);
            Handles.BeginGUI();
            for (var tier = EdgeTierUnrelated; tier <= EdgeTierSelected; tier++)
            {
                edgesInTier.Clear();
                foreach (var edge in documents.semantic.edges)
                {
                    if (EdgeTier(edge) != tier)
                        continue;
                    var route = GetEdgeRoute(edge, documents);
                    if (route.Count >= 2 && RouteIsVisible(route, viewRect))
                        edgesInTier.Add((edge, route));
                }

                var width = tier == EdgeTierSelected ? 3f : tier == EdgeTierUnrelated ? 1.75f : 2.5f;
                var arrowLength = tier == EdgeTierUnrelated ? 9f : 12f;
                foreach (var (edge, route) in edgesInTier)
                {
                    var filled = string.Equals(edge.kind, ArchitectorEdgeKinds.Dependency, StringComparison.Ordinal);
                    var count = FillRouteBuffer(route, filled ? arrowLength * 0.8f : 0f);
                    if (tier != EdgeTierUnrelated)
                    {
                        Handles.color = casing;
                        Handles.DrawAAPolyLine(width + 3f, count, routeBuffer);
                    }
                    Handles.color = EdgeDrawColor(edge, palette, tier, hasSelection);
                    DrawEdgeRoute(count, edge.kind, filled ? width + 0.25f : width);
                }
                foreach (var (edge, route) in edgesInTier)
                {
                    DrawArrowHead(
                        route[route.Count - 2],
                        route[route.Count - 1],
                        EdgeDrawColor(edge, palette, tier, hasSelection),
                        width,
                        arrowLength,
                        string.Equals(edge.kind, ArchitectorEdgeKinds.Dependency, StringComparison.Ordinal));
                }
            }
            Handles.color = Color.white;
            Handles.EndGUI();
        }

        private int EdgeTier(ArchitectorEdgeRecord edge)
        {
            if (string.Equals(edge.id, selectedEdgeId, StringComparison.Ordinal))
                return EdgeTierSelected;
            if (selectedNodeIds.Contains(edge.sourceNodeId))
                return EdgeTierOutgoing;
            return selectedNodeIds.Contains(edge.targetNodeId) ? EdgeTierIncoming : EdgeTierUnrelated;
        }

        private Color EdgeDrawColor(ArchitectorEdgeRecord edge, ArchitectorThemePaletteSettings palette, int tier, bool hasSelection)
        {
            var color = EdgeColor(edge, palette);
            if (tier == EdgeTierUnrelated && hasSelection)
                color.a *= 0.45f;
            return color;
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

        private static bool RouteIsVisible(IReadOnlyList<Vector2> route, Rect viewRect)
        {
            var min = route[0];
            var max = route[0];
            for (var index = 1; index < route.Count; index++)
            {
                min = Vector2.Min(min, route[index]);
                max = Vector2.Max(max, route[index]);
            }
            return max.x >= viewRect.xMin - 16f && min.x <= viewRect.xMax + 16f
                                                && max.y >= viewRect.yMin - 16f && min.y <= viewRect.yMax + 16f;
        }

        private int FillRouteBuffer(IReadOnlyList<Vector2> route, float tipInset)
        {
            if (routeBuffer.Length < route.Count)
                routeBuffer = new Vector3[route.Count * 2];
            for (var index = 0; index < route.Count; index++)
                routeBuffer[index] = route[index];
            var last = route.Count - 1;
            var finalSegment = route[last] - route[last - 1];
            if (tipInset > 0f && finalSegment.magnitude > tipInset)
                routeBuffer[last] = route[last] - finalSegment.normalized * tipInset;
            return route.Count;
        }

        private void DrawEdgeRoute(int count, string kind, float width)
        {
            switch (kind)
            {
                case ArchitectorEdgeKinds.DataFlow:
                    DrawPatternedRoute(count, width, 10f, 6f);
                    return;
                case ArchitectorEdgeKinds.Event:
                    DrawPatternedRoute(count, width, 1.5f, 4.5f);
                    return;
                case ArchitectorEdgeKinds.Callback:
                    DrawPatternedRoute(count, width, 9f, 4f, 1.5f, 4f);
                    return;
                default:
                    Handles.DrawAAPolyLine(width, count, routeBuffer);
                    return;
            }
        }

        private void DrawPatternedRoute(int count, float width, params float[] pattern)
        {
            var patternIndex = 0;
            var patternRemaining = pattern[0];
            var drawing = true;
            for (var routeIndex = 1; routeIndex < count; routeIndex++)
            {
                Vector2 cursor = routeBuffer[routeIndex - 1];
                var segment = (Vector2)routeBuffer[routeIndex] - cursor;
                var segmentRemaining = segment.magnitude;
                if (segmentRemaining <= 0.001f)
                    continue;
                var direction = segment / segmentRemaining;
                while (segmentRemaining > 0.001f)
                {
                    var step = Mathf.Min(segmentRemaining, patternRemaining);
                    var next = cursor + direction * step;
                    if (drawing)
                    {
                        dashBuffer[0] = cursor;
                        dashBuffer[1] = next;
                        Handles.DrawAAPolyLine(width, 2, dashBuffer);
                    }
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

        private static void DrawArrowHead(Vector2 previous, Vector2 tip, Color color, float width, float length, bool filled)
        {
            var direction = (tip - previous).normalized;
            if (direction.sqrMagnitude < 0.5f)
                return;
            var perpendicular = new Vector2(-direction.y, direction.x);
            var basePoint = tip - direction * length;
            Handles.color = color;
            var firstWing = basePoint + perpendicular * length * 0.5f;
            var secondWing = basePoint - perpendicular * length * 0.5f;
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
            return edgeRouteCache.TryGetValue(edge.id, out var route) ? route : EmptyRoute;
        }

        private bool HasDragMovement => draggingNode && dragPreviewDelta.sqrMagnitude > DragMovementThresholdSquared;

        private void EnsureEdgeRouteCache(ArchitectorLayerDocuments documents)
        {
            var dragged = HasDragMovement;
            var layoutChanged = edgeRouteCacheRevision != currentRepositoryRevision
                                || edgeRoutesDragged != dragged
                                || dragged && edgeRouteDragDelta != dragPreviewDelta;
            if (layoutChanged)
            {
                RebuildEdgeRoutes(documents);
                edgeRouteCacheRevision = currentRepositoryRevision;
                edgeRoutesDragged = dragged;
                edgeRouteDragDelta = dragPreviewDelta;
            }
            else if (Mathf.Approximately(edgeRouteCacheZoom, zoom) && (edgeRouteCachePan - pan).sqrMagnitude < 0.001f)
            {
                return;
            }

            foreach (var pair in edgeWorldRoutes)
            {
                if (!edgeRouteCache.TryGetValue(pair.Key, out var screenRoute))
                {
                    screenRoute = new List<Vector2>(pair.Value.Count);
                    edgeRouteCache.Add(pair.Key, screenRoute);
                }
                screenRoute.Clear();
                foreach (var point in pair.Value)
                    screenRoute.Add(WorldToScreen(point));
            }
            edgeRouteCacheZoom = zoom;
            edgeRouteCachePan = pan;
        }

        private void RebuildEdgeRoutes(ArchitectorLayerDocuments documents)
        {
            var rects = new Dictionary<string, Rect>(documents.layout.nodes.Count, StringComparer.Ordinal);
            foreach (var node in documents.layout.nodes)
                rects[node.nodeId] = GetWorldNodeRect(node);
            var edgeLayouts = new Dictionary<string, ArchitectorEdgeLayoutRecord>(StringComparer.Ordinal);
            foreach (var edgeLayout in documents.layout.edges)
                edgeLayouts[edgeLayout.edgeId] = edgeLayout;
            var requests = new List<ArchitectorRouteRequest>(documents.semantic.edges.Count);
            foreach (var edge in documents.semantic.edges)
            {
                edgeLayouts.TryGetValue(edge.id, out var edgeLayout);
                requests.Add(new ArchitectorRouteRequest(
                    edge.id,
                    edge.sourceNodeId,
                    edge.targetNodeId,
                    edgeLayout?.preferredSourceSide,
                    edgeLayout?.preferredTargetSide));
            }

            edgeWorldRoutes.Clear();
            edgeRouteCache.Clear();
            foreach (var route in edgeRouter.Route(rects, requests))
                edgeWorldRoutes[route.Key] = ArchitectorEdgeRouter.RoundCorners(route.Value, EdgeCornerRadius);
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
                var anchor = ArchitectorEdgeRouter.Anchor(rect, side);
                var center = anchor + ArchitectorEdgeRouter.SideDirection(side) * 12f;
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

        private void ResetEdgeCycle()
        {
            lastEdgeCyclePosition = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            lastEdgeCycleIds.Clear();
            lastEdgeCycleIndex = -1;
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
            edgeWorldRoutes.Clear();
            edgeRouteCacheRevision = -1;
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
    }
}
