using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Architector.Editor
{
    public static class ArchitectorGroupLocks
    {
        public const string DefaultNotice =
            "This group is protected by the project architect. Do not edit it; propose changes to the architect.";

        public static string HashPassword(string password)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes("architector-group-lock\n" + (password ?? string.Empty)));
            return "sha256:" + string.Concat(hash.Select(value => value.ToString("x2")));
        }
    }

    public sealed partial class ArchitectorRepository
    {
        private readonly HashSet<string> sessionUnlockedGroupIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> keptUnlocks = new(StringComparer.Ordinal);

        public bool IsGroupLocked(string groupId)
        {
            return FindGroup(groupId)?.lockState?.locked == true;
        }

        public bool IsGroupLockedForEditing(string groupId)
        {
            return IsGroupLocked(groupId) && !sessionUnlockedGroupIds.Contains(groupId);
        }

        public bool IsGroupUnlockedForSession(string groupId)
        {
            return IsGroupLocked(groupId) && sessionUnlockedGroupIds.Contains(groupId);
        }

        public string FindEffectiveLock(string entryId)
        {
            var parentId = FindGroup(entryId) != null ? entryId : FindLayer(entryId)?.parentGroupId;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrEmpty(parentId) && visited.Add(parentId))
            {
                if (IsGroupLockedForEditing(parentId))
                    return parentId;
                parentId = FindGroup(parentId)?.parentGroupId;
            }
            return string.Empty;
        }

        public void LockGroup(string groupId, string password, string notice)
        {
            var group = FindGroup(groupId) ?? throw new ArchitectorDataException($"Group '{groupId}' does not exist.");
            if (string.IsNullOrEmpty(password))
                throw new ArchitectorDataException("A lock requires a password.");
            if (IsGroupLockedForEditing(groupId))
                throw new ArchitectorDataException($"Group '{group.name}' is already locked.");
            ExecuteProjectChange("Lock Group", project =>
            {
                project.groups.First(candidate => candidate.id == groupId).lockState = new ArchitectorGroupLockRecord
                {
                    locked = true,
                    passwordHash = ArchitectorGroupLocks.HashPassword(password),
                    notice = string.IsNullOrWhiteSpace(notice) ? ArchitectorGroupLocks.DefaultNotice : notice.Trim()
                };
            });
            sessionUnlockedGroupIds.Remove(groupId);
            keptUnlocks.Remove(groupId);
            Changed?.Invoke();
        }

        public bool UnlockGroupForSession(string groupId, string password, bool keepUntilEditorRestart = false)
        {
            var group = FindGroup(groupId) ?? throw new ArchitectorDataException($"Group '{groupId}' does not exist.");
            if (group.lockState?.locked != true)
                return true;
            if (!string.Equals(group.lockState.passwordHash, ArchitectorGroupLocks.HashPassword(password), StringComparison.Ordinal))
                return false;
            sessionUnlockedGroupIds.Add(groupId);
            if (keepUntilEditorRestart)
                keptUnlocks[groupId] = group.lockState.passwordHash;
            else
                keptUnlocks.Remove(groupId);
            Changed?.Invoke();
            return true;
        }

        public string ExportKeptUnlocks()
        {
            return string.Join(";", keptUnlocks.Select(pair => pair.Key + "=" + pair.Value));
        }

        public void RestoreKeptUnlocks(string kept)
        {
            foreach (var entry in (kept ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = entry.IndexOf('=');
                if (separator <= 0)
                    continue;
                var groupId = entry.Substring(0, separator);
                var passwordHash = entry.Substring(separator + 1);
                var group = FindGroup(groupId);
                if (group?.lockState?.locked != true
                    || !string.Equals(group.lockState.passwordHash, passwordHash, StringComparison.Ordinal))
                    continue;
                sessionUnlockedGroupIds.Add(groupId);
                keptUnlocks[groupId] = passwordHash;
            }
            Changed?.Invoke();
        }

        public void RelockGroup(string groupId)
        {
            keptUnlocks.Remove(groupId);
            if (sessionUnlockedGroupIds.Remove(groupId))
                Changed?.Invoke();
        }

        public void RemoveGroupLock(string groupId)
        {
            var group = FindGroup(groupId) ?? throw new ArchitectorDataException($"Group '{groupId}' does not exist.");
            if (IsGroupLockedForEditing(groupId))
                throw new ArchitectorDataException($"Unlock group '{group.name}' before removing its lock.");
            ExecuteProjectChange("Remove Group Lock", project =>
                project.groups.First(candidate => candidate.id == groupId).lockState = new ArchitectorGroupLockRecord());
            sessionUnlockedGroupIds.Remove(groupId);
            keptUnlocks.Remove(groupId);
        }

        private void EnforceGroupLocks(
            ArchitectorProjectDocument nextProject,
            IReadOnlyDictionary<string, ArchitectorLayerDocuments> nextLayers,
            IReadOnlyDictionary<string, string> previousFiles,
            IReadOnlyDictionary<string, string> nextFiles)
        {
            foreach (var locked in Project.groups.Where(group => IsGroupLockedForEditing(group.id)))
            {
                var groupIds = new HashSet<string>(StringComparer.Ordinal);
                var layerIds = new HashSet<string>(StringComparer.Ordinal);
                CollectDescendants(Project, locked.id, groupIds, layerIds);

                var protectedEntries = groupIds.Select(id => (ArchitectorPaths.GroupsDirectoryName, ArchitectorPaths.GroupFileSuffix, id))
                    .Concat(layerIds.SelectMany(id => new[]
                    {
                        (ArchitectorPaths.LayersDirectoryName, ArchitectorPaths.LayerFileSuffix, id),
                        (ArchitectorPaths.LayoutsDirectoryName, ArchitectorPaths.LayoutFileSuffix, id),
                        (ArchitectorPaths.BaselinesDirectoryName, ArchitectorPaths.BaselineFileSuffix, id)
                    }));
                var changed = protectedEntries.Any(entry =>
                    !string.Equals(
                        ArchitectorStorageCodec.FindEntry(previousFiles, entry.Item1, entry.Item2, entry.id),
                        ArchitectorStorageCodec.FindEntry(nextFiles, entry.Item1, entry.Item2, entry.id),
                        StringComparison.Ordinal));
                var added = nextProject.groups.Any(group => groupIds.Contains(group.parentGroupId ?? string.Empty) && !groupIds.Contains(group.id))
                            || nextProject.layers.Any(layer => groupIds.Contains(layer.parentGroupId ?? string.Empty) && !layerIds.Contains(layer.id));
                if (changed || added || AddsOutgoingDependency(layerIds, nextLayers))
                    throw new ArchitectorDataException(LockViolationMessage(locked));
            }
        }

        private void EnforceLayoutLock(ArchitectorLayerRecord layer)
        {
            foreach (var locked in Project.groups.Where(group => IsGroupLockedForEditing(group.id)))
            {
                var groupIds = new HashSet<string>(StringComparer.Ordinal);
                var layerIds = new HashSet<string>(StringComparer.Ordinal);
                CollectDescendants(Project, locked.id, groupIds, layerIds);
                if (layerIds.Contains(layer.id))
                    throw new ArchitectorDataException(LockViolationMessage(locked));
            }
        }

        private bool AddsOutgoingDependency(
            ISet<string> lockedLayerIds,
            IReadOnlyDictionary<string, ArchitectorLayerDocuments> nextLayers)
        {
            var existing = lockedLayerIds
                .Where(layerDocuments.ContainsKey)
                .SelectMany(id => layerDocuments[id].semantic.edges)
                .Select(RelationshipIdentity)
                .ToHashSet(StringComparer.Ordinal);
            return nextLayers.Values
                .SelectMany(documents => documents.semantic.edges)
                .Where(edge => string.Equals(edge.kind, ArchitectorEdgeKinds.Dependency, StringComparison.Ordinal)
                               && lockedLayerIds.Contains(edge.sourceLayerId)
                               && !lockedLayerIds.Contains(edge.targetLayerId))
                .Any(edge => !existing.Contains(RelationshipIdentity(edge)));
        }

        private static string RelationshipIdentity(ArchitectorEdgeRecord edge)
        {
            return $"{edge.id}|{edge.kind}|{edge.sourceLayerId}|{edge.sourceNodeId}|{edge.targetLayerId}|{edge.targetNodeId}";
        }

        private string LockViolationMessage(ArchitectorGroupRecord group)
        {
            var notice = string.IsNullOrWhiteSpace(group.lockState?.notice)
                ? ArchitectorGroupLocks.DefaultNotice
                : group.lockState.notice;
            return $"Group '{GetEntryPath(group.id)}' is locked. {notice} " +
                   "Do not try to bypass the lock or look for its password; propose the change to the user.";
        }
    }
}
