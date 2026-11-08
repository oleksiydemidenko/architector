using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;

namespace Architector.CodeFacts;

// Read-only answers over the model. Compact by default: counts and the most frequent type pairs;
// full lists come by offset/limit paging or with output "file".
public static class FactsQueries
{
    public static readonly string[] Actions = { "code_evidence", "layer_facts", "scope_what_if", "layer_dump", "lookup" };
    public static readonly string[] ArchitectureOnlyActions = { "layer_dump", "lookup" };

    // Replies are paid for in tokens: no indentation and no escaping of characters such as -> or '.
    public static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly JsonSerializerOptions Indented = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

    private const int PairSamples = 3;
    private const int LayerPairSamples = 5;

    public static JsonObject Answer(FactsModel model, string action, JsonNode? arguments, string root)
    {
        var result = action switch
        {
            "code_evidence" => CodeEvidence(model, arguments),
            "layer_facts" => LayerFacts(model, arguments),
            "scope_what_if" => ScopeWhatIf(model, arguments),
            "layer_dump" => LayerDump(model.Architecture, arguments),
            "lookup" => Lookup(model.Architecture, arguments),
            _ => Failure("unknown_action", $"Unknown code facts action '{action}'. Valid: {string.Join(", ", Actions)}.")
        };
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

        var include = Text(arguments, "include");
        if (include == "references")
        {
            var rows = selected.SelectMany(pair => pair.Value.Select(reference => (JsonNode?)new JsonArray(reference.SourceType, reference.TargetType, reference.At)));
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
                .Select(reference => (JsonNode?)new JsonArray(reference.SourceType, reference.TargetType, reference.At));
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

    public static JsonObject LayerFacts(FactsModel model, JsonNode? arguments)
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
            var type = owned.FirstOrDefault(candidate => candidate.FullName == typeName)
                       ?? owned.SingleOrDefault(candidate => candidate.Name == typeName);
            if (type == null)
                return Failure("type_not_found", $"Layer '{layer.Path}' owns no type '{typeName}'.");
            var detail = Success();
            detail["layer"] = layer.Path;
            detail["type"] = TypeEntry(architecture, type);
            Page(detail, "members", type.Members.Select(member => (JsonNode?)new JsonArray(
                member.Kind,
                member.Display,
                member.At.Length == 0 ? null : member.At)), arguments, 100);
            if (type.Extras.Count > 0)
            {
                var extra = new JsonObject();
                foreach (var (kind, values) in type.Extras)
                    extra[kind] = Array(values.Select(pair => (JsonNode?)new JsonArray(pair.Key, pair.Value)));
                detail["extra"] = extra;
            }
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
                ["node"] = item.type.Nodes.FirstOrDefault()
            })
            .ToArray();
        if (model.RuleSuggestions.Count > 0)
            result["rule_suggestions"] = $"{model.RuleSuggestions.Count}; code_evidence lists them";
        result["node_candidates_total"] = candidates.Length;
        result["node_candidates"] = Array(candidates.Take(IntOr(arguments, "candidates", 40)));
        Page(result, "type_list", owned.OrderBy(type => type.At, StringComparer.Ordinal).Select(type => (JsonNode?)TypeEntry(architecture, type)), arguments, 50);
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
                ["example"] = suggestion.Example
            }));
            rules["hint"] = "The code repeats these but Architector/code-facts.json does not cover them. Read a few examples; when they wire types together (dependency access, injection), propose adding them to the user.";
        }
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

    private static Dictionary<(string Source, string Target), List<FactsReference>> Pairs(IEnumerable<FactsReference> references)
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

    private static void Page(JsonObject result, string name, IEnumerable<JsonNode?> rows, JsonNode? arguments, int defaultLimit)
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

    private static JsonArray Array(IEnumerable<JsonNode?> items)
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

    private static int IntOr(JsonNode? node, string name, int fallback)
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

    private static JsonObject LayerFailure(FactsArchitecture architecture, string reference)
    {
        var failure = Failure("layer_not_found", $"No layer '{reference}'. Use a layer id, its path (Group/Layer) or a unique name.");
        failure["layers"] = Array(architecture.Layers.Values
            .Where(layer => reference.Length > 0 && layer.Path.Contains(reference, StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .Select(layer => (JsonNode?)layer.Path));
        return failure;
    }

    private static JsonObject Success() => new() { ["success"] = true };

    private static JsonObject Failure(string code, string message) => new() { ["success"] = false, ["code"] = code, ["message"] = message };
}
