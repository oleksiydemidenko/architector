using System.Collections.Generic;
using Lab.Core;

namespace Lab.Game
{
    public sealed class HealthUser : UnityEngine.MonoBehaviour
    {
        private Container container;
        private Health health;
        private readonly Dictionary<string, Point> points = new Dictionary<string, Point>();

        private void Awake()
        {
            container.Add(new Health());
            health.Set(3f);
            container.Get<Health>();
        }

        private void Move(Point point, Health other)
        {
            point.X = 1f;
            var copy = points["a"];
            copy.Y = 2f;
            points["a"] = copy;
            other.Value = 2f;
        }
    }
}
