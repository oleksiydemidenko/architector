#if UNITY_6000_3_OR_NEWER
using UnityEditor;
using UnityEditor.Toolbars;
using UnityEngine;

namespace Architector.Editor
{
    internal static class ArchitectorMainToolbar
    {
        private const string ElementPath = "Architector/Window";

        [MainToolbarElement(
            ElementPath,
            defaultDockPosition = MainToolbarDockPosition.Left,
            defaultDockIndex = 9)]
        private static MainToolbarElement CreateOpenButton()
        {
            var theme = EditorGUIUtility.isProSkin ? "dark" : "light";
            var scale = EditorGUIUtility.pixelsPerPoint > 1f ? "@2x" : string.Empty;
            var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(ArchitectorPackagePaths.Icon($"toolbar_icon_{theme}{scale}.png"))
                       ?? AssetDatabase.LoadAssetAtPath<Texture2D>(ArchitectorPackagePaths.Icon($"toolbar_icon_{theme}.png"));
            var content = new MainToolbarContent(icon, "Open Architector");
            return new MainToolbarButton(content, ArchitectorMasterWindow.Open);
        }
    }
}
#endif
