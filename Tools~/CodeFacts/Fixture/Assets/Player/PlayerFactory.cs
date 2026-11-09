namespace Sample.Player
{
    public static class PlayerFactory
    {
        public static void Make(UnityEngine.GameObject target)
        {
            target.AddComponent<PlayerSpawner>();
        }
    }
}
