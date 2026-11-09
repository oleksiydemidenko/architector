namespace Sample.Player
{
    // Wiring facts: bases that placed components inherit from are base_only; a base nobody inherits is unwired.
    public abstract class PlayerPart : UnityEngine.MonoBehaviour
    {
    }

    public abstract class PlayerStat<T> : UnityEngine.MonoBehaviour
    {
        public T Value;
    }

    public abstract class PlayerLegacy : UnityEngine.MonoBehaviour
    {
    }

    // A class serialized by value: assets never name its type, so it is not_tracked.
    [System.Serializable]
    public sealed class PlayerStatsValue
    {
        public int Lives;
    }

    // Managed references written by Unity with a wrapped type line and in block form.
    [System.Serializable]
    public sealed class PlayerLoadoutInitializerWithAVeryLongNameThatWraps
    {
    }

    [System.Serializable]
    public sealed class PlayerBlockEntry
    {
    }
}
