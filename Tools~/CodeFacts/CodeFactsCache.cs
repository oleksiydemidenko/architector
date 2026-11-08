using Microsoft.CodeAnalysis;

namespace Architector.CodeFacts;

// Keeps one analysed model for a long-running host (the MCP server). It is built once in the background at start
// and rebuilt only when a C# file, the Architector data or the define list changed since, so later calls are fast.
public sealed class FactsCache
{
    private readonly string root;
    private readonly Func<IEnumerable<MetadataReference>> references;
    private readonly object gate = new();
    private FactsModel? model;
    private string signature = string.Empty;

    public FactsCache(string root, Func<IEnumerable<MetadataReference>> references)
    {
        this.root = root;
        this.references = references;
    }

    public void Warm()
    {
        if (!Directory.Exists(Path.Combine(root, "Architector")))
            return;
        var thread = new Thread(() =>
        {
            try
            {
                Get();
            }
            catch (Exception)
            {
                // A failed warm-up is repeated by the first call, which reports the error.
            }
        }) { IsBackground = true, Name = "Architector code facts" };
        thread.Start();
    }

    public FactsModel Get()
    {
        lock (gate)
        {
            var current = Signature();
            if (model == null || signature != current)
            {
                model = FactsModel.Build(root, references());
                signature = current;
            }
            return model;
        }
    }

    public FactsModel Architecture() => FactsModel.ArchitectureOnly(root);

    private string Signature()
    {
        long count = 0;
        long latest = 0;
        void Add(string file)
        {
            count++;
            latest = Math.Max(latest, File.GetLastWriteTimeUtc(file).Ticks);
        }
        foreach (var file in FactsModel.SourceFilePaths(root))
            Add(file);
        var data = Path.Combine(root, "Architector");
        if (Directory.Exists(data))
            foreach (var file in Directory.GetFiles(data, "*.json", SearchOption.AllDirectories))
                Add(file);
        var project = Path.Combine(root, "Assembly-CSharp.csproj");
        if (File.Exists(project))
            Add(project);
        return $"{count}:{latest}";
    }
}
