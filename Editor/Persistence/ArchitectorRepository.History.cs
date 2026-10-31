using System;
using System.Collections.Generic;
using System.Linq;

namespace Architector.Editor
{
    public sealed partial class ArchitectorRepository
    {
        public string ExportHistory()
        {
            var document = new ArchitectorHistoryDocument
            {
                undo = undo.Reverse().Select(ExportRecord).ToList(),
                redo = redo.Reverse().Select(ExportRecord).ToList()
            };
            return ArchitectorJson.Serialize(document);
        }

        public bool ImportHistory(string json)
        {
            undo.Clear();
            redo.Clear();
            if (string.IsNullOrEmpty(json))
                return false;
            var document = ArchitectorJson.Deserialize<ArchitectorHistoryDocument>(json, "Architector history");
            var undoRecords = (document.undo ?? new List<ArchitectorHistoryRecord>()).Select(ImportRecord).ToList();
            var redoRecords = (document.redo ?? new List<ArchitectorHistoryRecord>()).Select(ImportRecord).ToList();
            if (undoRecords.Count > 0 && !MatchesDisk(undoRecords[^1].after)
                || redoRecords.Count > 0 && !MatchesDisk(redoRecords[^1].before))
                return false;
            foreach (var record in undoRecords)
                undo.Push(record);
            foreach (var record in redoRecords)
                redo.Push(record);
            Changed?.Invoke();
            return undo.Count + redo.Count > 0;
        }

        private bool MatchesDisk(IEnumerable<ArchitectorFileSnapshot> snapshots)
        {
            return snapshots.All(snapshot => snapshot.content == null
                ? !diskFiles.ContainsKey(snapshot.relativePath)
                : diskFiles.TryGetValue(snapshot.relativePath, out var content)
                  && string.Equals(content, snapshot.content, StringComparison.Ordinal));
        }

        private static ArchitectorHistoryRecord ExportRecord(ArchitectorUndoRecord record)
        {
            return new ArchitectorHistoryRecord
            {
                name = record.name,
                before = record.before.Select(ExportSnapshot).ToList(),
                after = record.after.Select(ExportSnapshot).ToList()
            };
        }

        private static ArchitectorHistorySnapshot ExportSnapshot(ArchitectorFileSnapshot snapshot)
        {
            return new ArchitectorHistorySnapshot
            {
                path = snapshot.relativePath,
                exists = snapshot.content != null,
                content = snapshot.content ?? string.Empty
            };
        }

        private static ArchitectorUndoRecord ImportRecord(ArchitectorHistoryRecord record)
        {
            return new ArchitectorUndoRecord(
                record.name ?? string.Empty,
                (record.before ?? new List<ArchitectorHistorySnapshot>()).Select(ImportSnapshot).ToList(),
                (record.after ?? new List<ArchitectorHistorySnapshot>()).Select(ImportSnapshot).ToList());
        }

        private static ArchitectorFileSnapshot ImportSnapshot(ArchitectorHistorySnapshot snapshot)
        {
            return new ArchitectorFileSnapshot(snapshot.path, snapshot.exists ? snapshot.content : null);
        }

        [Serializable]
        private sealed class ArchitectorHistoryDocument
        {
            public List<ArchitectorHistoryRecord> undo = new();
            public List<ArchitectorHistoryRecord> redo = new();
        }

        [Serializable]
        private sealed class ArchitectorHistoryRecord
        {
            public string name = string.Empty;
            public List<ArchitectorHistorySnapshot> before = new();
            public List<ArchitectorHistorySnapshot> after = new();
        }

        [Serializable]
        private sealed class ArchitectorHistorySnapshot
        {
            public string path = string.Empty;
            public bool exists;
            public string content = string.Empty;
        }
    }
}
