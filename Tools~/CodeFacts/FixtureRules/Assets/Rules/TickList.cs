namespace Rules
{
    [System.Serializable]
    public class TickList : IBehaviour
    {
        [UnityEngine.SerializeField] private System.Collections.Generic.List<Handler> _handlers;
    }
}
