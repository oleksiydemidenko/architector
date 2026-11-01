using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor
{
    internal static class ArchitectorIcons
    {
        private static readonly Dictionary<(string name, bool dark), GUIContent> Icons = new();

        public static GUIContent Get(string name)
        {
            var key = (name, EditorGUIUtility.isProSkin);
            if (!Icons.TryGetValue(key, out var icon))
            {
                icon = EditorGUIUtility.IconContent(name);
                Icons[key] = icon;
            }
            return icon;
        }
    }
}
