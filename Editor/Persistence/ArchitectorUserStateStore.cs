using System;
using System.IO;
using System.Linq;

namespace Architector.Editor
{
    public sealed class ArchitectorUserStateStore
    {
        private readonly ArchitectorPaths paths;
        private ArchitectorUserStateDocument document;

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

        public ArchitectorWindowNavigationRecord GetMasterNavigation()
        {
            return ArchitectorJson.Clone(document.masterNavigation);
        }

        public void SaveMasterNavigation(ArchitectorWindowNavigationRecord navigation)
        {
            if (navigation == null
                || string.Equals(ArchitectorJson.Serialize(navigation), ArchitectorJson.Serialize(document.masterNavigation), StringComparison.Ordinal))
                return;
            document.masterNavigation = ArchitectorJson.Clone(navigation);
            Save();
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
        }
    }
}
