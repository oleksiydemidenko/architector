using System;
using System.Collections.Generic;
using System.Linq;

namespace Architector.Editor
{
    // The layer whose code scope owns a linked type: the layer with the longest folder or namespace scope that covers
    // the type's first source file or its namespace. Two layers that match equally own nothing. The code facts engine
    // and DependencyEvidence use the same rule, so the Editor, the route and the evidence agree.
    public static class ArchitectorScopeOwners
    {
        public static ArchitectorLayerRecord OwnerOf(ArchitectorProjectDocument project, ArchitectorTypeRecord type)
        {
            if (project == null || type == null)
                return null;
            var file = type.sourceDeclarations?.FirstOrDefault(declaration => !string.IsNullOrEmpty(declaration.repositoryPath))?.repositoryPath ?? string.Empty;
            return OwnerOf(project, file, Namespace(type.metadataName));
        }

        public static ArchitectorLayerRecord OwnerOf(ArchitectorProjectDocument project, string file, string space)
        {
            var matches = project.layers
                .Select(layer => (layer, length: Math.Max(
                    (layer.scope?.folders ?? new List<string>())
                        .Select(folder => folder.TrimEnd('/'))
                        .Where(folder => folder.Length > 0 && file.Length > 0 && InFolder(file, folder))
                        .Select(folder => folder.Length)
                        .DefaultIfEmpty(0)
                        .Max(),
                    (layer.scope?.namespaces ?? new List<string>())
                        .Where(scope => scope.Length > 0 && InNamespace(space, scope))
                        .Select(scope => scope.Length)
                        .DefaultIfEmpty(0)
                        .Max())))
                .Where(match => match.length > 0)
                .OrderByDescending(match => match.length)
                .ToArray();
            return matches.Length == 1 || matches.Length > 1 && matches[0].length > matches[1].length ? matches[0].layer : null;
        }

        private static string Namespace(string metadataName)
        {
            var outer = (metadataName ?? string.Empty).Split('+')[0];
            var dot = outer.LastIndexOf('.');
            return dot < 0 ? string.Empty : outer.Substring(0, dot);
        }

        private static bool InFolder(string file, string folder) =>
            string.Equals(file, folder, StringComparison.Ordinal) || file.StartsWith(folder + "/", StringComparison.Ordinal);

        private static bool InNamespace(string name, string scope) =>
            name.Length > 0 && (name == scope || name.StartsWith(scope + ".", StringComparison.Ordinal));
    }
}
