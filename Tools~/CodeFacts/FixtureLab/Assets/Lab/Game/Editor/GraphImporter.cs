namespace Lab.Game.Editor
{
    public static class GraphImporter
    {
        public static GraphAsset Import() => UnityEngine.ScriptableObject.CreateInstance<GraphAsset>();
    }
}
