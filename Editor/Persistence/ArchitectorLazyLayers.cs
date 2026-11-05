using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Architector.Editor
{
    internal sealed class ArchitectorLazyLayers : IDictionary<string, ArchitectorLayerDocuments>, IReadOnlyDictionary<string, ArchitectorLayerDocuments>
    {
        private readonly IReadOnlyDictionary<string, ArchitectorLayerDocuments> session;
        private readonly Func<ArchitectorLayerDocuments, ArchitectorLayerDocuments> clone;
        private readonly Dictionary<string, ArchitectorLayerDocuments> values;
        private readonly HashSet<string> touched = new(StringComparer.Ordinal);
        private bool frozen;

        public ArchitectorLazyLayers(
            IReadOnlyDictionary<string, ArchitectorLayerDocuments> session,
            Func<ArchitectorLayerDocuments, ArchitectorLayerDocuments> clone)
        {
            this.session = session;
            this.clone = clone;
            values = session.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }

        public IReadOnlyCollection<string> Touched => touched;

        public void Freeze() => frozen = true;

        public ArchitectorLayerDocuments Peek(string layerId) => values[layerId];

        public ArchitectorLayerDocuments Mutable(string layerId)
        {
            var current = values[layerId];
            if (session.TryGetValue(layerId, out var shared) && IsShared(current, shared))
            {
                current = clone(current);
                values[layerId] = current;
            }
            touched.Add(layerId);
            return current;
        }

        public static bool IsShared(ArchitectorLayerDocuments first, ArchitectorLayerDocuments second) =>
            ReferenceEquals(first.semantic, second.semantic) && ReferenceEquals(first.layout, second.layout);

        public static ArchitectorLayerDocuments Peek(IDictionary<string, ArchitectorLayerDocuments> layers, string layerId) =>
            layers is ArchitectorLazyLayers lazy ? lazy.Peek(layerId) : layers[layerId];

        public static ArchitectorLayerDocuments Peek(IReadOnlyDictionary<string, ArchitectorLayerDocuments> layers, string layerId) =>
            layers is ArchitectorLazyLayers lazy ? lazy.Peek(layerId) : layers[layerId];

        public static ArchitectorLayerDocuments Mutable(IReadOnlyDictionary<string, ArchitectorLayerDocuments> layers, string layerId) =>
            layers is ArchitectorLazyLayers lazy ? lazy.Mutable(layerId) : layers[layerId];

        public static IEnumerable<ArchitectorLayerDocuments> PeekAll(IDictionary<string, ArchitectorLayerDocuments> layers) =>
            layers is ArchitectorLazyLayers lazy ? lazy.values.Values : layers.Values;

        private ArchitectorLayerDocuments Get(string layerId) => frozen ? values[layerId] : Mutable(layerId);

        public ArchitectorLayerDocuments this[string key]
        {
            get => Get(key);
            set
            {
                values[key] = value;
                touched.Add(key);
            }
        }

        public bool TryGetValue(string key, out ArchitectorLayerDocuments value)
        {
            if (!values.ContainsKey(key))
            {
                value = default;
                return false;
            }
            value = Get(key);
            return true;
        }

        public bool ContainsKey(string key) => values.ContainsKey(key);

        public void Add(string key, ArchitectorLayerDocuments value)
        {
            values.Add(key, value);
            touched.Add(key);
        }

        public bool Remove(string key)
        {
            touched.Add(key);
            return values.Remove(key);
        }

        public int Count => values.Count;

        public ICollection<string> Keys => values.Keys;

        IEnumerable<string> IReadOnlyDictionary<string, ArchitectorLayerDocuments>.Keys => values.Keys;

        public ICollection<ArchitectorLayerDocuments> Values => values.Keys.ToArray().Select(Get).ToArray();

        IEnumerable<ArchitectorLayerDocuments> IReadOnlyDictionary<string, ArchitectorLayerDocuments>.Values => Values;

        public IEnumerator<KeyValuePair<string, ArchitectorLayerDocuments>> GetEnumerator() =>
            values.Keys.ToArray().Select(key => new KeyValuePair<string, ArchitectorLayerDocuments>(key, Get(key))).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public bool IsReadOnly => false;

        public void Add(KeyValuePair<string, ArchitectorLayerDocuments> item) => Add(item.Key, item.Value);

        public void Clear()
        {
            foreach (var key in values.Keys)
                touched.Add(key);
            values.Clear();
        }

        public bool Contains(KeyValuePair<string, ArchitectorLayerDocuments> item) =>
            values.TryGetValue(item.Key, out var value) && IsShared(value, item.Value);

        public void CopyTo(KeyValuePair<string, ArchitectorLayerDocuments>[] array, int arrayIndex)
        {
            foreach (var pair in this)
                array[arrayIndex++] = pair;
        }

        public bool Remove(KeyValuePair<string, ArchitectorLayerDocuments> item) => Contains(item) && Remove(item.Key);
    }
}
