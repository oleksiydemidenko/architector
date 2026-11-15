using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Architector.CodeFacts;

// Where scripts are used in the project's serialized assets, read from the YAML text of prefabs, scenes and assets
// without the Unity Editor: MonoBehaviour and ScriptableObject documents by their m_Script GUID (matched to the
// script's .meta), their m_Enabled state, the lists of other components that point to them ({fileID: N} entries,
// the activation and handler lists), and [SerializeReference] values by their serialized type names, also those a
// prefab variant adds by override (managedReferences[...] modifications). Also what the absence rules need: which
// [SerializeReference] value holds a list, list entries a variant or nested instance adds by override, prefab instances
// (variant root or nested) with their parent and active/enabled overrides, stripped documents that stand for objects of
// a source prefab, and whether each component's GameObject and its parents are active. Every file's result is kept in
// Library/Architector/wiring-index.json and read again only when its size or time changes.
public sealed class AssetWiring
{
    private const int FormatVersion = 10;
    // A component's own text field whose value may name another asset (an asset id, address or name): one token.
    private static readonly Regex TextField = new(@"^  ([A-Za-z_]\w*): ([A-Za-z][A-Za-z0-9_./-]{2,})\s*$", RegexOptions.Compiled);
    private static readonly Regex LinkField = new(@"^[A-Za-z_]\w*$", RegexOptions.Compiled);
    private static readonly Regex LinkValue = new(@"^[A-Za-z][A-Za-z0-9_./-]{2,}$", RegexOptions.Compiled);
    private static readonly Regex Header = new(@"^--- !u!(\d+) &(-?\d+)( stripped)?", RegexOptions.Compiled);
    private static readonly Regex CorrespondingSource = new(@"^  m_CorrespondingSourceObject: \{fileID: (-?\d+), guid: ([0-9a-f]{32})", RegexOptions.Compiled);
    private static readonly Regex PrefabInstanceOf = new(@"^  m_PrefabInstance: \{fileID: (-?\d+)\}", RegexOptions.Compiled);
    private static readonly Regex Father = new(@"^  m_Father: \{fileID: (-?\d+)\}", RegexOptions.Compiled);
    private static readonly Regex TransformParent = new(@"^\s*m_TransformParent: \{fileID: (-?\d+)\}", RegexOptions.Compiled);
    private static readonly Regex ManagedReferencePath = new(@"^managedReferences\[(-?\d+)\]$", RegexOptions.Compiled);
    private static readonly Regex ScriptReference = new(@"m_Script: \{fileID: -?\d+, guid: ([0-9a-f]{32})", RegexOptions.Compiled);
    private static readonly Regex GameObjectReference = new(@"^  m_GameObject: \{fileID: (-?\d+)\}", RegexOptions.Compiled);
    private static readonly Regex Enabled = new(@"^  m_Enabled: (\d)", RegexOptions.Compiled);
    private static readonly Regex ObjectName = new(@"^  m_Name: (.*)$", RegexOptions.Compiled);
    private static readonly Regex Key = new(@"^(\s*)([A-Za-z_][\w]*):\s*$", RegexOptions.Compiled);
    private static readonly Regex ListItem = new(@"^\s*- \{fileID: (-?\d+)\}", RegexOptions.Compiled);
    private static readonly Regex TypeStart = new(@"^(\s*)(?:- )?type:\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex TypeClass = new(@"(?:^|[{,\s])class:\s*([\w`]+)", RegexOptions.Compiled);
    private static readonly Regex TypeSpace = new(@"(?:^|[{,\s])ns:\s*([\w.]*)", RegexOptions.Compiled);
    private static readonly Regex MetaGuid = new(@"^guid: ([0-9a-f]{32})", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex ModificationTarget = new(@"^\s*- target: \{fileID: (-?\d+)(?:, guid: ([0-9a-f]{32}))?", RegexOptions.Compiled);
    private static readonly Regex ObjectReferenceValue = new(@"^\s*objectReference: \{fileID: (-?\d+)", RegexOptions.Compiled);
    private static readonly Regex ListOverridePath = new(@"^(?:managedReferences\[(-?\d+)\]\.)?(\w+)\.Array\.data\[\d+\]$", RegexOptions.Compiled);
    private static readonly Regex SourcePrefab = new(@"^\s*m_SourcePrefab: \{fileID: -?\d+, guid: ([0-9a-f]{32})", RegexOptions.Compiled);
    private static readonly Regex ReferenceId = new(@"^\s*- rid: (-?\d+)\s*$", RegexOptions.Compiled);
    private static readonly Regex IsActive = new(@"^  m_IsActive: (\d)", RegexOptions.Compiled);
    // A plain scalar as written, or the text of a quoted one: Unity quotes property paths with brackets and values with
    // colons ('managedReferences[1]', 'Name: x'); a doubled quote inside single quotes is one quote.
    internal static string YamlScalar(string raw)
    {
        var value = raw.Trim();
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1].Replace("''", "'");
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        // A quoted scalar that Unity wrapped onto the next line: its first line has only the opening quote.
        if (value.Length >= 1 && value[0] is '\'' or '"')
            return value[1..];
        return value;
    }

    private static readonly Regex PropertyPath = new(@"^\s*propertyPath: (.*)$", RegexOptions.Compiled);
    private static readonly Regex ModificationValue = new(@"^\s*value: (.*)$", RegexOptions.Compiled);
    private static readonly string[] AssetPatterns = { "*.prefab", "*.unity", "*.asset" };
    private static readonly object Gate = new();
    private static readonly Dictionary<string, AssetWiring> Loaded = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, FileEntry> files = new(StringComparer.Ordinal);
    private Dictionary<string, MetaEntry> metas = new(StringComparer.Ordinal);

    public sealed class Component
    {
        public string Guid { get; set; } = string.Empty;
        public string FileId { get; set; } = string.Empty;
        public string Object { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        // Whether its GameObject and every parent in this file are active (m_IsActive). Under names the stripped
        // document (an object of a source prefab) the chain of parents reaches, whose state lives in that prefab.
        public bool Active { get; set; } = true;
        public string Under { get; set; } = string.Empty;
        // A stripped document stands for a component of a source prefab: "<prefab guid>:<fileID there>", and the prefab
        // instance (variant root or nested) it belongs to.
        public bool Stripped { get; set; }
        public string Source { get; set; } = string.Empty;
        public string Instance { get; set; } = string.Empty;
        // Fields of other components in the same file whose list points to this one: "<lister script guid>|<field>", or
        // "<lister script guid>|<managed reference class>.<field>" for a list inside a [SerializeReference] value.
        public List<string> ListedBy { get; set; } = new();
    }

    // A list entry a prefab variant or instance adds by override: the target component of the source prefab, the
    // managed reference id when the list sits in a [SerializeReference] value, the field and the component it points to.
    public sealed class OverrideListEntry
    {
        public string TargetFileId { get; set; } = string.Empty;
        public string TargetPrefab { get; set; } = string.Empty;
        public string Rid { get; set; } = string.Empty;
        public string Field { get; set; } = string.Empty;
        public string Object { get; set; } = string.Empty;
        public string Instance { get; set; } = string.Empty;
    }

    // A prefab instance in this file: the root of a variant (no parent) or a nested instance under a transform of this
    // file, with the objects it deactivates and the components it disables by override (fileIDs in the source prefab).
    public sealed class PrefabInstance
    {
        public string FileId { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Parent { get; set; } = "0";
        public List<string> Inactive { get; set; } = new();
        public List<string> Disabled { get; set; } = new();
    }

    public sealed class FileEntry
    {
        public long Length { get; set; }
        public long Ticks { get; set; }
        public List<Component> Components { get; set; } = new();
        public List<string> SerializedTypes { get; set; } = new();
        public List<SerializedUse> SerializedUses { get; set; } = new();
        // The asset's own GUID (its .meta), the prefabs it instantiates or derives from, the managed reference classes
        // by "<component fileID>:<rid>", and list entries added by override.
        public string Guid { get; set; } = string.Empty;
        public List<string> Sources { get; set; } = new();
        public Dictionary<string, string> ManagedTypes { get; set; } = new(StringComparer.Ordinal);
        public List<OverrideListEntry> OverrideLists { get; set; } = new();
        // The prefab a variant derives from: the source of the prefab instance that is the file's root (a prefab whose
        // root is its own GameObject is no variant, however many prefabs it nests).
        public string Variant { get; set; } = string.Empty;
        public List<PrefabInstance> Instances { get; set; } = new();
        // Stripped GameObjects and Transforms: fileID -> "<instance>|<fileID in the source>|<source guid>".
        public Dictionary<string, string> Stripped { get; set; } = new(StringComparer.Ordinal);
        // GameObjects and Transforms of a prefab that are inactive by themselves or through a parent in the file.
        public List<string> Inactive { get; set; } = new();
        // Text fields of components that may name another asset: "<component fileID>|<field>|<value>".
        public List<string> Links { get; set; } = new();
    }

    // One [SerializeReference] value: its type, the object that holds it and how it got there.
    public sealed class SerializedUse
    {
        public string Type { get; set; } = string.Empty;
        public string Object { get; set; } = string.Empty;
        public string Via { get; set; } = "managed_reference";
    }

    public sealed class MetaEntry
    {
        public long Length { get; set; }
        public long Ticks { get; set; }
        public string Guid { get; set; } = string.Empty;
    }

    // The script's repository path for every script GUID, and where each script and serialized type is used.
    public Dictionary<string, string> ScriptByGuid { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> GuidByScript { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<(string File, Component Component)>> ComponentsByGuid { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> FilesBySerializedType { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<(string File, SerializedUse Use)>> UsesBySerializedType { get; } = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, FileEntry> Files => files;
    public Dictionary<string, string> FileByGuid { get; } = new(StringComparer.Ordinal);
    private string projectRoot = string.Empty;
    public string Root => projectRoot;
    private readonly Dictionary<string, Hierarchy?> hierarchies = new(StringComparer.Ordinal);

    // One list that holds a component: the lister's script GUID and the field ("<managed class>.<field>" inside a
    // [SerializeReference] value).
    public readonly record struct ListMembership(string ListerGuid, string Field);

    // The prefab a text value names: by GUID, by project path, or by file name when exactly one prefab has it.
    public string? PrefabNamed(string value)
    {
        if (prefabsByName == null)
        {
            prefabsByName = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in files.Keys.Where(path => path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                prefabsByName[name] = prefabsByName.ContainsKey(name) ? null : path;
            }
        }
        if (FileByGuid.TryGetValue(value, out var byGuid))
            return byGuid;
        if (files.ContainsKey(value))
            return value;
        return prefabsByName.TryGetValue(value, out var byName) ? byName : null;
    }

    private Dictionary<string, string?>? prefabsByName;

    public FileEntry? FileOf(string guid) => FileByGuid.TryGetValue(guid, out var path) && files.TryGetValue(path, out var entry) ? entry : null;

    // The component a fileID names in a prefab, followed through stripped documents to the prefab that declares it.
    public (string Path, Component Component)? Declared(string prefabGuid, string fileId, int depth = 0)
    {
        if (depth > 8 || !FileByGuid.TryGetValue(prefabGuid, out var path) || !files.TryGetValue(path, out var entry))
            return null;
        var component = entry.Components.FirstOrDefault(item => item.FileId == fileId);
        if (component == null)
        {
            // An object that comes from a prefab instance (a variant's base, a nested prefab) has no document of its own;
            // Unity names it (instance fileID XOR source fileID) with the sign bit cleared.
            if (!long.TryParse(fileId, out var target))
                return null;
            foreach (var instance in entry.Instances)
                if (long.TryParse(instance.FileId, out var owner)
                    && Declared(instance.Source, ((target ^ owner) & long.MaxValue).ToString(System.Globalization.CultureInfo.InvariantCulture), depth + 1) is { } found)
                    return found;
            return null;
        }
        if (component.Stripped)
        {
            var source = component.Source.Split(':');
            return source.Length == 2 ? Declared(source[0], source[1], depth + 1) ?? (path, component) : (path, component);
        }
        return (path, component);
    }

    // Every list in the file that holds the component: its own lists, and the list entries an override in the file adds
    // (the lister is a component of the source prefab; a rid names the [SerializeReference] value of that lister).
    public List<ListMembership> ListsOf(FileEntry entry, Component component)
    {
        var result = new List<ListMembership>();
        foreach (var value in component.ListedBy)
        {
            var separator = value.IndexOf('|');
            if (separator > 0)
                result.Add(new ListMembership(value[..separator], value[(separator + 1)..]));
        }
        foreach (var added in entry.OverrideLists)
        {
            if (added.Object != component.FileId)
                continue;
            var prefab = added.TargetPrefab.Length > 0 ? added.TargetPrefab : entry.Instances.FirstOrDefault(item => item.FileId == added.Instance)?.Source ?? string.Empty;
            var lister = Declared(prefab, added.TargetFileId);
            if (lister == null)
                continue;
            var field = added.Field;
            if (added.Rid.Length > 0)
            {
                var managed = entry.ManagedTypes.GetValueOrDefault($"{added.TargetFileId}:{added.Rid}")
                              ?? ManagedClass(prefab, added.TargetFileId, added.Rid, 0)
                              ?? ManagedClass(lister.Value.Path, lister.Value.Component.FileId, added.Rid);
                field = $"{managed ?? "?"}.{added.Field}";
            }
            var membership = new ListMembership(lister.Value.Component.Guid, field);
            if (!result.Contains(membership))
                result.Add(membership);
        }
        return result;
    }

    // The class of a [SerializeReference] value by its rid, in the prefab and the prefabs it derives from.
    private string? ManagedClass(string prefabGuid, string fileId, string rid, int depth)
    {
        if (depth > 8)
            return null;
        var entry = FileOf(prefabGuid);
        if (entry == null)
            return null;
        return entry.ManagedTypes.GetValueOrDefault($"{fileId}:{rid}")
               ?? (entry.Variant.Length > 0 ? ManagedClass(entry.Variant, fileId, rid, depth + 1) : null);
    }

    private string? ManagedClass(string path, string fileId, string rid) =>
        path.Length > 0 && files.TryGetValue(path, out var entry) ? entry.ManagedTypes.GetValueOrDefault($"{fileId}:{rid}") : null;

    // Whether an object of the file (a GameObject or Transform, own or stripped) is active, following stripped
    // documents into their source prefab, the instance's overrides and the instance's parent in this file.
    public bool ObjectActive(FileEntry entry, string fileId, int depth = 0)
    {
        if (depth > 8)
            return true;
        if (entry.Stripped.TryGetValue(fileId, out var stripped))
        {
            var parts = stripped.Split('|');
            if (parts.Length != 3)
                return true;
            var instance = entry.Instances.FirstOrDefault(item => item.FileId == parts[0]);
            var source = FileOf(parts[2]);
            if (source != null && source.Inactive.Contains(parts[1]))
                return false;
            return instance == null || InstanceShows(entry, instance, parts[1], depth);
        }
        return !entry.Inactive.Contains(fileId);
    }

    // Whether a prefab instance leaves an object of its source (a GameObject, Transform or component's GameObject)
    // active: no override deactivates it or a parent, and the instance's own parent in the file is active.
    public bool InstanceShows(FileEntry entry, PrefabInstance instance, string sourceObject, int depth = 0)
    {
        if (instance.Inactive.Count > 0)
        {
            var hierarchy = HierarchyOf(instance.Source);
            if (hierarchy == null)
            {
                if (instance.Inactive.Contains(sourceObject))
                    return false;
            }
            else if (hierarchy.Ancestors(sourceObject).Any(instance.Inactive.Contains))
                return false;
        }
        return instance.Parent == "0" || ObjectActive(entry, instance.Parent, depth + 1);
    }

    // Whether a component counts as active in the file: its GameObject chain in the file, then the stripped object the
    // chain reaches.
    public bool ComponentActive(FileEntry entry, Component component) =>
        component.Active && (component.Under.Length == 0 || ObjectActive(entry, component.Under));

    // The GameObject/Transform hierarchy of a prefab, read from its file only when an override deactivates something.
    public sealed class Hierarchy
    {
        public Dictionary<string, string> ObjectOfTransform { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> TransformOfObject { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Father { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> ObjectOfComponent { get; } = new(StringComparer.Ordinal);

        // The object itself and every GameObject and Transform above it.
        public IEnumerable<string> Ancestors(string fileId)
        {
            var current = ObjectOfComponent.GetValueOrDefault(fileId) ?? fileId;
            for (var step = 0; step < 256 && current.Length > 0 && current != "0"; step++)
            {
                string? transform;
                if (ObjectOfTransform.TryGetValue(current, out var owner))
                {
                    transform = current;
                    yield return owner;
                }
                else
                {
                    transform = TransformOfObject.GetValueOrDefault(current);
                    yield return current;
                }
                if (transform == null)
                    yield break;
                yield return transform;
                var father = Father.GetValueOrDefault(transform);
                if (father == null || father == "0")
                    yield break;
                current = father;
            }
        }
    }

    private Hierarchy? HierarchyOf(string prefabGuid)
    {
        if (hierarchies.TryGetValue(prefabGuid, out var known))
            return known;
        Hierarchy? hierarchy = null;
        if (FileByGuid.TryGetValue(prefabGuid, out var path))
        {
            try
            {
                hierarchy = new Hierarchy();
                string? classId = null;
                string? fileId = null;
                foreach (var line in File.ReadLines(Path.Combine(projectRoot, path)))
                {
                    var header = Header.Match(line);
                    if (header.Success)
                    {
                        classId = header.Groups[1].Value;
                        fileId = header.Groups[2].Value;
                        continue;
                    }
                    if (fileId == null)
                        continue;
                    var owner = GameObjectReference.Match(line);
                    if (owner.Success)
                    {
                        if (classId is "4" or "224")
                        {
                            hierarchy.ObjectOfTransform[fileId] = owner.Groups[1].Value;
                            hierarchy.TransformOfObject.TryAdd(owner.Groups[1].Value, fileId);
                        }
                        else
                            hierarchy.ObjectOfComponent[fileId] = owner.Groups[1].Value;
                        continue;
                    }
                    var father = Father.Match(line);
                    if (father.Success && classId is "4" or "224")
                        hierarchy.Father[fileId] = father.Groups[1].Value;
                }
            }
            catch (IOException)
            {
                hierarchy = null;
            }
        }
        hierarchies[prefabGuid] = hierarchy;
        return hierarchy;
    }

    public static AssetWiring Load(string root)
    {
        lock (Gate)
        {
            if (!Loaded.TryGetValue(root, out var wiring))
            {
                wiring = new AssetWiring();
                wiring.ReadStored(root);
                Loaded[root] = wiring;
            }
            wiring.Refresh(root);
            return wiring;
        }
    }

    private static string StorePath(string root) => Path.Combine(root, "Library", "Architector", "wiring-index.json");

    private void ReadStored(string root)
    {
        try
        {
            var path = StorePath(root);
            if (!File.Exists(path))
                return;
            // Written and read by hand: the self-contained server is trimmed, which leaves no reflection-based JSON.
            var stored = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (stored == null || (int?)stored["format"] != FormatVersion)
                return;
            foreach (var (key, value) in stored["metas"] as JsonObject ?? new JsonObject())
                if (value is JsonObject meta)
                    metas[key] = new MetaEntry { Length = (long?)meta["length"] ?? 0, Ticks = (long?)meta["ticks"] ?? 0, Guid = (string?)meta["guid"] ?? string.Empty };
            foreach (var (key, value) in stored["files"] as JsonObject ?? new JsonObject())
            {
                if (value is not JsonObject file)
                    continue;
                var entry = new FileEntry { Length = (long?)file["length"] ?? 0, Ticks = (long?)file["ticks"] ?? 0 };
                foreach (var item in file["components"] as JsonArray ?? new JsonArray())
                    if (item is JsonObject component)
                        entry.Components.Add(new Component
                        {
                            Guid = (string?)component["guid"] ?? string.Empty,
                            FileId = (string?)component["fileId"] ?? string.Empty,
                            Object = (string?)component["object"] ?? string.Empty,
                            Enabled = (bool?)component["enabled"] ?? true,
                            Active = (bool?)component["active"] ?? true,
                            Under = (string?)component["under"] ?? string.Empty,
                            Stripped = (bool?)component["stripped"] ?? false,
                            Source = (string?)component["source"] ?? string.Empty,
                            Instance = (string?)component["instance"] ?? string.Empty,
                            ListedBy = Strings(component["listedBy"])
                        });
                entry.SerializedTypes.AddRange(Strings(file["serializedTypes"]));
                foreach (var item in file["serializedUses"] as JsonArray ?? new JsonArray())
                    if (item is JsonObject use)
                        entry.SerializedUses.Add(new SerializedUse
                        {
                            Type = (string?)use["type"] ?? string.Empty,
                            Object = (string?)use["object"] ?? string.Empty,
                            Via = (string?)use["via"] ?? "managed_reference"
                        });
                entry.Guid = (string?)file["guid"] ?? string.Empty;
                entry.Sources.AddRange(Strings(file["sources"]));
                foreach (var (rid, type) in file["managedTypes"] as JsonObject ?? new JsonObject())
                    entry.ManagedTypes[rid] = (string?)type ?? string.Empty;
                foreach (var item in file["overrideLists"] as JsonArray ?? new JsonArray())
                    if (item is JsonObject added)
                        entry.OverrideLists.Add(new OverrideListEntry
                        {
                            TargetFileId = (string?)added["target"] ?? string.Empty,
                            TargetPrefab = (string?)added["prefab"] ?? string.Empty,
                            Rid = (string?)added["rid"] ?? string.Empty,
                            Field = (string?)added["field"] ?? string.Empty,
                            Object = (string?)added["object"] ?? string.Empty,
                            Instance = (string?)added["instance"] ?? string.Empty
                        });
                entry.Variant = (string?)file["variant"] ?? string.Empty;
                foreach (var item in file["instances"] as JsonArray ?? new JsonArray())
                    if (item is JsonObject instance)
                        entry.Instances.Add(new PrefabInstance
                        {
                            FileId = (string?)instance["fileId"] ?? string.Empty,
                            Source = (string?)instance["source"] ?? string.Empty,
                            Parent = (string?)instance["parent"] ?? "0",
                            Inactive = Strings(instance["inactive"]),
                            Disabled = Strings(instance["disabled"])
                        });
                foreach (var (id, standIn) in file["stripped"] as JsonObject ?? new JsonObject())
                    entry.Stripped[id] = (string?)standIn ?? string.Empty;
                entry.Inactive = Strings(file["inactive"]);
                entry.Links = Strings(file["links"]);
                files[key] = entry;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            files.Clear();
            metas.Clear();
        }
    }

    private void Refresh(string root)
    {
        projectRoot = root;
        var assets = Path.Combine(root, "Assets");
        var changed = false;
        var seenMetas = new HashSet<string>(StringComparer.Ordinal);
        var seenFiles = new HashSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(assets))
        {
            foreach (var meta in Visible(assets, "*.cs.meta"))
            {
                var relative = Relative(root, meta);
                seenMetas.Add(relative);
                var info = new FileInfo(meta);
                if (!metas.TryGetValue(relative, out var entry) || entry.Length != info.Length || entry.Ticks != info.LastWriteTimeUtc.Ticks)
                {
                    var match = MetaGuid.Match(File.ReadAllText(meta));
                    metas[relative] = new MetaEntry { Length = info.Length, Ticks = info.LastWriteTimeUtc.Ticks, Guid = match.Success ? match.Groups[1].Value : string.Empty };
                    changed = true;
                }
            }
            foreach (var asset in AssetPatterns.SelectMany(pattern => Visible(assets, pattern)))
            {
                var relative = Relative(root, asset);
                seenFiles.Add(relative);
                var info = new FileInfo(asset);
                if (files.TryGetValue(relative, out var entry) && entry.Length == info.Length && entry.Ticks == info.LastWriteTimeUtc.Ticks)
                    continue;
                files[relative] = Read(asset, info);
                changed = true;
            }
        }
        foreach (var gone in metas.Keys.Where(key => !seenMetas.Contains(key)).ToArray())
        {
            metas.Remove(gone);
            changed = true;
        }
        foreach (var gone in files.Keys.Where(key => !seenFiles.Contains(key)).ToArray())
        {
            files.Remove(gone);
            changed = true;
        }
        ScriptByGuid.Clear();
        GuidByScript.Clear();
        foreach (var (path, meta) in metas)
            if (meta.Guid.Length > 0)
            {
                ScriptByGuid[meta.Guid] = path[..^".meta".Length];
                GuidByScript[path[..^".meta".Length]] = meta.Guid;
            }
        ComponentsByGuid.Clear();
        FilesBySerializedType.Clear();
        UsesBySerializedType.Clear();
        FileByGuid.Clear();
        hierarchies.Clear();
        prefabsByName = null;
        foreach (var (path, entry) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            if (entry.Guid.Length > 0)
                FileByGuid.TryAdd(entry.Guid, path);
        foreach (var (path, entry) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            foreach (var component in entry.Components)
            {
                // A stripped document repeats a component of a scanned source prefab, where it is counted.
                if (component.Stripped && FileByGuid.ContainsKey(component.Source.Split(':')[0]))
                    continue;
                if (!ComponentsByGuid.TryGetValue(component.Guid, out var places))
                    ComponentsByGuid[component.Guid] = places = new List<(string File, Component Component)>();
                places.Add((path, component));
            }
            foreach (var type in entry.SerializedTypes.Distinct(StringComparer.Ordinal))
            {
                if (!FilesBySerializedType.TryGetValue(type, out var list))
                    FilesBySerializedType[type] = list = new List<string>();
                list.Add(path);
            }
            foreach (var use in entry.SerializedUses)
            {
                if (!UsesBySerializedType.TryGetValue(use.Type, out var uses))
                    UsesBySerializedType[use.Type] = uses = new List<(string File, SerializedUse Use)>();
                uses.Add((path, use));
            }
        }
        if (changed)
            Save(root);
    }

    private void Save(string root)
    {
        try
        {
            var path = StorePath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var storedMetas = new JsonObject();
            foreach (var (key, meta) in metas)
                storedMetas[key] = new JsonObject { ["length"] = meta.Length, ["ticks"] = meta.Ticks, ["guid"] = meta.Guid };
            var storedFiles = new JsonObject();
            foreach (var (key, file) in files)
            {
                var stored = new JsonObject
                {
                    ["length"] = file.Length,
                    ["ticks"] = file.Ticks,
                    ["components"] = new JsonArray(file.Components.Select(component =>
                    {
                        var item = new JsonObject
                        {
                            ["guid"] = component.Guid,
                            ["fileId"] = component.FileId,
                            ["object"] = component.Object,
                            ["enabled"] = component.Enabled,
                            ["active"] = component.Active,
                            ["listedBy"] = Values(component.ListedBy)
                        };
                        if (component.Under.Length > 0)
                            item["under"] = component.Under;
                        if (component.Stripped)
                        {
                            item["stripped"] = true;
                            item["source"] = component.Source;
                            item["instance"] = component.Instance;
                        }
                        return (JsonNode?)item;
                    }).ToArray()),
                    ["serializedTypes"] = Values(file.SerializedTypes),
                    ["guid"] = file.Guid,
                    ["sources"] = Values(file.Sources),
                    ["managedTypes"] = new JsonObject(file.ManagedTypes.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value))),
                    ["overrideLists"] = new JsonArray(file.OverrideLists.Select(added => (JsonNode?)new JsonObject
                    {
                        ["target"] = added.TargetFileId,
                        ["prefab"] = added.TargetPrefab,
                        ["rid"] = added.Rid,
                        ["field"] = added.Field,
                        ["object"] = added.Object,
                        ["instance"] = added.Instance
                    }).ToArray()),
                    ["serializedUses"] = new JsonArray(file.SerializedUses.Select(use => (JsonNode?)new JsonObject
                    {
                        ["type"] = use.Type,
                        ["object"] = use.Object,
                        ["via"] = use.Via
                    }).ToArray())
                };
                if (file.Variant.Length > 0)
                    stored["variant"] = file.Variant;
                if (file.Instances.Count > 0)
                    stored["instances"] = new JsonArray(file.Instances.Select(instance => (JsonNode?)new JsonObject
                    {
                        ["fileId"] = instance.FileId,
                        ["source"] = instance.Source,
                        ["parent"] = instance.Parent,
                        ["inactive"] = Values(instance.Inactive),
                        ["disabled"] = Values(instance.Disabled)
                    }).ToArray());
                if (file.Stripped.Count > 0)
                    stored["stripped"] = new JsonObject(file.Stripped.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value)));
                if (file.Inactive.Count > 0)
                    stored["inactive"] = Values(file.Inactive);
                if (file.Links.Count > 0)
                    stored["links"] = Values(file.Links);
                storedFiles[key] = stored;
            }
            File.WriteAllText(path, new JsonObject { ["format"] = FormatVersion, ["metas"] = storedMetas, ["files"] = storedFiles }.ToJsonString());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static List<string> Strings(JsonNode? node) =>
        (node as JsonArray ?? new JsonArray()).Select(value => (string?)value ?? string.Empty).ToList();

    private static JsonArray Values(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)value).ToArray());

    private static IEnumerable<string> Visible(string assets, string pattern) =>
        Directory.EnumerateFiles(assets, pattern, SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(assets, file).Replace('\\', '/').Split('/').Any(part => part.EndsWith('~') || part.StartsWith('.')));

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    private static FileEntry Read(string path, FileInfo info)
    {
        var entry = new FileEntry { Length = info.Length, Ticks = info.LastWriteTimeUtc.Ticks };
        try
        {
            var meta = path + ".meta";
            if (File.Exists(meta))
            {
                var match = MetaGuid.Match(File.ReadAllText(meta));
                entry.Guid = match.Success ? match.Groups[1].Value : string.Empty;
            }
        }
        catch (IOException)
        {
        }
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var inactiveObjects = new HashSet<string>(StringComparer.Ordinal);
        // Inside a MonoBehaviour's "references:" block lists belong to the [SerializeReference] value that holds them.
        var inReferences = false;
        string? managedClass = null;
        string? pendingRid = null;
        string? modificationPrefab = null;
        var lists = new List<(string ListerFileId, string Field, string TargetFileId)>();
        var componentGameObjects = new Dictionary<string, string>(StringComparer.Ordinal);
        var scriptsByFileId = new Dictionary<string, string>(StringComparer.Ordinal);
        // The hierarchy: the GameObject of each Transform, the Transform of each GameObject and each Transform's parent.
        var transformObjects = new Dictionary<string, string>(StringComparer.Ordinal);
        var objectTransforms = new Dictionary<string, string>(StringComparer.Ordinal);
        var fathers = new Dictionary<string, string>(StringComparer.Ordinal);
        // Stripped documents: fileID -> (instance, fileID in the source, source guid).
        var strippedSources = new Dictionary<string, (string FileId, string Guid)>(StringComparer.Ordinal);
        var strippedInstances = new Dictionary<string, string>(StringComparer.Ordinal);
        string? classId = null;
        string? fileId = null;
        var stripped = false;
        Component? component = null;
        PrefabInstance? instance = null;
        var key = string.Empty;
        string? pendingType = null;
        var pendingIndent = 0;
        var pendingBlock = false;
        string? pendingOwner = null;
        // Uses with the fileID of the document that holds them; object names are resolved at the end.
        var uses = new List<(string Type, string OwnerFileId, string Via)>();
        var modificationNames = new Dictionary<string, string>(StringComparer.Ordinal);
        string? modificationTarget = null;
        var modificationPath = string.Empty;
        void AddSerialized(string text, string? owner)
        {
            var name = TypeClass.Match(text);
            if (!name.Success)
                return;
            var space = TypeSpace.Match(text);
            var spaceName = space.Success ? space.Groups[1].Value : string.Empty;
            var type = spaceName.Length > 0 ? $"{spaceName}.{name.Groups[1].Value}" : name.Groups[1].Value;
            if (inReferences)
            {
                managedClass = name.Groups[1].Value;
                if (pendingRid != null && owner != null)
                    entry.ManagedTypes[$"{owner}:{pendingRid}"] = managedClass;
                pendingRid = null;
            }
            entry.SerializedTypes.Add(type);
            uses.Add((type, owner ?? string.Empty, "managed_reference"));
        }
        foreach (var line in File.ReadLines(path))
        {
            if (pendingType != null)
            {
                var indent = line.Length - line.TrimStart().Length;
                if (pendingBlock && (indent <= pendingIndent || line.Trim().Length == 0))
                {
                    AddSerialized(pendingType, pendingOwner);
                    pendingType = null;
                }
                else
                {
                    pendingType += " " + line.Trim();
                    if (!pendingBlock && line.Contains('}'))
                    {
                        AddSerialized(pendingType, pendingOwner);
                        pendingType = null;
                    }
                    continue;
                }
            }
            var header = Header.Match(line);
            if (header.Success)
            {
                classId = header.Groups[1].Value;
                fileId = header.Groups[2].Value;
                stripped = header.Groups[3].Success;
                component = null;
                instance = null;
                key = string.Empty;
                inReferences = false;
                managedClass = null;
                pendingRid = null;
                modificationTarget = null;
                if (classId == "1001")
                {
                    instance = new PrefabInstance { FileId = fileId };
                    entry.Instances.Add(instance);
                }
                continue;
            }
            if (fileId == null)
                continue;
            if (stripped)
            {
                var source = CorrespondingSource.Match(line);
                if (source.Success)
                {
                    strippedSources[fileId] = (source.Groups[1].Value, source.Groups[2].Value);
                    continue;
                }
                var owningInstance = PrefabInstanceOf.Match(line);
                if (owningInstance.Success)
                {
                    strippedInstances[fileId] = owningInstance.Groups[1].Value;
                    continue;
                }
                if (classId != "114")
                    continue;
            }
            if (classId == "1001")
            {
                // A prefab instance or variant: its modifications may add [SerializeReference] values by override
                // (propertyPath managedReferences[<rid>], value "<Assembly> <Namespace.Type>"), add list entries,
                // deactivate objects, disable components and rename objects.
                var parent = TransformParent.Match(line);
                if (parent.Success)
                {
                    instance!.Parent = parent.Groups[1].Value;
                    continue;
                }
                var source = SourcePrefab.Match(line);
                if (source.Success)
                {
                    instance!.Source = source.Groups[1].Value;
                    if (!entry.Sources.Contains(source.Groups[1].Value))
                        entry.Sources.Add(source.Groups[1].Value);
                    continue;
                }
                var target = ModificationTarget.Match(line);
                if (target.Success)
                {
                    modificationTarget = target.Groups[1].Value;
                    modificationPrefab = target.Groups[2].Success ? target.Groups[2].Value : string.Empty;
                    modificationPath = string.Empty;
                    continue;
                }
                var reference = ObjectReferenceValue.Match(line);
                if (reference.Success && modificationTarget != null && reference.Groups[1].Value != "0")
                {
                    var listPath = ListOverridePath.Match(modificationPath);
                    if (listPath.Success)
                        entry.OverrideLists.Add(new OverrideListEntry
                        {
                            TargetFileId = modificationTarget,
                            TargetPrefab = modificationPrefab ?? string.Empty,
                            Rid = listPath.Groups[1].Success ? listPath.Groups[1].Value : string.Empty,
                            Field = listPath.Groups[2].Value,
                            Object = reference.Groups[1].Value,
                            Instance = instance!.FileId
                        });
                    continue;
                }
                var propertyPath = PropertyPath.Match(line);
                if (propertyPath.Success)
                {
                    modificationPath = YamlScalar(propertyPath.Groups[1].Value);
                    continue;
                }
                var modification = ModificationValue.Match(line);
                if (modification.Success && modificationTarget != null)
                {
                    var value = YamlScalar(modification.Groups[1].Value);
                    if (modificationPath == "m_Name")
                        modificationNames[modificationTarget] = value;
                    else if (!modificationPath.StartsWith("m_", StringComparison.Ordinal) && LinkField.IsMatch(modificationPath) && LinkValue.IsMatch(value) && value is not ("true" or "false"))
                        entry.Links.Add($"override:{modificationTarget}|{modificationPath}|{value}");
                    else if (modificationPath == "m_IsActive" && value == "0")
                        instance!.Inactive.Add(modificationTarget);
                    else if (modificationPath == "m_Enabled" && value == "0")
                        instance!.Disabled.Add(modificationTarget);
                    else if (modificationPath.StartsWith("managedReferences[", StringComparison.Ordinal))
                    {
                        var separator = value.IndexOf(' ');
                        if (separator > 0 && separator < value.Length - 1)
                        {
                            var type = value[(separator + 1)..].Trim().Replace('/', '.');
                            var arguments = type.IndexOf("[[", StringComparison.Ordinal);
                            if (arguments > 0)
                                type = type[..arguments];
                            entry.SerializedTypes.Add(type);
                            uses.Add((type, "override:" + modificationTarget, "variant_override"));
                            // The class of a managed reference the override adds, for list entries added into it.
                            var added = ManagedReferencePath.Match(modificationPath);
                            if (added.Success)
                                entry.ManagedTypes[$"{modificationTarget}:{added.Groups[1].Value}"] = type.Split('.').Last();
                        }
                    }
                }
                continue;
            }
            var typeStart = TypeStart.Match(line);
            if (typeStart.Success)
            {
                var value = typeStart.Groups[2].Value.Trim();
                if (value.StartsWith('{') && value.Contains('}'))
                {
                    AddSerialized(value, fileId);
                    continue;
                }
                if (value.StartsWith('{') || value.Length == 0)
                {
                    pendingType = value;
                    pendingOwner = fileId;
                    pendingIndent = typeStart.Groups[1].Value.Length;
                    pendingBlock = value.Length == 0;
                    continue;
                }
            }
            if (classId == "1")
            {
                var name = ObjectName.Match(line);
                if (name.Success)
                    names[fileId] = name.Groups[1].Value.Trim();
                var active = IsActive.Match(line);
                if (active.Success && active.Groups[1].Value == "0")
                    inactiveObjects.Add(fileId);
                continue;
            }
            if (classId is "4" or "224")
            {
                var transformObject = GameObjectReference.Match(line);
                if (transformObject.Success)
                {
                    transformObjects[fileId] = transformObject.Groups[1].Value;
                    objectTransforms.TryAdd(transformObject.Groups[1].Value, fileId);
                    continue;
                }
                var father = Father.Match(line);
                if (father.Success)
                    fathers[fileId] = father.Groups[1].Value;
                continue;
            }
            if (classId != "114")
                continue;
            var documentName = ObjectName.Match(line);
            if (documentName.Success)
            {
                names["doc:" + fileId] = documentName.Groups[1].Value.Trim();
                continue;
            }
            var script = ScriptReference.Match(line);
            if (script.Success)
            {
                component ??= new Component { FileId = fileId };
                component.Guid = script.Groups[1].Value;
                scriptsByFileId[fileId] = component.Guid;
                if (!entry.Components.Contains(component))
                    entry.Components.Add(component);
                continue;
            }
            var owner = GameObjectReference.Match(line);
            if (owner.Success)
            {
                componentGameObjects[fileId] = owner.Groups[1].Value;
                continue;
            }
            var enabled = Enabled.Match(line);
            if (enabled.Success)
            {
                component ??= new Component { FileId = fileId };
                component.Enabled = enabled.Groups[1].Value != "0";
                continue;
            }
            if (!inReferences && !stripped)
            {
                var text = TextField.Match(line);
                if (text.Success && !text.Groups[1].Value.StartsWith("m_", StringComparison.Ordinal) && text.Groups[2].Value is not ("true" or "false"))
                    entry.Links.Add($"{fileId}|{text.Groups[1].Value}|{text.Groups[2].Value}");
            }
            var item = ListItem.Match(line);
            if (item.Success && key.Length > 0 && item.Groups[1].Value != "0")
            {
                lists.Add((fileId, inReferences && managedClass != null ? $"{managedClass}.{key}" : key, item.Groups[1].Value));
                continue;
            }
            if (inReferences)
            {
                var rid = ReferenceId.Match(line);
                if (rid.Success)
                {
                    pendingRid = rid.Groups[1].Value;
                    managedClass = null;
                    continue;
                }
            }
            var field = Key.Match(line);
            if (field.Success)
            {
                key = field.Groups[2].Value;
                if (line.StartsWith("  references:", StringComparison.Ordinal))
                    inReferences = true;
            }
        }
        if (pendingType != null)
            AddSerialized(pendingType, pendingOwner);
        foreach (var (strippedId, source) in strippedSources)
        {
            var owningInstance = strippedInstances.GetValueOrDefault(strippedId, string.Empty);
            var strippedComponent = entry.Components.FirstOrDefault(item => item.FileId == strippedId);
            if (strippedComponent != null)
            {
                strippedComponent.Stripped = true;
                strippedComponent.Source = $"{source.Guid}:{source.FileId}";
                strippedComponent.Instance = owningInstance;
            }
            else
                entry.Stripped[strippedId] = $"{owningInstance}|{source.FileId}|{source.Guid}";
        }
        // A prefab whose root is a prefab instance is a variant of that instance's source.
        if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            entry.Variant = entry.Instances.FirstOrDefault(item => item.Parent == "0" && item.Source.Length > 0)?.Source ?? string.Empty;
        // Whether a GameObject and its parents in this file are active; the chain stops at a stripped Transform or
        // GameObject, whose state lives in the source prefab.
        var states = new Dictionary<string, (bool Active, string Under)>(StringComparer.Ordinal);
        (bool Active, string Under) StateOf(string gameObject, int depth)
        {
            if (states.TryGetValue(gameObject, out var known))
                return known;
            if (entry.Stripped.ContainsKey(gameObject))
                return (true, gameObject);
            var own = !inactiveObjects.Contains(gameObject);
            var state = (own, string.Empty);
            if (depth < 256 && objectTransforms.TryGetValue(gameObject, out var transform) && fathers.TryGetValue(transform, out var father) && father != "0")
            {
                if (entry.Stripped.ContainsKey(father))
                    state = (own, father);
                else if (transformObjects.TryGetValue(father, out var parentObject))
                {
                    var parent = StateOf(parentObject, depth + 1);
                    state = (own && parent.Active, parent.Under);
                }
            }
            states[gameObject] = state;
            return state;
        }
        var byFileId = entry.Components.ToDictionary(item => item.FileId, StringComparer.Ordinal);
        string ObjectOf(string documentFileId) =>
            componentGameObjects.TryGetValue(documentFileId, out var gameObject) && names.TryGetValue(gameObject, out var name) ? name
            : names.TryGetValue("doc:" + documentFileId, out var documentObject) ? documentObject
            : string.Empty;
        foreach (var item in entry.Components)
        {
            item.Object = ObjectOf(item.FileId);
            if (item.Stripped || !componentGameObjects.TryGetValue(item.FileId, out var gameObject) || gameObject == "0")
                continue;
            var state = StateOf(gameObject, 0);
            item.Active = state.Active;
            item.Under = state.Under;
        }
        if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            foreach (var gameObject in names.Keys.Where(name => !name.StartsWith("doc:", StringComparison.Ordinal)).ToArray())
                if (!StateOf(gameObject, 0).Active)
                {
                    entry.Inactive.Add(gameObject);
                    if (objectTransforms.TryGetValue(gameObject, out var transform))
                        entry.Inactive.Add(transform);
                }
        foreach (var (type, owner, via) in uses)
            entry.SerializedUses.Add(new SerializedUse
            {
                Type = type,
                Via = via,
                // An override names its target object only when the variant renamed it; otherwise the variant's root name.
                Object = owner.StartsWith("override:", StringComparison.Ordinal)
                    ? modificationNames.TryGetValue(owner["override:".Length..], out var renamed) ? renamed : modificationNames.Values.FirstOrDefault() ?? string.Empty
                    : ObjectOf(owner)
            });
        foreach (var (lister, field, target) in lists)
            if (lister != target && byFileId.TryGetValue(target, out var listed) && scriptsByFileId.TryGetValue(lister, out var listerGuid))
            {
                var value = $"{listerGuid}|{field}";
                if (!listed.ListedBy.Contains(value))
                    listed.ListedBy.Add(value);
            }
        entry.Components.RemoveAll(item => item.Guid.Length == 0);
        return entry;
    }
}
