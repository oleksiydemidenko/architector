namespace Architector.CodeFacts;

// Who writes and who reads the state of a type, from the behaviour facts "writes" and "reads" of every other type:
// configured read and write calls (genericCalls Get<T>, a non-generic overload Add(T), memberCalls T.Set). The digest
// and route print these lists next to a node, so "who writes or reads X" is answered from the code, not from the
// node description. Assignments to members of other types (behaviour "assigns") are not part of it: they name the
// declaring type of the member, often a shared base, and are listed by layer_facts.
public sealed class FactsStateAccess
{
    private readonly Dictionary<string, List<FactsAccessor>> writers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<FactsAccessor>> readers = new(StringComparer.Ordinal);

    public static FactsStateAccess Build(FactsModel model)
    {
        var access = new FactsStateAccess();
        var byShortName = model.Types
            .GroupBy(type => ShortName(type.Name), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        FactsType? Resolve(string name)
        {
            if (model.TypesByName.TryGetValue(name, out var exact))
                return exact;
            return byShortName.TryGetValue(ShortName(name), out var candidates) && candidates.Length == 1 ? candidates[0] : null;
        }
        foreach (var source in model.Types)
            foreach (var (section, list) in new[] { ("writes", access.writers), ("reads", access.readers) })
            {
                if (!source.Behaviour.TryGetValue(section, out var entries))
                    continue;
                foreach (var (value, at) in entries)
                    foreach (var name in StateTypeNames(value))
                    {
                        var target = Resolve(name);
                        if (target == null || ReferenceEquals(target, source))
                            continue;
                        if (!list.TryGetValue(target.FullName, out var accessors))
                            list[target.FullName] = accessors = new List<FactsAccessor>();
                        var existing = accessors.FindIndex(item => item.Type == source.FullName);
                        if (existing < 0)
                            accessors.Add(new FactsAccessor(source.FullName, source.Name, source.OwnerLayerId, at, 1));
                        else
                            accessors[existing] = accessors[existing] with { Uses = accessors[existing].Uses + 1 };
                    }
            }
        return access;
    }

    // Accessors sorted by layer path, then type name; the layer path comes from the architecture.
    public IReadOnlyList<FactsAccessor> Writers(string typeFullName, FactsArchitecture architecture) => Sorted(writers, typeFullName, architecture);

    public IReadOnlyList<FactsAccessor> Readers(string typeFullName, FactsArchitecture architecture) => Sorted(readers, typeFullName, architecture);

    private static IReadOnlyList<FactsAccessor> Sorted(Dictionary<string, List<FactsAccessor>> map, string type, FactsArchitecture architecture)
    {
        if (!map.TryGetValue(type, out var accessors))
            return Array.Empty<FactsAccessor>();
        return accessors
            .OrderBy(item => item.LayerId == null ? "~" : architecture.Path(item.LayerId), StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Type, StringComparer.Ordinal)
            .ToArray();
    }

    // "Type (Layer/Path), Other (Layer/Path) +3 more (5)": at most limit names, then the rest as a count.
    public static string Describe(IReadOnlyList<FactsAccessor> accessors, FactsArchitecture architecture, int limit)
    {
        var shown = accessors.Take(Math.Max(1, limit))
            .Select(item => $"{item.Name} ({(item.LayerId == null ? "no layer" : architecture.Path(item.LayerId))})");
        var text = string.Join(", ", shown);
        return accessors.Count > limit ? $"{text} +{accessors.Count - limit} more ({accessors.Count})" : text;
    }

    // The types whose state a behaviour value names: the type arguments of Get<T> (every argument, generic arguments of
    // arguments included), the argument type of Add(T), the receiver of T.Set.
    public static IEnumerable<string> StateTypeNames(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            yield break;
        var open = value.IndexOf('<');
        if (open > 0 && value.EndsWith('>'))
        {
            foreach (var name in TypeNamesIn(value[(open + 1)..^1]))
                yield return name;
            yield break;
        }
        var parenthesis = value.IndexOf('(');
        if (parenthesis > 0 && value.EndsWith(')'))
        {
            foreach (var name in TypeNamesIn(value[(parenthesis + 1)..^1]))
                yield return name;
            yield break;
        }
        var dot = value.LastIndexOf('.');
        if (dot > 0)
            yield return value[..dot];
    }

    // Every type name in a type argument list: "Dictionary<string, Lab.Health>, Point" gives Dictionary, string,
    // Lab.Health and Point; the caller keeps the ones that are project types.
    private static IEnumerable<string> TypeNamesIn(string text)
    {
        var start = -1;
        for (var index = 0; index <= text.Length; index++)
        {
            var character = index < text.Length ? text[index] : ',';
            var part = char.IsLetterOrDigit(character) || character is '_' or '.' or '@';
            if (part && start < 0)
                start = index;
            else if (!part && start >= 0)
            {
                var name = text[start..index].Trim('.').TrimStart('@');
                if (name.Length > 0 && !char.IsDigit(name[0]))
                    yield return name;
                start = -1;
            }
        }
    }

    private static string ShortName(string name)
    {
        var dot = name.LastIndexOf('.');
        var shortName = dot >= 0 ? name[(dot + 1)..] : name;
        var tick = shortName.IndexOf('`');
        return tick >= 0 ? shortName[..tick] : shortName;
    }
}

// A type that writes or reads another type's state: the first place and how many places in total.
public sealed record FactsAccessor(string Type, string Name, string? LayerId, string At, int Uses);
