using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Architector.CodeFacts;

// Where scripts are used in the project's serialized assets, read from the YAML text of prefabs, scenes and assets
// without the Unity Editor: MonoBehaviour and ScriptableObject documents by their m_Script GUID (matched to the
// script's .meta), their m_Enabled state, the lists of other components that point to them ({fileID: N} entries,
// the activation and handler lists), and [SerializeReference] values by their serialized type names, also those a
// prefab variant adds by override (managedReferences[...] modifications). Every file's result is kept in
// Library/Architector/wiring-index.json and read again only when its size or time changes.
public sealed class AssetWiring
{
    private const int FormatVersion = 3;
    private static readonly Regex Header = new(@"^--- !u!(\d+) &(-?\d+)", RegexOptions.Compiled);
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
    private static readonly Regex ModificationTarget = new(@"^\s*- target: \{fileID: (-?\d+)", RegexOptions.Compiled);
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
        // Fields of other components in the same file whose list points to this one: "<lister script guid>|<field>".
        public List<string> ListedBy { get; set; } = new();
    }

    public sealed class FileEntry
    {
        public long Length { get; set; }
        public long Ticks { get; set; }
        public List<Component> Components { get; set; } = new();
        public List<string> SerializedTypes { get; set; } = new();
        public List<SerializedUse> SerializedUses { get; set; } = new();
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
                            ListedBy = (component["listedBy"] as JsonArray ?? new JsonArray()).Select(listed => (string?)listed ?? string.Empty).ToList()
                        });
                entry.SerializedTypes.AddRange((file["serializedTypes"] as JsonArray ?? new JsonArray()).Select(type => (string?)type ?? string.Empty));
                foreach (var item in file["serializedUses"] as JsonArray ?? new JsonArray())
                    if (item is JsonObject use)
                        entry.SerializedUses.Add(new SerializedUse
                        {
                            Type = (string?)use["type"] ?? string.Empty,
                            Object = (string?)use["object"] ?? string.Empty,
                            Via = (string?)use["via"] ?? "managed_reference"
                        });
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
        foreach (var (path, entry) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            foreach (var component in entry.Components)
            {
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
                storedFiles[key] = new JsonObject
                {
                    ["length"] = file.Length,
                    ["ticks"] = file.Ticks,
                    ["components"] = new JsonArray(file.Components.Select(component => (JsonNode?)new JsonObject
                    {
                        ["guid"] = component.Guid,
                        ["fileId"] = component.FileId,
                        ["object"] = component.Object,
                        ["enabled"] = component.Enabled,
                        ["listedBy"] = new JsonArray(component.ListedBy.Select(listed => (JsonNode?)listed).ToArray())
                    }).ToArray()),
                    ["serializedTypes"] = new JsonArray(file.SerializedTypes.Select(type => (JsonNode?)type).ToArray()),
                    ["serializedUses"] = new JsonArray(file.SerializedUses.Select(use => (JsonNode?)new JsonObject
                    {
                        ["type"] = use.Type,
                        ["object"] = use.Object,
                        ["via"] = use.Via
                    }).ToArray())
                };
            File.WriteAllText(path, new JsonObject { ["format"] = FormatVersion, ["metas"] = storedMetas, ["files"] = storedFiles }.ToJsonString());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static IEnumerable<string> Visible(string assets, string pattern) =>
        Directory.EnumerateFiles(assets, pattern, SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(assets, file).Replace('\\', '/').Split('/').Any(part => part.EndsWith('~') || part.StartsWith('.')));

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    private static FileEntry Read(string path, FileInfo info)
    {
        var entry = new FileEntry { Length = info.Length, Ticks = info.LastWriteTimeUtc.Ticks };
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var lists = new List<(string ListerFileId, string Field, string TargetFileId)>();
        var componentGameObjects = new Dictionary<string, string>(StringComparer.Ordinal);
        var scriptsByFileId = new Dictionary<string, string>(StringComparer.Ordinal);
        string? classId = null;
        string? fileId = null;
        Component? component = null;
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
                component = null;
                key = string.Empty;
                continue;
            }
            if (fileId == null)
                continue;
            if (classId == "1001")
            {
                // A prefab instance or variant: its modifications may add [SerializeReference] values by override
                // (propertyPath managedReferences[<rid>], value "<Assembly> <Namespace.Type>") and rename objects.
                var target = ModificationTarget.Match(line);
                if (target.Success)
                {
                    modificationTarget = target.Groups[1].Value;
                    modificationPath = string.Empty;
                    continue;
                }
                var propertyPath = PropertyPath.Match(line);
                if (propertyPath.Success)
                {
                    modificationPath = propertyPath.Groups[1].Value.Trim();
                    continue;
                }
                var modification = ModificationValue.Match(line);
                if (modification.Success && modificationTarget != null)
                {
                    var value = modification.Groups[1].Value.Trim();
                    if (modificationPath == "m_Name")
                        modificationNames[modificationTarget] = value;
                    else if (modificationPath.StartsWith("managedReferences[", StringComparison.Ordinal))
                    {
                        var separator = value.IndexOf(' ');
                        if (separator > 0 && separator < value.Length - 1)
                        {
                            var type = value[(separator + 1)..].Trim().Replace('/', '.');
                            entry.SerializedTypes.Add(type);
                            uses.Add((type, "override:" + modificationTarget, "variant_override"));
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
            var item = ListItem.Match(line);
            if (item.Success && key.Length > 0 && item.Groups[1].Value != "0")
            {
                lists.Add((fileId, key, item.Groups[1].Value));
                continue;
            }
            var field = Key.Match(line);
            if (field.Success)
                key = field.Groups[2].Value;
        }
        if (pendingType != null)
            AddSerialized(pendingType, pendingOwner);
        var byFileId = entry.Components.ToDictionary(item => item.FileId, StringComparer.Ordinal);
        string ObjectOf(string documentFileId) =>
            componentGameObjects.TryGetValue(documentFileId, out var gameObject) && names.TryGetValue(gameObject, out var name) ? name
            : names.TryGetValue("doc:" + documentFileId, out var documentObject) ? documentObject
            : string.Empty;
        foreach (var item in entry.Components)
            item.Object = ObjectOf(item.FileId);
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
