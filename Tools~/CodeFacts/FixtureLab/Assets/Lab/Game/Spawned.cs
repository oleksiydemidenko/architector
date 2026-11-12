namespace Lab.Game
{
    public sealed class Spawned : UnityEngine.MonoBehaviour
    {
    }

    public sealed class SpawnPoint : UnityEngine.MonoBehaviour
    {
        private void Start()
        {
            gameObject.AddComponent<Spawned>();
        }
    }
}
