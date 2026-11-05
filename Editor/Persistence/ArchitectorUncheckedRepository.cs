using System;
using System.Collections.Generic;
using System.Linq;

namespace Architector.Editor
{
    public sealed partial class ArchitectorRepository
    {
        public string FindUncheckedGroup(string entryId)
        {
            var groupId = FindGroup(entryId) != null ? entryId : FindLayer(entryId)?.parentGroupId;
            return ArchitectorUncheckedGroups.FindUncheckedGroup(Project, Settings, groupId);
        }

        public bool IsUnchecked(string entryId) => FindUncheckedGroup(entryId).Length > 0;

        public bool IsGroupMarkedUnchecked(string groupId) => Settings.rules.uncheckedGroupIds?.Contains(groupId) == true;

        public HashSet<string> UncheckedLayerIds() => ArchitectorUncheckedGroups.LayerIds(Project, Settings);

        public string ChecksOffBlocker(string groupId)
        {
            var group = FindGroup(groupId);
            if (group == null)
                return $"Group '{groupId}' does not exist.";
            var lockedId = LockedSelfOrAncestor(groupId);
            if (lockedId.Length > 0)
                return $"Group '{FindGroup(lockedId).name}' is locked; checks stay on in locked groups.";
            var inherited = FindUncheckedGroup(groupId);
            if (inherited.Length > 0)
                return inherited == groupId
                    ? $"Checks are already off for group '{group.name}'."
                    : $"Checks are already off through group '{FindGroup(inherited).name}'.";
            var approved = LayersInside(groupId).Where(layer => !string.IsNullOrEmpty(layer.approval?.semanticHash)).ToArray();
            if (approved.Length > 0)
                return $"Revoke the approval of {approved.Length} layer(s) first, such as '{approved[0].name}': approval means the layer is checked.";
            return string.Empty;
        }

        public void SetGroupChecks(string groupId, bool enabled)
        {
            var group = FindGroup(groupId) ?? throw new ArchitectorDataException($"Group '{groupId}' does not exist.");
            if (!enabled)
            {
                var blocker = ChecksOffBlocker(groupId);
                if (blocker.Length > 0)
                    throw new ArchitectorDataException(blocker);
                UpdateSettings("Turn Checks Off", settings => settings.rules.uncheckedGroupIds.Add(groupId));
                return;
            }

            if (!IsGroupMarkedUnchecked(groupId))
            {
                var inherited = FindUncheckedGroup(groupId);
                throw new ArchitectorDataException(inherited.Length > 0
                    ? $"Checks are off through group '{FindGroup(inherited).name}'; turn them on there."
                    : $"Checks are already on for group '{group.name}'.");
            }
            if (Settings.rules.dependencyCycles == ArchitectorDependencyCyclePolicy.Reject)
            {
                var next = ArchitectorJson.Clone(Settings);
                next.rules.uncheckedGroupIds.Remove(groupId);
                var stillUnchecked = ArchitectorUncheckedGroups.LayerIds(Project, next);
                var cycle = FindDependencyCycles().FirstOrDefault(candidate => candidate.LayerIds.Any(layerId => !stillUnchecked.Contains(layerId)));
                if (cycle != null)
                    throw new ArchitectorDataException(
                        $"Turning checks on would bring a dependency cycle under the Reject rule: {DescribeDependencyCycle(cycle)}. Remove the cycle first.");
            }
            UpdateSettings("Turn Checks On", settings => settings.rules.uncheckedGroupIds.Remove(groupId));
        }

        private string LockedSelfOrAncestor(string groupId)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrEmpty(groupId) && visited.Add(groupId))
            {
                if (IsGroupLocked(groupId))
                    return groupId;
                groupId = FindGroup(groupId)?.parentGroupId;
            }
            return string.Empty;
        }

        private IEnumerable<ArchitectorLayerRecord> LayersInside(string groupId)
        {
            var group = FindGroup(groupId);
            if (group == null)
                yield break;
            foreach (var layerId in group.layerIds)
                yield return FindLayer(layerId);
            foreach (var childId in group.childGroupIds)
            foreach (var layer in LayersInside(childId))
                yield return layer;
        }
    }
}
