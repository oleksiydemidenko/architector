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

    // For check: an index without waiting for an analysis. The current one ("fresh"), else the one in memory or saved
    // even when the code changed since ("stale"; a refresh starts in the background), else none ("building"; the first
    // analysis starts in the background).
    public (CodeIndex? Index, string State) IndexForCheck()
    {
        var signature = CodeIndex.ComputeSignature(root);
        CodeIndex? current;
        lock (gate)
            current = index;
        current ??= CodeIndex.TryLoad(CodeIndex.SnapshotPath(root, referenceSet), null);
        if (current != null && current.Signature == signature)
        {
            lock (gate)
                index ??= current;
            return (current, "fresh");
        }
        Refresh();
        return (current, current == null ? "building" : "stale");
    }

    private int refreshing;

    // Analyses the code once in the background, unless an analysis already runs.
    private void Refresh()
    {
        if (Interlocked.Exchange(ref refreshing, 1) == 1)
            return;
        var thread = new Thread(() =>
        {
            try
            {
                lock (gate)
                    index = CodeIndex.LoadOrBuild(root, referenceSet, references);
            }
            catch (Exception)
            {
                // The next facts call analyses again and reports the error.
            }
            finally
            {
                Interlocked.Exchange(ref refreshing, 0);
            }
        }) { IsBackground = true, Name = "Architector code index refresh" };
        thread.Start();
    }
}
