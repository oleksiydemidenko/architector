using System;
using System.Collections.Generic;
using System.Linq;

namespace Architector.Editor.Agents
{
    public sealed class ArchitectorIntegrationEntry
    {
        public ArchitectorIntegrationEntry(string id, string displayName, IEnumerable<string> tags, Action drawDetails, int order = 0)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(displayName))
                throw new ArgumentException("An integration needs an ID and display name.");
            var labels = tags?.Where(tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (labels == null || labels.Length == 0)
                throw new ArgumentException("An integration needs at least one tag.");
            Id = id;
            DisplayName = displayName;
            Tags = Array.AsReadOnly(labels);
            DrawDetails = drawDetails ?? throw new ArgumentNullException(nameof(drawDetails));
            Order = order;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public IReadOnlyList<string> Tags { get; }
        public Action DrawDetails { get; }
        public int Order { get; }
    }
}
