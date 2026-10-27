namespace AresToys.App.Services;

/// <summary>Case insensitive ordering that reads runs of digits as numbers, so "512p" sorts before
/// "1024p" and "square 512p" before "square 1024p". Used for workflow lists whose names carry
/// sizes.</summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var si = i;
                var sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;

                var numX = x.AsSpan(si, i - si).TrimStart('0');
                var numY = y.AsSpan(sj, j - sj).TrimStart('0');
                if (numX.Length != numY.Length) return numX.Length < numY.Length ? -1 : 1;
                var byDigits = numX.CompareTo(numY, StringComparison.Ordinal);
                if (byDigits != 0) return byDigits;
            }
            else
            {
                var byChar = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (byChar != 0) return byChar;
                i++;
                j++;
            }
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
