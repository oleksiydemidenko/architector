namespace Rules
{
    public class Gauge : UnityEngine.MonoBehaviour
    {
        private Speed _speed;

        private void Update() { Show(_speed.Value); }

        private void Show(float value) { }
    }
}
