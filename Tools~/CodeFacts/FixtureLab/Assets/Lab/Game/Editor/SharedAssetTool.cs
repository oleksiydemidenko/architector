namespace Lab.Game.Editor
{
    public static class SharedAssetTool
    {
        public static SharedAsset Make() => UnityEngine.ScriptableObject.CreateInstance<SharedAsset>();
    }
}
