using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Architector.Editor
{
    public sealed class ArchitectorRouteNetwork
    {
        public Dictionary<string, List<Vector2>> Routes { get; } = new(StringComparer.Ordinal);
        public List<ArchitectorRouteStroke> Strokes { get; } = new();

        public Dictionary<string, List<Vector2>> Decorate(float cornerRadius, float bridgeRadius)
        {
            var routes = Strokes.ToDictionary(stroke => stroke.Id, stroke => stroke.Points, StringComparer.Ordinal);
            var arrows = new HashSet<string>(Strokes.Where(stroke => stroke.Arrow).Select(stroke => stroke.Id), StringComparer.Ordinal);
            return ArchitectorEdgeRouter.Decorate(routes, cornerRadius, bridgeRadius, arrows);
        }
    }
}
