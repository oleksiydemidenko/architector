using Architector.CodeFacts;
using System.Text.RegularExpressions;
using System.Xml.Linq;

// Which Unity assembly compiles each C# file of the project, as the Editor's CompilationPipeline says: from the project
// files Unity writes (<AssemblyName> and <Compile Include> of every *.csproj in the project folder) when they exist;
// otherwise from the folders: the nearest .asmdef or .asmref, else Unity's predefined assemblies (Assembly-CSharp,
// -Editor, -firstpass). A linked git worktree (or a shared clone) has no project files and no Library: it reads those of
// its main checkout, whose paths are the same, and a file only the worktree has goes by its folders.
internal sealed class McpProjectAssemblies
{
    private static readonly Regex NameField = new("\"name\"\\s*:\\s*\"([^\"]+)\"");
    private static readonly Regex ReferenceField = new("\"reference\"\\s*:\\s*\"([^\"]+)\"");

    private readonly string root;
    private readonly string referenceRoot;
    private readonly Dictionary<string, string> byFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> byFolder = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string>? asmdefByGuid;

    private McpProjectAssemblies(string root, string referenceRoot)
    {
        this.root = root;
        this.referenceRoot = referenceRoot;
    }

    // The checkout whose Unity project files and Library/ScriptAssemblies give the references: this one, or the main
    // checkout of a linked worktree.
    public string ReferenceRoot => referenceRoot;

    // Project-relative paths of the project's own sources (those under Assets), the files the Editor compiles itself.
    public IReadOnlyList<string> SourceFiles { get; private set; } = Array.Empty<string>();

    public string[] Defines { get; private set; } = Array.Empty<string>();

    public static McpProjectAssemblies Read(string root)
    {
        var full = Path.GetFullPath(root);
        var reference = HasProjectFiles(full) ? full : MainCheckout(full) is { } main && HasProjectFiles(main) ? main : full;
        var result = new McpProjectAssemblies(full, reference);
        result.Load();
        return result;
    }

    private void Load()
    {
        var sources = new List<string>();
        foreach (var project in Directory.GetFiles(referenceRoot, "*.csproj"))
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
                var full = Path.GetFullPath(Path.Combine(referenceRoot, include.Replace('\\', Path.DirectorySeparatorChar)));
                var path = Normalize(Path.GetRelativePath(referenceRoot, full));
                byFile[path] = name;
                if (path.StartsWith("Assets/", StringComparison.Ordinal) && File.Exists(Path.Combine(root, path)))
                    sources.Add(path);
            }
            if (string.Equals(name, "Assembly-CSharp", StringComparison.Ordinal))
                Defines = document.Descendants().FirstOrDefault(node => node.Name.LocalName == "DefineConstants")?.Value
                    .Split(';', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
        }
        // Every source of this checkout: the project files may be older than a worktree's new files.
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

    private static bool HasProjectFiles(string folder) => Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.csproj").Any();

    // A linked worktree's .git file names its git folder, whose commondir is the main checkout's .git; a clone made with
    // --shared names the main checkout's objects in objects/info/alternates. No git process is started.
    private static string? MainCheckout(string folder)
    {
        try
        {
            var dotGit = Path.Combine(folder, ".git");
            if (File.Exists(dotGit))
            {
                var line = File.ReadAllText(dotGit).Trim();
                if (!line.StartsWith("gitdir:", StringComparison.Ordinal))
                    return null;
                var gitDir = Path.GetFullPath(Path.Combine(folder, line.Substring("gitdir:".Length).Trim()));
                var common = Path.Combine(gitDir, "commondir");
                if (!File.Exists(common))
                    return null;
                return Directory.GetParent(Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(common).Trim())).TrimEnd('\\', '/'))?.FullName;
            }
            var alternates = Path.Combine(dotGit, "objects", "info", "alternates");
            if (!File.Exists(alternates))
                return null;
            var objects = File.ReadLines(alternates).FirstOrDefault(entry => entry.Trim().Length > 0)?.Trim();
            return objects == null ? null : Directory.GetParent(Directory.GetParent(Path.GetFullPath(objects).TrimEnd('\\', '/'))!.FullName)?.FullName;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
