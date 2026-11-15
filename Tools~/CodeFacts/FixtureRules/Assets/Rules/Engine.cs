namespace Rules
{
    public class Engine : UnityEngine.MonoBehaviour
    {
        private Speed _speed;
        private Ticker _ticker;

        private void OnEnable() { _ticker.Subscribe(Tick, Phase.Post); }

        private void OnDisable()
        {
            _ticker.Unsubscribe(Tick, Phase.Post);
            _speed.Set(0f);
        }

        private void Tick() { Push(); }

        private void Push() { _speed.Set(1f); }
    }
}
