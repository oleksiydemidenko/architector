using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Architector.CodeFacts;

// The digest and route as the MCP tools and the command line answer them: the approved digest the Editor writes
// (Architector/Architecture.digest.md) read section by section, in a compact or a full form, with the writers and
// readers of each node's type generated from the code facts. Nothing here needs the Unity Editor.
public static partial class McpServer
{
    private const int CompactAccessLimit = 4;
    private const int FullAccessLimit = 10;
    private const int RoleLength = 80;

    private static readonly Regex DigestNodeLine = new(@"^- \*\*(?<title>.+?)\*\*: (?:(?<concept>concept)|(?<kind>\w+) `(?<type>[^`]+)`(?<manual> \(manual\))?)(?: — (?<role>.*))?$", RegexOptions.Compiled);

    private sealed record DigestNode(string Title, string Kind, string Type, bool Manual, string Role, int Line, int End);

    private sealed record DigestSection(string Heading, string Path, int Start, int End, List<DigestNode> Nodes);

    // The text of architector_digest and of the digest command. error is true for an unknown layer or no digest.
    private static string DigestText(JsonNode? arguments, out bool error)
    {
        error = true;
        var path = System.IO.Path.Combine(DataRoot, "Architecture.digest.md");
        if (!File.Exists(path))
            return "No layer is approved yet, so there is no digest. Use architector_query (or facts layer) for drafts.";
        var lines = File.ReadAllLines(path);
        var sections = ParseDigest(lines);
        var requested = new List<string>();
        if (arguments?["layer"] is JsonValue single && single.TryGetValue<string>(out var layerText) && layerText.Trim().Length > 0)
            requested.AddRange(layerText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (arguments?["layers"] is JsonArray list)
            requested.AddRange(list.Select(item => (string?)item).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!.Trim()));
        else if (arguments?["layers"] is JsonValue layersText && layersText.TryGetValue<string>(out var joined))
            requested.AddRange(joined.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var form = ((string?)arguments?["form"])?.Trim().ToLowerInvariant();
        if (form is not (null or "" or "compact" or "full" or "list"))
            return $"form takes compact, full or list, not '{form}'.";

        // Layers asked by name keep the digest order; a group names all its approved layers.
        var selected = sections;
        var group = false;
        if (requested.Count > 0)
        {
            var chosen = new HashSet<DigestSection>();
            foreach (var name in requested)
            {
                var (matches, isGroup) = MatchSections(sections, name);
                if (matches.Count == 0)
                    return $"No approved layer or group is called {name}. The digest lists approved layers only; call digest with form list for their names, or architector_query layer_facts for a draft layer.";
                if (matches.Count > 1 && !isGroup)
                    return $"'{name}' names several approved layers: {string.Join(", ", matches.Select(section => section.Path))}. Use a longer path.";
                group |= isGroup;
                chosen.UnionWith(matches);
            }
            selected = sections.Where(chosen.Contains).ToList();
        }
        if (string.IsNullOrEmpty(form))
            form = requested.Count == 0 || group ? "compact" : "full";
        error = false;
        if (form == "list")
            return string.Join("\n", selected.Select(section => section.Heading));

        var access = StateAccessSource(out var accessNote);
        var text = new StringBuilder();
        if (form == "compact")
        {
            text.Append("# Architecture Digest (compact)\n\n");
            text.Append($"{selected.Count} approved layer{(selected.Count == 1 ? string.Empty : "s")}, {selected.Sum(section => section.Nodes.Count)} nodes. "
                        + "A node line is its title, its kind unless class and its type when the name differs, then the first sentence of its responsibility, at most 80 characters (authored, may drift from the code). "
                        + "\"written by\" and \"read by\" are generated from the code facts (configured read and write calls), at most "
                        + $"{CompactAccessLimit} names each, then the total. Full form with members and relationships: form full, or name the layers. A group path names all its layers in this compact form.\n");
            if (accessNote != null)
                text.Append(accessNote).Append('\n');
            foreach (var section in selected)
            {
                text.Append($"\n## {section.Heading}\n");
                foreach (var node in section.Nodes)
                {
                    text.Append(CompactNodeLine(node)).Append('\n');
                    var generated = AccessLines(access, section, node, CompactAccessLimit);
                    if (generated.Count > 0)
                        text.Append("  ").Append(string.Join("; ", generated)).Append('\n');
                }
            }
            return text.ToString().TrimEnd();
        }

        // Full: the digest as the Editor wrote it, with the generated lines after each node's own lines.
        if (requested.Count == 0)
        {
            var preamble = sections.Count > 0 ? sections[0].Start : lines.Length;
            for (var index = 0; index < preamble; index++)
                text.Append(lines[index]).Append('\n');
            if (accessNote != null)
                text.Append(accessNote).Append('\n');
        }
        else if (accessNote != null)
            text.Append(accessNote).Append('\n').Append('\n');
        foreach (var section in selected)
        {
            var after = section.Nodes.ToDictionary(node => node.End, node => node);
            for (var index = section.Start; index < section.End; index++)
            {
                text.Append(lines[index]).Append('\n');
                if (after.TryGetValue(index + 1, out var node))
                    foreach (var line in AccessLines(access, section, node, FullAccessLimit))
                        text.Append("  - ").Append(line).Append(" (generated from code facts)\n");
            }
            text.Append('\n');
        }
        return text.ToString().TrimEnd();
    }

    private static List<DigestSection> ParseDigest(string[] lines)
    {
        var sections = new List<DigestSection>();
        var headings = lines.Select((line, index) => (Line: line, Index: index)).Where(entry => entry.Line.StartsWith("## ", StringComparison.Ordinal)).ToList();
        for (var position = 0; position < headings.Count; position++)
        {
            var start = headings[position].Index;
            var end = position + 1 < headings.Count ? headings[position + 1].Index : lines.Length;
            var heading = headings[position].Line[3..].Trim();
            var layerPath = heading.EndsWith(" (Planned)", StringComparison.Ordinal) ? heading[..^" (Planned)".Length] : heading;
            var nodes = new List<DigestNode>();
            var inNodes = false;
            for (var index = start + 1; index < end; index++)
            {
                var line = lines[index];
                if (line.StartsWith("### ", StringComparison.Ordinal))
                {
                    inNodes = line.Trim() == "### Nodes";
                    continue;
                }
                if (!inNodes)
                    continue;
                var match = DigestNodeLine.Match(line);
                if (!match.Success)
                    continue;
                var nodeEnd = index + 1;
                while (nodeEnd < end && lines[nodeEnd].StartsWith("  ", StringComparison.Ordinal))
                    nodeEnd++;
                nodes.Add(new DigestNode(
                    match.Groups["title"].Value,
                    match.Groups["concept"].Success ? "concept" : match.Groups["kind"].Value,
                    match.Groups["type"].Value,
                    match.Groups["manual"].Success,
                    match.Groups["role"].Value,
                    index,
                    nodeEnd));
            }
            // The section ends before the blank lines that separate it from the next one.
            while (end > start + 1 && lines[end - 1].Trim().Length == 0)
                end--;
            sections.Add(new DigestSection(heading, layerPath, start, end, nodes));
        }
        return sections;
    }

    // A layer path, case-insensitive, its id, or a unique trailing part of the path ("Movement" for
    // "Core/Player/Movement"); otherwise a group path or a trailing part of one ("Player" for "Core/Player"), which
    // names every approved layer under it.
    private static (List<DigestSection> Matches, bool Group) MatchSections(List<DigestSection> sections, string name)
    {
        var value = string.Join("/", name.Trim().Trim('/').Split('/').Select(segment => segment.Trim()));
        var exact = sections.Where(section => string.Equals(section.Path, value, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0)
            return (exact, false);
        var byId = TryArchitecture()?.Layers.GetValueOrDefault(value)?.Path;
        if (byId != null)
            return (sections.Where(section => string.Equals(section.Path, byId, StringComparison.Ordinal)).ToList(), false);
        var trailing = sections.Where(section => section.Path.EndsWith("/" + value, StringComparison.OrdinalIgnoreCase)).ToList();
        if (trailing.Count > 0)
            return (trailing, false);
        return (sections.Where(section => section.Path.StartsWith(value + "/", StringComparison.OrdinalIgnoreCase)
                                          || section.Path.Contains("/" + value + "/", StringComparison.OrdinalIgnoreCase)).ToList(), true);
    }

    private static FactsArchitecture? TryArchitecture()
    {
        try
        {
            return FactsArchitecture.Load(DataRoot);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string CompactNodeLine(DigestNode node)
    {
        var shortType = ShortTypeName(node.Type);
        // class is the common kind and is left out; other kinds and a type named differently from the node are shown.
        var kindWord = node.Kind == "class" ? string.Empty : node.Kind;
        var type = node.Kind == "concept" || string.Equals(shortType, node.Title, StringComparison.Ordinal) ? string.Empty : shortType;
        var tags = string.Join(" ", new[] { kindWord, type }.Where(part => part.Length > 0)) + (node.Manual ? ", manual" : string.Empty);
        var role = FirstSentence(node.Role);
        return $"- {node.Title}{(tags.Length > 0 ? $" ({tags.TrimStart(',', ' ')})" : string.Empty)}" + (role.Length > 0 ? ": " + role : string.Empty);
    }

    // "Game.Items.Inventory<T>" -> "Inventory<T>".
    private static string ShortTypeName(string type)
    {
        var generic = type.IndexOf('<');
        var name = generic >= 0 ? type[..generic] : type;
        var dot = name.LastIndexOf('.');
        return (dot >= 0 ? name[(dot + 1)..] : name) + (generic >= 0 ? type[generic..] : string.Empty);
    }

    private static string FirstSentence(string text)
    {
        text = text.Trim();
        for (var index = 0; index < text.Length - 1; index++)
            if (text[index] is '.' or '!' or '?' or ';' && text[index + 1] == ' ')
            {
                text = text[..(index + 1)];
                break;
            }
        if (text.Length <= RoleLength)
            return text;
        // Cut after the last word that fits, so a role never ends in half a word.
        var cut = text.LastIndexOf(' ', RoleLength - 1);
        return text[..(cut > RoleLength / 2 ? cut : RoleLength - 1)].TrimEnd(' ', ',', ';', ':') + "…";
    }

    // The code facts for writers and readers, or null with a note on why there are none.
    private static FactsModel? StateAccessSource(out string? note)
    {
        note = null;
        try
        {
            var model = (facts ?? new FactsCache(root!, "net100", () => Basic.Reference.Assemblies.Net100.References.All)).Get();
            if (model.Index == null)
            {
                note = "Writers and readers: not available, no layer has a code scope.";
                return null;
            }
            return model;
        }
        catch (Exception exception)
        {
            note = $"Writers and readers: not available, the code facts failed ({exception.GetType().Name}: {exception.Message}).";
            return null;
        }
    }

    private static List<string> AccessLines(FactsModel? model, DigestSection section, DigestNode node, int limit)
    {
        var lines = new List<string>();
        if (model == null || node.Kind == "concept")
            return lines;
        var type = ResolveNodeType(model, section, node);
        if (type == null)
            return lines;
        var writers = model.StateAccess.Writers(type.FullName, model.Architecture);
        var readers = model.StateAccess.Readers(type.FullName, model.Architecture);
        if (writers.Count > 0)
            lines.Add("written by: " + FactsStateAccess.Describe(writers, model.Architecture, limit));
        if (readers.Count > 0)
            lines.Add("read by: " + FactsStateAccess.Describe(readers, model.Architecture, limit));
        return lines;
    }

    private static FactsType? ResolveNodeType(FactsModel model, DigestSection section, DigestNode node)
    {
        var generic = node.Type.IndexOf('<');
        var name = generic < 0 ? node.Type : $"{node.Type[..generic]}`{node.Type[generic..].Count(character => character == ',') + 1}";
        if (model.TypesByName.TryGetValue(name, out var exact))
            return exact;
        // The current graph knows the node's metadata name when the digest's display name differs.
        var layer = model.Architecture.Layers.Values.FirstOrDefault(candidate => string.Equals(candidate.Path, section.Path, StringComparison.Ordinal));
        var linked = layer?.Nodes.FirstOrDefault(candidate => candidate.Title == node.Title)?.TypeName;
        if (!string.IsNullOrEmpty(linked) && model.TypesByName.TryGetValue(linked, out var byNode))
            return byNode;
        return model.FindType(generic < 0 ? node.Type : node.Type[..generic]);
    }

    // The route text (or its JSON with --json) with the writers and readers of the node a query names.
    private static string RouteText(IEnumerable<string> queries, int? limit, bool json, out bool allFound)
    {
        var routeArguments = new List<string> { root! };
        routeArguments.AddRange(queries);
        if (limit != null)
            routeArguments.Add("--limit=" + limit.Value);
        if (json)
            routeArguments.Add("--json");
        var model = StateAccessSource(out _);
        Program.NodeAccess = model == null ? null : (layerId, title) =>
        {
            var layer = model.Architecture.Layers.GetValueOrDefault(layerId);
            var typeName = layer?.Nodes.FirstOrDefault(node => node.Title == title)?.TypeName;
            if (string.IsNullOrEmpty(typeName) || !model.TypesByName.TryGetValue(typeName, out var type))
                return null;
            var writers = model.StateAccess.Writers(type.FullName, model.Architecture);
            var readers = model.StateAccess.Readers(type.FullName, model.Architecture);
            if (writers.Count == 0 && readers.Count == 0)
                return null;
            return (writers.Count == 0 ? null : FactsStateAccess.Describe(writers, model.Architecture, FullAccessLimit),
                readers.Count == 0 ? null : FactsStateAccess.Describe(readers, model.Architecture, FullAccessLimit));
        };
        var console = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        int code;
        try
        {
            code = Program.Main(routeArguments.ToArray());
        }
        finally
        {
            Console.SetOut(console);
            Program.NodeAccess = null;
        }
        allFound = code == 0;
        return captured.ToString();
    }
}
