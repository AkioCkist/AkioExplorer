using System.Globalization;

namespace AkioExplorer.Core.Logic;

/// <summary>
/// "File 2" &lt; "File 10" ordering, case-insensitive and culture-aware, matching Windows Explorer.
/// Allocation free.
/// </summary>
public sealed class NaturalStringComparer : IComparer<string?>
{
    public static NaturalStringComparer Instance { get; } = new();

    private readonly CompareInfo _compareInfo;

    public NaturalStringComparer(CultureInfo? culture = null)
    {
        _compareInfo = (culture ?? CultureInfo.CurrentCulture).CompareInfo;
    }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        ReadOnlySpan<char> a = x;
        ReadOnlySpan<char> b = y;
        int i = 0, j = 0;

        while (i < a.Length && j < b.Length)
        {
            bool aDigit = char.IsAsciiDigit(a[i]);
            bool bDigit = char.IsAsciiDigit(b[j]);

            if (aDigit && bDigit)
            {
                int startA = i, startB = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;

                var runA = a[startA..i].TrimStart('0');
                var runB = b[startB..j].TrimStart('0');
                if (runA.Length != runB.Length) return runA.Length < runB.Length ? -1 : 1;

                int c = runA.SequenceCompareTo(runB);
                if (c != 0) return c < 0 ? -1 : 1;

                // Same value: fewer leading zeros first ("1" < "01").
                int lenA = i - startA, lenB = j - startB;
                if (lenA != lenB) return lenA < lenB ? -1 : 1;
            }
            else if (!aDigit && !bDigit)
            {
                int startA = i, startB = j;
                while (i < a.Length && !char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && !char.IsAsciiDigit(b[j])) j++;

                int c = _compareInfo.Compare(a[startA..i], b[startB..j], CompareOptions.IgnoreCase);
                if (c != 0)
                {
                    // Text runs of different length where one is a prefix: compare only the common
                    // part so "a1" vs "ab" keeps culture ordering.
                    return c < 0 ? -1 : 1;
                }
            }
            else
            {
                // Digits sort before letters.
                return aDigit ? -1 : 1;
            }
        }

        if (i < a.Length) return 1;
        if (j < b.Length) return -1;
        return string.CompareOrdinal(x, y) switch { < 0 => -1, > 0 => 1, _ => 0 };
    }
}
