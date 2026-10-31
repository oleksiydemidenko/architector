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
    }
}
