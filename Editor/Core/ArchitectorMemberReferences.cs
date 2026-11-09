using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Architector.Editor
{
    // A member named in a plan by {kind, name, parameters} instead of its symbol key. The parameters are short
    // metadata names ("Single", "List", "ref:Clock", "Int32[]", "!0"), the form the code facts list for every member,
    // so an agent can write a plan from the facts without asking the Editor for keys.
    public static class ArchitectorMemberReferences
    {
        public static string Name(ArchitectorIndexedMember member)
        {
            var key = member?.LooseKey ?? string.Empty;
            var separator = key.LastIndexOf('|');
            return separator < 0 ? key : key.Substring(separator + 1);
        }

        public static IReadOnlyList<string> Parameters(ArchitectorIndexedMember member)
        {
            var key = member?.SymbolKey ?? string.Empty;
            var loose = member?.LooseKey ?? string.Empty;
            if (loose.Length == 0 || !key.StartsWith(loose + "|", StringComparison.Ordinal))
                return Array.Empty<string>();
            var rest = key.Substring(loose.Length + 1);
            if (member.Kind is "method" or "operator")
            {
                var bar = rest.IndexOf('|');
                rest = bar < 0 ? rest : rest.Substring(bar + 1);
            }
            return SplitTopLevel(rest).Select(ShortName).ToArray();
        }

        public static bool Matches(ArchitectorIndexedMember member, string kind, string name, IReadOnlyList<string> parameters)
        {
            if (member == null || !string.Equals(Name(member), name?.Trim(), StringComparison.Ordinal))
                return false;
            if (!string.IsNullOrWhiteSpace(kind) && !string.Equals(member.Kind, kind.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;
            if (parameters == null)
                return true;
            var actual = Parameters(member);
            return actual.Count == parameters.Count
                   && actual.Zip(parameters, (left, right) => string.Equals(left, right?.Trim(), StringComparison.OrdinalIgnoreCase)).All(same => same);
        }

        public static string Describe(ArchitectorIndexedMember member) =>
            $"{{\"kind\": \"{member.Kind}\", \"name\": \"{Name(member)}\", \"parameters\": [{string.Join(", ", Parameters(member).Select(parameter => $"\"{parameter}\""))}]}} ({member.Declaration})";

        // "System.Collections.Generic.List`1[System.Int32]" -> "List", "ref:Sample.Clock" -> "ref:Clock",
        // "System.Int32[]" -> "Int32[]", "!0" stays.
        public static string ShortName(string identity)
        {
            var value = identity?.Trim() ?? string.Empty;
            var prefix = string.Empty;
            foreach (var candidate in new[] { "out:", "ref:", "in:" })
                if (value.StartsWith(candidate, StringComparison.Ordinal))
                {
                    prefix = candidate;
                    value = value.Substring(candidate.Length);
                    break;
                }
            var suffix = new StringBuilder();
            while (value.EndsWith("]", StringComparison.Ordinal))
            {
                var open = value.LastIndexOf('[');
                var inside = open < 0 ? "x" : value.Substring(open + 1, value.Length - open - 2);
                if (inside.Any(symbol => symbol != ','))
                    break;
                suffix.Insert(0, value.Substring(open));
                value = value.Substring(0, open);
            }
            if (value.StartsWith("!", StringComparison.Ordinal))
                return prefix + value + suffix;
            var generic = value.IndexOf('[');
            if (generic >= 0)
                value = value.Substring(0, generic);
            var lastDot = Math.Max(value.LastIndexOf('.'), value.LastIndexOf('+'));
            if (lastDot >= 0)
                value = value.Substring(lastDot + 1);
            var arity = value.IndexOf('`');
            if (arity >= 0)
                value = value.Substring(0, arity);
            return prefix + value + suffix;
        }

        private static IEnumerable<string> SplitTopLevel(string text)
        {
            if (string.IsNullOrEmpty(text))
                yield break;
            var depth = 0;
            var start = 0;
            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] == '[')
                    depth++;
                else if (text[index] == ']')
                    depth--;
                else if (text[index] == ',' && depth == 0)
                {
                    yield return text.Substring(start, index - start);
                    start = index + 1;
                }
            }
            yield return text.Substring(start);
        }
    }
}
