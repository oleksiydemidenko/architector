using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Architector.CodeFacts;

public static class Program
{
    private const int ReferenceSamples = 3;

    // dotnet run --project Tools~/DependencyEvidence -- <root> [--observed] [--json=<file>]
    // dotnet run --project Tools~/DependencyEvidence -- <root> --facts=<action> [--args=<json> | --args-file=<file>]
    public static int Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(arg => !arg.StartsWith("--")) ?? Directory.GetCurrentDirectory());
        var factsAction = Option(args, "facts");
        if (factsAction != null)
            return Facts(root, factsAction, args);

        var jsonPath = Option(args, "json");
        // --no-platform-references analyses like the self-contained server, which has no .NET assemblies on disk.
        var model = FactsModel.Build(root, args.Contains("--no-platform-references") ? Array.Empty<Microsoft.CodeAnalysis.MetadataReference>() : FactsModel.PlatformReferences());
        var architecture = model.Architecture;
        if (model.ScopedLayers.Count == 0)
        {
            Console.WriteLine("No Architector layer declares a code scope; nothing to check.");
            return 0;
        }

        var evidence = new Dictionary<(string Source, string Target), Evidence>();
        foreach (var reference in model.References.Where(reference => reference.CountsAsEvidence))
        {
            var key = (reference.SourceLayerId!, reference.TargetLayerId!);
            if (!evidence.TryGetValue(key, out var item))
                evidence[key] = item = new Evidence();
            item.Add($"{reference.SourceName} -> {reference.TargetName} ({reference.File}:{reference.Line})");
        }

        var violations = evidence
            .Where(pair => !architecture.AllowedDependencies.Contains(pair.Key))
            .OrderBy(pair => architecture.Path(pair.Key.Source), StringComparer.Ordinal)
            .ThenBy(pair => architecture.Path(pair.Key.Target), StringComparer.Ordinal)
            .ToArray();
        var scopedIds = model.ScopedLayers.Select(layer => layer.Id).ToHashSet(StringComparer.Ordinal);
        var unevidenced = architecture.AllowedDependencies
            .Where(pair => scopedIds.Contains(pair.Source) && scopedIds.Contains(pair.Target) && !evidence.ContainsKey(pair))
            .OrderBy(pair => architecture.Path(pair.Source), StringComparer.Ordinal)
            .ThenBy(pair => architecture.Path(pair.Target), StringComparer.Ordinal)
            .ToArray();

        var report = new StringBuilder();
        report.AppendLine($"Architector dependency evidence: {model.ScopedLayers.Count} scoped layers, {evidence.Count} observed layer dependencies.");
        foreach (var warning in model.EvidenceWarnings)
            report.AppendLine($"warning: {warning}");
        if (args.Contains("--observed"))
        {
            report.AppendLine("Observed layer dependencies:");
            foreach (var pair in evidence
                         .OrderBy(pair => architecture.Path(pair.Key.Source), StringComparer.Ordinal)
                         .ThenBy(pair => architecture.Path(pair.Key.Target), StringComparer.Ordinal))
            {
                report.AppendLine($"  {architecture.Path(pair.Key.Source)} -> {architecture.Path(pair.Key.Target)} ({pair.Value.Count} references)");
                foreach (var sample in pair.Value.Samples)
                    report.AppendLine($"    {sample}");
            }
        }
        if (violations.Length > 0)
        {
            report.AppendLine("Code dependencies without a permitting dependency relationship:");
            foreach (var violation in violations)
            {
                report.AppendLine($"  {architecture.Path(violation.Key.Source)} -> {architecture.Path(violation.Key.Target)} ({violation.Value.Count} references)");
                foreach (var sample in violation.Value.Samples)
                    report.AppendLine($"    {sample}");
            }
        }
        var cycles = evidence.Keys
            .Where(pair => string.CompareOrdinal(pair.Source, pair.Target) < 0 && evidence.ContainsKey((pair.Target, pair.Source)))
            .Select(pair => string.CompareOrdinal(architecture.Path(pair.Source), architecture.Path(pair.Target)) <= 0
                ? (pair.Source, pair.Target)
                : (pair.Target, pair.Source))
            .OrderBy(pair => architecture.Path(pair.Item1), StringComparer.Ordinal)
            .ToArray();
        if (cycles.Length > 0)
        {
            report.AppendLine("Layers that depend on each other (move the shared type to the layer that owns the contract):");
            foreach (var (first, second) in cycles)
            {
                report.AppendLine($"  {architecture.Path(first)} <-> {architecture.Path(second)}");
                report.AppendLine($"    {evidence[(first, second)].Samples.First()}");
                report.AppendLine($"    {evidence[(second, first)].Samples.First()}");
            }
        }
        if (unevidenced.Length > 0)
        {
            report.AppendLine("Declared dependencies without code evidence (information):");
            foreach (var pair in unevidenced)
                report.AppendLine($"  {architecture.Path(pair.Source)} -> {architecture.Path(pair.Target)}");
        }
        report.AppendLine(violations.Length == 0 ? "Result: no undeclared layer dependencies." : $"Result: {violations.Length} undeclared layer dependencies.");
        Console.Write(report.ToString());

        if (!string.IsNullOrEmpty(jsonPath))
        {
            var json = new
            {
                scopedLayers = model.ScopedLayers.Count,
                violations = violations.Select(pair => new
                {
                    sourceLayerId = pair.Key.Source,
                    sourceLayer = architecture.Path(pair.Key.Source),
                    targetLayerId = pair.Key.Target,
                    targetLayer = architecture.Path(pair.Key.Target),
                    references = pair.Value.Count,
                    samples = pair.Value.Samples
                }),
                unevidenced = unevidenced.Select(pair => new
                {
                    sourceLayerId = pair.Source,
                    sourceLayer = architecture.Path(pair.Source),
                    targetLayerId = pair.Target,
                    targetLayer = architecture.Path(pair.Target)
                }),
                layerCycles = cycles.Select(pair => new { first = architecture.Path(pair.Item1), second = architecture.Path(pair.Item2) }),
                warnings = model.EvidenceWarnings
            };
            File.WriteAllText(Path.GetFullPath(jsonPath), JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
        }
        return violations.Length == 0 ? 0 : 1;
    }

    private static int Facts(string root, string action, string[] args)
    {
        var argumentsFile = Option(args, "args-file");
        var argumentsText = argumentsFile != null ? File.ReadAllText(argumentsFile) : Option(args, "args") ?? "{}";
        var arguments = JsonNode.Parse(argumentsText);
        var model = FactsQueries.ArchitectureOnlyActions.Contains(action)
            ? FactsModel.ArchitectureOnly(root)
            : FactsModel.Build(root, FactsModel.PlatformReferences());
        var answer = FactsQueries.Answer(model, action, arguments, root);
        Console.WriteLine(answer.ToJsonString(args.Contains("--compact") ? FactsQueries.Compact : FactsQueries.Indented));
        return answer["success"]?.GetValue<bool>() == true ? 0 : 1;
    }

    private static string? Option(string[] args, string name) =>
        args.LastOrDefault(arg => arg.StartsWith("--" + name + "="))?[(name.Length + 3)..];

    private sealed class Evidence
    {
        public int Count { get; private set; }
        public List<string> Samples { get; } = new();

        public void Add(string sample)
        {
            Count++;
            if (Samples.Count < ReferenceSamples && !Samples.Contains(sample))
                Samples.Add(sample);
        }
    }
}
