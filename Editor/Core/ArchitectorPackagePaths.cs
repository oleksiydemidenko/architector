using UnityEditor.PackageManager;

namespace Architector.Editor
{
    public static class ArchitectorPackagePaths
    {
        private const string SourceRoot = "Packages/com.oleksiydemidenko.architector";
        private static string root;

        public static string Root => root ??= Resolve();
        public static string Icon(string fileName) => $"{Root}/Editor/Icons/{fileName}";

        private static string Resolve()
        {
            var package = PackageInfo.FindForAssembly(typeof(ArchitectorPackagePaths).Assembly);
            return package != null ? package.assetPath : SourceRoot;
        }
    }
}
