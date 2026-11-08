using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Architector.Editor
{
    public sealed class ArchitectorUserStateStore
    {
        private readonly ArchitectorPaths paths;
        private ArchitectorUserStateDocument document;
        private bool layerViewsDirty;

        public ArchitectorUserStateStore(ArchitectorPaths paths)
        {
            this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
            Load();
        }

        public ArchitectorLayerViewStateRecord GetLayerView(string viewId, string layerId)
        {
            var state = document.layerViews.FirstOrDefault(candidate => candidate.viewId == viewId && candidate.layerId == layerId);
            if (state != null)
                return state;

            state = new ArchitectorLayerViewStateRecord
            {
                viewId = viewId,
                layerId = layerId
            };
            document.layerViews.Add(state);
            Save();
            return state;
        }

        public void SaveLayerView(string viewId, string layerId, float panX, float panY, float zoom)
        {
            var state = GetLayerView(viewId, layerId);
            state.panX = panX;
            state.panY = panY;
            state.zoom = zoom;
            Save();
        }

        public void SetLayerView(string viewId, string layerId, float panX, float panY, float zoom)
        {
            var state = GetLayerView(viewId, layerId);
            state.panX = panX;
            state.panY = panY;
            state.zoom = zoom;
            layerViewsDirty = true;
        }

        public void FlushLayerViews()
        {
            if (layerViewsDirty)
                Save();
        }

        public bool GetConformanceExpanded(string viewId, string layerId)
        {
            return document.layerInspectors.FirstOrDefault(candidate =>
                candidate.viewId == viewId && candidate.layerId == layerId)?.conformanceExpanded == true;
        }

        public void SaveConformanceExpanded(string viewId, string layerId, bool expanded)
        {
            var state = document.layerInspectors.FirstOrDefault(candidate =>
                candidate.viewId == viewId && candidate.layerId == layerId);
            if (state == null)
            {
                state = new ArchitectorLayerInspectorStateRecord
                {
                    viewId = viewId,
                    layerId = layerId
                };
                document.layerInspectors.Add(state);
            }
            if (state.conformanceExpanded == expanded)
                return;
            state.conformanceExpanded = expanded;
            Save();
        }

        public bool IsArranged(string layerId, string fingerprint)
        {
            return document.arrangedLayers.Any(record => record.layerId == layerId && record.fingerprint == fingerprint);
        }

        public void SetArranged(string layerId, string fingerprint)
        {
            document.arrangedLayers.RemoveAll(record => record.layerId == layerId);
            if (!string.IsNullOrEmpty(fingerprint))
                document.arrangedLayers.Add(new ArchitectorArrangedLayerRecord { layerId = layerId, fingerprint = fingerprint });
            document.arrangedLayers.Sort((left, right) => string.CompareOrdinal(left.layerId, right.layerId));
            Save();
        }

        public ArchitectorWindowNavigationRecord GetMasterNavigation()
        {
            return ArchitectorJson.Clone(document.masterNavigation);
        }

        public void SaveMasterNavigation(ArchitectorWindowNavigationRecord navigation)
        {
            if (navigation == null)
                return;
            document.masterNavigation = ArchitectorJson.Clone(navigation);
            Save();
        }

        public bool MatchesMasterNavigation(
            string currentGroupId,
            string activeLayerId,
            IReadOnlyList<string> openLayerIds,
            IReadOnlyList<string> layerVisitHistory,
            int layerVisitHistoryIndex,
            float layerTabsScroll)
        {
            var saved = document.masterNavigation;
            return string.Equals(saved.currentGroupId, currentGroupId, StringComparison.Ordinal)
                   && string.Equals(saved.activeLayerId, activeLayerId, StringComparison.Ordinal)
                   && saved.layerVisitHistoryIndex == layerVisitHistoryIndex
                   && saved.layerTabsScroll.Equals(layerTabsScroll)
                   && SameIds(saved.openLayerIds, openLayerIds)
                   && SameIds(saved.layerVisitHistory, layerVisitHistory);
        }

        private static bool SameIds(IReadOnlyList<string> saved, IReadOnlyList<string> current)
        {
            var count = current?.Count ?? 0;
            if ((saved?.Count ?? 0) != count)
                return false;
            for (var index = 0; index < count; index++)
            {
                if (!string.Equals(saved[index], current[index], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private void Load()
        {
            Directory.CreateDirectory(paths.UserStateRoot);
            if (!File.Exists(paths.UserStatePath))
            {
                document = new ArchitectorUserStateDocument();
                Save();
                return;
            }

            try
            {
                document = ArchitectorJson.Read<ArchitectorUserStateDocument>(paths.UserStatePath);
                if (document.schemaVersion != ArchitectorValidation.CurrentSchemaVersion)
                    throw new ArchitectorDataException($"Unsupported user-state schema version {document.schemaVersion}.");
                document.layerViews ??= new System.Collections.Generic.List<ArchitectorLayerViewStateRecord>();
                document.layerInspectors ??= new System.Collections.Generic.List<ArchitectorLayerInspectorStateRecord>();
                document.masterNavigation ??= new ArchitectorWindowNavigationRecord();
                document.arrangedLayers ??= new System.Collections.Generic.List<ArchitectorArrangedLayerRecord>();
                document.masterNavigation.openLayerIds ??= new System.Collections.Generic.List<string>();
                document.masterNavigation.layerVisitHistory ??= new System.Collections.Generic.List<string>();
            }
            catch
            {
                document = new ArchitectorUserStateDocument();
                Save();
            }
        }

        private void Save()
        {
            document.layerViews.Sort((left, right) =>
            {
                var viewComparison = string.CompareOrdinal(left.viewId, right.viewId);
                return viewComparison != 0 ? viewComparison : string.CompareOrdinal(left.layerId, right.layerId);
            });
            document.layerInspectors.Sort((left, right) =>
            {
                var viewComparison = string.CompareOrdinal(left.viewId, right.viewId);
                return viewComparison != 0 ? viewComparison : string.CompareOrdinal(left.layerId, right.layerId);
            });
            ArchitectorJson.WriteAtomic(paths.UserStatePath, document);
            layerViewsDirty = false;
        }
    }
}
