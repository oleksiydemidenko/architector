using System;
using System.Collections.Generic;
using System.Linq;

namespace Architector.Editor
{
    public static class ArchitectorUncheckedGroups
    {
        public static HashSet<string> LayerIds(ArchitectorProjectDocument project, ArchitectorSettingsDocument settings)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (settings?.rules?.uncheckedGroupIds == null || settings.rules.uncheckedGroupIds.Count == 0)
                return result;
            foreach (var layer in project.layers.Where(layer => FindUncheckedGroup(project, settings, layer.parentGroupId).Length > 0))
                result.Add(layer.id);
            return result;
        }

        public static IEnumerable<IGrouping<string, ArchitectorEdgeRecord>> LinksToUnchecked(
            string layerId,
            IEnumerable<ArchitectorLayerDocuments> layers,
            ICollection<string> uncheckedLayers)
        {
            if (uncheckedLayers.Count == 0 || uncheckedLayers.Contains(layerId))
                return Enumerable.Empty<IGrouping<string, ArchitectorEdgeRecord>>();
            return layers.SelectMany(documents => documents.semantic.edges)
                .GroupBy(edge => edge.id, StringComparer.Ordinal)
                .Select(same => same.First())
                .Where(edge => edge.sourceLayerId == layerId && uncheckedLayers.Contains(edge.targetLayerId)
                               || edge.targetLayerId == layerId && uncheckedLayers.Contains(edge.sourceLayerId))
                .GroupBy(edge => edge.sourceLayerId == layerId ? edge.targetLayerId : edge.sourceLayerId, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal);
        }

        public static string FindUncheckedGroup(ArchitectorProjectDocument project, ArchitectorSettingsDocument settings, string groupId)
        {
            var marked = settings?.rules?.uncheckedGroupIds;
            if (marked == null || marked.Count == 0)
                return string.Empty;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrEmpty(groupId) && visited.Add(groupId))
            {
                if (marked.Contains(groupId))
                    return groupId;
                groupId = project.groups.FirstOrDefault(group => group.id == groupId)?.parentGroupId;
            }
            return string.Empty;
        }
    }
}
