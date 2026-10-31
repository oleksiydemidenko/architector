using System;
using System.Collections.Generic;
using System.Linq;

namespace Architector.Editor
{
    public static class ArchitectorOrderKeys
    {
        private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

        public static bool IsValid(string key)
        {
            return !string.IsNullOrEmpty(key)
                   && key.All(character => Digits.IndexOf(character) >= 0)
                   && key[key.Length - 1] != Digits[0];
        }

        public static string Between(string lower, string upper)
        {
            lower ??= string.Empty;
            if (lower.Length > 0 && !IsValid(lower))
                throw new ArgumentException($"Invalid lower order key '{lower}'.", nameof(lower));
            if (upper != null && !IsValid(upper))
                throw new ArgumentException($"Invalid upper order key '{upper}'.", nameof(upper));
            if (upper != null && string.CompareOrdinal(lower, upper) >= 0)
                throw new ArgumentException($"Order key '{lower}' must precede '{upper}'.");
            return Midpoint(lower, upper);
        }

        public static IReadOnlyDictionary<string, string> Assign(IReadOnlyList<(string id, string key)> orderedEntries)
        {
            var count = orderedEntries.Count;
            var kept = LongestIncreasingValidKeys(orderedEntries);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var index = 0;
            while (index < count)
            {
                if (kept[index])
                {
                    result.Add(orderedEntries[index].id, orderedEntries[index].key);
                    index++;
                    continue;
                }

                var runStart = index;
                while (index < count && !kept[index])
                    index++;
                var lower = runStart > 0 ? result[orderedEntries[runStart - 1].id] : null;
                var upper = index < count ? orderedEntries[index].key : null;
                var generated = new string[index - runStart];
                Fill(generated, 0, generated.Length, lower, upper);
                for (var offset = 0; offset < generated.Length; offset++)
                    result.Add(orderedEntries[runStart + offset].id, generated[offset]);
            }
            return result;
        }

        public static int Compare(string firstKey, string firstId, string secondKey, string secondId)
        {
            var byKey = string.CompareOrdinal(firstKey ?? string.Empty, secondKey ?? string.Empty);
            return byKey != 0 ? byKey : string.CompareOrdinal(firstId, secondId);
        }

        private static void Fill(string[] target, int start, int length, string lower, string upper)
        {
            if (length <= 0)
                return;
            var middle = start + length / 2;
            target[middle] = Between(lower, upper);
            Fill(target, start, middle - start, lower, target[middle]);
            Fill(target, middle + 1, start + length - middle - 1, target[middle], upper);
        }

        private static bool[] LongestIncreasingValidKeys(IReadOnlyList<(string id, string key)> entries)
        {
            var count = entries.Count;
            var length = new int[count];
            var previous = new int[count];
            var best = -1;
            for (var current = 0; current < count; current++)
            {
                previous[current] = -1;
                if (!IsValid(entries[current].key))
                    continue;
                length[current] = 1;
                for (var candidate = 0; candidate < current; candidate++)
                {
                    if (length[candidate] == 0
                        || string.CompareOrdinal(entries[candidate].key, entries[current].key) >= 0
                        || length[candidate] + 1 <= length[current])
                        continue;
                    length[current] = length[candidate] + 1;
                    previous[current] = candidate;
                }
                if (best < 0 || length[current] > length[best])
                    best = current;
            }

            var kept = new bool[count];
            for (var cursor = best; cursor >= 0; cursor = previous[cursor])
                kept[cursor] = true;
            return kept;
        }

        private static string Midpoint(string lower, string upper)
        {
            if (upper != null)
            {
                var shared = 0;
                while (shared < upper.Length && CharAt(lower, shared) == upper[shared])
                    shared++;
                if (shared > 0)
                {
                    return upper.Substring(0, shared) + Midpoint(
                        lower.Length > shared ? lower.Substring(shared) : string.Empty,
                        upper.Substring(shared));
                }
            }

            var lowerDigit = lower.Length > 0 ? Digits.IndexOf(lower[0]) : 0;
            var upperDigit = upper != null ? Digits.IndexOf(upper[0]) : Digits.Length;
            if (upperDigit - lowerDigit > 1)
                return Digits[(lowerDigit + upperDigit + 1) / 2].ToString();
            if (upper != null && upper.Length > 1)
                return upper.Substring(0, 1);
            return Digits[lowerDigit] + Midpoint(lower.Length > 1 ? lower.Substring(1) : string.Empty, null);
        }

        private static char CharAt(string value, int index)
        {
            return index < value.Length ? value[index] : Digits[0];
        }
    }
}
