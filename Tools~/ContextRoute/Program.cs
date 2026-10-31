using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public static class Program
{
    private const string ConfigFile = "Architector/context-route.json";
    private static readonly string[] DefaultDocumentRoots = { "Documentation", "docs" };
    private static string[] DocumentRoots = DefaultDocumentRoots;

    public static int Main(string[] args)
    {
        var positional = args.Where(arg => !arg.StartsWith("--")).ToList();
        var root = positional.Count > 0 && Directory.Exists(Path.Combine(positional[0], "Architector")) ? Path.GetFullPath(positional[0]) : Directory.GetCurrentDirectory();
        var queries = positional.Count > 0 && Path.GetFullPath(positional[0]) == root ? positional.Skip(1).ToList() : positional;
        string? Option(string name) => args.LastOrDefault(arg => arg.StartsWith("--" + name + "="))?[(name.Length + 3)..];
        var limit = int.TryParse(Option("limit"), out var parsedLimit) ? parsedLimit : 6;
        if (queries.Count == 0)
        {
            Console.Error.WriteLine("Usage: context_route [<repo root>] <file path or type name>... [--limit=N] [--docs=<folder,folder>] [--json]");
            return 2;
        }

        DocumentRoots = LoadDocumentRoots(root, Option("docs"));
        var model = Model.Load(root);
        var documents = DocumentRoots.Select(directory => Path.Combine(root, directory)).Where(Directory.Exists)
            .SelectMany(directory => Directory.GetFiles(directory, "*.md", SearchOption.AllDirectories))
            .Select(file => (Path: Path.GetRelativePath(root, file).Replace('\\', '/'), Text: File.ReadAllText(file))).ToList();
        var routes = queries.Select(query => Route.Resolve(query, root, model, documents, limit)).ToList();

        if (args.Contains("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(routes.Select(route => route.ToJson()), new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return routes.All(route => route.Found) ? 0 : 1;
        }

        var text = new StringBuilder();
        foreach (var route in routes)
            route.Write(text);
        Console.Write(text.ToString());
        return routes.All(route => route.Found) ? 0 : 1;
    }

    private static string[] LoadDocumentRoots(string root, string? option)
    {
        if (!string.IsNullOrEmpty(option))
            return option.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var config = Path.Combine(root, ConfigFile);
        if (!File.Exists(config))
            return DefaultDocumentRoots;
        return JsonNode.Parse(File.ReadAllText(config))?["documentRoots"]?.AsArray().Select(item => (string)item!).ToArray()
            ?? DefaultDocumentRoots;
    }

    private sealed class Layer
    {
        public string Id = "";
        public string Name = "";
        public string Path = "";
        public string Purpose = "";
        public List<string> Folders = new();
        public List<(string Title, string Type, List<string> Files)> Nodes = new();
        public HashSet<string> Connected = new();
        public JsonNode Raw = null!;
    }

    private sealed class Model
    {
        public List<Layer> Layers = new();
        public Dictionary<string, (int Start, int End)> DigestSections = new();
        public Dictionary<string, string> NodeTitles = new();

        public List<string> Summary(Layer layer)
        {
            var lines = new List<string>();
            foreach (var node in layer.Raw["nodes"]?.AsArray() ?? new JsonArray())
            {
                if ((string?)node!["kind"] == "layerPortal")
                    continue;
                var type = node["type"];
                var name = (string?)type?["name"] ?? "";
                var declared = (string?)type?["mode"] == "linked" ? $"{type!["kind"]} `{name}`" : name.Length > 0 ? $"planned {type!["kind"]} `{name}`" : "planned";
                lines.Add($"{node["title"]}: {declared} — {node["responsibility"]}");
                var baseName = (string?)type?["baseType"]?["name"];
                if (!string.IsNullOrEmpty(baseName))
                    lines.Add("  base " + baseName);
                var interfaces = type?["interfaces"]?.AsArray().Select(item => (string?)item!["name"]).Where(item => !string.IsNullOrEmpty(item)).ToList();
                if (interfaces?.Count > 0)
                    lines.Add("  implements " + string.Join(", ", interfaces));
                lines.AddRange(node["members"]?.AsArray().Select(member => "  " + (string?)member!["declaration"]) ?? Enumerable.Empty<string>());
            }
            foreach (var edge in (layer.Raw["edges"]?.AsArray() ?? new JsonArray()).Where(edge => (string?)edge!["sourceLayerId"] == layer.Id))
            {
                var label = (string?)edge!["label"];
                lines.Add($"{NodeTitle(edge["sourceNodeId"], layer.Id)} -> {NodeTitle(edge["targetNodeId"], layer.Id)} ({edge["kind"]})" + (string.IsNullOrEmpty(label) ? "" : $" \"{label}\""));
            }
            return lines;
        }

        private string NodeTitle(JsonNode? id, string layerId)
        {
            if (!NodeTitles.TryGetValue((string?)id ?? "", out var title))
                return "?";
            var owner = title.Split('|');
            return owner[0] == layerId ? owner[1] : (Layers.FirstOrDefault(layer => layer.Id == owner[0])?.Path ?? "?") + "/" + owner[1];
        }

        private static string[] JsonFiles(string architector, string folder)
        {
            var path = Path.Combine(architector, folder);
            return Directory.Exists(path) ? Directory.GetFiles(path, "*.json") : Array.Empty<string>();
        }

        public static Model Load(string root)
        {
            var model = new Model();
            var architector = Path.Combine(root, "Architector");
            var groups = JsonFiles(architector, "Groups")
                .Select(file => JsonNode.Parse(File.ReadAllText(file))!)
                .ToDictionary(node => (string)node["id"]!, node => (Name: (string)node["name"]!, Parent: (string?)node["parentGroupId"] ?? ""));
            string GroupPath(string id) => groups.TryGetValue(id, out var group) ? (group.Parent.Length > 0 ? GroupPath(group.Parent) + "/" : "") + group.Name : "";

            foreach (var file in JsonFiles(architector, "Layers"))
            {
                var node = JsonNode.Parse(File.ReadAllText(file))!;
                var layer = new Layer
                {
                    Id = (string)node["layerId"]!,
                    Name = (string)node["name"]!,
                    Purpose = (string?)node["purpose"] ?? "",
                    Raw = node
                };
                var groupPath = GroupPath((string?)node["parentGroupId"] ?? "");
                layer.Path = groupPath.Length > 0 ? groupPath + "/" + layer.Name : layer.Name;
                layer.Folders.AddRange(node["scope"]?["folders"]?.AsArray().Select(item => ((string)item!).TrimEnd('/')) ?? Enumerable.Empty<string>());
                foreach (var item in node["nodes"]?.AsArray() ?? new JsonArray())
                {
                    var type = item!["type"];
                    var files = type?["sourceDeclarations"]?.AsArray().Select(declaration => (string)declaration!["repositoryPath"]!).ToList() ?? new List<string>();
                    layer.Nodes.Add(((string?)item["title"] ?? "", (string?)type?["name"] ?? "", files));
                    if ((string?)item["kind"] != "layerPortal")
                        model.NodeTitles[(string?)item["id"] ?? ""] = layer.Id + "|" + ((string?)item["title"] ?? "");
                    var target = (string?)item["portal"]?["targetLayerId"];
                    if (!string.IsNullOrEmpty(target))
                        layer.Connected.Add(target);
                }
                foreach (var edge in node["edges"]?.AsArray() ?? new JsonArray())
                {
                    var target = (string?)edge!["targetLayerId"];
                    if (!string.IsNullOrEmpty(target) && target != layer.Id)
                        layer.Connected.Add(target);
                }
                model.Layers.Add(layer);
            }

            foreach (var file in JsonFiles(architector, "Links"))
            {
                var ids = JsonNode.Parse(File.ReadAllText(file))!["endpoints"]?.AsArray().Select(endpoint => (string)endpoint!["layerId"]!).ToArray() ?? Array.Empty<string>();
                foreach (var layer in model.Layers.Where(layer => ids.Contains(layer.Id)))
                    layer.Connected.UnionWith(ids.Where(id => id != layer.Id));
            }

            var digest = Path.Combine(architector, "Architecture.digest.md");
            if (File.Exists(digest))
            {
                var lines = File.ReadAllLines(digest);
                var headings = lines.Select((line, index) => (line, index)).Where(entry => entry.line.StartsWith("## ")).ToList();
                for (var i = 0; i < headings.Count; i++)
                    model.DigestSections[headings[i].line[3..].Trim()] = (headings[i].index + 1, i + 1 < headings.Count ? headings[i + 1].index : lines.Length);
            }
            return model;
        }
    }

    private sealed class Route
    {
        public string Query = "";
        public string? File;
        public List<string> Types = new();
        public List<(Layer Layer, string Reason)> Layers = new();
        public List<(string Path, string Matches)> Documents = new();
        public Model Model = null!;
        public bool Found => Layers.Count > 0 || Documents.Count > 0;

        public static Route Resolve(string query, string root, Model model, List<(string Path, string Text)> documents, int limit)
        {
            var route = new Route { Query = query, Model = model };
            var relative = query.Replace('\\', '/');
            if (System.IO.File.Exists(Path.Combine(root, relative)) || Directory.Exists(Path.Combine(root, relative)))
            {
                route.File = Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(root, relative))).Replace('\\', '/');
                if (route.File.EndsWith(".cs"))
                    route.Types.Add(Path.GetFileNameWithoutExtension(route.File).Split('.')[0]);
            }
            else
            {
                route.Types.Add(query);
                var declared = model.Layers.SelectMany(layer => layer.Nodes).Where(node => node.Type == query).SelectMany(node => node.Files).FirstOrDefault();
                route.File = declared ?? Directory.GetFiles(Path.Combine(root, "Assets"), query + ".cs", SearchOption.AllDirectories)
                    .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/')).FirstOrDefault();
            }

            foreach (var layer in model.Layers)
            {
                var node = layer.Nodes.FirstOrDefault(node => (route.File != null && node.Files.Contains(route.File)) || route.Types.Contains(node.Type));
                if (node.Title != null)
                    route.Layers.Add((layer, "node " + node.Title));
            }
            if (route.File != null && route.Layers.Count == 0)
            {
                var scoped = model.Layers
                    .Select(layer => (Layer: layer, Folder: layer.Folders.Where(folder => route.File == folder || route.File.StartsWith(folder + "/")).OrderByDescending(folder => folder.Length).FirstOrDefault()))
                    .Where(entry => entry.Folder != null).OrderByDescending(entry => entry.Folder!.Length).ToList();
                route.Layers.AddRange(scoped.TakeWhile(entry => entry.Folder!.Length == scoped[0].Folder!.Length).Select(entry => (entry.Layer, "scope " + entry.Folder)));
            }

            var terms = new List<(string Label, Regex Pattern)>();
            foreach (var type in route.Types)
                terms.Add((type, new Regex(@"(?<![\w.])" + Regex.Escape(type) + @"(?!\w)")));
            if (route.File != null)
            {
                var folder = route.File.EndsWith(".cs") ? Path.GetDirectoryName(route.File)!.Replace('\\', '/') : route.File;
                var mechanic = Regex.Replace(folder, @"/(Editor|Modules|Parameters|Handlers|Other|Utilities|Tests|Mcp)(/.*)?$", "");
                terms.Add((mechanic, new Regex(Regex.Escape(mechanic) + @"(?![\w-])")));
            }
            foreach (var (layer, _) in route.Layers)
            {
                terms.Add((layer.Path, new Regex(Regex.Escape(layer.Path), RegexOptions.IgnoreCase)));
                if (layer.Name.Contains(' ') || layer.Name.Length >= 10)
                    terms.Add((layer.Name, new Regex(@"(?<!\w)" + Regex.Escape(layer.Name) + @"(?!\w)", RegexOptions.IgnoreCase)));
                var layerTypes = layer.Nodes.Select(node => node.Type).Where(type => type.Length >= 4 && !route.Types.Contains(type)).Distinct().ToList();
                if (layerTypes.Count > 0)
                    terms.Add(("layer types", new Regex(@"(?<![\w.])(" + string.Join("|", layerTypes.Select(Regex.Escape)) + @")(?!\w)")));
            }

            route.Documents = documents
                .Select(document => (document.Path, Hits: terms.Select(term => (term.Label, Count: term.Pattern.Matches(document.Text).Count)).Where(hit => hit.Count > 0).ToList()))
                .Where(entry => entry.Hits.Count > 0)
                .OrderByDescending(entry => entry.Hits.Count)
                .ThenBy(entry => Array.FindIndex(DocumentRoots, directory => entry.Path.StartsWith(directory + "/", StringComparison.Ordinal)))
                .ThenByDescending(entry => entry.Hits.Sum(hit => hit.Count)).ThenBy(entry => entry.Path, StringComparer.Ordinal)
                .Take(limit)
                .Select(entry => (entry.Path, string.Join(", ", entry.Hits.Select(hit => $"{hit.Label} ×{hit.Count}")))).ToList();
            return route;
        }

        public void Write(StringBuilder text)
        {
            text.AppendLine(Query + (File != null && File != Query ? " -> " + File : ""));
            if (Layers.Count == 0)
                text.AppendLine("  layer: none (no Architector node or scope folder covers it)");
            foreach (var (layer, reason) in Layers)
            {
                text.AppendLine($"  layer: {layer.Path} ({reason}): {Shorten(layer.Purpose)}");
                var approved = Model.DigestSections.TryGetValue(layer.Path, out var section);
                text.AppendLine("    digest: " + (approved
                    ? $"Architector/Architecture.digest.md lines {section.Start}-{section.End}"
                    : "not in the digest (layer not approved); its current design follows"));
                var connected = layer.Connected.Select(id => Model.Layers.FirstOrDefault(other => other.Id == id)?.Path).Where(path => path != null).Order(StringComparer.Ordinal).ToList();
                if (connected.Count > 0)
                    text.AppendLine("    connected: " + string.Join(", ", connected));
                if (!approved)
                    foreach (var line in Model.Summary(layer))
                        text.AppendLine("    " + line);
            }
            foreach (var (path, matches) in Documents)
                text.AppendLine($"  read: {path} ({matches})");
        }

        public object ToJson()
        {
            return new
            {
                query = Query,
                file = File,
                layers = Layers.Select(entry => new
                {
                    path = entry.Layer.Path,
                    id = entry.Layer.Id,
                    reason = entry.Reason,
                    purpose = entry.Layer.Purpose,
                    digest = Model.DigestSections.TryGetValue(entry.Layer.Path, out var section) ? $"{section.Start}-{section.End}" : null,
                    connected = entry.Layer.Connected.Select(id => Model.Layers.FirstOrDefault(other => other.Id == id)?.Path).Where(path => path != null).Order(StringComparer.Ordinal),
                    design = Model.DigestSections.ContainsKey(entry.Layer.Path) ? null : Model.Summary(entry.Layer)
                }),
                documents = Documents.Select(entry => new { path = entry.Path, matches = entry.Matches })
            };
        }

        private static string Shorten(string text) => text.Length <= 160 ? text : text[..157] + "...";
    }
}
