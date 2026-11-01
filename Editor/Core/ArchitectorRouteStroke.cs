using System.Collections.Generic;
using UnityEngine;

namespace Architector.Editor
{
    public sealed class ArchitectorRouteStroke
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string[] EdgeIds { get; set; }
        public List<Vector2> Points { get; set; }
        public bool Arrow { get; set; }
        public bool JunctionAtStart { get; set; }
    }
}
