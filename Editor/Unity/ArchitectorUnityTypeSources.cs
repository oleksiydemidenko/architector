using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using Assembly = System.Reflection.Assembly;

namespace Architector.Editor
{
    // The code of the open Unity Editor for the reflection type facts: the compiled assemblies of CompilationPipeline
    // that are loaded now, and their source files in the index scope with their asset GUIDs.
    public sealed class ArchitectorUnityTypeSources : IArchitectorTypeSources
    {
        public ArchitectorUnityTypeSources()
        {
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        public event Action CodeChanged;

        public ArchitectorTypeSourceSet Collect(IReadOnlyList<string> includedRoots, IReadOnlyList<string> excludedRoots)
        {
            var compilationAssemblies = CompilationPipeline.GetAssemblies();
            var compiledNames = new HashSet<string>(compilationAssemblies.Select(assembly => assembly.name), StringComparer.Ordinal);
            var assemblies = new Dictionary<string, Assembly>(StringComparer.Ordinal);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (compiledNames.Contains(name) && !assemblies.ContainsKey(name))
                    assemblies[name] = assembly;
            }
            var sources = new List<ArchitectorTypeIndexSource>();
            foreach (var compilationAssembly in compilationAssemblies)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in compilationAssembly.sourceFiles)
                {
                    var path = ProjectRelativePath(file);
                    if (ArchitectorTypeIndex.IsInScope(path, includedRoots, excludedRoots) && seen.Add(path))
                        sources.Add(new ArchitectorTypeIndexSource(compilationAssembly.name, path, AssetDatabase.AssetPathToGUID(path)));
                }
            }
            return new ArchitectorTypeSourceSet(sources, assemblies);
        }

        public void Dispose()
        {
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
        }

        private void OnCompilationFinished(object _) => CodeChanged?.Invoke();

        // No worker outlives an assembly reload: the index cancels its running builds on this event.
        private void OnBeforeAssemblyReload() => CodeChanged?.Invoke();

        private static string ProjectRelativePath(string path)
        {
            var relative = FileUtil.GetProjectRelativePath(path);
            return ArchitectorTypeIndex.NormalizePath(string.IsNullOrEmpty(relative) ? path : relative);
        }
    }
}
