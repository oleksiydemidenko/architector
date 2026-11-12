namespace Lab.Game
{
    public abstract class Turret<T> : UnityEngine.MonoBehaviour
    {
    }

    public sealed class Turret : Turret<int>
    {
    }
}
