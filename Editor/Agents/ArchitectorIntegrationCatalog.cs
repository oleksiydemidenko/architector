using System;
using System.Collections.Generic;
using System.Linq;

namespace Architector.Editor.Agents
{
    public static class ArchitectorIntegrationCatalog
    {
        private static readonly Dictionary<string, ArchitectorIntegrationEntry> entries = new(StringComparer.Ordinal);

        public static event Action Changed;
        public static IReadOnlyList<ArchitectorIntegrationEntry> Entries => entries.Values.ToArray();

        public static void Register(ArchitectorIntegrationEntry entry)
        {
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));
            if (entries.ContainsKey(entry.Id) || ArchitectorAgentIntegrationRegistry.Descriptors.Any(item => item.id == entry.Id)
                || ArchitectorAgentWorkflowModes.Optional.Any(item => item.Id == entry.Id))
                throw new InvalidOperationException("An integration already uses ID '" + entry.Id + "'.");
            entries.Add(entry.Id, entry);
            Changed?.Invoke();
        }

        public static void Unregister(string id)
        {
            if (entries.Remove(id))
                Changed?.Invoke();
        }

        public static IReadOnlyList<ArchitectorIntegrationEntry> Filter(IEnumerable<ArchitectorIntegrationEntry> source, string search, string tag = "")
        {
            var terms = (search ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            return source.Where(entry => (string.IsNullOrEmpty(tag) || entry.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    && terms.All(term => Matches(entry, term)))
                .OrderBy(entry => entry.Tags[0], StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Order)
                .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Id, StringComparer.Ordinal)
                .ToArray();
        }

        private static bool Matches(ArchitectorIntegrationEntry entry, string term)
        {
            if (term.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
                return entry.Tags.Contains(term.Substring(4), StringComparer.OrdinalIgnoreCase);
            if (term.StartsWith("#", StringComparison.Ordinal))
                return entry.Tags.Contains(term.Substring(1), StringComparer.OrdinalIgnoreCase);
            return entry.DisplayName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                || entry.Tags.Any(tag => tag.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
