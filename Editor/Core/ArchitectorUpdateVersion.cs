using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Architector.Editor
{
    public readonly struct ArchitectorUpdateVersion : IComparable<ArchitectorUpdateVersion>
    {
        private static readonly Regex Pattern = new(@"^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$");
        private readonly Version number;
        private readonly string prerelease;

        private ArchitectorUpdateVersion(Version number, string prerelease)
        {
            this.number = number;
            this.prerelease = prerelease;
        }

        public bool IsPrerelease => prerelease.Length > 0;

        public static bool TryParse(string text, out ArchitectorUpdateVersion version)
        {
            version = default;
            var match = Pattern.Match(text ?? string.Empty);
            if (!match.Success || !Version.TryParse($"{match.Groups[1].Value}.{match.Groups[2].Value}.{match.Groups[3].Value}", out var number))
                return false;
            var prerelease = match.Groups[4].Value;
            foreach (var identifier in prerelease.Split('.'))
                if (IsNumeric(identifier) && identifier.Length > 1 && identifier[0] == '0')
                    return false;
            version = new ArchitectorUpdateVersion(number, prerelease);
            return true;
        }

        public int CompareTo(ArchitectorUpdateVersion other)
        {
            var comparison = number.CompareTo(other.number);
            if (comparison != 0)
                return comparison;
            if (prerelease.Length == 0 || other.prerelease.Length == 0)
                return prerelease.Length == other.prerelease.Length ? 0 : prerelease.Length == 0 ? 1 : -1;
            var left = prerelease.Split('.');
            var right = other.prerelease.Split('.');
            for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
            {
                var leftNumeric = IsNumeric(left[index]);
                var rightNumeric = IsNumeric(right[index]);
                comparison = leftNumeric && rightNumeric
                    ? left[index].Length.CompareTo(right[index].Length)
                    : leftNumeric != rightNumeric ? leftNumeric ? -1 : 1 : 0;
                if (comparison == 0)
                    comparison = string.Compare(left[index], right[index], StringComparison.Ordinal);
                if (comparison != 0)
                    return comparison;
            }
            return left.Length.CompareTo(right.Length);
        }

        public static bool SupportsUnity(string minimum, string minimumRelease, string current)
        {
            if (string.IsNullOrEmpty(minimum))
                return true;
            var required = Regex.Match(minimum + "." + (string.IsNullOrEmpty(minimumRelease) ? "0a0" : minimumRelease), @"^([0-9]+)\.([0-9]+)\.([0-9]+)([abfp])([0-9]+)$");
            var installed = Regex.Match(current ?? string.Empty, @"^([0-9]+)\.([0-9]+)\.([0-9]+)([abfp])([0-9]+)");
            if (!required.Success || !installed.Success)
                return false;
            for (var index = 1; index <= 5; index++)
            {
                var comparison = index == 4
                    ? "abfp".IndexOf(installed.Groups[index].Value, StringComparison.Ordinal).CompareTo("abfp".IndexOf(required.Groups[index].Value, StringComparison.Ordinal))
                    : int.Parse(installed.Groups[index].Value, CultureInfo.InvariantCulture).CompareTo(int.Parse(required.Groups[index].Value, CultureInfo.InvariantCulture));
                if (comparison != 0)
                    return comparison > 0;
            }
            return true;
        }

        private static bool IsNumeric(string text)
        {
            if (text.Length == 0)
                return false;
            foreach (var character in text)
                if (character < '0' || character > '9')
                    return false;
            return true;
        }
    }
}
