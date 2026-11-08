using Microsoft.CodeAnalysis;

namespace Architector.CodeFacts;

// Keeps the code index for a long-running host (the MCP server). The index is analysed once, or loaded from the
// snapshot another process saved, and reused while no C# file, csproj or code-facts.json changed. The graph is read
// again on every call and applied on top in a fraction of a second, so graph edits never trigger a new analysis.
public sealed class FactsCache
{
    private readonly string root;
    private readonly string referenceSet;
    private readonly Func<IEnumerable<MetadataReference>> references;
    private readonly object gate = new();
    private CodeIndex? index;

    public FactsCache(string root, string referenceSet, Func<IEnumerable<MetadataReference>> references)
    {
        this.root = root;
        this.referenceSet = referenceSet;
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
        var architecture = FactsArchitecture.Load(Path.Combine(root, "Architector"));
        if (!architecture.Layers.Values.Any(layer => layer.Scoped))
            return FactsModel.Apply(root, architecture, null);
        CodeIndex current;
        lock (gate)
        {
            var signature = CodeIndex.ComputeSignature(root);
            if (index == null || index.Signature != signature)
                index = CodeIndex.LoadOrBuild(root, referenceSet, references);
            current = index;
        }
        return FactsModel.Apply(root, architecture, current);
    }

    public FactsModel Architecture() => FactsModel.ArchitectureOnly(root);
}
