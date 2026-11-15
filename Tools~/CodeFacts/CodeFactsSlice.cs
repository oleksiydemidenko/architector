using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Architector.CodeFacts;

// slice: the answer to "what writes and reads this, where does it run, and on which object" in about 1,500 tokens.
// For a state type (a parameter, a model, any project type others use): its layer and node, the prefabs that hold it,
// then per holder the writers and readers with their placement (the prefab, the field of the holder that loads that
// prefab, the start lists that hold them) and their use sites: the lines inside methods that ticks, callbacks, events
// or Unity run, each with how it is run, not the line that fetches the value. For a component type: its placement and
// start lists, how its methods are run, its lifecycle effects (overrides such as Activate and Deactivate, SetActive and
// enabled toggles) and the state it writes with the other writers and readers of that state.
public static class FactsSlice
{
    private const int Cap = 6000;
    private const int SitesPerAccessor = 2;
    private static readonly string[] LifecycleNames = { "Awake", "Start", "OnEnable", "OnDisable", "OnDestroy", "Activate", "Deactivate", "Release", "Dispose", "Initialize" };

    private sealed record Site(string Type, string Member, bool Write, string Holder, string At, bool Subscribe = false);

    private sealed class TypeFacts
    {
        public List<Site> Sites = new();
        public Dictionary<string, List<(string How, string Where, string At)>> Runs = new(StringComparer.Ordinal);
        public Dictionary<string, List<string>> Callers = new(StringComparer.Ordinal);
        public List<(string Text, string Holder, string At)> Toggles = new();
    }

    public static JsonObject Answer(FactsModel model, JsonNode? arguments, string root)
    {
        var subject = ((string?)arguments?["target"] ?? (string?)arguments?["type"] ?? string.Empty).Trim();
        if (subject.Length == 0)
            return FactsQueries.Failure("usage", "slice needs target: a type name or a C# file.");
        if (model.Index == null)
            return FactsQueries.Failure("no_code_scope", "No layer has a code scope, so there are no code facts to slice.");
        var type = Resolve(model, subject, root);
        if (type == null)
            return FactsQueries.Failure("type_not_found", $"No project type or C# file '{subject}'.");
        var text = Text(model, AssetWiring.Load(root), root, type);
        var result = new JsonObject { ["success"] = true, ["type"] = type.FullName, ["chars"] = text.Length, ["text"] = text };
        return result;
    }

    private static FactsType? Resolve(FactsModel model, string subject, string root)
    {
        var path = subject.Replace('\\', '/');
        if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            if (Path.IsPathRooted(path))
                path = Path.GetRelativePath(root, path).Replace('\\', '/');
            var inFile = model.Types.Where(type => type.Files.Any(part => part.StartsWith(path + ":", StringComparison.OrdinalIgnoreCase))).ToArray();
            return inFile.FirstOrDefault(type => string.Equals(type.Name, Path.GetFileNameWithoutExtension(path), StringComparison.Ordinal)) ?? inFile.FirstOrDefault();
        }
        var found = model.FindType(subject);
        if (found != null)
            return found;
        var caseless = model.Types.Where(type => string.Equals(type.Name, subject, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return caseless.Length == 1 ? caseless[0] : null;
    }

    private static TypeFacts FactsOf(FactsType type)
    {
        var facts = new TypeFacts();
        if (type.Behaviour.TryGetValue("sites", out var sites))
            foreach (var (value, at) in sites)
            {
                var parts = value.Split('|');
                var dot = parts[0].LastIndexOf('.');
                if (parts.Length == 3 && dot > 0)
                    facts.Sites.Add(new Site(parts[0][..dot], parts[0][(dot + 1)..], parts[1] == "w", parts[2], at, parts[1] == "s"));
            }
        if (type.Behaviour.TryGetValue("runs", out var runs))
            foreach (var (value, at) in runs)
            {
                var parts = value.Split('|');
                if (parts.Length != 3)
                    continue;
                if (!facts.Runs.TryGetValue(parts[0], out var list))
                    facts.Runs[parts[0]] = list = new List<(string How, string Where, string At)>();
                list.Add((parts[1], parts[2], at));
            }
        if (type.Behaviour.TryGetValue("calls", out var calls))
            foreach (var (value, _) in calls)
            {
                var separator = value.IndexOf('>');
                if (separator <= 0)
                    continue;
                var callee = value[(separator + 1)..];
                if (!facts.Callers.TryGetValue(callee, out var callers))
                    facts.Callers[callee] = callers = new List<string>();
                if (!callers.Contains(value[..separator]))
                    callers.Add(value[..separator]);
            }
        if (type.Behaviour.TryGetValue("toggles", out var toggles))
            foreach (var (value, at) in toggles)
            {
                var separator = value.LastIndexOf('|');
                if (separator > 0)
                    facts.Toggles.Add((value[..separator], value[(separator + 1)..], at));
            }
        return facts;
    }

    // How a method runs: its own registrations, else those of the methods that call it (up to four calls away).
    private static string HowRun(TypeFacts facts, string holder)
    {
        var queue = new Queue<(string Method, int Depth)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        queue.Enqueue((holder, 0));
        var fallback = string.Empty;
        while (queue.Count > 0)
        {
            var (method, depth) = queue.Dequeue();
            if (!seen.Add(method))
                continue;
            if (facts.Runs.TryGetValue(method, out var runs))
            {
                var callback = runs.FirstOrDefault(run => run.How is not ("override" or "Unity"));
                if (callback.How != null)
                    return callback.How + (callback.Where.Length > 0 ? $", set up in {callback.Where.Split('~')[0]}" : string.Empty) + (method != holder ? $"; via {method}" : string.Empty);
                if (fallback.Length == 0)
                    fallback = (runs[0].How == "Unity" ? "Unity " : string.Empty) + method.Split('~')[0];
            }
            else if (depth > 0 && LifecycleNames.Contains(method) && fallback.Length == 0)
                fallback = method;
            if (depth < 4 && facts.Callers.TryGetValue(method.Split('~')[0] == method ? method : method, out var callers))
                foreach (var caller in callers)
                    queue.Enqueue((caller, depth + 1));
        }
        if (fallback.Length > 0)
            return fallback;
        var name = holder.Split('~')[0];
        return LifecycleNames.Contains(name) ? name : name.Length == 0 ? "field initializer" : $"in {name}";
    }

    // Every way a method is run, through the methods that call it: "event X.Changed (via HandleChanged)", "Unity OnEnable".
    private static List<string> AllRuns(TypeFacts facts, string holder)
    {
        var result = new List<string>();
        var queue = new Queue<(string Method, int Depth)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        queue.Enqueue((holder, 0));
        while (queue.Count > 0)
        {
            var (method, depth) = queue.Dequeue();
            if (!seen.Add(method))
                continue;
            if (facts.Runs.TryGetValue(method, out var runs))
                foreach (var run in runs)
                {
                    var text = run.How == "override" ? method : run.How == "Unity" ? "Unity " + method
                        : run.How + (method != holder ? $" (via {method.Split('~')[0]})" : string.Empty);
                    if (!result.Contains(text))
                        result.Add(text);
                }
            if (depth < 4 && facts.Callers.TryGetValue(method, out var callers))
                foreach (var caller in callers)
                    queue.Enqueue((caller, depth + 1));
        }
        return result;
    }

    // A type name without namespaces: GameEngine.ListParameter<GameEngine.Seat> -> ListParameter<Seat>.
    private static string TypeName(string name) => Regex.Replace(name, @"\b[A-Za-z_]\w*\.", string.Empty);

    // Ranks a site: writes run by a callback first, then reads run by one, then lifecycle methods, then the rest.
    private static int Rank(Site site, string how)
    {
        var method = how.StartsWith("Unity ", StringComparison.Ordinal) ? how["Unity ".Length..] : how;
        return (site.Write ? 0 : 1) + (how.StartsWith("in ", StringComparison.Ordinal) || method is "Initialize" or ".ctor" or "field initializer" ? 4
            : LifecycleNames.Contains(method) ? 2 : 0);
    }

    private static string Line(string root, string at)
    {
        var separator = at.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(at[(separator + 1)..], out var number))
            return string.Empty;
        try
        {
            var lines = File.ReadLines(Path.Combine(root, at[..separator])).Skip(number - 1).Take(2).Select(text => text.Trim()).ToArray();
            var line = lines.Length > 0 ? lines[0] : string.Empty;
            // "var seats = x.List;" says little alone; the next line usually shows what is taken from it.
            if (line.StartsWith("var ", StringComparison.Ordinal) && line.Length < 80 && lines.Length > 1 && lines[1].Length > 0 && lines[1] != "{")
                line += " / " + lines[1];
            return line.Length > 150 ? line[..147] + "..." : line;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string Short(string at) => at.Split('/').Last();

    private sealed record Place(string File, string Object, List<string> Lists, bool Active, List<string> Missing);

    private static string Text(FactsModel model, AssetWiring assets, string root, FactsType subject)
    {
        var architecture = model.Architecture;
        var wiring = FactsQueries.WiringStatuses(model, assets);
        var byFullName = model.Types.GroupBy(type => type.FullName, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var factsCache = new Dictionary<string, TypeFacts>(StringComparer.Ordinal);
        TypeFacts Facts(FactsType type) => factsCache.TryGetValue(type.Key, out var known) ? known : factsCache[type.Key] = FactsOf(type);
        // Prefabs that name another prefab in a text field: target prefab -> (holder prefab, field).
        var loadedBy = new Dictionary<string, List<(string Holder, string Field)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, entry) in assets.Files)
            foreach (var link in entry.Links)
            {
                var parts = link.Split('|');
                if (parts.Length != 3 || assets.PrefabNamed(parts[2]) is not { } target || string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!loadedBy.TryGetValue(target, out var holders))
                    loadedBy[target] = holders = new List<(string Holder, string Field)>();
                if (!holders.Contains((path, parts[1])))
                    holders.Add((path, parts[1]));
            }
        List<Place> PlacesOf(FactsType type) => wiring.TryGetValue(type.Key, out var item)
            ? item.Places.Where(place => place.Via.Length == 0 && !model.Extractors.InTestFolder(place.File))
                .Select(place => new Place(place.File, place.Object, place.ListedBy, place.Active,
                    item.RuleFindings.FirstOrDefault(finding => finding.File == place.File && finding.FileId == place.FileId)?.Missing ?? new List<string>()))
                .ToList()
            : new List<Place>();
        // The prefabs that load a prefab through a text field, prefabs first (a registry asset naming it is weaker).
        List<(string Holder, string Field)> Loaders(string file) => loadedBy.TryGetValue(file, out var holders)
            ? holders.OrderBy(holder => holder.Holder.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList()
            : new List<(string Holder, string Field)>();
        // Start lists: when a startedBy rule names lists, they are printed once and a place says "started" or what it misses.
        var ruleLists = model.RuleResult?.Rules.SelectMany(rule => rule.Lists.Select(list => list.Text)).Distinct(StringComparer.Ordinal).ToList() ?? new List<string>();
        var printedRuleLists = false;
        string Lists(Place place)
        {
            if (place.Missing.Count > 0)
                return "RULE: missing from " + string.Join(", ", place.Missing);
            if (ruleLists.Count > 0 && ruleLists.All(list => place.Lists.Contains(list)))
            {
                printedRuleLists = true;
                return "started (all start lists)";
            }
            return place.Lists.Count > 0 ? "lists: " + string.Join(", ", place.Lists) : "in no list";
        }
        string PlaceText(Place place, string? loader = null)
        {
            var loaders = Loaders(place.File);
            loader ??= loaders.Count > 0 ? $"{Short(loaders[0].Holder)}.{loaders[0].Field}" + (loaders.Count > 1 ? $" +{loaders.Count - 1}" : string.Empty) : string.Empty;
            var text = Short(place.File) + (loader.Length > 0 ? $" (loaded by {loader})" : string.Empty);
            if (place.Object.Length > 0 && !string.Equals(place.Object, Path.GetFileNameWithoutExtension(place.File), StringComparison.Ordinal))
                text += $" object {place.Object}";
            if (!place.Active)
                text += ", inactive";
            return text + "; " + Lists(place);
        }
        string LayerOf(FactsType type) => type.OwnerLayerId == null ? "no layer" : architecture.Path(type.OwnerLayerId);

        var output = new StringBuilder();
        var node = subject.Nodes.Count > 0 ? architecture.FindNode(subject.Nodes[0]) : null;
        var role = node?.Responsibility.Split(new[] { ". " }, 2, StringSplitOptions.None)[0].TrimEnd('.') ?? string.Empty;
        output.AppendLine($"# {subject.Name} ({(subject.IsAbstract ? "abstract " : string.Empty)}{subject.Kind}{(subject.Base != null ? " : " + TypeName(subject.Base) : string.Empty)}) - {LayerOf(subject)}{(node != null ? $", node {node.Title}" : string.Empty)}{(role.Length > 0 ? ": " + role : string.Empty)}");

        // Who uses the subject's state, with the use sites themselves.
        var accessors = new List<(FactsType Type, List<(Site Site, string How)> Sites)>();
        foreach (var type in model.Types)
        {
            if (ReferenceEquals(type, subject) || model.Extractors.InTestFolder(type.FirstFile) || FactsQueries.IsEditorFile(type.FirstFile))
                continue;
            var facts = Facts(type);
            var sites = facts.Sites.Where(site => site.Type == subject.FullName).Select(site => (Site: site, How: HowRun(facts, site.Holder))).ToList();
            if (sites.Count > 0)
                accessors.Add((type, sites.OrderBy(item => Rank(item.Site, item.How)).ThenBy(item => item.Site.At, StringComparer.Ordinal)
                    .GroupBy(item => item.Site.At, StringComparer.Ordinal).Select(group => group.First()).ToList()));
        }
        var subjectPlaces = PlacesOf(subject);
        var held = wiring.TryGetValue(subject.Key, out var subjectWiring)
            ? subjectWiring.Places.Where(place => place.Via.Length > 0).Select(place => place.File).Concat(subjectWiring.SerializedIn)
                .Where(file => !model.Extractors.InTestFolder(file)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
        // A variant of a holder holds it too (its own file often does not name the type).
        foreach (var (path, entry) in assets.Files)
        {
            var visited = new HashSet<AssetWiring.FileEntry>(ReferenceEqualityComparer.Instance);
            for (var current = entry; current.Variant.Length > 0 && visited.Add(current) && assets.FileOf(current.Variant) is { } source; current = source)
                if (held.Contains(assets.FileByGuid[current.Variant], StringComparer.OrdinalIgnoreCase))
                {
                    if (!held.Contains(path, StringComparer.OrdinalIgnoreCase))
                        held.Add(path);
                    break;
                }
        }
        if (held.Count > 0)
            output.AppendLine($"held in: {string.Join(", ", held.Take(6).Select(Short))}{(held.Count > 6 ? $" +{held.Count - 6} more" : string.Empty)}");

        // A component, or a base class whose subclasses are components: placement, how it runs, lifecycle, what it writes.
        var subclassPlaces = subjectPlaces.Count > 0 || !wiring.TryGetValue(subject.Key, out var baseWiring) ? new List<(string Name, Place Place)>()
            : baseWiring.BaseOf.Select(name => byFullName.TryGetValue(name, out var subclass) ? subclass : null).Where(subclass => subclass != null)
                .SelectMany(subclass => PlacesOf(subclass!).Select(place => (subclass!.Name, Place: place))).ToList();
        var subjectFacts = Facts(subject);
        var component = subjectPlaces.Count > 0 || subclassPlaces.Count > 0 || subjectFacts.Toggles.Count > 0
                        || subjectFacts.Runs.Values.Any(runs => runs.Any(run => run.How is not ("override" or "Unity")));
        if (component)
        {
            foreach (var place in subjectPlaces.Take(4))
                output.AppendLine("placed: " + PlaceText(place));
            if (subjectPlaces.Count > 4)
                output.AppendLine($"placed: +{subjectPlaces.Count - 4} more prefabs");
            foreach (var group in subclassPlaces.GroupBy(item => item.Name).Take(3))
                output.AppendLine($"placed as {group.Key}: {string.Join("; ", group.Take(2).Select(item => PlaceText(item.Place)))}{(group.Count() > 2 ? $" +{group.Count() - 2} more" : string.Empty)}");
            // Text fields of its components that name prefabs (asset ids, addresses): which prefabs it loads, by field.
            var guids = new HashSet<string>(new[] { subject }.Concat(subclassPlaces.Select(item => byFullName.Values.FirstOrDefault(type => type.Name == item.Name)).Where(type => type != null).Select(type => type!))
                .Select(type => wiring.TryGetValue(type.Key, out var item) && assets.GuidByScript.TryGetValue(item.ScriptFile, out var guid) ? guid : string.Empty)
                .Where(guid => guid.Length > 0), StringComparer.Ordinal);
            var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var (path, entry) in assets.Files)
                foreach (var link in entry.Links)
                {
                    var parts = link.Split('|');
                    if (parts.Length != 3 || assets.PrefabNamed(parts[2]) is not { } target)
                        continue;
                    var owner = parts[0].StartsWith("override:", StringComparison.Ordinal)
                        ? entry.Instances.Select(instance => assets.Declared(instance.Source, parts[0]["override:".Length..])).FirstOrDefault(found => found != null)?.Component
                        : entry.Components.FirstOrDefault(item => item.FileId == parts[0]);
                    if (owner == null || !guids.Contains(owner.Guid))
                        continue;
                    if (!fields.TryGetValue(parts[1], out var examples))
                        fields[parts[1]] = examples = new List<string>();
                    examples.Add($"{Short(path)} -> {Short(target)}");
                }
            foreach (var (field, examples) in fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                output.AppendLine($"field {field} names the prefab it loads, in {examples.Count} prefabs: {string.Join(", ", examples.Take(2))}");
            ComponentSection(output, subject, subjectFacts, root, byFullName, model, Facts);
        }

        if (accessors.Count > 0)
        {
            // Each accessor place belongs to an instance: the holder prefab itself, or the holder that loads the place's
            // prefab through a text field (the field says which assemble: base, authority, ...). Instances whose
            // writers and readers are the same types with the same fields are printed once.
            var members = new Dictionary<string, List<(FactsType Type, Place Place, string Field)>>(StringComparer.OrdinalIgnoreCase);
            var unplaced = new List<FactsType>();
            void Add(string key, FactsType type, Place place, string field)
            {
                if (!members.TryGetValue(key, out var list))
                    members[key] = list = new List<(FactsType Type, Place Place, string Field)>();
                list.Add((type, place, field));
            }
            foreach (var (type, _) in accessors)
            {
                var places = PlacesOf(type);
                if (places.Count == 0)
                    unplaced.Add(type);
                foreach (var place in places)
                {
                    if (held.Contains(place.File, StringComparer.OrdinalIgnoreCase))
                    {
                        Add(place.File, type, place, string.Empty);
                        continue;
                    }
                    var owners = Loaders(place.File).Where(holder => held.Contains(holder.Holder, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (owners.Count == 0)
                        Add("~elsewhere", type, place, string.Empty);
                    foreach (var owner in owners)
                        Add(owner.Holder, type, place, owner.Field);
                }
            }
            var sitesOf = accessors.ToDictionary(item => item.Type.Key, item => item.Sites, StringComparer.Ordinal);
            var printedSites = new HashSet<string>(StringComparer.Ordinal);
            var groups = members.Where(pair => pair.Key != "~elsewhere")
                .GroupBy(pair => string.Join(";", pair.Value.Select(member => $"{member.Type.Key}@{member.Field}").Distinct().OrderBy(value => value, StringComparer.Ordinal)))
                .Select(group => (Holders: group.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal).ToList(), Members: group.First().Value))
                .OrderByDescending(group => group.Members.Any(member => sitesOf[member.Type.Key].Any(item => item.Site.Write)))
                .ThenByDescending(group => group.Holders.Count)
                .ToList();
            if (members.TryGetValue("~elsewhere", out var elsewhere))
                groups.Add((new List<string>(), elsewhere));
            void Accessors(List<(FactsType Type, Place Place, string Field)> list, string holder)
            {
                foreach (var write in new[] { true, false })
                {
                    var side = list.Where(member => sitesOf[member.Type.Key].Any(item => item.Site.Write) == write)
                        .GroupBy(member => member.Type.Key)
                        .Select(group => group.First())
                        .OrderBy(member => member.Type.Name, StringComparer.Ordinal)
                        .ToList();
                    if (side.Count == 0)
                        continue;
                    output.AppendLine(write ? "writers:" : "readers:");
                    foreach (var (type, place, field) in side)
                    {
                        var loader = field.Length > 0 ? $"{Short(holder)}.{field}" : null;
                        output.AppendLine($"- {type.Name} ({LayerOf(type)}) in {PlaceText(place, loader)}");
                        if (!printedSites.Add(type.Key))
                            continue;
                        foreach (var (site, how) in sitesOf[type.Key].Where(item => !write || item.Site.Write).Take(SitesPerAccessor))
                            output.AppendLine($"  {(site.Subscribe ? "subscribes, " : string.Empty)}{how}: {Short(site.At)} {Line(root, site.At)}");
                    }
                }
            }
            foreach (var (holders, list) in groups)
            {
                output.AppendLine(holders.Count == 0 ? "## elsewhere (no holder of it loads these prefabs)"
                    : holders.Count == 1 ? $"## instance on {Short(holders[0])}"
                    : $"## instances on {string.Join(", ", holders.Take(4).Select(Short))}{(holders.Count > 4 ? $" +{holders.Count - 4} more" : string.Empty)} (same writers and readers)");
                Accessors(list, holders.FirstOrDefault() ?? string.Empty);
            }
            if (unplaced.Count > 0)
            {
                output.AppendLine("## not placed as components (behaviours, services, plain classes)");
                foreach (var type in unplaced.OrderBy(type => sitesOf[type.Key].Any(item => item.Site.Write) ? 0 : 1).ThenBy(type => type.Name, StringComparer.Ordinal).Take(8))
                {
                    var (site, how) = sitesOf[type.Key][0];
                    output.AppendLine($"- {type.Name} ({LayerOf(type)}) {(site.Write ? "writes" : "reads")}, {how}: {Short(site.At)} {Line(root, site.At)}");
                }
                if (unplaced.Count > 8)
                    output.AppendLine($"- +{unplaced.Count - 8} more");
            }
        }
        else if (!component)
        {
            var writers = model.StateAccess.Writers(subject.FullName, architecture);
            var readers = model.StateAccess.Readers(subject.FullName, architecture);
            if (writers.Count + readers.Count == 0)
                output.AppendLine("No other type uses this type's members.");
            else
                output.AppendLine($"written by: {FactsStateAccess.Describe(writers, architecture, 8)}\nread by: {FactsStateAccess.Describe(readers, architecture, 8)}");
        }
        if (printedRuleLists)
            output.AppendLine("start lists: " + string.Join(", ", ruleLists));
        var text = output.ToString().Replace("\r\n", "\n");
        if (text.Length > Cap)
        {
            var cut = text[..Cap];
            text = cut[..cut.LastIndexOf('\n')] + $"\n[cut at {Cap} characters; facts layer --type gives the full lists]\n";
        }
        return text;
    }

    private static void ComponentSection(StringBuilder output, FactsType subject, TypeFacts facts, string root,
        Dictionary<string, FactsType> byFullName, FactsModel model, Func<FactsType, TypeFacts> factsOf)
    {
        var callbacks = facts.Runs.SelectMany(pair => pair.Value.Where(run => run.How is not ("override" or "Unity"))
                .Select(run => $"{pair.Key.Split('~')[0]}{(pair.Key.Contains('~') ? " (lambda)" : string.Empty)} <- {run.How}{(run.Where.Length > 0 ? " in " + run.Where.Split('~')[0] : string.Empty)}"))
            .Distinct(StringComparer.Ordinal).ToList();
        if (callbacks.Count > 0)
            output.AppendLine("runs: " + string.Join("; ", callbacks.Take(6)));
        // Lifecycle: what overrides and Unity callbacks change, and the SetActive / enabled toggles with every trigger.
        foreach (var site in facts.Sites.Where(site => site.Write && LifecycleNames.Contains(site.Holder.Split('~')[0]) && site.Holder != "Initialize")
                     .GroupBy(site => site.At, StringComparer.Ordinal).Select(group => group.First()).Take(4))
            output.AppendLine($"lifecycle {site.Holder}: {Short(site.At)} {Line(root, site.At)}");
        foreach (var toggle in facts.Toggles.Take(3))
        {
            var triggers = toggle.Holder.Length > 0 ? AllRuns(facts, toggle.Holder) : new List<string>();
            output.AppendLine($"toggle in {(toggle.Holder.Length > 0 ? toggle.Holder.Split('~')[0] : "?")}{(triggers.Count > 0 ? " (run by " + string.Join("; ", triggers.Take(4)) + ")" : string.Empty)}: {Short(toggle.At)} {Line(root, toggle.At)}");
        }
        // What it writes and reads, each state type once with its best site. Services it gets injected, its own base
        // types and the values it only fetches in Initialize are left out.
        var injected = subject.Extras.TryGetValue("injected", out var injectedTypes) ? injectedTypes.Keys.Select(name => name.Split('.').Last()).ToHashSet(StringComparer.Ordinal) : new HashSet<string>();
        bool Noise(Site site) => injected.Contains(site.Type.Split('.').Last()) || site.Holder is "Initialize" or ".ctor" or "Release" or "Dispose"
                                 || DerivesFrom(subject, site.Type, byFullName);
        foreach (var write in new[] { true, false })
        {
            var byState = facts.Sites.Where(site => site.Write == write && !site.Subscribe && byFullName.ContainsKey(site.Type) && !Noise(site))
                .Select(site => (Site: site, How: HowRun(facts, site.Holder)))
                .GroupBy(item => item.Site.Type, StringComparer.Ordinal)
                .Select(group => group.OrderBy(item => Rank(item.Site, item.How)).First())
                .OrderBy(item => Rank(item.Site, item.How))
                .Take(write ? 6 : 5)
                .ToList();
            foreach (var (site, how) in byState)
                output.AppendLine($"{(write ? "writes" : "reads")} {site.Type.Split('.').Last()}.{site.Member} ({how}): {Short(site.At)} {Line(root, site.At)}");
        }
        // For what it writes: the other writers and the readers, by name.
        foreach (var state in facts.Sites.Where(site => site.Write && !Noise(site)).Select(site => site.Type).Distinct(StringComparer.Ordinal).Take(3))
        {
            if (!byFullName.TryGetValue(state, out var stateType))
                continue;
            var others = model.Types.Where(type => !ReferenceEquals(type, subject) && !model.Extractors.InTestFolder(type.FirstFile))
                .Select(type => (Type: type, Sites: factsOf(type).Sites.Where(site => site.Type == state).ToList()))
                .Where(item => item.Sites.Count > 0)
                .ToList();
            var writers = others.Where(item => item.Sites.Any(site => site.Write)).Select(item => item.Type.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();
            var readers = others.Where(item => item.Sites.All(site => !site.Write)).Select(item => item.Type.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();
            output.AppendLine($"{stateType.Name}: other writers {(writers.Count == 0 ? "none" : string.Join(", ", writers.Take(5)) + (writers.Count > 5 ? $" +{writers.Count - 5}" : string.Empty))}; "
                              + $"readers {(readers.Count == 0 ? "none" : string.Join(", ", readers.Take(6)) + (readers.Count > 6 ? $" +{readers.Count - 6}" : string.Empty))}");
        }
    }

    private static bool DerivesFrom(FactsType type, string baseName, Dictionary<string, FactsType> byFullName)
    {
        var current = type;
        for (var step = 0; step < 16 && current?.Base != null; step++)
        {
            var name = current.Base;
            var generic = name.IndexOf('<');
            var plain = generic >= 0 ? name[..generic] : name;
            if (plain == baseName || plain.Split('.').Last() == baseName.Split('.').Last().Split('`')[0])
                return true;
            current = byFullName.TryGetValue(plain, out var next) ? next
                : generic >= 0 && byFullName.TryGetValue($"{plain}`{name[generic..].Count(character => character == ',') + 1}", out var genericNext) ? genericNext : null;
        }
        return false;
    }
}
