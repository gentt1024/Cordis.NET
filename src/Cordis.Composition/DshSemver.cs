using System.Globalization;
using System.Text.RegularExpressions;

namespace Cordis.Composition;

// npm range comparisons with includePrerelease=true. Build identifiers participate in identity, not ordering.
internal static class DshSemver
{
    internal sealed record Version(long Major, long Minor, long Patch, string[] Prerelease, string Text)
        : IComparable<Version>
    {
        private const string Identifiers = @"[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*";

        private static readonly Regex Exact = new(
            @"^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(" + Identifiers + @"))?(?:\+(" + Identifiers +
            @"))?$",
            RegexOptions.CultureInvariant);

        internal static bool TryParse(string? text, out Version parsed)
        {
            parsed = null!;
            if (text is null || text.Length > 256)
                return false;
            var match = Exact.Match(text.Trim());
            if (!match.Success || !Number(match.Groups[1].Value, out var major) ||
                !Number(match.Groups[2].Value, out var minor) || !Number(match.Groups[3].Value, out var patch))
                return false;
            var pre = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
            if (pre.Any(id => Numeric(id) && id.Length > 1 && id[0] == '0'))
                return false;
            var canonical = $"{major}.{minor}.{patch}" + (pre.Length == 0 ? "" : "-" + string.Join('.', pre)) +
                (match.Groups[5].Success ? "+" + match.Groups[5].Value : "");
            parsed = new(major, minor, patch, pre, canonical);
            return true;
        }

        internal static Version Bound(long major, long minor, long patch, bool prereleaseFloor = false)
        {
            if (major > 9007199254740991 || minor > 9007199254740991 || patch > 9007199254740991)
                throw new FormatException("npm version component exceeds MAX_SAFE_INTEGER.");
            return new(
                major,
                minor,
                patch,
                prereleaseFloor ? ["0"] : [],
                $"{major}.{minor}.{patch}" + (prereleaseFloor ? "-0" : ""));
        }

        public int CompareTo(Version? other)
        {
            if (other is null)
                return 1;
            var comparison = Major.CompareTo(other.Major);
            if (comparison == 0)
                comparison = Minor.CompareTo(other.Minor);
            if (comparison == 0)
                comparison = Patch.CompareTo(other.Patch);
            if (comparison != 0)
                return comparison;
            if (Prerelease.Length == 0 || other.Prerelease.Length == 0)
                return other.Prerelease.Length.CompareTo(Prerelease.Length);
            for (var index = 0;index < Math.Min(Prerelease.Length, other.Prerelease.Length);index++)
            {
                var left = Prerelease[index];
                var right = other.Prerelease[index];
                var leftNumeric = Numeric(left);
                var rightNumeric = Numeric(right);
                comparison = leftNumeric && rightNumeric ? CompareNumeric(left, right) :
                    leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1) : string.CompareOrdinal(left, right);
                if (comparison != 0)
                    return comparison;
            }

            return Prerelease.Length.CompareTo(other.Prerelease.Length);
        }

        // npm compares numeric prerelease identifiers using JavaScript Number, including its rounding above MAX_SAFE_INTEGER.
        private static int CompareNumeric(string left, string right) =>
            double
                .Parse(left, CultureInfo.InvariantCulture)
                .CompareTo(double.Parse(right, CultureInfo.InvariantCulture));
    }

    private sealed record Partial(long Major, long Minor, long Patch, int Specified, Version? Exact);

    private sealed record Comparator(string Operator, Version Version)
    {
        internal bool Test(Version candidate)
        {
            var comparison = candidate.CompareTo(Version);
            return Operator switch
            {
                ">" => comparison > 0,
                ">=" => comparison >= 0,
                "<" => comparison < 0,
                "<=" => comparison <= 0,
                _ => comparison == 0
            };
        }
    }

    private static bool Numeric(string value) => value.All(c => c is >= '0' and <= '9');

    private static bool Number(string value, out long number) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number <= 9007199254740991;

    private static readonly Regex Part = new(
        @"^v?([0-9]+|[xX*])(?:\.([0-9]+|[xX*]))?(?:\.([0-9]+|[xX*]))?(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant);

    private static bool ParsePartial(string text, out Partial part, bool ignoreRightOfWildcard = false)
    {
        part = null!;
        var match = Part.Match(text);
        if (!match.Success || text.Length > 256)
            return false;
        if (match.Groups[4].Success && !match.Groups[3].Success)
            return false;
        if (match.Groups[4].Success &&
            match.Groups[4].Value.Split('.').Any(id => Numeric(id) && id.Length > 1 && id[0] == '0'))
            return false;
        var numbers = new long[3];
        var specified = 0;
        var wildcard = false;
        for (var index = 0;index < 3;index++)
        {
            var value = match.Groups[index + 1].Value;
            if (value.Length == 0 || value is "x" or "X" or "*")
            {
                wildcard = true;
                continue;
            }

            if (wildcard && !ignoreRightOfWildcard)
                return false;
            if (!Number(value, out var number) || value.Length > 1 && value[0] == '0')
                return false;
            if (!wildcard)
            {
                numbers[index] = number;
                specified++;
            }
        }

        Version? exact = null;
        if (specified == 3 && !Version.TryParse(text.TrimStart('='), out exact!))
            return false;
        part = new(numbers[0], numbers[1], numbers[2], specified, exact);
        return true;
    }

    internal static bool Satisfies(Version candidate, string range)
    {
        if (string.IsNullOrWhiteSpace(range))
            return false;
        var alternatives = new List<List<Comparator>>();
        try
        {
            foreach (var branch in range.Split("||", StringSplitOptions.None))
            {
                if (!ParseBranch(branch.Trim(), out var comparators))
                    return false;
                alternatives.Add(comparators);
            }
        }
        catch (FormatException)
        {
            return false;
        }

        return alternatives.Any(comparators => comparators.All(comparator => comparator.Test(candidate)));
    }

    private static bool ParseBranch(string branch, out List<Comparator> comparators)
    {
        comparators = [];
        if (branch.Length == 0)
            return true;
        var hyphen = Regex.Match(branch, @"^(\S+)\s+-\s+(\S+)$", RegexOptions.CultureInvariant);
        if (hyphen.Success)
        {
            if (!ParsePartial(hyphen.Groups[1].Value, out var lower, true) ||
                !ParsePartial(hyphen.Groups[2].Value, out var upper, true))
                return false;
            if (lower.Specified != 0)
                comparators.Add(
                    new(
                        ">=",
                        lower.Exact is { Prerelease.Length: > 0 }
                            ? lower.Exact
                            : Version.Bound(lower.Major, lower.Minor, lower.Patch, true)));
            if (upper.Specified == 3 && upper.Exact is { Prerelease.Length: > 0 })
                comparators.Add(new("<=", upper.Exact));
            else if (upper.Specified != 0)
                comparators.Add(
                    new(
                        "<",
                        upper.Specified switch
                        {
                            1 => Version.Bound(upper.Major + 1, 0, 0, true),
                            2 => Version.Bound(upper.Major, upper.Minor + 1, 0, true),
                            _ => Version.Bound(upper.Major, upper.Minor, upper.Patch + 1, true)
                        }));
            return true;
        }

        branch = Regex.Replace(branch, @"(<=|>=|<|>|=|\^|~>?)\s+", "$1", RegexOptions.CultureInvariant);
        foreach (var token in branch.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(token, @"^(<=|>=|<|>|=|\^|~>?)?(.*)$", RegexOptions.CultureInvariant);
            var op = match.Groups[1].Value;
            if (!ParsePartial(match.Groups[2].Value, out var part, op is "^" or "~" or "~>"))
                return false;
            if (op is "^" or "~" or "~>")
            {
                if (part.Specified == 0)
                    continue;
                comparators.Add(new(">=", part.Exact ?? Version.Bound(part.Major, part.Minor, part.Patch, true)));
                var upper = op != "^"
                    ? (part.Specified == 1
                        ? Version.Bound(part.Major + 1, 0, 0, true)
                        : Version.Bound(part.Major, part.Minor + 1, 0, true))
                    : part.Major > 0 || part.Specified == 1
                        ? Version.Bound(part.Major + 1, 0, 0, true)
                        : part.Minor > 0 || part.Specified == 2
                            ? Version.Bound(part.Major, part.Minor + 1, 0, true)
                            : Version.Bound(part.Major, part.Minor, part.Patch + 1, true);
                comparators.Add(new("<", upper));
            }
            else if (part.Specified == 3)
                comparators.Add(new(op, part.Exact!));
            else if (part.Specified == 0)
            {
                if (op is "<" or ">")
                    comparators.Add(new("<", Version.Bound(0, 0, 0, true)));
            }
            else
            {
                var lower = Version.Bound(part.Major, part.Minor, 0, true);
                var upper = part.Specified == 1
                    ? Version.Bound(part.Major + 1, 0, 0, true)
                    : Version.Bound(part.Major, part.Minor + 1, 0, true);
                if (op == ">")
                    comparators.Add(new(">=", upper));
                else if (op == "<=")
                    comparators.Add(new("<", upper));
                else if (op is ">=" or "<")
                    comparators.Add(new(op, lower));
                else
                {
                    comparators.Add(new(">=", lower));
                    comparators.Add(new("<", upper));
                }
            }
        }

        return true;
    }
}
