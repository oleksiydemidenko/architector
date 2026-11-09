using System;
using System.Collections.Generic;
using System.Linq;

namespace Architector.Editor
{
    public static class ArchitectorEntryPaths
    {
        public const char Separator = '/';

        public static string Describe(ArchitectorProjectDocument project, string id)
        {
            var segments = new List<string>();
            var group = project.groups.FirstOrDefault(candidate => candidate.id == id);
            string parentId;
            if (group != null)
            {
                segments.Add(group.name);
                parentId = group.parentGroupId;
            }
            else
            {
                var layer = project.layers.FirstOrDefault(candidate => candidate.id == id);
                if (layer == null)
                    return string.Empty;
                segments.Add(layer.name);
                parentId = layer.parentGroupId;
            }

            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrEmpty(parentId) && visited.Add(parentId))
            {
                var parent = project.groups.FirstOrDefault(candidate => candidate.id == parentId);
                if (parent == null)
                    break;
                segments.Add(parent.name);
                parentId = parent.parentGroupId;
            }
            segments.Reverse();
            return string.Join(Separator.ToString(), segments);
        }

        public static string Resolve(ArchitectorProjectDocument project, string path)
        {
            var segments = (path ?? string.Empty)
                .Split(Separator)
                .Select(segment => segment.Trim())
                .ToArray();
            if (segments.Length == 0 || segments.Any(segment => segment.Length == 0))
                return string.Empty;

            var parentId = string.Empty;
            for (var index = 0; index < segments.Length; index++)
            {
                var name = segments[index];
                var group = project.groups.FirstOrDefault(candidate =>
                    string.Equals(candidate.parentGroupId ?? string.Empty, parentId, StringComparison.Ordinal)
                    && string.Equals(candidate.name, name, StringComparison.OrdinalIgnoreCase));
                if (index == segments.Length - 1)
                {
                    if (group != null)
                        return group.id;
                    return project.layers.FirstOrDefault(candidate =>
                               string.Equals(candidate.parentGroupId ?? string.Empty, parentId, StringComparison.Ordinal)
                               && string.Equals(candidate.name, name, StringComparison.OrdinalIgnoreCase))?.id
                           ?? string.Empty;
                }
                if (group == null)
                    return string.Empty;
                parentId = group.id;
            }
            return string.Empty;
        }

        // The layers a reference names: a layer id, its full Explorer path, or any trailing part of the path
        // ("Group/Layer" for "Root/Group/Layer"; a layer name is the shortest such part). One match resolves; several
        // are ambiguous and are returned as candidates. The code facts engine applies the same rule.
        public static IReadOnlyList<string> MatchLayers(ArchitectorProjectDocument project, string reference)
        {
            var value = (reference ?? string.Empty).Trim().Trim(Separator);
            if (value.Length == 0)
                return Array.Empty<string>();
            if (project.layers.Any(layer => layer.id == value))
                return new[] { value };
            var normalized = string.Join(Separator.ToString(), value.Split(Separator).Select(segment => segment.Trim()));
            var paths = project.layers.Select(layer => (layer.id, Path: Describe(project, layer.id))).ToArray();
            var exact = paths.Where(entry => string.Equals(entry.Path, normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (exact.Length > 0)
                return exact.Select(entry => entry.id).ToArray();
            return paths
                .Where(entry => entry.Path.EndsWith(Separator + normalized, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.id)
                .ToArray();
        }

        public static string ResolveLayer(ArchitectorProjectDocument project, string reference)
        {
            var matches = MatchLayers(project, reference);
            return matches.Count == 1 ? matches[0] : string.Empty;
        }
    }
}
