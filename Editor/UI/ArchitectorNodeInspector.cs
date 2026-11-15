using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor
{
    public sealed class ArchitectorNodeInspector
    {
        public const float Width = 430f;

        private static readonly string[] TypeKinds =
        {
            "class", "interface", "struct", "enum", "delegate", "record class", "record struct"
        };

        private static readonly string[] MemberKinds =
        {
            "field", "constant", "property", "indexer", "event", "constructor", "method", "operator"
        };

        private string loadedLayerId = string.Empty;
        private string loadedNodeId = string.Empty;
        private int loadedRevision = -1;
        private string nodeTitle = string.Empty;
        private string typeKind = "class";
        private string typeNamespace = string.Empty;
        private string typeName = string.Empty;
        private string genericParameters = string.Empty;
        private string modifiers = string.Empty;
        private string baseTypeName = string.Empty;
        private List<string> interfaceNames = new();
        private List<ArchitectorMemberRecord> members = new();
        private Vector2 scroll;
        private bool dirty;
        public static bool TitleFocusRequested { get; set; }

        private static string DrawTitleField(string title)
        {
            const string controlName = "ArchitectorNodeTitle";
            GUI.SetNextControlName(controlName);
            var result = EditorGUILayout.TextField("Node Title", title);
            if (TitleFocusRequested && Event.current.type == EventType.Repaint)
            {
                TitleFocusRequested = false;
                EditorGUI.FocusTextInControl(controlName);
            }
            return result;
        }

        private ArchitectorLayerDocuments cachedDocuments;
        private string cachedDocumentsLayerId = string.Empty;
        private int cachedDocumentsRevision = -1;
        private IReadOnlyList<ArchitectorPortalRelationshipRow> cachedPortalRelationshipRows =
            Array.Empty<ArchitectorPortalRelationshipRow>();
        private string cachedPortalRelationshipLayerId = string.Empty;
        private string cachedPortalRelationshipNodeId = string.Empty;
        private int cachedPortalRelationshipRevision = -1;
        private string pendingFocusNodeId = string.Empty;
        private string pendingFocusMemberId = string.Empty;
        private bool membersExpanded = true;
        private bool relationshipsExpanded = true;
        private IReadOnlyList<ArchitectorNodeRelationshipGroup> cachedRelationshipGroups =
            Array.Empty<ArchitectorNodeRelationshipGroup>();
        private string cachedRelationshipLayerId = string.Empty;
        private string cachedRelationshipNodeId = string.Empty;
        private int cachedRelationshipRevision = -1;

        public void RequestMemberFocus(string nodeId, string memberId)
        {
            pendingFocusNodeId = nodeId ?? string.Empty;
            pendingFocusMemberId = memberId ?? string.Empty;
        }

        public void Draw(
            string layerId,
            string nodeId,
            ArchitectorRepository repository,
            ArchitectorTypeIndex typeIndex,
            Action<Action> execute,
            Action<string> selectOnly,
            Action<string> openLayer,
            Action<string, string> revealNode = null)
        {
            if (!string.Equals(cachedDocumentsLayerId, layerId, StringComparison.Ordinal)
                || cachedDocumentsRevision != repository.Revision)
            {
                cachedDocuments = repository.LoadLayer(layerId);
                cachedDocumentsLayerId = layerId;
                cachedDocumentsRevision = repository.Revision;
            }
            var documents = cachedDocuments;
            var node = documents.semantic.nodes.FirstOrDefault(candidate => candidate.id == nodeId);
            if (node == null)
                return;

            if (!string.Equals(loadedLayerId, layerId, StringComparison.Ordinal)
                || !string.Equals(loadedNodeId, nodeId, StringComparison.Ordinal)
                || (!dirty && loadedRevision != repository.Revision))
            {
                Load(node, layerId, repository.Revision);
            }

            using (new LabelWidthScope(155f))
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.Width(Width), GUILayout.ExpandHeight(true)))
            {
                GUILayout.Label("Node Inspector", EditorStyles.boldLabel);
                if (string.Equals(node.kind, ArchitectorNodeKinds.LayerPortal, StringComparison.Ordinal))
                {
                    DrawLayerPortal(layerId, node, repository, execute, openLayer);
                    return;
                }
                DrawResponsibility(layerId, node, repository, execute);
                if (string.Equals(node.type?.mode, "none", StringComparison.Ordinal))
                {
                    DrawEmptyNode(node, layerId, repository, typeIndex, execute, selectOnly, revealNode);
                    return;
                }
                if (string.Equals(node.type?.mode, "linked", StringComparison.Ordinal))
                {
                    DrawLinkedNode(node, layerId, repository, typeIndex, execute, revealNode);
                    return;
                }

                EditorGUI.BeginChangeCheck();
                scroll = EditorGUILayout.BeginScrollView(scroll);
                nodeTitle = DrawTitleField(nodeTitle);
                var typeKindIndex = Mathf.Max(0, Array.IndexOf(TypeKinds, typeKind));
                typeKind = TypeKinds[EditorGUILayout.Popup("Type Kind", typeKindIndex, TypeKinds)];
                typeNamespace = EditorGUILayout.TextField("Namespace", typeNamespace);
                typeName = EditorGUILayout.TextField("Type Name", typeName);
                genericParameters = EditorGUILayout.TextField("Generic Parameters", genericParameters);
                modifiers = EditorGUILayout.TextField("Modifiers", modifiers);

                EditorGUILayout.Space(8f);
                GUILayout.Label("Inheritance", EditorStyles.boldLabel);
                baseTypeName = EditorGUILayout.TextField("Direct Base Type", baseTypeName);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label($"Interfaces ({interfaceNames.Count})", EditorStyles.miniBoldLabel);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Add", EditorStyles.miniButton, GUILayout.Width(50f)))
                    {
                        interfaceNames.Add("Namespace.IInterface");
                        GUI.changed = true;
                    }
                }
                for (var index = 0; index < interfaceNames.Count; index++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        interfaceNames[index] = EditorGUILayout.TextField(interfaceNames[index]);
                        if (GUILayout.Button("−", EditorStyles.miniButton, GUILayout.Width(24f)))
                        {
                            interfaceNames.RemoveAt(index);
                            GUI.changed = true;
                            GUI.FocusControl(null);
                            break;
                        }
                    }
                }

                EditorGUILayout.Space(10f);
                using (var box = FoldoutBox($"Members ({members.Count})", ref membersExpanded))
                {
                    if (box.Expanded)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.FlexibleSpace();
                            if (GUILayout.Button("Add", EditorStyles.miniButton, GUILayout.Width(50f)))
                                ShowAddMemberMenu();
                        }

                        members = ArchitectorMemberOrder.SortStable(members);
                        for (var index = 0; index < members.Count; index++)
                        {
                            var member = members[index];
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                var memberKindIndex = Mathf.Max(0, Array.IndexOf(MemberKinds, member.kind));
                                member.kind = MemberKinds[EditorGUILayout.Popup(memberKindIndex, MemberKinds, GUILayout.Width(88f))];
                                member.declaration = EditorGUILayout.TextField(member.declaration);
                                if (GUILayout.Button("−", EditorStyles.miniButton, GUILayout.Width(24f)))
                                {
                                    members.RemoveAt(index);
                                    GUI.changed = true;
                                    GUI.FocusControl(null);
                                    break;
                                }
                            }
                        }
                    }
                }
                DrawRelationships(layerId, node, repository, revealNode);
                EditorGUILayout.EndScrollView();
                if (EditorGUI.EndChangeCheck())
                    dirty = true;

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!dirty))
                    {
                        if (GUILayout.Button("Revert"))
                            Load(node, layerId, repository.Revision);
                    }
                    using (new EditorGUI.DisabledScope(!dirty || string.IsNullOrWhiteSpace(nodeTitle) || string.IsNullOrWhiteSpace(typeName)))
                    {
                        if (GUILayout.Button("Save"))
                        {
                            var revisionBeforeSave = repository.Revision;
                            execute(() => repository.UpdateManualNode(
                                layerId,
                                nodeId,
                                nodeTitle,
                                typeKind,
                                typeNamespace,
                                typeName,
                                Split(genericParameters),
                                Split(modifiers),
                                members,
                                string.IsNullOrWhiteSpace(baseTypeName)
                                    ? null
                                    : ArchitectorTypeReference.Declared(baseTypeName, "class"),
                                interfaceNames.Where(value => !string.IsNullOrWhiteSpace(value))
                                    .Select(value => ArchitectorTypeReference.Declared(value, "interface"))
                                    .ToArray()));
                            if (repository.Revision != revisionBeforeSave)
                            {
                                var savedNode = repository.LoadLayer(layerId).semantic.nodes.First(candidate => candidate.id == nodeId);
                                Load(savedNode, layerId, repository.Revision);
                            }
                        }
                    }
                }
                using (new EditorGUI.DisabledScope(dirty))
                {
                    if (GUILayout.Button("Link Existing Type"))
                    {
                        var documentsForPicker = repository.LoadLayer(layerId);
                        var represented = new HashSet<string>(documentsForPicker.semantic.nodes
                            .Where(candidate => candidate.id != node.id && string.Equals(candidate.type?.mode, "linked", StringComparison.Ordinal))
                            .Select(candidate => ArchitectorTypeIdentity.Key(candidate.type.assemblyName, candidate.type.metadataName)),
                            StringComparer.Ordinal);
                        var available = typeIndex.Types.Where(type => !represented.Contains(type.Key)).ToArray();
                        var screenPosition = GUIUtility.GUIToScreenPoint(Event.current.mousePosition);
                        ArchitectorTypePickerWindow.Show(screenPosition, available, indexedType =>
                        {
                            var linkedType = new ArchitectorTypeRecord
                            {
                                mode = "linked",
                                assemblyName = indexedType.AssemblyName,
                                metadataName = indexedType.MetadataName
                            };
                            var availableMembers = typeIndex.GetMembers(linkedType);
                            execute(() => repository.LinkManualNode(layerId, node.id, indexedType, availableMembers));
                        });
                    }
                }
            }
        }

        private static void DrawResponsibility(
            string layerId,
            ArchitectorNodeRecord node,
            ArchitectorRepository repository,
            Action<Action> execute)
        {
            var current = node.responsibility ?? string.Empty;
            var value = EditorGUILayout.DelayedTextField(
                new GUIContent("Responsibility", "One line: what this node owns or is for. It explains the node and is not part of the approved contract."),
                current);
            if (!string.Equals(value, current, StringComparison.Ordinal))
                execute(() => repository.UpdateNodeResponsibility(layerId, node.id, value));
            EditorGUILayout.Space(4f);
        }

        private void DrawLayerPortal(
            string layerId,
            ArchitectorNodeRecord node,
            ArchitectorRepository repository,
            Action<Action> execute,
            Action<string> openLayer)
        {
            var destination = repository.FindLayer(node.portal?.targetLayerId);
            EditorGUILayout.LabelField("Node Kind", "Layer Portal");
            EditorGUILayout.LabelField(
                "Portal Role",
                string.Equals(node.portal?.role, ArchitectorPortalRoles.Entry, StringComparison.Ordinal) ? "Entry" : "Exit");
            EditorGUILayout.LabelField("Destination", destination?.name ?? "Missing Layer");
            using (new EditorGUI.DisabledScope(destination == null))
            {
                if (GUILayout.Button("Open Destination"))
                    openLayer?.Invoke(destination.id);
            }
            EditorGUILayout.Space(6f);
            if (!string.Equals(cachedPortalRelationshipLayerId, layerId, StringComparison.Ordinal)
                || !string.Equals(cachedPortalRelationshipNodeId, node.id, StringComparison.Ordinal)
                || cachedPortalRelationshipRevision != repository.Revision)
            {
                cachedPortalRelationshipRows = repository.GetPortalRelationshipRows(layerId, node.id, false);
                cachedPortalRelationshipLayerId = layerId;
                cachedPortalRelationshipNodeId = node.id;
                cachedPortalRelationshipRevision = repository.Revision;
            }
            GUILayout.Label($"Relationships ({cachedPortalRelationshipRows.Count})", EditorStyles.boldLabel);
            foreach (var row in cachedPortalRelationshipRows)
            {
                var relationship = row.Relationship;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(
                            $"{ArchitectorEdgeKinds.DisplayName(relationship.kind)} · {row.Caption}",
                            EditorStyles.wordWrappedMiniLabel);
                        var previous = GUI.backgroundColor;
                        GUI.backgroundColor = new Color(1f, 0.62f, 0.56f);
                        var remove = GUILayout.Button(
                            new GUIContent("×", "Delete relationship"),
                            EditorStyles.miniButton,
                            GUILayout.Width(24f));
                        GUI.backgroundColor = previous;
                        if (remove)
                        {
                            execute(() => repository.DeleteEdge(layerId, relationship.id));
                            GUI.FocusControl(null);
                            return;
                        }
                    }
                    if (!string.IsNullOrEmpty(relationship.label))
                        GUILayout.Label(relationship.label, EditorStyles.miniLabel);
                }
            }
            EditorGUILayout.Space(6f);
            EditorGUILayout.HelpBox(
                "This Portal projects exact relationships between the two layers. Deleting either endpoint removes the Portal pair and those cross-layer relationships; Undo restores the whole operation.",
                destination == null ? MessageType.Error : MessageType.Info);
        }

        private void DrawEmptyNode(
            ArchitectorNodeRecord node,
            string layerId,
            ArchitectorRepository repository,
            ArchitectorTypeIndex typeIndex,
            Action<Action> execute,
            Action<string> selectOnly,
            Action<string, string> revealNode)
        {
            EditorGUI.BeginChangeCheck();
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.HelpBox(
                "Empty Node represents a high-level architectural idea without a C# type. It can later become a Manual Type or be linked to an indexed project type.",
                MessageType.Info);
            nodeTitle = DrawTitleField(nodeTitle);
            EditorGUILayout.Space(8f);
            DrawRelationships(layerId, node, repository, revealNode);
            EditorGUILayout.EndScrollView();
            if (EditorGUI.EndChangeCheck())
                dirty = true;

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!dirty))
                {
                    if (GUILayout.Button("Revert"))
                        Load(node, layerId, repository.Revision);
                }
                using (new EditorGUI.DisabledScope(!dirty || string.IsNullOrWhiteSpace(nodeTitle)))
                {
                    if (GUILayout.Button("Save"))
                    {
                        var revisionBeforeSave = repository.Revision;
                        execute(() => repository.RenameEmptyNode(layerId, node.id, nodeTitle));
                        if (repository.Revision != revisionBeforeSave)
                        {
                            var savedNode = repository.LoadLayer(layerId).semantic.nodes.First(candidate => candidate.id == node.id);
                            Load(savedNode, layerId, repository.Revision);
                        }
                    }
                }
            }

            EditorGUILayout.Space(10f);
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(nodeTitle)))
            {
                if (GUILayout.Button("Convert to Manual Type"))
                {
                    string replacementId = null;
                    execute(() => replacementId = repository.ConvertEmptyNodeToManual(layerId, node.id, nodeTitle));
                    if (!string.IsNullOrEmpty(replacementId))
                        selectOnly(replacementId);
                }
            }
            if (GUILayout.Button("Link Existing Type"))
            {
                var documents = repository.LoadLayer(layerId);
                var represented = new HashSet<string>(documents.semantic.nodes
                    .Where(candidate => string.Equals(candidate.type?.mode, "linked", StringComparison.Ordinal))
                    .Select(candidate => ArchitectorTypeIdentity.Key(candidate.type.assemblyName, candidate.type.metadataName)),
                    StringComparer.Ordinal);
                var available = typeIndex.Types.Where(type => !represented.Contains(type.Key)).ToArray();
                var screenPosition = GUIUtility.GUIToScreenPoint(Event.current.mousePosition);
                ArchitectorTypePickerWindow.Show(screenPosition, available, indexedType =>
                {
                    string replacementId = null;
                    execute(() => replacementId = repository.ConvertEmptyNodeToLinked(layerId, node.id, indexedType));
                    if (!string.IsNullOrEmpty(replacementId))
                        selectOnly(replacementId);
                });
            }
        }

        private void DrawLinkedNode(
            ArchitectorNodeRecord node,
            string layerId,
            ArchitectorRepository repository,
            ArchitectorTypeIndex typeIndex,
            Action<Action> execute,
            Action<string, string> revealNode)
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            var resolution = typeIndex.Resolve(node.type);
            EditorGUILayout.LabelField("Mode", "Linked Type");
            EditorGUILayout.LabelField("Status", resolution.ToString());
            EditorGUILayout.LabelField("Type", string.IsNullOrEmpty(node.type.typeNamespace)
                ? node.type.name
                : $"{node.type.typeNamespace}.{node.type.name}");
            EditorGUILayout.LabelField("Kind", node.type.kind);
            EditorGUILayout.LabelField("Assembly", node.type.assemblyName);
            EditorGUILayout.LabelField("Metadata Name", node.type.metadataName);
            DrawLinkedTypeFacts(node, layerId, repository, typeIndex, execute, resolution);
            EditorGUILayout.Space(8f);
            var declaration = node.type.sourceDeclarations?.FirstOrDefault();
            DrawPathLabel("Source", declaration?.repositoryPath ?? "Unavailable");
            using (new EditorGUI.DisabledScope(declaration == null))
            {
                if (GUILayout.Button("Open Script"))
                {
                    var script = AssetDatabase.LoadAssetAtPath<MonoScript>(declaration.repositoryPath);
                    if (script != null)
                    {
                        if (declaration.line > 0)
                            AssetDatabase.OpenAsset(script, declaration.line);
                        else
                            AssetDatabase.OpenAsset(script);
                    }
                }
            }
            EditorGUILayout.Space(4f);
            var standalone = repository.StandaloneReason(layerId, node.id);
            var editedStandalone = EditorGUILayout.DelayedTextField(
                new GUIContent("Standalone", "Why this linked node has no relationship on purpose, for example a type wired only in scenes. With a reason, the missing relationship is a warning and does not block approval. Clear the text to remove the mark."),
                standalone);
            if (!string.Equals(editedStandalone, standalone, StringComparison.Ordinal))
                execute(() => repository.SetStandaloneNode(layerId, node.id, editedStandalone));
            EditorGUILayout.Space(8f);
            // A member focus request (a diagnostic row) needs the member list open to scroll to it.
            if (string.Equals(pendingFocusNodeId, node.id, StringComparison.Ordinal))
                membersExpanded = true;
            using (var box = FoldoutBox($"Members ({node.members.Count})", ref membersExpanded))
            {
                if (box.Expanded)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        using (new EditorGUI.DisabledScope(resolution != ArchitectorTypeResolution.Resolved))
                        {
                            if (GUILayout.Button("Add", EditorStyles.miniButton, GUILayout.Width(50f)))
                            {
                                var selectedKeys = new HashSet<string>(node.members.Select(member => member.symbolKey), StringComparer.Ordinal);
                                var available = typeIndex.GetMembers(node.type)
                                    .Where(member => !selectedKeys.Contains(member.SymbolKey))
                                    .ToArray();
                                var screenPosition = GUIUtility.GUIToScreenPoint(Event.current.mousePosition);
                                ArchitectorMemberPickerWindow.Show(
                                    screenPosition,
                                    available,
                                    repository.Settings.optimization,
                                    indexedMember => execute(() => repository.AddLinkedMember(layerId, node.id, indexedMember)),
                                    indexedMembers => execute(() => repository.AddLinkedMembers(layerId, node.id, indexedMembers)));
                            }
                        }
                    }
                    foreach (var member in ArchitectorMemberOrder.SortStable(node.members))
                    {
                        var memberResolution = typeIndex.ResolveMember(node.type, member);
                        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                var statusStyle = new GUIStyle(EditorStyles.miniBoldLabel);
                                statusStyle.normal.textColor = MemberStatusColor(memberResolution);
                                GUILayout.Label(memberResolution.ToString(), statusStyle, GUILayout.Width(112f));
                                GUILayout.FlexibleSpace();
                                if (GUILayout.Button("−", EditorStyles.miniButton, GUILayout.Width(24f)))
                                {
                                    execute(() => repository.RemoveLinkedMember(layerId, node.id, member.id));
                                    GUIUtility.ExitGUI();
                                }
                            }
                            declarationStyle ??= new GUIStyle(EditorStyles.wordWrappedMiniLabel) { richText = true };
                            GUILayout.Label(
                                new GUIContent(ArchitectorSemanticPalette.RichDeclaration(member.declaration), member.declaration),
                                declarationStyle);
                        }
                        if (string.Equals(pendingFocusNodeId, node.id, StringComparison.Ordinal)
                            && string.Equals(pendingFocusMemberId, member.id, StringComparison.Ordinal)
                            && Event.current.type == EventType.Repaint)
                        {
                            var memberRect = GUILayoutUtility.GetLastRect();
                            scroll.y = Mathf.Max(0f, memberRect.y - 28f);
                            pendingFocusNodeId = string.Empty;
                            pendingFocusMemberId = string.Empty;
                        }
                    }
                }
            }
            DrawRelationships(layerId, node, repository, revealNode);
            if (resolution != ArchitectorTypeResolution.Resolved)
            {
                EditorGUILayout.HelpBox(
                    resolution == ArchitectorTypeResolution.OutOfScope
                        ? "The linked source is outside the configured C# source roots. The reference is retained as OutOfScope."
                        : "The compiled type could not be resolved through the current exact MonoScript index.",
                    resolution == ArchitectorTypeResolution.OutOfScope ? MessageType.Warning : MessageType.Error);
            }
            EditorGUILayout.HelpBox(
                "Only explicitly selected public/protected members are part of the visible contract. Method bodies and private implementation are not analyzed.",
                MessageType.Info);
            EditorGUILayout.EndScrollView();
        }

        private static GUIStyle declarationStyle;

        // Opening or closing a box is not an edit: GUI.changed is kept so a manual node does not turn dirty.
        private static ArchitectorFoldoutBox FoldoutBox(string title, ref bool expanded)
        {
            var changed = GUI.changed;
            var box = new ArchitectorFoldoutBox(title, ref expanded);
            GUI.changed = changed;
            return box;
        }

        private void DrawRelationships(
            string layerId,
            ArchitectorNodeRecord node,
            ArchitectorRepository repository,
            Action<string, string> revealNode)
        {
            if (!string.Equals(cachedRelationshipLayerId, layerId, StringComparison.Ordinal)
                || !string.Equals(cachedRelationshipNodeId, node.id, StringComparison.Ordinal)
                || cachedRelationshipRevision != repository.Revision)
            {
                cachedRelationshipGroups = LoadRelationshipGroups(layerId, node.id, repository);
                cachedRelationshipLayerId = layerId;
                cachedRelationshipNodeId = node.id;
                cachedRelationshipRevision = repository.Revision;
            }
            var groups = cachedRelationshipGroups;
            using var box = FoldoutBox($"Relationships ({groups.Sum(group => group.Rows.Count)})", ref relationshipsExpanded);
            if (!box.Expanded)
                return;
            if (groups.Count == 0)
            {
                GUILayout.Label("No relationships.", EditorStyles.miniLabel);
                return;
            }
            bool? incoming = null;
            foreach (var group in groups)
            {
                if (incoming != group.Incoming)
                {
                    if (incoming != null)
                        EditorGUILayout.Space(6f);
                    incoming = group.Incoming;
                    var count = groups.Where(candidate => candidate.Incoming == group.Incoming).Sum(candidate => candidate.Rows.Count);
                    GUILayout.Label($"{(group.Incoming ? "Incoming" : "Outgoing")} ({count})", EditorStyles.boldLabel);
                }
                GUILayout.Label($"{group.Title} ({group.Rows.Count})", EditorStyles.miniBoldLabel);
                foreach (var row in group.Rows)
                {
                    if (DrawRelationshipRow(group, row) && revealNode != null)
                    {
                        revealNode(row.LayerId, row.NodeId);
                        GUI.FocusControl(null);
                    }
                }
            }
        }

        // A row is the node at the other end in the color of its type kind, as members color type names, and its layer;
        // a click reports true so the caller selects and frames that node.
        private static bool DrawRelationshipRow(ArchitectorNodeRelationshipGroup group, ArchitectorNodeRelationshipRow row)
        {
            declarationStyle ??= new GUIStyle(EditorStyles.wordWrappedMiniLabel) { richText = true };
            Color? titleColor = string.IsNullOrEmpty(row.TypeKind)
                ? null
                : (Color?)ArchitectorSemanticPalette.ForTypeKind(row.TypeKind.EndsWith("struct", StringComparison.Ordinal) ? "struct" : row.TypeKind);
            var title = ArchitectorSemanticPalette.RichSegments(new List<(string Text, Color? Color)> { (row.Title, titleColor) });
            var label = string.IsNullOrEmpty(row.Label) || !string.IsNullOrEmpty(group.Label) ? string.Empty : $" · {row.Label}";
            var tooltip = $"{group.Title}{label}\n{row.LayerName} / {row.Title}";
            var layerWidth = Mathf.Min(EditorStyles.miniLabel.CalcSize(new GUIContent(row.LayerName)).x, 160f);
            Rect rowRect;
            using (var scope = new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(new GUIContent(title, tooltip), declarationStyle);
                GUILayout.Label(
                    new GUIContent(Ellipsize(row.LayerName, layerWidth, EditorStyles.miniLabel), tooltip),
                    EditorStyles.miniLabel,
                    GUILayout.Width(layerWidth));
                rowRect = scope.rect;
            }
            EditorGUIUtility.AddCursorRect(rowRect, MouseCursor.Link);
            var current = Event.current;
            if (current.type == EventType.Repaint && rowRect.Contains(current.mousePosition))
            {
                var hover = GUI.skin.settings.selectionColor;
                hover.a = Mathf.Min(hover.a, 0.2f);
                EditorGUI.DrawRect(rowRect, hover);
            }
            if (current.type == EventType.MouseUp && current.button == 0 && rowRect.Contains(current.mousePosition))
            {
                current.Use();
                return true;
            }
            return false;
        }

        private static IReadOnlyList<ArchitectorNodeRelationshipGroup> LoadRelationshipGroups(
            string layerId,
            string nodeId,
            ArchitectorRepository repository)
        {
            var nodesByLayerId = new Dictionary<string, Dictionary<string, ArchitectorNodeRecord>>(StringComparer.Ordinal);
            ArchitectorNodeRecord FindNode(string otherLayerId, string otherNodeId)
            {
                if (string.IsNullOrEmpty(otherLayerId) || string.IsNullOrEmpty(otherNodeId) || repository.FindLayer(otherLayerId) == null)
                    return null;
                if (!nodesByLayerId.TryGetValue(otherLayerId, out var nodes))
                {
                    nodes = new Dictionary<string, ArchitectorNodeRecord>(StringComparer.Ordinal);
                    foreach (var candidate in repository.LoadLayer(otherLayerId).semantic.nodes)
                        nodes[candidate.id] = candidate;
                    nodesByLayerId.Add(otherLayerId, nodes);
                }
                return nodes.TryGetValue(otherNodeId, out var found) ? found : null;
            }
            return ArchitectorNodeRelationships.Build(
                layerId,
                nodeId,
                repository.GetRelationshipsForLayer(layerId),
                FindNode,
                otherLayerId => repository.FindLayer(otherLayerId)?.name);
        }

        private static Color MemberStatusColor(ArchitectorMemberResolution resolution)
        {
            return resolution switch
            {
                ArchitectorMemberResolution.Resolved => EditorGUIUtility.isProSkin
                    ? new Color(0.45f, 0.82f, 0.48f)
                    : new Color(0.08f, 0.48f, 0.12f),
                ArchitectorMemberResolution.SignatureChanged => EditorGUIUtility.isProSkin
                    ? new Color(0.95f, 0.72f, 0.25f)
                    : new Color(0.65f, 0.38f, 0.02f),
                _ => EditorGUIUtility.isProSkin
                    ? new Color(0.95f, 0.38f, 0.34f)
                    : new Color(0.70f, 0.08f, 0.06f)
            };
        }

        private static void DrawPathLabel(string label, string path)
        {
            var rect = EditorGUILayout.GetControlRect();
            var valueRect = EditorGUI.PrefixLabel(rect, new GUIContent(label));
            var style = EditorStyles.label;
            var display = Ellipsize(path, valueRect.width, style);
            GUI.Label(valueRect, new GUIContent(display, path), style);
        }

        private static string Ellipsize(string value, float availableWidth, GUIStyle style)
        {
            if (string.IsNullOrEmpty(value) || style.CalcSize(new GUIContent(value)).x <= availableWidth)
                return value;

            const string suffix = "...";
            var low = 0;
            var high = value.Length;
            while (low < high)
            {
                var length = (low + high + 1) / 2;
                var candidate = value.Substring(0, length) + suffix;
                if (style.CalcSize(new GUIContent(candidate)).x <= availableWidth)
                    low = length;
                else
                    high = length - 1;
            }
            return value.Substring(0, low) + suffix;
        }

        private void Load(ArchitectorNodeRecord node, string layerId, int revision)
        {
            loadedLayerId = layerId;
            loadedNodeId = node.id;
            loadedRevision = revision;
            nodeTitle = node.title;
            typeKind = node.type?.kind ?? "class";
            typeNamespace = node.type?.typeNamespace ?? string.Empty;
            typeName = node.type?.name ?? string.Empty;
            genericParameters = string.Join(", ", node.type?.genericParameters ?? new List<string>());
            modifiers = string.Join(" ", node.type?.modifiers ?? new List<string>());
            baseTypeName = ArchitectorTypeReference.FullName(node.type?.baseType);
            interfaceNames = (node.type?.interfaces ?? new List<ArchitectorTypeReferenceRecord>())
                .Select(ArchitectorTypeReference.FullName)
                .ToList();
            members = ArchitectorMemberOrder.SortStable(
                node.members?.Select(member => ArchitectorJson.Clone(member)) ?? Enumerable.Empty<ArchitectorMemberRecord>());
            dirty = false;
            scroll = Vector2.zero;
        }

        private static void DrawLinkedTypeFacts(
            ArchitectorNodeRecord node,
            string layerId,
            ArchitectorRepository repository,
            ArchitectorTypeIndex typeIndex,
            Action<Action> execute,
            ArchitectorTypeResolution resolution)
        {
            var actualBase = resolution == ArchitectorTypeResolution.Resolved ? typeIndex.GetDirectBase(node.type) : null;
            var selectedInterfaceKeys = new HashSet<string>((node.type.interfaces ?? new List<ArchitectorTypeReferenceRecord>())
                .Select(ArchitectorTypeReference.Key), StringComparer.Ordinal);
            IReadOnlyList<ArchitectorIndexedTypeReference> actualInterfaces = resolution == ArchitectorTypeResolution.Resolved
                ? typeIndex.GetInterfaces(node.type)
                : Array.Empty<ArchitectorIndexedTypeReference>();
            var availableInterfaces = actualInterfaces
                .Where(reference => !selectedInterfaceKeys.Contains(reference.Key))
                .Select(ToIndexedType)
                .ToArray();
            var hasBaseContract = actualBase != null || node.type.baseType != null;
            var hasInterfaceContracts = actualInterfaces.Count > 0 || selectedInterfaceKeys.Count > 0;
            if (!hasBaseContract && !hasInterfaceContracts)
                return;

            EditorGUILayout.Space(8f);
            GUILayout.Label("Inheritance", EditorStyles.boldLabel);
            // The declaration as code shows it, base and interfaces in their colors; the controls below edit them.
            declarationStyle ??= new GUIStyle(EditorStyles.wordWrappedMiniLabel) { richText = true };
            var typeDeclaration = ArchitectorSemanticPalette.TypeDeclarationSegments(node.type);
            GUILayout.Label(
                new GUIContent(ArchitectorSemanticPalette.RichSegments(typeDeclaration), string.Concat(typeDeclaration.Select(segment => segment.Text))),
                declarationStyle);
            if (hasBaseContract)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawPathLabel("Direct Base", node.type.baseType == null
                        ? "Not selected"
                        : ArchitectorTypeReference.FullName(node.type.baseType));
                    if (node.type.baseType == null)
                    {
                        if (GUILayout.Button("Add", EditorStyles.miniButton, GUILayout.Width(50f)))
                        {
                            var interfaces = node.type.interfaces?.Select(ArchitectorJson.Clone).ToArray()
                                             ?? Array.Empty<ArchitectorTypeReferenceRecord>();
                            execute(() => repository.UpdateTypeFacts(layerId, node.id, actualBase.ToRecord(), interfaces));
                            GUIUtility.ExitGUI();
                        }
                    }
                    else
                    {
                        var previous = GUI.backgroundColor;
                        GUI.backgroundColor = new Color(1f, 0.62f, 0.56f);
                        var remove = GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(24f));
                        GUI.backgroundColor = previous;
                        if (remove)
                        {
                            var interfaces = node.type.interfaces?.Select(ArchitectorJson.Clone).ToArray()
                                             ?? Array.Empty<ArchitectorTypeReferenceRecord>();
                            execute(() => repository.UpdateTypeFacts(layerId, node.id, null, interfaces));
                            GUIUtility.ExitGUI();
                        }
                    }
                }
            }
            if (hasInterfaceContracts)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label($"Interfaces ({node.type.interfaces?.Count ?? 0})", EditorStyles.miniBoldLabel);
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(availableInterfaces.Length == 0))
                    {
                        if (GUILayout.Button("Add", EditorStyles.miniButton, GUILayout.Width(50f)))
                        {
                            var screenPosition = GUIUtility.GUIToScreenPoint(Event.current.mousePosition);
                            ArchitectorTypePickerWindow.Show(screenPosition, availableInterfaces, indexedType =>
                            {
                                var next = (node.type.interfaces ?? new List<ArchitectorTypeReferenceRecord>())
                                    .Select(ArchitectorJson.Clone)
                                    .ToList();
                                next.Add(ToTypeReference(indexedType));
                                execute(() => repository.UpdateTypeFacts(layerId, node.id, node.type.baseType, next));
                            });
                        }
                    }
                }
                foreach (var interfaceType in node.type.interfaces ?? new List<ArchitectorTypeReferenceRecord>())
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(new GUIContent(ArchitectorTypeReference.FullName(interfaceType), ArchitectorTypeReference.Key(interfaceType)), EditorStyles.miniLabel);
                        if (GUILayout.Button("−", EditorStyles.miniButton, GUILayout.Width(24f)))
                        {
                            var key = ArchitectorTypeReference.Key(interfaceType);
                            var next = node.type.interfaces.Where(candidate => !string.Equals(ArchitectorTypeReference.Key(candidate), key, StringComparison.Ordinal))
                                .Select(ArchitectorJson.Clone)
                                .ToArray();
                            execute(() => repository.UpdateTypeFacts(layerId, node.id, node.type.baseType, next));
                            GUIUtility.ExitGUI();
                        }
                    }
                }
            }
        }

        private static ArchitectorIndexedType ToIndexedType(ArchitectorIndexedTypeReference reference)
        {
            return new ArchitectorIndexedType
            {
                AssemblyName = reference.AssemblyName,
                MetadataName = reference.MetadataName,
                DisplayName = reference.DisplayName,
                Namespace = reference.Namespace,
                Kind = reference.Kind
            };
        }

        private static ArchitectorTypeReferenceRecord ToTypeReference(ArchitectorIndexedType type)
        {
            return new ArchitectorTypeReferenceRecord
            {
                mode = "linked",
                kind = type.Kind,
                typeNamespace = type.Namespace,
                name = type.DisplayName,
                assemblyName = type.AssemblyName,
                metadataName = type.MetadataName
            };
        }

        private void ShowAddMemberMenu()
        {
            var menu = new GenericMenu();
            foreach (var kind in MemberKinds)
            {
                var capturedKind = kind;
                menu.AddItem(new GUIContent(ToDisplayName(kind)), false, () =>
                {
                    members.Add(new ArchitectorMemberRecord
                    {
                        id = $"member_{Guid.NewGuid():N}",
                        kind = capturedKind,
                        declaration = DefaultDeclaration(capturedKind)
                    });
                    members = ArchitectorMemberOrder.SortStable(members);
                    dirty = true;
                });
            }
            menu.ShowAsContext();
        }

        private static List<string> Split(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .ToList();
        }

        private static string ToDisplayName(string value)
        {
            return string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value.Substring(1);
        }

        private static string DefaultDeclaration(string kind)
        {
            return kind switch
            {
                "field" => "private int value;",
                "constant" => "public const int Value = 0;",
                "property" => "public int Value { get; }",
                "indexer" => "public int this[int index] { get; }",
                "event" => "public event Action Changed;",
                "constructor" => "public TypeName()",
                "operator" => "public static TypeName operator +(TypeName left, TypeName right)",
                _ => "public void Method()"
            };
        }

        private readonly struct LabelWidthScope : IDisposable
        {
            private readonly float previous;

            public LabelWidthScope(float width)
            {
                previous = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = width;
            }

            public void Dispose()
            {
                EditorGUIUtility.labelWidth = previous;
            }
        }
    }
}
