using Architector.CodeFacts;
using System.Text.RegularExpressions;
using System.Xml.Linq;

// Which Unity assembly compiles each C# file of the project, as the Editor's CompilationPipeline says: from the project
// files Unity writes (<AssemblyName> and <Compile Include> of every *.csproj in the project folder) when they exist;
// otherwise from the folders: the nearest .asmdef or .asmref, else Unity's predefined assemblies (Assembly-CSharp,
// -Editor, -firstpass). A git worktree has no project files, so it uses the folders.
internal sealed class McpProjectAssemblies
{
    private static readonly Regex NameField = new("\"name\"\\s*:\\s*\"([^\"]+)\"");
    private static readonly Regex ReferenceField = new("\"reference\"\\s*:\\s*\"([^\"]+)\"");

    private readonly string root;
    private readonly Dictionary<string, string> byFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> byFolder = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string>? asmdefByGuid;

    private McpProjectAssemblies(string root)
    {
        this.root = root;
    }

    // Project-relative paths of the project's own sources (those under Assets), the files the Editor compiles itself.
    public IReadOnlyList<string> SourceFiles { get; private set; } = Array.Empty<string>();

    public string[] Defines { get; private set; } = Array.Empty<string>();

    public static McpProjectAssemblies Read(string root)
    {
        var result = new McpProjectAssemblies(Path.GetFullPath(root));
        result.Load();
        return result;
    }

    private void Load()
    {
        var sources = new List<string>();
        foreach (var project in Directory.GetFiles(root, "*.csproj"))
        {
            XDocument document;
            try
            {
                document = XDocument.Load(project);
            }
            catch (Exception exception) when (exception is IOException or System.Xml.XmlException or UnauthorizedAccessException)
            {
                continue;
            }
            var name = document.Descendants().FirstOrDefault(node => node.Name.LocalName == "AssemblyName")?.Value.Trim();
            if (string.IsNullOrEmpty(name))
                name = Path.GetFileNameWithoutExtension(project);
            foreach (var include in document.Descendants().Where(node => node.Name.LocalName == "Compile")
                         .Select(node => node.Attribute("Include")?.Value ?? string.Empty)
                         .Where(value => value.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                var full = Path.GetFullPath(Path.Combine(root, include.Replace('\\', Path.DirectorySeparatorChar)));
                var path = Normalize(Path.GetRelativePath(root, full));
                byFile[path] = name;
                if (path.StartsWith("Assets/", StringComparison.Ordinal) && File.Exists(full))
                    sources.Add(path);
            }
            if (string.Equals(name, "Assembly-CSharp", StringComparison.Ordinal))
                Defines = document.Descendants().FirstOrDefault(node => node.Name.LocalName == "DefineConstants")?.Value
                    .Split(';', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
        }
        if (byFile.Count == 0)
            sources.AddRange(FactsModel.SourceFilePaths(root).Select(file => Normalize(Path.GetRelativePath(root, file))));
        if (Defines.Length == 0)
            Defines = new[] { "UNITY_EDITOR", "UNITY_EDITOR_WIN", "UNITY_2021_2_OR_NEWER", "UNITY_2022_3_OR_NEWER", "UNITY_6000_0_OR_NEWER", "UNITY_6000_3_OR_NEWER" };
        SourceFiles = sources.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    public string AssemblyOf(string projectRelativePath)
    {
        var path = Normalize(projectRelativePath);
        if (byFile.TryGetValue(path, out var known))
            return known;
        var name = FromFolders(path);
        byFile[path] = name;
        return name;
    }

    private string FromFolders(string path)
    {
        var folder = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? string.Empty;
        for (var current = folder; current.Length > 0; current = Path.GetDirectoryName(current)?.Replace('\\', '/') ?? string.Empty)
        {
            if (!byFolder.TryGetValue(current, out var assembly))
                byFolder[current] = assembly = DefinedIn(current);
            if (assembly != null)
                return assembly;
        }
        var parts = path.Split('/');
        var firstpass = parts.Length > 1 && parts[0] == "Assets" && parts[1] is "Plugins" or "Standard Assets" or "Pro Standard Assets";
        var editor = parts.Take(parts.Length - 1).Contains("Editor");
        return "Assembly-CSharp" + (editor ? "-Editor" : string.Empty) + (firstpass ? "-firstpass" : string.Empty);
    }

    private string? DefinedIn(string folder)
    {
        var full = Path.Combine(root, folder);
        if (!Directory.Exists(full))
            return null;
        var definition = Directory.GetFiles(full, "*.asmdef").FirstOrDefault();
        if (definition != null)
            return NameField.Match(File.ReadAllText(definition)) is { Success: true } match ? match.Groups[1].Value : Path.GetFileNameWithoutExtension(definition);
        var reference = Directory.GetFiles(full, "*.asmref").FirstOrDefault();
        if (reference == null)
            return null;
        var target = ReferenceField.Match(File.ReadAllText(reference));
        if (!target.Success)
            return null;
        var value = target.Groups[1].Value;
        if (!value.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase))
            return value;
        return AsmdefByGuid().TryGetValue(value.Substring(5), out var named) ? named : null;
    }

    private Dictionary<string, string> AsmdefByGuid()
    {
        if (asmdefByGuid != null)
            return asmdefByGuid;
        asmdefByGuid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var top in new[] { "Assets", "Packages" }.Select(folder => Path.Combine(root, folder)).Where(Directory.Exists))
        {
            foreach (var definition in Directory.GetFiles(top, "*.asmdef", SearchOption.AllDirectories))
            {
                var meta = definition + ".meta";
                if (!File.Exists(meta))
                    continue;
                var guid = File.ReadLines(meta).FirstOrDefault(line => line.StartsWith("guid:", StringComparison.Ordinal))?.Substring(5).Trim();
                if (guid != null && NameField.Match(File.ReadAllText(definition)) is { Success: true } match)
                    asmdefByGuid[guid] = match.Groups[1].Value;
            }
        }
        return asmdefByGuid;
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
