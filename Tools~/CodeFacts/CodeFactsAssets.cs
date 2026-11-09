using System.Text.Json;
using System.Text.RegularExpressions;

namespace Architector.CodeFacts;

// Where scripts are used in the project's serialized assets, read from the YAML text of prefabs, scenes and assets
// without the Unity Editor: MonoBehaviour and ScriptableObject documents by their m_Script GUID (matched to the
// script's .meta), their m_Enabled state, the lists of other components that point to them ({fileID: N} entries,
// the activation and handler lists), and [SerializeReference] values by their serialized type names. Every file's
// result is kept in Library/Architector/wiring-index.json and read again only when its size or time changes.
public sealed class AssetWiring
{
    private const int FormatVersion = 1;
    private static readonly Regex Header = new(@"^--- !u!(\d+) &(-?\d+)", RegexOptions.Compiled);
    private static readonly Regex ScriptReference = new(@"m_Script: \{fileID: -?\d+, guid: ([0-9a-f]{32})", RegexOptions.Compiled);
    private static readonly Regex GameObjectReference = new(@"^  m_GameObject: \{fileID: (-?\d+)\}", RegexOptions.Compiled);
    private static readonly Regex Enabled = new(@"^  m_Enabled: (\d)", RegexOptions.Compiled);
    private static readonly Regex ObjectName = new(@"^  m_Name: (.*)$", RegexOptions.Compiled);
    private static readonly Regex Key = new(@"^(\s*)([A-Za-z_][\w]*):\s*$", RegexOptions.Compiled);
    private static readonly Regex ListItem = new(@"^\s*- \{fileID: (-?\d+)\}", RegexOptions.Compiled);
    private static readonly Regex SerializedType = new(@"type: \{class: ([\w`]+), ns: ([\w.]*), asm: ([\w.\-]+)\}", RegexOptions.Compiled);
    private static readonly Regex MetaGuid = new(@"^guid: ([0-9a-f]{32})", RegexOptions.Compiled | RegexOptions.Multiline);
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
    }

    public sealed class MetaEntry
    {
        public long Length { get; set; }
        public long Ticks { get; set; }
        public string Guid { get; set; } = string.Empty;
    }

    private sealed class Stored
    {
        public int Format { get; set; }
        public Dictionary<string, FileEntry> Files { get; set; } = new();
        public Dictionary<string, MetaEntry> Metas { get; set; } = new();
    }

    // The script's repository path for every script GUID, and where each script and serialized type is used.
    public Dictionary<string, string> ScriptByGuid { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> GuidByScript { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<(string File, Component Component)>> ComponentsByGuid { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> FilesBySerializedType { get; } = new(StringComparer.Ordinal);
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
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
            if (stored == null || stored.Format != FormatVersion)
                return;
            files = new Dictionary<string, FileEntry>(stored.Files, StringComparer.Ordinal);
            metas = new Dictionary<string, MetaEntry>(stored.Metas, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
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
            File.WriteAllText(path, JsonSerializer.Serialize(new Stored { Format = FormatVersion, Files = files, Metas = metas }));
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
        foreach (var line in File.ReadLines(path))
        {
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
            var serialized = SerializedType.Match(line);
            if (serialized.Success)
            {
                var space = serialized.Groups[2].Value;
                entry.SerializedTypes.Add(space.Length > 0 ? $"{space}.{serialized.Groups[1].Value}" : serialized.Groups[1].Value);
                continue;
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
        var byFileId = entry.Components.ToDictionary(item => item.FileId, StringComparer.Ordinal);
        foreach (var item in entry.Components)
            if (componentGameObjects.TryGetValue(item.FileId, out var gameObject) && names.TryGetValue(gameObject, out var name))
                item.Object = name;
            else if (names.TryGetValue("doc:" + item.FileId, out var documentObject))
                item.Object = documentObject;
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
