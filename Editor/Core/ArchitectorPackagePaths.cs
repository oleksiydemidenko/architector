using UnityEditor.PackageManager;

namespace Architector.Editor
{
    public static class ArchitectorPackagePaths
    {
        private const string SourceRoot = "Packages/com.oleksiydemidenko.architector";
        private static string root;
        private static string version;

        public static string Root => root ??= Resolve();
        public static string Version => version ??= PackageInfo.FindForAssembly(typeof(ArchitectorPackagePaths).Assembly)?.version ?? "0.0.0";
        public static string DependencyEvidenceProject =>
            (PackageInfo.FindForAssembly(typeof(ArchitectorPackagePaths).Assembly)?.resolvedPath ?? SourceRoot).Replace('\\', '/') + "/Tools~/DependencyEvidence";
        public static string Icon(string fileName) => $"{Root}/Editor/Icons/{fileName}";

        private static string Resolve()
        {
            var package = PackageInfo.FindForAssembly(typeof(ArchitectorPackagePaths).Assembly);
            return package != null ? package.assetPath : SourceRoot;
        }
    }
}
