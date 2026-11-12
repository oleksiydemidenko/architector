using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;

namespace Architector.CodeFacts;

// Read-only answers over the model. Compact by default: counts and the most frequent type pairs;
// full lists come by offset/limit paging or with output "file".
public static class FactsQueries
{
    public static readonly string[] Actions = { "code_evidence", "layer_facts", "scope_what_if", "layer_dump", "lookup", "facts_audit", "facts_snapshot", "facts_diff" };
    public static readonly string[] ArchitectureOnlyActions = { "layer_dump", "lookup" };

    // Replies are paid for in tokens: no indentation and no escaping of characters such as -> or '.
    public static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly JsonSerializerOptions Indented = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

    private const int PairSamples = 3;
    private const int LayerPairSamples = 5;

    public static JsonObject Answer(FactsModel model, string action, JsonNode? arguments, string root)
    {
        // A file holds the full answer: every list unpaged unless the caller set a limit.
        if (Text(arguments, "output") == "file" && arguments?["limit"] == null)
        {
            var full = arguments?.DeepClone() as JsonObject ?? new JsonObject();
            full["limit"] = int.MaxValue;
            full["full"] = true;
            arguments = full;
        }
        var result = action switch
        {
            "code_evidence" => CodeEvidence(model, arguments),
            "layer_facts" => LayerFacts(model, arguments, root),
            "scope_what_if" => ScopeWhatIf(model, arguments),
            "layer_dump" => LayerDump(model.Architecture, arguments),
            "lookup" => Lookup(model.Architecture, arguments),
            "facts_audit" => Audit(model, arguments, root),
            "facts_snapshot" => FactsSnapshots.Save(model, arguments, root),
            "facts_diff" => FactsSnapshots.Diff(model, arguments, root),
            _ => Failure("unknown_action", $"Unknown code facts action '{action}'. Valid: {string.Join(", ", Actions)}.")
        };
        if (root.Length > 0)
            result["project"] = root;
        return Text(arguments, "output") == "file" && result["success"]?.GetValue<bool>() == true
            ? ToFile(result, action, root)
            : result;
    }

    public static JsonObject CodeEvidence(FactsModel model, JsonNode? arguments)
    {
        var architecture = model.Architecture;
        var evidence = Pairs(model.References.Where(reference => reference.CountsAsEvidence));
        var scopedIds = model.ScopedLayers.Select(layer => layer.Id).ToHashSet(StringComparer.Ordinal);
        var unevidenced = architecture.AllowedDependencies
            .Where(pair => scopedIds.Contains(pair.Source) && scopedIds.Contains(pair.Target) && !evidence.ContainsKey(pair))
            .ToHashSet();
        var cycles = evidence.Keys.Where(pair => evidence.ContainsKey((pair.Target, pair.Source))).ToHashSet();

        var layerFilter = Text(arguments, "layer");
        var filterLayer = layerFilter.Length == 0 ? null : architecture.FindLayer(layerFilter);
        if (layerFilter.Length > 0 && filterLayer == null)
            return LayerFailure(architecture, layerFilter);
        var pairFilter = Text(arguments, "pair");
        var status = Text(arguments, "status");
        var selected = evidence
            .Where(pair => filterLayer == null || pair.Key.Source == filterLayer.Id || pair.Key.Target == filterLayer.Id)
            .Where(pair => pairFilter.Length == 0 || MatchesPair(architecture, pair.Key, pairFilter))
            .Where(pair => status switch
            {
                "undeclared" => !architecture.AllowedDependencies.Contains(pair.Key),
                "cycle" => cycles.Contains(pair.Key),
                "unevidenced" => false,
                _ => true
            })
            .OrderBy(pair => architecture.Path(pair.Key.Source), StringComparer.Ordinal)
            .ThenBy(pair => architecture.Path(pair.Key.Target), StringComparer.Ordinal)
            .ToArray();

        var result = Success();
        result["scoped_layers"] = model.ScopedLayers.Count;
        result["references"] = evidence.Values.Sum(list => list.Count);
        result["layer_pairs"] = evidence.Count;
        result["undeclared"] = evidence.Keys.Count(pair => !architecture.AllowedDependencies.Contains(pair));
        result["cycles"] = cycles.Count / 2;
        result["unevidenced"] = unevidenced.Count;
        if (model.EvidenceWarnings.Count > 0)
            result["warnings"] = Array(model.EvidenceWarnings.Take(10).Select(value => (JsonNode?)value));
        result["rules"] = RulesEntry(model);
        if (model.Index != null)
        {
            var index = model.Index;
            var binding = new JsonObject
            {
                ["unity_references"] = index.UnityReferenceCount,
                ["project_references"] = new JsonObject
                {
                    ["compiled"] = index.ProjectReferencesCompiled,
                    ["from_sources"] = index.ProjectReferencesFromSources,
                    ["missing"] = index.MissingAssemblies.Count
                },
                ["missing_assemblies"] = Array(index.MissingAssemblies.Select(name => (JsonNode?)name)),
                ["unseen_base_types"] = index.UnresolvedBaseTypes,
                ["unseen_base_examples"] = Array(index.UnseenBaseExamples.Select(example => (JsonNode?)new JsonObject
                {
                    ["type"] = example.Type,
                    ["base"] = example.Base,
                    ["file"] = example.At,
                    ["types_with_this_base"] = example.Count
                }))
            };
            if (index.MissingAssemblies.Count > 0)
                binding["hint"] = $"These referenced assemblies are neither compiled in Library/ScriptAssemblies nor available as sources: {string.Join(", ", index.MissingAssemblies)}. Their base classes stay unseen until Unity compiles the project once.";
            else if (index.UnityReferenceCount == 0 && index.ProjectReferencesCompiled + index.ProjectReferencesFromSources == 0 && index.UnresolvedBaseTypes > 0)
                binding["hint"] = "No Unity project files (Assembly-CSharp.csproj) were found, so engine and package base classes are unseen and names inherited from them are not counted. Open the project in Unity with an IDE set in External Tools to generate them.";
            else if (index.UnresolvedBaseTypes > 0)
                binding["hint"] = "unseen_base_examples shows which base classes remain unseen and how many types inherit each; names inherited from them are not counted.";
            result["binding"] = binding;
            if (index.ReflectionSites.Count > 0)
            {
                var byKey = model.Types.ToDictionary(type => type.Key, StringComparer.Ordinal);
                result["reflection_sites"] = Array(index.ReflectionSites.Select(site => (JsonNode?)new JsonObject
                {
                    ["base"] = byKey.TryGetValue(site.BaseKey, out var baseType) ? baseType.FullName : site.BaseKey,
                    ["at"] = site.At,
                    ["matching_types"] = baseType == null ? 0 : ReflectionCreated(model, baseType).Count()
                }));
            }
        }

        var include = Text(arguments, "include");
        if (include == "references")
        {
            var rows = selected.SelectMany(pair => pair.Value.Select(reference => (JsonNode?)new JsonArray(
                reference.SourceType, reference.TargetType, reference.At, architecture.Path(pair.Key.Source), architecture.Path(pair.Key.Target))));
            Page(result, "items", rows, arguments, 200);
        }
        else if (include == "owners")
        {
            var rows = model.Types
                .Where(type => filterLayer == null || type.OwnerLayerId == filterLayer.Id)
                .Select(type => (JsonNode?)new JsonArray(type.FullName, type.OwnerLayerId == null ? null : architecture.Path(type.OwnerLayerId), type.At));
            Page(result, "items", rows, arguments, 200);
        }
        else if (include == "intra")
        {
            var rows = model.References
                .Where(reference => reference.SourceLayerId != null && reference.SourceLayerId == reference.TargetLayerId)
                .Where(reference => filterLayer == null || reference.SourceLayerId == filterLayer.Id)
                .Select(reference => (JsonNode?)new JsonArray(reference.SourceType, reference.TargetType, reference.At, architecture.Path(reference.SourceLayerId!)));
            Page(result, "items", rows, arguments, 200);
        }
        else
        {
            var rows = selected.Select(pair => (JsonNode?)PairEntry(architecture, pair.Key, pair.Value, cycles.Contains(pair.Key), PairSamples));
            Page(result, "pairs", rows, arguments, 50);
            if (status is "" or "unevidenced")
                result["unevidenced_pairs"] = Array(unevidenced
                    .Where(pair => filterLayer == null || pair.Source == filterLayer.Id || pair.Target == filterLayer.Id)
                    .OrderBy(pair => architecture.Path(pair.Source), StringComparer.Ordinal)
                    .Select(pair => (JsonNode?)$"{architecture.Path(pair.Source)} -> {architecture.Path(pair.Target)}"));
        }
        return result;
    }

    public static JsonObject LayerFacts(FactsModel model, JsonNode? arguments, string root = "")
    {
        var architecture = model.Architecture;
        var layer = architecture.FindLayer(Text(arguments, "target"));
        if (layer == null)
            return LayerFailure(architecture, Text(arguments, "target"));
        if (!layer.Scoped)
            return Failure("layer_not_scoped", $"Layer '{layer.Path}' has no code scope, so no code belongs to it. Set its scope first (layerScopes in a plan).");

        var owned = model.Types.Where(type => type.OwnerLayerId == layer.Id).ToArray();
        var typeName = Text(arguments, "type");
        if (typeName.Length > 0)
        {
            // A short name shared by a generic and a non-generic type (Turret<T>, Turret) means the non-generic one;
            // the generic one is named by its full name (Ns.Turret`1).
            var named = owned.Where(candidate => candidate.Name == typeName).ToArray();
            var type = owned.FirstOrDefault(candidate => candidate.FullName == typeName)
                       ?? (named.Length == 1 ? named[0] : named.Where(candidate => !candidate.IsGeneric).ToArray() is { Length: 1 } plain ? plain[0] : null);
            if (type == null && named.Length > 1)
            {
                var ambiguous = Failure("type_ambiguous", $"Layer '{layer.Path}' owns {named.Length} types named '{typeName}'. Use the full name.");
                ambiguous["candidates"] = Array(named.Select(candidate => (JsonNode?)candidate.FullName));
                return ambiguous;
            }
            if (type == null)
                return Failure("type_not_found", $"Layer '{layer.Path}' owns no type '{typeName}'.");
            var detail = Success();
            detail["layer"] = layer.Path;
            detail["type"] = TypeEntry(architecture, type);
            Page(detail, "members", type.Members.Select(member => (JsonNode?)new JsonArray(
                member.Kind,
                member.Display,
                member.At.Length == 0 ? null : member.At,
                MemberReference(member))), arguments, 100);
            detail["member_ref_hint"] = "The fourth value of each member is its member_ref: put it into a plan's members ({kind, name, parameters}) instead of a symbolKey.";
            if (type.Extras.Count > 0)
            {
                var extra = new JsonObject();
                foreach (var (kind, values) in type.Extras)
                    extra[kind] = Array(values.Select(pair => (JsonNode?)new JsonArray(pair.Key, pair.Value)));
                detail["extra"] = extra;
            }
            detail["behaviour"] = BehaviourEntry(model, type, arguments);
            if (root.Length > 0)
                detail["wiring"] = WiringEntry(WiringStatuses(model, AssetWiring.Load(root))[type.Key], arguments);
            return detail;
        }

        var outgoing = Pairs(model.References.Where(reference => reference.CountsAsEvidence && reference.SourceLayerId == layer.Id));
        var incoming = Pairs(model.References.Where(reference => reference.CountsAsEvidence && reference.TargetLayerId == layer.Id));
        var usedElsewhere = incoming.Values.SelectMany(list => list).Select(reference => reference.TargetType).ToHashSet(StringComparer.Ordinal);

        var result = Success();
        result["layer"] = layer.Path;
        result["files"] = owned.SelectMany(type => type.Files.Select(file => file[..file.LastIndexOf(':')])).Distinct().Count();
        result["types"] = owned.Length;
        result["outgoing"] = Array(outgoing
            .OrderByDescending(pair => pair.Value.Count)
            .Select(pair => (JsonNode?)LayerPairEntry(architecture, pair.Key.Target, architecture.AllowedDependencies.Contains(pair.Key), pair.Value)));
        result["incoming"] = Array(incoming
            .OrderByDescending(pair => pair.Value.Count)
            .Select(pair => (JsonNode?)LayerPairEntry(architecture, pair.Key.Source, architecture.AllowedDependencies.Contains(pair.Key), pair.Value)));

        var candidates = owned
            .Select(type => (type, rules: CandidateRules(type, layer.Id, usedElsewhere)))
            .Where(item => item.rules.Count > 0)
            .OrderBy(item => item.type.Nodes.Count > 0)
            .ThenByDescending(item => item.rules.Count)
            .ThenBy(item => item.type.FullName, StringComparer.Ordinal)
            .Select(item => (JsonNode?)new JsonObject
            {
                ["type"] = item.type.FullName,
                ["at"] = item.type.At,
                ["rules"] = Array(item.rules.Select(rule => (JsonNode?)rule)),
                ["node"] = item.type.Nodes.FirstOrDefault(),
                ["node_title"] = item.type.Nodes.Count == 0 ? null : architecture.FindNode(item.type.Nodes[0])?.Title
            })
            .ToArray();
        if (model.RuleSuggestions.Count > 0)
            result["rule_suggestions"] = $"{model.RuleSuggestions.Count}; code_evidence lists them";
        result["node_candidates_total"] = candidates.Length;
        result["node_candidates"] = Array(candidates.Take(Full(arguments) ? int.MaxValue : IntOr(arguments, "candidates", 40)));
        var statuses = root.Length > 0 ? WiringStatuses(model, AssetWiring.Load(root)) : null;
        if (statuses != null)
        {
            var summary = new JsonObject();
            foreach (var status in WiringStatusNames)
                summary[status] = owned.Count(type => statuses[type.Key].Status == status);
            result["wiring"] = summary;
            result["wiring_note"] = "not tracked: no evidence either way (assets cannot show these types; this is not 'unused'). Only unwired marks a dead-code candidate.";
            result["wiring_test_places"] = Array(model.Extractors.TestFolders.Select(pattern => (JsonNode?)pattern));
        }
        Page(result, "type_list", owned.OrderBy(type => type.At, StringComparer.Ordinal).Select(type =>
        {
            var entry = TypeEntry(architecture, type);
            if (statuses != null)
                entry["wiring"] = statuses[type.Key].Status;
            return (JsonNode?)entry;
        }), arguments, 50);
        return result;
    }

    public static JsonObject ScopeWhatIf(FactsModel model, JsonNode? arguments)
    {
        var architecture = model.Architecture;
        if (arguments?["moves"] is not JsonArray moveList || moveList.Count == 0)
            return Failure("moves_required", "Give moves: [{\"type\" | \"file\" | \"folder\": \"...\", \"to\": \"Layer/Path\"}].");
        var moved = new Dictionary<string, string>(StringComparer.Ordinal);
        var problems = new JsonArray();
        foreach (var item in moveList)
        {
            var to = architecture.FindLayer(Text(item, "to"));
            if (to == null)
            {
                problems.Add((JsonNode)$"Unknown layer '{Text(item, "to")}'.");
                continue;
            }
            var types = Text(item, "type") is { Length: > 0 } typeName
                ? model.FindType(typeName) is { } single ? new[] { single } : System.Array.Empty<FactsType>()
                : Text(item, "file") is { Length: > 0 } file
                    ? model.Types.Where(type => type.Files.Any(at => at.StartsWith(file.Replace('\\', '/') + ":", StringComparison.Ordinal))).ToArray()
                    : Text(item, "folder") is { Length: > 0 } folder
                        ? model.Types.Where(type => type.Files.Any(at => at.StartsWith(folder.Replace('\\', '/').TrimEnd('/') + "/", StringComparison.Ordinal))).ToArray()
                        : System.Array.Empty<FactsType>();
            if (types.Length == 0)
                problems.Add((JsonNode)$"Nothing matches {item?.ToJsonString(Compact)}.");
            foreach (var type in types)
                moved[type.FullName] = to.Id;
        }

        Dictionary<(string Source, string Target), List<FactsReference>> PairsAfter(IReadOnlyDictionary<string, string> moves)
        {
            var pairs = new Dictionary<(string, string), List<FactsReference>>();
            foreach (var reference in model.References)
            {
                var source = moves.TryGetValue(reference.SourceType, out var movedSource) ? movedSource : reference.SourceLayerId;
                var target = moves.TryGetValue(reference.TargetType, out var movedTarget) ? movedTarget : reference.TargetLayerId;
                if (source == null || target == null || source == target)
                    continue;
                if (!pairs.TryGetValue((source, target), out var list))
                    pairs[(source, target)] = list = new List<FactsReference>();
                list.Add(reference);
            }
            return pairs;
        }

        var before = PairsAfter(new Dictionary<string, string>());
        var after = PairsAfter(moved);
        var allowed = architecture.AllowedDependencies;
        HashSet<(string, string)> Undeclared(Dictionary<(string Source, string Target), List<FactsReference>> pairs) =>
            pairs.Keys.Where(pair => !allowed.Contains(pair)).ToHashSet();
        HashSet<(string, string)> Cycles(Dictionary<(string Source, string Target), List<FactsReference>> pairs) =>
            pairs.Keys.Where(pair => pairs.ContainsKey((pair.Target, pair.Source)) && string.CompareOrdinal(pair.Source, pair.Target) < 0).ToHashSet();
        var undeclaredBefore = Undeclared(before);
        var undeclaredAfter = Undeclared(after);
        var cyclesBefore = Cycles(before);
        var cyclesAfter = Cycles(after);

        var result = Success();
        result["moved_types"] = moved.Count;
        if (problems.Count > 0)
            result["problems"] = problems;
        result["undeclared"] = $"{undeclaredBefore.Count} -> {undeclaredAfter.Count}";
        result["new_undeclared"] = Array(undeclaredAfter.Except(undeclaredBefore).Select(pair => (JsonNode?)PairEntry(architecture, pair, after[pair], false, PairSamples)));
        result["removed_undeclared"] = Array(undeclaredBefore.Except(undeclaredAfter).Select(pair => (JsonNode?)$"{architecture.Path(pair.Item1)} -> {architecture.Path(pair.Item2)}"));
        result["cycles"] = $"{cyclesBefore.Count} -> {cyclesAfter.Count}";
        result["new_cycles"] = Array(cyclesAfter.Except(cyclesBefore).Select(pair => (JsonNode?)new JsonObject
        {
            ["layers"] = $"{architecture.Path(pair.Item1)} <-> {architecture.Path(pair.Item2)}",
            ["forward"] = TypePairs(after[pair], PairSamples),
            ["back"] = TypePairs(after[(pair.Item2, pair.Item1)], PairSamples)
        }));
        result["removed_cycles"] = Array(cyclesBefore.Except(cyclesAfter).Select(pair => (JsonNode?)$"{architecture.Path(pair.Item1)} <-> {architecture.Path(pair.Item2)}"));
        result["relationships_losing_evidence"] = Array(architecture.CrossRelationships
            .Where(relationship => relationship.Kind == "dependency")
            .Where(relationship => before.ContainsKey((relationship.SourceLayerId, relationship.TargetLayerId))
                                   && !after.ContainsKey((relationship.SourceLayerId, relationship.TargetLayerId)))
            .Select(relationship => (JsonNode?)RelationshipEntry(architecture, relationship)));
        result["linked_nodes_moving"] = Array(moved
            .Select(pair => (type: model.TypesByName[pair.Key], to: pair.Value))
            .Where(item => item.type.Nodes.Count > 0 && item.type.OwnerLayerId != item.to)
            .SelectMany(item => item.type.Nodes.Select(nodeId => (JsonNode?)new JsonObject
            {
                ["node"] = nodeId,
                ["title"] = architecture.FindNode(nodeId)?.Title,
                ["from"] = architecture.LayerOfNode(nodeId)?.Path,
                ["to"] = architecture.Path(item.to)
            })));
        return result;
    }

    public static JsonObject LayerDump(FactsArchitecture architecture, JsonNode? arguments)
    {
        var layer = architecture.FindLayer(Text(arguments, "target"));
        if (layer == null)
            return LayerFailure(architecture, Text(arguments, "target"));
        string Title(string nodeId) => architecture.FindNode(nodeId)?.Title ?? nodeId;
        string Qualified(string layerId, string nodeId) => $"{architecture.Path(layerId)}::{Title(nodeId)}";

        var items = new List<JsonNode?>();
        foreach (var node in layer.Nodes.Where(node => !node.IsPortal))
            items.Add(new JsonObject
            {
                ["node"] = node.Id,
                ["title"] = node.Title,
                ["type"] = node.TypeName.Length == 0 ? null : node.TypeName,
                ["responsibility"] = node.Responsibility
            });
        foreach (var edge in layer.Edges.Where(edge => edge.SourceLayerId == edge.TargetLayerId))
            items.Add(new JsonObject
            {
                ["edge"] = edge.Id,
                ["from"] = Title(edge.SourceNodeId),
                ["to"] = Title(edge.TargetNodeId),
                ["kind"] = edge.Kind,
                ["label"] = edge.Label.Length == 0 ? null : edge.Label
            });
        foreach (var relationship in architecture.CrossRelationships.Where(item => item.SourceLayerId == layer.Id || item.TargetLayerId == layer.Id))
            items.Add(new JsonObject
            {
                ["link"] = relationship.Id,
                ["from"] = Qualified(relationship.SourceLayerId, relationship.SourceNodeId),
                ["to"] = Qualified(relationship.TargetLayerId, relationship.TargetNodeId),
                ["kind"] = relationship.Kind,
                ["label"] = relationship.Label.Length == 0 ? null : relationship.Label
            });

        var result = Success();
        result["layer"] = layer.Path;
        result["id"] = layer.Id;
        result["purpose"] = layer.Purpose;
        Page(result, "items", items, arguments, 80);
        return result;
    }

    // Names to ids: "Layer/Path", "Layer/Path::Node Title" and "Layer/Path::Node Title::Member".
    public static JsonObject Lookup(FactsArchitecture architecture, JsonNode? arguments)
    {
        if (arguments?["names"] is not JsonArray names || names.Count == 0)
            return Failure("names_required", "Give names: [\"Layer/Path\", \"Layer/Path::Node Title\", \"Layer/Path::Node Title::Member\"].");
        var results = new JsonArray();
        foreach (var item in names)
        {
            var name = item?.GetValue<string>() ?? string.Empty;
            var parts = name.Split("::");
            var entry = new JsonObject { ["name"] = name };
            var layer = architecture.FindLayer(parts[0]);
            if (layer == null)
            {
                entry["error"] = $"No layer '{parts[0]}'.";
                results.Add(entry);
                continue;
            }
            entry["layer"] = layer.Id;
            if (parts.Length > 1)
            {
                var nodes = layer.Nodes.Where(node => string.Equals(node.Title, parts[1].Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
                if (nodes.Length != 1)
                {
                    entry["error"] = nodes.Length == 0 ? $"No node '{parts[1]}' in {layer.Path}." : $"Several nodes are called '{parts[1]}' in {layer.Path}.";
                    entry["candidates"] = Array(layer.Nodes
                        .Where(node => node.Title.Contains(parts[1].Trim(), StringComparison.OrdinalIgnoreCase))
                        .Take(8)
                        .Select(node => (JsonNode?)$"{node.Title} ({node.Id})"));
                    results.Add(entry);
                    continue;
                }
                entry["node"] = nodes[0].Id;
                if (parts.Length > 2)
                {
                    var memberName = parts[2].Trim();
                    var members = nodes[0].Members.Where(member => DeclaresName(member.Declaration, memberName)).ToArray();
                    if (members.Length == 1)
                    {
                        entry["member"] = members[0].Id;
                        entry["kind"] = members[0].Kind;
                        if (members[0].SymbolKey.Length > 0)
                            entry["symbolKey"] = members[0].SymbolKey;
                    }
                    else
                    {
                        entry["error"] = nodes[0].Members.Count == 0
                            ? $"Node '{nodes[0].Title}' records no members in the graph; layer_facts with type lists the members in code."
                            : members.Length == 0 ? $"Node '{nodes[0].Title}' has no member '{memberName}'." : $"Several members of '{nodes[0].Title}' are called '{memberName}'.";
                        entry["candidates"] = Array(members.Select(member => (JsonNode?)$"{member.Declaration} ({member.Id})"));
                    }
                }
            }
            results.Add(entry);
        }
        var result = Success();
        result["results"] = results;
        return result;
    }

    // The project's extraction rules and what the code repeats that they do not cover.
    private static JsonObject RulesEntry(FactsModel model)
    {
        var rules = new JsonObject { ["presets"] = Array(model.Extractors.ActivePresets.Select(value => (JsonNode?)value)) };
        if (model.Extractors.Problems.Count > 0)
            rules["problems"] = Array(model.Extractors.Problems.Select(value => (JsonNode?)value));
        if (model.RuleSuggestions.Count > 0)
        {
            rules["suggestions"] = Array(model.RuleSuggestions.Select(suggestion => (JsonNode?)new JsonObject
            {
                ["rule"] = suggestion.Rule,
                ["name"] = suggestion.Name,
                ["uses"] = suggestion.Uses,
                ["example"] = suggestion.Example,
                ["folders"] = Array(suggestion.Folders.Select(folder => (JsonNode?)folder))
            }));
            rules["hint"] = "The code repeats these but Architector/code-facts.json does not cover them. Read a few examples; when they wire types together (dependency access, injection), propose adding them to the user.";
        }
        if (model.Extractors.SuggestionExclude.Count > 0)
            rules["suggestions_exclude"] = Array(model.Extractors.SuggestionExclude.Select(value => (JsonNode?)value));
        return rules;
    }

    private static bool DeclaresName(string declaration, string name)
    {
        var index = declaration.IndexOf(name, StringComparison.Ordinal);
        while (index >= 0)
        {
            var before = index == 0 || !char.IsLetterOrDigit(declaration[index - 1]) && declaration[index - 1] != '_';
            var end = index + name.Length;
            var after = end >= declaration.Length || !char.IsLetterOrDigit(declaration[end]) && declaration[end] != '_';
            if (before && after)
                return true;
            index = declaration.IndexOf(name, index + 1, StringComparison.Ordinal);
        }
        return false;
    }

    private static List<string> CandidateRules(FactsType type, string layerId, HashSet<string> usedElsewhere)
    {
        var rules = new List<string>();
        if (usedElsewhere.Contains(type.FullName))
            rules.Add("used_by_other_layers");
        if (type.DerivedOwnerLayers.Any(owner => owner.Length > 0 && owner != layerId))
            rules.Add("inherited_by_other_layers");
        rules.AddRange(type.Rules);
        if (type.IsExtensionPoint)
            rules.Add("extension_point");
        return rules;
    }

    internal static Dictionary<(string Source, string Target), List<FactsReference>> Pairs(IEnumerable<FactsReference> references)
    {
        var pairs = new Dictionary<(string, string), List<FactsReference>>();
        foreach (var reference in references)
        {
            var key = (reference.SourceLayerId!, reference.TargetLayerId!);
            if (!pairs.TryGetValue(key, out var list))
                pairs[key] = list = new List<FactsReference>();
            list.Add(reference);
        }
        return pairs;
    }

    private static JsonObject PairEntry(FactsArchitecture architecture, (string Source, string Target) pair, List<FactsReference> references, bool cycle, int samples)
    {
        var entry = new JsonObject
        {
            ["source"] = architecture.Path(pair.Source),
            ["target"] = architecture.Path(pair.Target),
            ["declared"] = architecture.AllowedDependencies.Contains(pair),
            ["references"] = references.Count,
            ["type_pairs"] = TypePairs(references, samples)
        };
        if (cycle)
            entry["cycle"] = true;
        var distinct = references.Select(reference => (reference.SourceType, reference.TargetType)).Distinct().Count();
        if (distinct > samples)
            entry["more_type_pairs"] = distinct - samples;
        return entry;
    }

    private static JsonObject LayerPairEntry(FactsArchitecture architecture, string otherLayerId, bool declared, List<FactsReference> references)
    {
        var entry = new JsonObject
        {
            ["layer"] = architecture.Path(otherLayerId),
            ["declared"] = declared,
            ["references"] = references.Count,
            ["type_pairs"] = TypePairs(references, LayerPairSamples)
        };
        var distinct = references.Select(reference => (reference.SourceType, reference.TargetType)).Distinct().Count();
        if (distinct > LayerPairSamples)
            entry["more_type_pairs"] = distinct - LayerPairSamples;
        return entry;
    }

    private static JsonArray TypePairs(IEnumerable<FactsReference> references, int take)
    {
        return Array(references
            .GroupBy(reference => (reference.SourceType, reference.TargetType))
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.SourceType, StringComparer.Ordinal)
            .ThenBy(group => group.Key.TargetType, StringComparer.Ordinal)
            .Take(take)
            .Select(group => (JsonNode?)new JsonArray(group.Key.SourceType, group.Key.TargetType, group.Count(), group.First().At)));
    }

    private static JsonObject TypeEntry(FactsArchitecture architecture, FactsType type)
    {
        var entry = new JsonObject
        {
            ["type"] = type.FullName,
            ["kind"] = type.Kind,
            ["at"] = type.At,
            ["public_members"] = type.Members.Count
        };
        if (type.Base != null)
            entry["base"] = type.Base;
        var interfaces = type.Interfaces.ToArray();
        if (interfaces.Length > 0)
            entry["interfaces"] = Array(interfaces.Select(value => (JsonNode?)value));
        if (type.Files.Count > 1)
            entry["parts"] = Array(type.Files.Skip(1).Select(value => (JsonNode?)value));
        if (type.Nodes.Count > 0)
            entry["nodes"] = Array(type.Nodes.Select(value => (JsonNode?)value));
        foreach (var (kind, values) in type.Extras)
            entry[kind] = values.Count;
        return entry;
    }

    private static JsonObject RelationshipEntry(FactsArchitecture architecture, FactsRelationship relationship)
    {
        return new JsonObject
        {
            ["relationship"] = relationship.Id,
            ["from"] = $"{architecture.Path(relationship.SourceLayerId)}::{architecture.FindNode(relationship.SourceNodeId)?.Title ?? relationship.SourceNodeId}",
            ["from_node"] = relationship.SourceNodeId,
            ["to"] = $"{architecture.Path(relationship.TargetLayerId)}::{architecture.FindNode(relationship.TargetNodeId)?.Title ?? relationship.TargetNodeId}",
            ["to_node"] = relationship.TargetNodeId
        };
    }

    private static bool MatchesPair(FactsArchitecture architecture, (string Source, string Target) pair, string filter)
    {
        var parts = filter.Split("->", 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2
               && architecture.FindLayer(parts[0])?.Id == pair.Source
               && architecture.FindLayer(parts[1])?.Id == pair.Target;
    }

    internal static bool Full(JsonNode? arguments) => arguments?["full"] is JsonValue value && value.TryGetValue<bool>(out var full) && full;

    internal static void Page(JsonObject result, string name, IEnumerable<JsonNode?> rows, JsonNode? arguments, int defaultLimit)
    {
        var all = rows.ToArray();
        var offset = Math.Max(0, IntOr(arguments, "offset", 0));
        var limit = Math.Max(1, IntOr(arguments, "limit", defaultLimit));
        result[name + "_total"] = all.Length;
        result[name] = Array(all.Skip(offset).Take(limit));
        if (offset + limit < all.Length)
            result["next_offset"] = offset + limit;
    }

    private static JsonObject ToFile(JsonObject result, string action, string root)
    {
        var folder = Path.Combine(root, "Library", "Architector", "facts");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{action}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, result.ToJsonString(Indented));
        var summary = Success();
        summary["file"] = path.Replace('\\', '/');
        foreach (var property in result.Where(property => property.Value is JsonValue))
            summary[property.Key] = property.Value!.DeepClone();
        return summary;
    }

    internal static JsonArray Array(IEnumerable<JsonNode?> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
            array.Add(item?.Parent == null ? item : item.DeepClone());
        return array;
    }

    internal static string Text(JsonNode? node, string name)
    {
        try
        {
            return node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    internal static int IntOr(JsonNode? node, string name, int fallback)
    {
        try
        {
            return node?[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    internal static JsonObject LayerFailure(FactsArchitecture architecture, string reference)
    {
        var matches = architecture.MatchLayers(reference);
        var failure = matches.Length > 1
            ? Failure("layer_ambiguous", $"Layer '{reference}' matches {matches.Length} layers. Use a longer part of its path.")
            : Failure("layer_not_found", $"No layer '{reference}'. Use a layer id, its full path or a unique trailing part of it (Group/Layer).");
        var candidates = matches.Length > 1
            ? matches.Select(layer => layer.Path)
            : architecture.Layers.Values
                .Where(layer => reference.Length > 0 && layer.Path.Contains(reference.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(layer => layer.Path)
                .OrderBy(path => path, StringComparer.Ordinal);
        failure["target"] = reference;
        failure["candidates"] = Array(candidates.Take(8).Select(path => (JsonNode?)path));
        failure["layers"] = failure["candidates"]!.DeepClone();
        return failure;
    }

    // Where a type is used in prefabs, scenes and assets: by its script GUID when it owns a script asset (the class Unity
    // names by the file), by [SerializeReference] values (also those a prefab variant adds by override), and how it is
    // created otherwise.
    internal sealed class TypeWiring
    {
        public bool Script;
        public string ScriptFile = string.Empty;
        public string ScriptMatch = string.Empty;
        public List<(string File, string Object, bool Enabled, List<string> ListedBy, string Via)> Places = new();
        public List<string> SerializedIn = new();
        public bool TestOnly;
        public bool Found;
        public string Status = "not_tracked";
        public string Reason = string.Empty;
        public string Note = string.Empty;
        public int SiblingsInAssets = -1;
        public List<string> BaseOf = new();
    }

    internal static readonly string[] WiringStatusNames = { "wired", "test_only", "base_only", "created_in_code", "created_by_reflection", "unwired", "not_tracked" };
    private static readonly string[] CreatingCalls = { "AddComponent", "CreateInstance" };

    // Every type gets a status. wired and test_only come from the assets. A type that is never placed itself but that
    // placed types inherit from (directly or further down) is base_only. A script type (MonoBehaviour, ScriptableObject)
    // that nothing places and nothing placed inherits from is unwired, and so is a [Serializable] class that only
    // [SerializeReference] fields can store when no asset stores it. Subclasses that a reflection site creates are
    // created_by_reflection. Everything else is not_tracked: assets do not name it (a class serialized by value) or it is
    // not serialized at all, so its absence proves nothing.
    internal static Dictionary<string, TypeWiring> WiringStatuses(FactsModel model, AssetWiring assets)
    {
        var byKey = model.Types.ToDictionary(type => type.Key, StringComparer.Ordinal);
        var scripts = ScriptFiles(model);
        var wiring = model.Types.ToDictionary(type => type.Key, type => WiringOf(model, assets, scripts, type), StringComparer.Ordinal);
        var createdAt = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in model.Types)
            if (type.Extras.TryGetValue("generic_calls", out var calls))
                foreach (var (call, at) in calls)
                    foreach (var name in CreatingCalls.Where(name => call.StartsWith(name + "<", StringComparison.Ordinal)))
                        createdAt.TryAdd(call[(name.Length + 1)..^1].Split('.').Last().Trim(), at);
        foreach (var (key, item) in wiring)
        {
            if (item.Found)
            {
                item.Status = item.TestOnly ? "test_only" : "wired";
                continue;
            }
            var type = byKey[key];
            var derived = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>(type.DerivedByKeys);
            while (queue.Count > 0)
            {
                var next = queue.Dequeue();
                if (!derived.Add(next) || !byKey.TryGetValue(next, out var child))
                    continue;
                foreach (var grandchild in child.DerivedByKeys)
                    queue.Enqueue(grandchild);
            }
            item.BaseOf = derived.Where(child => wiring.TryGetValue(child, out var childWiring) && childWiring.Found)
                .Select(child => byKey[child].FullName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            if (item.BaseOf.Count > 0)
                item.Status = "base_only";
            else if (type.Placeable && createdAt.TryGetValue(type.Name, out var created))
            {
                item.Status = "created_in_code";
                item.Reason = IsEditorFile(created)
                    ? $"created from Editor code at {created} (an importer or a tool; the assets it makes are not scanned)"
                    : "created from code at " + created;
            }
            else if (type.Placeable)
            {
                item.Status = "unwired";
                item.Reason = type.IsAbstract || type.IsGeneric
                    ? $"{(type.IsAbstract ? "abstract" : "generic")}, {(derived.Count == 0 ? "no subclasses" : "no subclass is placed")}"
                    : item.Script ? string.Empty : "no script asset of its own (its file is not named after it)";
            }
        }
        ManagedReferenceOnly(model, wiring, byKey);
        if (model.Index != null)
            foreach (var (baseKey, at) in model.Index.ReflectionSites)
            {
                if (!byKey.TryGetValue(baseKey, out var baseType))
                    continue;
                foreach (var created in ReflectionCreated(model, baseType))
                {
                    var item = wiring[created.Key];
                    if (item.Status is not ("unwired" or "not_tracked"))
                        continue;
                    item.Status = "created_by_reflection";
                    item.Reason = $"created by reflection at {at} (subclasses of {baseType.FullName})";
                    item.Note = string.Empty;
                    item.SiblingsInAssets = -1;
                }
            }
        return wiring;
    }

    // Unity compiles every script under a folder named Editor into the Editor only.
    internal static bool IsEditorFile(string at) => ("/" + at).Contains("/Editor/", StringComparison.Ordinal);

    // The non-abstract, non-generic classes a reflection site over this base type can create.
    internal static IEnumerable<FactsType> ReflectionCreated(FactsModel model, FactsType baseType)
    {
        var byKey = model.Types.ToDictionary(type => type.Key, StringComparer.Ordinal);
        return baseType.DerivedByKeys
            .Select(key => byKey.TryGetValue(key, out var type) ? type : null)
            .Where(type => type is { Kind: "class", IsAbstract: false, IsGeneric: false })
            .Select(type => type!)
            .Distinct();
    }

    // A [Serializable] class that only [SerializeReference] fields can hold (it is never a by-value serialized field and
    // never created with new) lives only in the assets that store it. When none does, it is unwired.
    private static void ManagedReferenceOnly(FactsModel model, Dictionary<string, TypeWiring> wiring, Dictionary<string, FactsType> byKey)
    {
        var fields = model.Types
            .SelectMany(owner => owner.Data.ReferenceFields.Where(field => field.ElementKey.Length > 0 && byKey.ContainsKey(field.ElementKey)))
            .ToArray();
        if (fields.Length == 0)
            return;
        bool Fits(FactsType type, string elementKey) => elementKey == type.Key || byKey[elementKey].Data.DerivedBy.Contains(type.Key);
        foreach (var type in model.Types)
        {
            var item = wiring[type.Key];
            if (item.Status != "not_tracked" || type.Kind != "class" || type.IsAbstract || type.IsGeneric
                || !type.Data.IsSerializable || type.Data.IsUnityObject || type.Placeable)
                continue;
            var matching = fields.Where(field => Fits(type, field.ElementKey)).ToArray();
            if (matching.Length == 0)
                continue;
            if (type.Data.ByValueAt.Length > 0)
            {
                item.Note = $"Stored by [SerializeReference] and also by value at {type.Data.ByValueAt}, where assets cannot name it; its absence proves nothing.";
                continue;
            }
            if (type.Data.CreatedAt.Length > 0)
            {
                item.Note = type.Data.CreatedBy is "new" or ""
                    ? $"Created with new at {type.Data.CreatedAt}; assets cannot show that use."
                    : $"Created in code by {type.Data.CreatedBy} at {type.Data.CreatedAt}; assets cannot show that use.";
                continue;
            }
            if (type.Data.TypeOfAt.Length > 0)
            {
                item.Note = $"Named by typeof at {type.Data.TypeOfAt}; reflection, a registry or an importer may create it where the facts cannot see, so its absence proves nothing.";
                continue;
            }
            item.Status = "unwired";
            item.Reason = "stored only through [SerializeReference] fields ("
                          + string.Join(", ", matching.Take(3).Select(field => $"{field.Field}: {byKey[field.ElementKey].Name}"))
                          + (matching.Length > 3 ? $", {matching.Length - 3} more" : string.Empty)
                          + "); no asset stores it";
            item.SiblingsInAssets = model.Types.Count(other => other.Key != type.Key && wiring[other.Key].Found
                                                               && matching.Any(field => Fits(other, field.ElementKey)));
        }
    }

    // The script asset of every type. Unity names a MonoBehaviour or ScriptableObject by its file, so in each C# file the
    // class named after the file owns the file's GUID; when none is, a class whose name differs only in letter case; when
    // that fails too, the only placeable class of the file. Every part of a partial type counts. Abstract and generic types
    // never own a script.
    private static Dictionary<string, (string File, string Match)> ScriptFiles(FactsModel model)
    {
        var byFile = new Dictionary<string, List<FactsType>>(StringComparer.Ordinal);
        foreach (var type in model.Types)
            foreach (var part in type.Files)
            {
                var file = part[..part.LastIndexOf(':')];
                if (!byFile.TryGetValue(file, out var list))
                    byFile[file] = list = new List<FactsType>();
                if (!list.Contains(type))
                    list.Add(type);
            }
        var rank = new Dictionary<string, int> { ["exact"] = 0, ["case"] = 1, ["single_class"] = 2 };
        var result = new Dictionary<string, (string File, string Match)>(StringComparer.Ordinal);
        foreach (var (file, types) in byFile)
        {
            var candidates = types.Where(type => type is { Kind: "class", IsAbstract: false, IsGeneric: false }).ToArray();
            var stem = Path.GetFileNameWithoutExtension(file);
            var exact = candidates.Where(type => string.Equals(type.Name, stem, StringComparison.Ordinal)).ToArray();
            var caseless = candidates.Where(type => string.Equals(type.Name, stem, StringComparison.OrdinalIgnoreCase)).ToArray();
            var placeable = candidates.Where(type => type.Placeable).ToArray();
            (FactsType Type, string Match)? owner = exact.Length == 1 ? (exact[0], "exact")
                : exact.Length == 0 && caseless.Length == 1 ? (caseless[0], "case")
                : exact.Length == 0 && caseless.Length == 0 && placeable.Length == 1 ? (placeable[0], "single_class")
                : null;
            if (owner == null)
                continue;
            var (ownerType, match) = owner.Value;
            if (!result.TryGetValue(ownerType.Key, out var known) || rank[match] < rank[known.Match])
                result[ownerType.Key] = (file, match);
        }
        return result;
    }

    private static TypeWiring WiringOf(FactsModel model, AssetWiring assets, Dictionary<string, (string File, string Match)> scripts, FactsType type)
    {
        var wiring = new TypeWiring();
        if (scripts.TryGetValue(type.Key, out var script) && assets.GuidByScript.TryGetValue(script.File, out var guid))
        {
            wiring.Script = type.UnityEntry;
            wiring.ScriptFile = script.File;
            wiring.ScriptMatch = script.Match;
            if (assets.ComponentsByGuid.TryGetValue(guid, out var components))
                foreach (var (path, component) in components)
                    wiring.Places.Add((path, component.Object, component.Enabled, component.ListedBy.Select(value =>
                    {
                        var parts = value.Split('|');
                        var lister = assets.ScriptByGuid.TryGetValue(parts[0], out var listerScript) ? Path.GetFileNameWithoutExtension(listerScript) : parts[0];
                        return $"{lister}.{parts[1]}";
                    }).ToList(), string.Empty));
        }
        // Abstract and generic types are never stored themselves; their concrete subclasses are.
        if (!type.IsAbstract && !type.IsGeneric)
        {
            if (assets.UsesBySerializedType.TryGetValue(type.FullName, out var uses))
                foreach (var (path, use) in uses)
                    wiring.Places.Add((path, use.Object, true, new List<string>(), use.Via));
            if (assets.FilesBySerializedType.TryGetValue(type.FullName, out var serializedIn))
                wiring.SerializedIn.AddRange(serializedIn);
        }
        var used = wiring.Places.Select(place => place.File).Concat(wiring.SerializedIn).ToArray();
        wiring.Found = used.Length > 0;
        wiring.TestOnly = used.Length > 0 && used.All(model.Extractors.InTestFolder);
        return wiring;
    }

    private static JsonObject WiringEntry(TypeWiring wiring, JsonNode? arguments)
    {
        var files = wiring.Places.Select(place => place.File).Concat(wiring.SerializedIn).Distinct(StringComparer.Ordinal).ToArray();
        var section = Text(arguments, "section");
        var entry = new JsonObject
        {
            ["status"] = wiring.Status,
            ["prefabs"] = files.Count(path => path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)),
            ["scenes"] = files.Count(path => path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)),
            ["assets"] = files.Count(path => path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)),
            ["test_only"] = wiring.TestOnly,
            ["unused"] = wiring.Status == "unwired",
            ["disabled_in"] = wiring.Places.Count(place => !place.Enabled),
            ["listed_by"] = wiring.Places.Sum(place => place.ListedBy.Count)
        };
        if (wiring.Reason.Length > 0)
            entry["reason"] = wiring.Reason;
        if (wiring.ScriptFile.Length > 0)
        {
            entry["script_name"] = new JsonObject { ["file"] = wiring.ScriptFile, ["match"] = wiring.ScriptMatch };
            if (wiring.ScriptMatch == "case")
                entry["name_warning"] = "The file name differs from the class name only in letter case. Unity uses the file's GUID for this class; renaming either one can break that.";
            else if (wiring.ScriptMatch == "single_class")
                entry["name_warning"] = "No class in the file is named after the file. Unity uses the file's GUID for its only class; a second class in the file or a rename can break that.";
        }
        if (wiring.BaseOf.Count > 0)
        {
            var rows = wiring.BaseOf.Select(name => (JsonNode?)name);
            if (section == "base_of")
            {
                var page = new JsonObject();
                Page(page, "items", rows, arguments, 200);
                entry["base_of"] = page;
            }
            else
                entry["base_of"] = Array(rows.Take(Full(arguments) ? int.MaxValue : 10));
            entry["base_of_count"] = wiring.BaseOf.Count;
            entry["base_of_total"] = wiring.BaseOf.Count;
        }
        if (wiring.SiblingsInAssets >= 0)
            entry["siblings_in_assets"] = wiring.SiblingsInAssets;
        if (wiring.Status == "not_tracked")
            entry["note"] = wiring.Note.Length > 0 ? wiring.Note : "Assets cannot show this type (serialized by value, or not serialized); its absence proves nothing.";
        if (wiring.SerializedIn.Count > 0)
            entry["serialized_references"] = Array(wiring.SerializedIn.Distinct(StringComparer.Ordinal).Take(BehaviourPerSection).Select(path => (JsonNode?)path));
        var places = new JsonObject();
        Page(places, "items", wiring.Places.Select(place =>
        {
            var row = new JsonObject { ["file"] = place.File, ["object"] = place.Object, ["enabled"] = place.Enabled };
            if (place.ListedBy.Count > 0)
                row["listed_by"] = Array(place.ListedBy.Select(value => (JsonNode?)value));
            if (place.Via.Length > 0)
                row["via"] = place.Via;
            return (JsonNode?)row;
        }), section == "wiring" || Full(arguments) ? arguments : null, BehaviourPerSection);
        entry["places"] = places;
        return entry;
    }

    // Contradictions between the graph and the code facts, each with its evidence. Read-only; precision before recall:
    // every item is a fact the graph disagrees with, never a guess.
    public static JsonObject Audit(FactsModel model, JsonNode? arguments, string root)
    {
        var architecture = model.Architecture;
        FactsLayer? only = null;
        var target = Text(arguments, "target");
        if (target.Length > 0 && (only = architecture.FindLayer(target)) == null)
            return LayerFailure(architecture, target);
        if (model.Index == null)
            return Failure("no_code_scope", "No layer has a code scope, so there are no code facts to audit the graph against.");
        var statuses = WiringStatuses(model, AssetWiring.Load(root));
        var minUses = Math.Max(1, IntOr(arguments, "min_uses", 20));
        bool InScope(string? layerId) => only == null || layerId == only.Id;
        var items = new List<JsonObject>();
        void Add(string kind, string layerId, FactsNode? node, string? type, JsonObject evidence) => items.Add(new JsonObject
        {
            ["kind"] = kind,
            ["layer"] = architecture.Path(layerId),
            ["node"] = node?.Id,
            ["node_title"] = node?.Title,
            ["type"] = type,
            ["evidence"] = evidence
        });
        FactsType? LinkedType(FactsNode? node) =>
            node != null && node.TypeMode == "linked" && node.TypeName.Length > 0 && model.TypesByName.TryGetValue(node.TypeName, out var type) ? type : null;

        foreach (var layer in architecture.Layers.Values.Where(layer => InScope(layer.Id)).OrderBy(layer => layer.Path, StringComparer.Ordinal))
            foreach (var node in layer.Nodes.Where(node => !node.IsPortal))
            {
                var type = LinkedType(node);
                if (type == null)
                    continue;
                var wiring = statuses[type.Key];
                var kind = wiring.Status switch
                {
                    "unwired" => "node_on_unwired_type",
                    "test_only" => "node_on_test_only_type",
                    "created_in_code" => "node_on_created_in_code_type",
                    _ => null
                };
                if (kind == null)
                    continue;
                var evidence = new JsonObject { ["status"] = wiring.Status };
                if (wiring.Reason.Length > 0)
                    evidence["reason"] = wiring.Reason;
                if (wiring.Status == "test_only")
                    evidence["places"] = Array(wiring.Places.Select(place => place.File).Concat(wiring.SerializedIn).Distinct(StringComparer.Ordinal).Take(5).Select(path => (JsonNode?)path));
                if (wiring.Status == "created_in_code")
                    evidence["check"] = wiring.Reason.StartsWith("created from Editor code", StringComparison.Ordinal)
                        ? "Editor code (an importer or a tool) makes this type; check that the node describes it as made in the Editor, not placed or created at run time."
                        : "The type is live but nothing places it in assets; check that the node describes it as created at run time.";
                Add(kind, layer.Id, node, type.FullName, evidence);
            }

        var evidenceReferences = model.References.Where(reference => reference.CountsAsEvidence).ToArray();
        var usesByType = evidenceReferences.GroupBy(reference => reference.TargetType, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var type in model.Types.Where(type => type.OwnerLayerId != null && InScope(type.OwnerLayerId) && type.Nodes.Count == 0))
        {
            if (statuses[type.Key].Status is "unwired" or "test_only" || model.Extractors.InTestFolder(type.FirstFile))
                continue;
            var uses = usesByType.TryGetValue(type.FullName, out var list) ? list.Where(reference => reference.SourceLayerId != type.OwnerLayerId).ToArray() : System.Array.Empty<FactsReference>();
            var subclassesElsewhere = type.DerivedByKeys
                .Select(key => model.Types.FirstOrDefault(candidate => candidate.Key == key))
                .Count(child => child?.OwnerLayerId != null && child.OwnerLayerId != type.OwnerLayerId);
            var extension = type.IsExtensionPoint && subclassesElsewhere > 0;
            if (uses.Length < minUses && !extension)
                continue;
            var evidence = new JsonObject
            {
                ["uses_by_other_layers"] = uses.Length,
                ["layers"] = Array(uses.GroupBy(reference => reference.SourceLayerId!)
                    .OrderByDescending(group => group.Count())
                    .Take(3)
                    .Select(group => (JsonNode?)new JsonArray(architecture.Path(group.Key), group.Count())))
            };
            if (extension)
                evidence["subclasses_in_other_layers"] = subclassesElsewhere;
            evidence["at"] = type.At;
            Add("central_type_without_node", type.OwnerLayerId!, null, type.FullName, evidence);
        }

        var pairs = Pairs(evidenceReferences);
        var scoped = model.ScopedLayers.Select(layer => layer.Id).ToHashSet(StringComparer.Ordinal);
        var dependencies = architecture.CrossRelationships.Where(relationship => relationship.Kind == "dependency"
                                                                                 && (InScope(relationship.SourceLayerId) || InScope(relationship.TargetLayerId))
                                                                                 && scoped.Contains(relationship.SourceLayerId)
                                                                                 && scoped.Contains(relationship.TargetLayerId)).ToArray();
        foreach (var relationship in dependencies.Where(relationship => !pairs.ContainsKey((relationship.SourceLayerId, relationship.TargetLayerId))))
        {
            var evidence = RelationshipEntry(architecture, relationship);
            evidence["references"] = 0;
            evidence["message"] = $"No code in {architecture.Path(relationship.SourceLayerId)} references {architecture.Path(relationship.TargetLayerId)}.";
            Add("relationship_without_evidence", relationship.SourceLayerId, architecture.FindNode(relationship.SourceNodeId), null, evidence);
        }
        foreach (var group in dependencies.GroupBy(relationship => (relationship.SourceLayerId, relationship.TargetLayerId)))
        {
            if (!pairs.TryGetValue(group.Key, out var references))
                continue;
            var typed = group.Select(relationship => (Relationship: relationship,
                    Source: LinkedType(architecture.FindNode(relationship.SourceNodeId)),
                    Target: LinkedType(architecture.FindNode(relationship.TargetNodeId))))
                .ToArray();
            // Only when every permission for the pair names types on both ends can the claim be checked.
            if (typed.Any(item => item.Source == null || item.Target == null))
                continue;
            if (typed.Any(item => references.Any(reference => reference.SourceType == item.Source!.FullName || reference.TargetType == item.Target!.FullName)))
                continue;
            var evidence = new JsonObject
            {
                ["relationships"] = Array(typed.Select(item => (JsonNode?)RelationshipEntry(architecture, item.Relationship))),
                ["references"] = references.Count,
                ["type_pairs"] = TypePairs(references, PairSamples),
                ["message"] = "The only permissions for this layer pair are drawn between types that hold none of its references; type_pairs shows the types that do."
            };
            Add("relationship_on_wrong_types", group.Key.SourceLayerId, null, null, evidence);
        }

        var connected = new HashSet<string>(architecture.CrossRelationships.SelectMany(relationship => new[] { relationship.SourceNodeId, relationship.TargetNodeId }), StringComparer.Ordinal);
        foreach (var layer in architecture.Layers.Values)
            foreach (var edge in layer.Edges)
            {
                connected.Add(edge.SourceNodeId);
                connected.Add(edge.TargetNodeId);
            }
        foreach (var layer in architecture.Layers.Values.Where(layer => InScope(layer.Id)).OrderBy(layer => layer.Path, StringComparer.Ordinal))
            foreach (var node in layer.Nodes.Where(node => !node.IsPortal && !connected.Contains(node.Id) && !architecture.StandaloneNodes.Contains(node.Id)))
                Add("isolated_node", layer.Id, node, node.TypeName.Length > 0 ? node.TypeName : null,
                    new JsonObject { ["type_mode"] = node.TypeMode.Length > 0 ? node.TypeMode : "none", ["message"] = "No relationship and no standalone mark." });

        var result = Success();
        if (only != null)
            result["layer"] = only.Path;
        result["min_uses"] = minUses;
        var byKind = new JsonObject();
        foreach (var group in items.GroupBy(item => (string)item["kind"]!).OrderBy(group => group.Key, StringComparer.Ordinal))
            byKind[group.Key] = group.Count();
        result["by_kind"] = byKind;
        Page(result, "items", items, arguments, 100);
        return result;
    }

    private static readonly string[] BehaviourSections = { "reads", "writes", "uses", "subscribes", "unsubscribes", "assigns", "lifecycle", "recurring" };
    private const int BehaviourPerSection = 40;

    // Counts per section, then each section's entries with file:line, at most 40 each; "section" with offset and limit
    // pages one section in full.
    private static JsonObject BehaviourEntry(FactsModel model, FactsType type, JsonNode? arguments)
    {
        var entry = new JsonObject();
        var only = Text(arguments, "section");
        var perSection = Full(arguments) ? int.MaxValue : BehaviourPerSection;
        var counts = new JsonObject();
        foreach (var section in BehaviourSections)
            counts[section] = type.Behaviour.TryGetValue(section, out var values) ? values.Count : 0;
        entry["counts"] = counts;
        foreach (var section in BehaviourSections)
        {
            if (!type.Behaviour.TryGetValue(section, out var values) || only.Length > 0 && only != section)
                continue;
            var rows = values.Select(value => (JsonNode?)new JsonArray(value.Value, value.At));
            if (only == section)
            {
                var page = new JsonObject();
                Page(page, "items", rows, arguments, 200);
                entry[section] = page;
            }
            else
            {
                entry[section] = Array(rows.Take(perSection));
                if (values.Count > perSection)
                    entry[section + "_more"] = values.Count - perSection;
            }
        }
        if (type.Behaviour.TryGetValue("lifecycle", out var lifecycle))
        {
            var names = lifecycle.Select(value => value.Value).ToHashSet(StringComparer.Ordinal);
            // Unity also calls the callbacks a project base class declares, so they count when pairing.
            var inherited = new List<(string Name, string Base, string At)>();
            foreach (var ancestor in ProjectBases(model, type))
                if (ancestor.Behaviour.TryGetValue("lifecycle", out var baseLifecycle))
                    foreach (var (name, at) in baseLifecycle)
                        if (!names.Contains(name) && inherited.All(item => item.Name != name))
                            inherited.Add((name, ancestor.FullName, at));
            var all = names.Concat(inherited.Select(item => item.Name)).ToHashSet(StringComparer.Ordinal);
            var unpaired = new[] { ("OnEnable", "OnDisable"), ("OnDisable", "OnEnable") }
                .Where(pair => names.Contains(pair.Item1) && !all.Contains(pair.Item2))
                .Select(pair => (JsonNode?)$"{pair.Item1} without {pair.Item2}")
                .ToArray();
            if (unpaired.Length > 0)
                entry["unpaired"] = Array(unpaired);
            var pairs = inherited.Where(item => (item.Name == "OnDisable" && names.Contains("OnEnable")) || (item.Name == "OnEnable" && names.Contains("OnDisable"))).ToArray();
            if (pairs.Length > 0)
                entry["inherited_lifecycle"] = Array(pairs.Select(item => (JsonNode?)new JsonArray(item.Name, item.Base, item.At)));
            var loops = new[] { "Update", "FixedUpdate", "LateUpdate" }.Where(names.Contains).Select(name => (JsonNode?)name).ToArray();
            if (loops.Length > 0)
                entry["loops"] = Array(loops);
        }
        if (type.Behaviour.ContainsKey("recurring"))
            entry["recurring_note"] = "recurring lists per-frame work outside Update: frame waits in loops, polling calls and the calls named in code-facts.json recurringCalls. Work registered on other schedulers or player-loop hooks is not seen.";
        return entry;
    }

    // The project base classes of a type, nearest first (generic arguments ignored).
    internal static IEnumerable<FactsType> ProjectBases(FactsModel model, FactsType type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current.Base != null;)
        {
            var name = current.Base;
            var generic = name.IndexOf('<');
            if (generic >= 0)
                name = name[..generic];
            var next = model.TypesByName.TryGetValue(name, out var exact) ? exact
                : model.Types.FirstOrDefault(candidate => candidate.FullName.StartsWith(name + "`", StringComparison.Ordinal));
            if (next == null || !seen.Add(next.Key))
                yield break;
            yield return next;
            current = next;
        }
    }

    private static JsonObject MemberReference(IndexMember member)
    {
        var reference = new JsonObject { ["kind"] = member.Name.Length == 0 ? member.Kind : ReferenceKind(member), ["name"] = member.Name };
        if (member.HasParameters)
            reference["parameters"] = Array(member.Parameters.Select(parameter => (JsonNode?)parameter));
        return reference;
    }

    private static string ReferenceKind(IndexMember member) => member.Kind switch
    {
        "method" when member.Name == ".ctor" => "constructor",
        "method" when member.Name.StartsWith("op_", StringComparison.Ordinal) => "operator",
        _ => member.Kind
    };

    internal static JsonObject Success() => new() { ["success"] = true };

    internal static JsonObject Failure(string code, string message) => new() { ["success"] = false, ["code"] = code, ["message"] = message };
}
