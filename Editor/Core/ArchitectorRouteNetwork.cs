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

        public bool IsJunction(ArchitectorRouteStroke stroke, Func<string, bool> visible)
        {
            var point = stroke.Points[0];
            bool Shown(ArchitectorRouteStroke candidate) => candidate.EdgeIds.Any(visible);
            var arriving = Strokes.Where(candidate => candidate.Points[candidate.Points.Count - 1] == point
                && Shown(candidate) && candidate.EdgeIds.Any(id => Array.IndexOf(stroke.EdgeIds, id) >= 0)).ToArray();
            var through = new HashSet<string>(stroke.EdgeIds.Concat(arriving.SelectMany(candidate => candidate.EdgeIds)), StringComparer.Ordinal);
            var leaving = Strokes.Count(candidate => candidate.Points[0] == point
                && Shown(candidate) && candidate.EdgeIds.Any(through.Contains));
            return arriving.Length + leaving >= 3;
        }

        public Dictionary<string, List<Vector2>> Decorate(float cornerRadius, float bridgeRadius)
        {
            var routes = Strokes.ToDictionary(stroke => stroke.Id, stroke => stroke.Points, StringComparer.Ordinal);
            var arrows = new HashSet<string>(Strokes.Where(stroke => stroke.Arrow).Select(stroke => stroke.Id), StringComparer.Ordinal);
            return ArchitectorEdgeRouter.Decorate(routes, cornerRadius, bridgeRadius, arrows);
        }
    }
}
